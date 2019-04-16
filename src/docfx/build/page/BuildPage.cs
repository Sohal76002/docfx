// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace Microsoft.Docs.Build
{
    internal static class BuildPage
    {
        public static async Task<(IEnumerable<Error> errors, PublishItem publishItem)> Build(
            Context context,
            Document file,
            TableOfContentsMap tocMap,
            Action<Document> buildChild)
        {
            Debug.Assert(file.ContentType == ContentType.Page);

            var (errors, schema, model, metadata) = await Load(context, file, buildChild);

            if (!string.IsNullOrEmpty(metadata.BreadcrumbPath))
            {
                var (breadcrumbError, breadcrumbPath, _) = context.DependencyResolver.ResolveLink(metadata.BreadcrumbPath, file, file, buildChild);
                errors.AddIfNotNull(breadcrumbError);
                metadata.BreadcrumbPath.Value = breadcrumbPath;
            }

            model.SchemaType = schema.Name;
            model.Locale = file.Docset.Locale;
            model.Metadata = metadata;
            model.TocRel = tocMap.FindTocRelativePath(file);
            model.CanonicalUrl = file.CanonicalUrl;
            model.Bilingual = file.Docset.Config.Localization.Bilingual;

            (model.DocumentId, model.DocumentVersionIndependentId) = file.Docset.Redirections.TryGetDocumentId(file, out var docId) ? docId : file.Id;
            (model.ContentGitUrl, model.OriginalContentGitUrl, model.OriginalContentGitUrlTemplate, model.Gitcommit) = context.ContributionProvider.GetGitUrls(file);

            List<Error> contributorErrors;
            (contributorErrors, model.Author, model.Contributors, model.UpdatedAt) = await context.ContributionProvider.GetAuthorAndContributors(file, metadata.Author);
            if (contributorErrors != null)
                errors.AddRange(contributorErrors);

            var isPage = schema.Attribute is PageSchemaAttribute;
            var outputPath = file.GetOutputPath(model.Monikers, isPage);
            var (output, extensionData) = ApplyTemplate(context, file, model, isPage);

            var publishItem = new PublishItem
            {
                Url = file.SiteUrl,
                Path = outputPath,
                Locale = file.Docset.Locale,
                Monikers = model.Monikers,
                ExtensionData = extensionData,
            };

            if (context.PublishModelBuilder.TryAdd(file, publishItem))
            {
                if (output is string str)
                {
                    publishItem.Hash = context.Output.WriteTextWithHash(str, publishItem.Path);
                }
                else
                {
                    publishItem.Hash = context.Output.WriteJsonWithHash(output, publishItem.Path);
                }

                if (file.Docset.Legacy && extensionData != null)
                {
                    var metadataPath = outputPath.Substring(0, outputPath.Length - ".raw.page.json".Length) + ".mta.json";
                    context.Output.WriteJson(extensionData, metadataPath);
                }
            }

            return (errors, publishItem);
        }

        private static async Task<(List<Error> errors, Schema schema, PageModel model, FileMetadata metadata)>
            Load(Context context, Document file, Action<Document> buildChild)
        {
            if (file.FilePath.EndsWith(".md", PathUtility.PathComparison))
            {
                return LoadMarkdown(context, file, buildChild);
            }
            if (file.FilePath.EndsWith(".yml", PathUtility.PathComparison))
            {
                return await LoadYaml(context, file, buildChild);
            }

            Debug.Assert(file.FilePath.EndsWith(".json", PathUtility.PathComparison));
            return await LoadJson(context, file, buildChild);
        }

        private static (List<Error> errors, Schema schema, PageModel model, FileMetadata metadata)
            LoadMarkdown(Context context, Document file, Action<Document> buildChild)
        {
            var errors = new List<Error>();
            var content = file.ReadText();
            GitUtility.CheckMergeConflictMarker(content, file.FilePath);

            var (yamlHeaderErrors, yamlHeader) = ExtractYamlHeader.Extract(file, context);
            errors.AddRange(yamlHeaderErrors);

            var (metaErrors, fileMetadata) = context.MetadataProvider.GetMetadata<FileMetadata>(file, yamlHeader);
            errors.AddRange(metaErrors);

            var (error, monikers) = context.MonikerProvider.GetFileLevelMonikers(file, fileMetadata.MonikerRange);
            errors.AddIfNotNull(error);

            // TODO: handle blank page
            var (markupErrors, html) = MarkdownUtility.ToHtml(
                content,
                file,
                context.DependencyResolver,
                buildChild,
                rangeString => context.MonikerProvider.GetZoneMonikers(rangeString, monikers, errors),
                key => context.Template?.GetToken(key),
                MarkdownPipelineType.Markdown);
            errors.AddRange(markupErrors);

            var htmlDom = HtmlUtility.LoadHtml(html);
            var wordCount = HtmlUtility.CountWord(htmlDom);
            var bookmarks = HtmlUtility.GetBookmarks(htmlDom);

            if (!HtmlUtility.TryExtractTitle(htmlDom, out var title, out var rawTitle))
            {
                errors.Add(Errors.HeadingNotFound(file));
            }

            var model = new PageModel
            {
                Content = HtmlPostProcess(file, htmlDom),
                Title = yamlHeader.Value<string>("title") ?? title,
                RawTitle = rawTitle,
                WordCount = wordCount,
                Monikers = monikers,
            };

            context.BookmarkValidator.AddBookmarks(file, bookmarks);

            return (errors, Schema.Conceptual, model, fileMetadata);
        }

        private static async Task<(List<Error> errors, Schema schema, PageModel model, FileMetadata metadata)>
            LoadYaml(Context context, Document file, Action<Document> buildChild)
        {
            var (errors, token) = YamlUtility.Parse(file, context);

            return await LoadSchemaDocument(context, errors, token, file, buildChild);
        }

        private static async Task<(List<Error> errors, Schema schema, PageModel model, FileMetadata metadata)>
            LoadJson(Context context, Document file, Action<Document> buildChild)
        {
            var (errors, token) = JsonUtility.Parse(file, context);

            return await LoadSchemaDocument(context, errors, token, file, buildChild);
        }

        private static async Task<(List<Error> errors, Schema schema, PageModel model, FileMetadata metadata)>
            LoadSchemaDocument(Context context, List<Error> errors, JToken token, Document file, Action<Document> buildChild)
        {
            // TODO: for backward compatibility, when #YamlMime:YamlDocument, documentType is used to determine schema.
            //       when everything is moved to SDP, we can refactor the mime check to Document.TryCreate
            var obj = token as JObject;
            var schema = file.Schema ?? Schema.GetSchema(obj?.Value<string>("documentType"));
            if (schema is null)
            {
                throw Errors.SchemaNotFound(file.Mime).ToException();
            }

            // todo: why not directly use strong model here?
            var (schemaViolationErrors, content) = JsonUtility.ToObject(token, schema.Type, transform: AttributeTransformer.TransformSDP(context, file, buildChild));
            errors.AddRange(schemaViolationErrors);

            // TODO: add check before to avoid case failure
            var yamlHeader = obj?.Value<JObject>("metadata") ?? new JObject();
            if (file.Docset.Legacy && schema.Type == typeof(LandingData))
            {
                // merge extension data to metadata in legacy model
                var landingData = (LandingData)content;
                var mergedMetadata = new JObject();
                JsonUtility.Merge(mergedMetadata, landingData.ExtensionData);
                JsonUtility.Merge(mergedMetadata, yamlHeader);
                yamlHeader = mergedMetadata;
            }
            var title = yamlHeader.Value<string>("title") ?? obj?.Value<string>("title");

            if (file.Docset.Legacy && schema.Attribute is PageSchemaAttribute)
            {
                var html = await RazorTemplate.Render(schema.Name, content);
                content = HtmlPostProcess(file, HtmlUtility.LoadHtml(html));
            }

            var (metaErrors, fileMetadata) = context.MetadataProvider.GetMetadata<FileMetadata>(file, yamlHeader);
            errors.AddRange(metaErrors);

            var model = new PageModel
            {
                Content = content,
                Title = title,
                RawTitle = file.Docset.Legacy ? $"<h1>{obj?.Value<string>("title")}</h1>" : null,
                Monikers = new List<string>(),
            };

            return (errors, schema, model, fileMetadata);
        }

        private static string HtmlPostProcess(Document file, HtmlNode html)
        {
            html = html.StripTags();

            if (file.Docset.Legacy)
            {
                html = html.AddLinkType(file.Docset.Locale)
                           .RemoveRerunCodepenIframes();
            }

            if (string.IsNullOrWhiteSpace(html.OuterHtml))
            {
                return "<div></div>";
            }

            return LocalizationUtility.AddLeftToRightMarker(file.Docset, html.OuterHtml);
        }

        private static (object output, JObject extensionData) ApplyTemplate(Context context, Document file, PageModel model, bool isPage)
        {
            var rawMetadata = context.Template is null ? JsonUtility.ToJObject(model.Metadata) : context.Template.CreateRawMetadata(model, file);

            if (!file.Docset.Config.Output.Json && context.Template != null)
            {
                return (context.Template.Render(model, file, rawMetadata), null);
            }

            if (file.Docset.Legacy)
            {
                if (isPage && context.Template != null)
                {
                    return context.Template.Transform(model, rawMetadata);
                }

                return (model, null);
            }

            return (model, isPage ? rawMetadata : null);
        }
    }
}