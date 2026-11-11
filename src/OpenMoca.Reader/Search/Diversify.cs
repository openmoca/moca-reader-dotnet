using OpenMoca.Reader.Citations;

namespace OpenMoca.Reader.Search;

/// <summary>
/// An opt-in strategy that spreads results across nodes (moca-spec ADR-0010). Pass its hooks to
/// <see cref="MocaSearch"/>. Without it, a search can return its whole top list from one node's chunks.
/// </summary>
/// <remarks>
/// Egress: keep each node's best <c>perNode</c> records in their ranked order, then the rest after them. Nothing is
/// dropped, so the limit still fills. A node is its versioned reference, so its locales count as one node. The gate
/// still runs last. <see cref="MocaSearch"/> fetches more than the limit whenever an egress hook is set, so there
/// are other nodes to bring forward.
/// </remarks>
public static class Diversify
{
    /// <summary>The hooks.</summary>
    /// <param name="perNode">How many records each node may have before any node's next one.</param>
    public static SearchHooks Hooks(int perNode = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perNode, 1);
        return new SearchHooks
        {
            Egress = (candidates, _, _) => ValueTask.FromResult(Reorder(candidates, perNode)),
        };
    }

    private static IReadOnlyList<CitationRecord> Reorder(IReadOnlyList<CitationRecord> candidates, int perNode)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new List<CitationRecord>();
        var rest = new List<CitationRecord>();
        foreach (var record in candidates)
        {
            var n = counts.GetValueOrDefault(record.Node.Ref);
            counts[record.Node.Ref] = n + 1;
            (n < perNode ? first : rest).Add(record);
        }

        return [.. first, .. rest];
    }
}
