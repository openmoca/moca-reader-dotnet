using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OpenMoca.Reader.Citations;

namespace OpenMoca.Reader.Structure;

/// <summary>
/// What a composing package's own structure binds one resolved member to (package spec §5.6): the concepts every
/// record of the member is bound to, and the concepts each node path is bound to. Plain JSON in a package state
/// (Reader contract §9.4).
/// </summary>
public sealed record CompositionBinding
{
    /// <summary>The member's digest.</summary>
    [JsonPropertyName("digest")] public required string Digest { get; init; }

    /// <summary>Concepts every record of the member is bound to, sorted.</summary>
    [JsonPropertyName("concepts")] public required IReadOnlyList<string> Concepts { get; init; }

    /// <summary>Concepts each node path is bound to, sorted.</summary>
    [JsonPropertyName("nodes")] public required IReadOnlyDictionary<string, IReadOnlyList<string>> Nodes { get; init; }
}

/// <summary>
/// Binding by composition (package spec §5.6, Reader contract §9): a composing package's own structure may use a
/// member's package id, at any depth, or a member's node identifier as a concept. The member's records are then bound
/// to it, and to every concept that has it as a descendant, for the concepts and scope filters and for upTo.
/// </summary>
public static class Composition
{
    private static readonly ConditionalWeakTable<PackageResult, IReadOnlyList<CompositionBinding>> Cache = new();

    /// <summary>What a composing package binds its resolved, valid members to; one entry per member digest.</summary>
    public static IReadOnlyList<CompositionBinding> Of(PackageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Cache.GetValue(result, Compute);
    }

    private static IReadOnlyList<CompositionBinding> Compute(PackageResult result)
    {
        var graph = result.Structure?.Graph;
        var byDigest = new SortedDictionary<string, (HashSet<string> Concepts, SortedDictionary<string, HashSet<string>> Nodes)>(StringComparer.Ordinal);
        bool Declared(string iri) => graph is not null && graph.Concepts.TryGetValue(iri, out var c) && c.Declared is not null;

        void Visit(IEnumerable<PackageMember> members, List<string> chain, HashSet<string> seen)
        {
            foreach (var m in members)
            {
                var child = m.Result;
                if (m.Status != "resolved" || child is null || !child.Valid || child.Digest is null || seen.Contains(child.Digest))
                {
                    continue;
                }

                var ids = new List<string>(chain) { child.Id };
                if (graph is not null)
                {
                    if (!byDigest.TryGetValue(child.Digest, out var entry))
                    {
                        entry = ([], new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal));
                        byDigest[child.Digest] = entry;
                    }

                    foreach (var id in ids.Where(Declared))
                    {
                        entry.Concepts.UnionWith(WithAncestors(graph, id));
                    }

                    foreach (var node in child.Nodes)
                    {
                        var nodeId = $"{child.Id}#{node.Path}";
                        if (!Declared(nodeId))
                        {
                            continue;
                        }

                        if (!entry.Nodes.TryGetValue(node.Path, out var set))
                        {
                            set = [];
                            entry.Nodes[node.Path] = set;
                        }

                        set.UnionWith(WithAncestors(graph, nodeId));
                    }
                }

                Visit(child.Members, ids, [.. seen, child.Digest]);
            }
        }

        Visit(result.Members, [], result.Digest is null ? [] : [result.Digest]);
        return [.. byDigest
            .Where(kv => kv.Value.Concepts.Count > 0 || kv.Value.Nodes.Count > 0)
            .Select(kv => new CompositionBinding
            {
                Digest = kv.Key,
                Concepts = [.. kv.Value.Concepts.OrderBy(c => c, StringComparer.Ordinal)],
                Nodes = kv.Value.Nodes.ToDictionary(n => n.Key, n => (IReadOnlyList<string>)[.. n.Value.OrderBy(c => c, StringComparer.Ordinal)], StringComparer.Ordinal),
            })];
    }

    /// <summary>The concept and every concept that has it as a descendant, by broader or hasPart, in one graph.</summary>
    private static HashSet<string> WithAncestors(StructureGraph graph, string iri)
    {
        var output = new HashSet<string>(StringComparer.Ordinal) { iri };
        var stack = new Stack<string>([iri]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var e in graph.Out("broader", n).Concat(graph.In("hasPart", n)))
            {
                if (output.Add(e.Iri))
                {
                    stack.Push(e.Iri);
                }
            }
        }

        return output;
    }

    /// <summary>An index from member digest to each loaded composing package's binding of it.</summary>
    public static Dictionary<string, List<(string Composer, CompositionBinding Binding)>> Index(IEnumerable<(string Composer, IReadOnlyList<CompositionBinding>? Bindings)> composers)
    {
        ArgumentNullException.ThrowIfNull(composers);
        var index = new Dictionary<string, List<(string, CompositionBinding)>>(StringComparer.Ordinal);
        foreach (var (composer, bindings) in composers)
        {
            foreach (var b in bindings ?? [])
            {
                if (b?.Digest is null || b.Concepts is null)
                {
                    continue;
                }

                if (!index.TryGetValue(b.Digest, out var list))
                {
                    list = [];
                    index[b.Digest] = list;
                }

                list.Add((composer, b));
            }
        }

        return index;
    }

    /// <summary>The concepts one composing package binds a record to.</summary>
    public static IEnumerable<string> ConceptsFor(CompositionBinding binding, CitationRecord record)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(record);
        var own = binding.Nodes is not null && binding.Nodes.TryGetValue(record.Node.Path, out var list) ? list : [];
        return binding.Concepts.Concat(own ?? []);
    }

    /// <summary>A record's concepts for the concepts and scope filters: its node's own, and every binding by composition.</summary>
    public static IReadOnlyList<string> BoundConcepts(CitationRecord record, Dictionary<string, List<(string Composer, CompositionBinding Binding)>> index)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(index);
        var output = new List<string>(record.Concepts ?? []);
        if (index.TryGetValue(record.Package.Digest, out var entries))
        {
            foreach (var (_, binding) in entries)
            {
                output.AddRange(ConceptsFor(binding, record));
            }
        }

        return output;
    }
}
