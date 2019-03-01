// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.Docs.Build
{
    /// <summary>
    /// A dependency map generated by one file building
    /// </summary>
    internal class DependencyMapBuilder
    {
        private readonly ConcurrentHashSet<DependencyItem> _dependencyItems = new ConcurrentHashSet<DependencyItem>();

        public void AddDependencyItem(Document from, Document to, DependencyType type)
        {
            Debug.Assert(from != null);

            if (to == null)
            {
                return;
            }

            var isLocalizedBuild = from.Docset.IsLocalizedBuild() || to.Docset.IsLocalizedBuild();
            if (isLocalizedBuild && !from.Docset.IsLocalized())
            {
                return;
            }

            _dependencyItems.TryAdd(new DependencyItem(from, to, type));
        }

        public DependencyMap Build()
        {
            return new DependencyMap(Flatten());
        }

        private Dictionary<Document, HashSet<DependencyItem>> Flatten()
        {
            var result = new Dictionary<Document, HashSet<DependencyItem>>();
            var graph = _dependencyItems
                .GroupBy(k => k.From)
                .ToDictionary(k => k.Key);

            foreach (var (from, value) in graph)
            {
                var dependencies = new HashSet<DependencyItem>();
                foreach (var item in value)
                {
                    var stack = new Stack<DependencyItem>();
                    stack.Push(item);
                    var visited = new HashSet<Document> { from };
                    while (stack.TryPop(out var current))
                    {
                        dependencies.Add(new DependencyItem(from, current.To, current.Type));

                        // if the dependency destination is already in the result set, we can reuse it
                        if (current.To != from && CanTransit(current) && result.TryGetValue(current.To, out var nextDependencies))
                        {
                            foreach (var dependency in nextDependencies)
                            {
                                dependencies.Add(new DependencyItem(from, dependency.To, dependency.Type));
                            }
                            continue;
                        }

                        if (graph.TryGetValue(current.To, out var toDependencies) && !visited.Contains(current.To) && CanTransit(current))
                        {
                            foreach (var dependencyItem in toDependencies)
                            {
                                visited.Add(current.To);
                                stack.Push(dependencyItem);
                            }
                        }
                    }
                }
                result[from] = dependencies;
            }
            return result;
        }

        private bool CanTransit(DependencyItem dependencyItem)
            => dependencyItem.Type == DependencyType.Inclusion;
    }
}
