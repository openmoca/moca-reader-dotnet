using OpenMoca.Reader.Citations;

namespace OpenMoca.Reader.Search;

/// <summary>
/// What a store backend records at ingest for one node representation, or one sidecar chunk (Reader contract §9.4):
/// a hit that carries the whole citation record, including the package digest, and the text to embed.
/// </summary>
/// <param name="Hit">The hit to return for this unit, with its citation record.</param>
/// <param name="Text">The text a store embeds or indexes: the node's title, then the unit's text.</param>
public sealed record StoreUnit(SearchHit Hit, string Text)
{
    /// <summary>The unit's citation record.</summary>
    public CitationRecord Record => Hit.Record!;

    /// <summary>Every unit of every loaded package: each sidecar chunk when a sidecar is bound, else each node representation.</summary>
    public static IReadOnlyList<StoreUnit> From(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var index = library.Relations();
        var now = library.Clock();
        var units = new List<StoreUnit>();
        foreach (var (result, chunks, _) in library.Packages)
        {
            var byPath = result.Nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
            var parts = chunks is not null
                ? chunks.Select(c => (Node: byPath.GetValueOrDefault(c.Path), c.Locale, Span: (CitationSpan?)new CitationSpan(c.Start, c.End), Chunk: (Sidecars.SidecarChunk?)c))
                : result.Nodes.SelectMany(n => n.Representations.Select(r => (Node: (Content.ContentNode?)n, r.Locale, Span: (CitationSpan?)null, Chunk: (Sidecars.SidecarChunk?)null)));
            foreach (var u in parts)
            {
                if (u.Node?.Representations.FirstOrDefault(r => r.Locale == u.Locale) is not { } rep)
                {
                    continue;
                }

                var text = u.Chunk is not null ? Sidecars.SidecarBinder.ChunkText(u.Chunk, rep) : rep.Body;
                var record = Citations.Citations.For(result, u.Node, rep, now, index, text);
                if (u.Span is not null)
                {
                    record = record with { Span = u.Span };
                }

                units.Add(new StoreUnit(
                    new SearchHit(result.Digest!, record.Node.Id, u.Node.Path, rep.Locale, u.Span, 0, record),
                    $"{record.Node.Title ?? ""} {text}"));
            }
        }

        return units;
    }
}
