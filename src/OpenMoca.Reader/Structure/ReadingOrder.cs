using System.Text.Json.Serialization;
using OpenMoca.Reader.Citations;

namespace OpenMoca.Reader.Structure;

/// <summary>
/// A package's reading order (package spec §4.10): the members of the ordered collection its manifest names, and the
/// position of each concept on it. Plain JSON in a package state (Reader contract §9.4).
/// </summary>
public sealed record ReadingOrder
{
    /// <summary>The collection IRI.</summary>
    [JsonPropertyName("iri")] public required string Iri { get; init; }

    /// <summary>The members, in list order.</summary>
    [JsonPropertyName("members")] public required IReadOnlyList<string> Members { get; init; }

    /// <summary>
    /// Each concept on the reading order, with the index of the first member that is the concept or has it as a
    /// descendant by narrower or hasPart.
    /// </summary>
    [JsonPropertyName("positions")] public required IReadOnlyDictionary<string, int> Positions { get; init; }

    /// <summary>
    /// The reading order <paramref name="iri"/> names in a package's own graph, or null when it is not a typed
    /// ordered collection with at least one member (M014).
    /// </summary>
    public static ReadingOrder? Resolve(StructureGraph? graph, string iri)
    {
        if (graph is null || !graph.Collections.TryGetValue(iri, out var collection) || !collection.Typed || collection.Members.Count == 0)
        {
            return null;
        }

        // Contract §3: one walk in list order, with one visited set. A concept first reached from member i keeps i, as it
        // would if each member were walked on its own, and nothing is walked twice.
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < collection.Members.Count; i++)
        {
            var stack = new Stack<string>([collection.Members[i]]);
            while (stack.TryPop(out var n))
            {
                if (!positions.TryAdd(n, i))
                {
                    continue;
                }

                // Package spec §7.4: an edition takes its slot's place, as a part does.
                foreach (var e in graph.In("broader", n).Concat(graph.Out("hasPart", n)).Concat(graph.Out("workExample", n)))
                {
                    if (!positions.ContainsKey(e.Iri))
                    {
                        stack.Push(e.Iri);
                    }
                }
            }
        }

        return new ReadingOrder { Iri = iri, Members = [.. collection.Members], Positions = new SortedDictionary<string, int>(positions, StringComparer.Ordinal) };
    }

    /// <summary>
    /// Reader contract §9: whether a record passes <paramref name="upTo"/>. It is placed as any concept is, so a
    /// descendant limits as the first member that contains it. A package whose reading order does not place it is
    /// not limited, nor is a record bound to no concept on it.
    /// </summary>
    public static bool Passes(CitationRecord record, ReadingOrder? order, string upTo)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (order is null || !order.Positions.TryGetValue(upTo, out var limit))
        {
            return true;
        }

        return (record.Concepts ?? []).All(c => !order.Positions.TryGetValue(c, out var p) || p <= limit);
    }
}
