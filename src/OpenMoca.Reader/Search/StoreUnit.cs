using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Search;

/// <summary>
/// Options for a store backend's ingest (Reader contract §9.4, Reader interface §5.2; moca-spec ADR-0041). Embedding
/// sends content to a model, so a host that embeds with a model it does not run on the person's device passes its
/// terms request here; a binding a model calls always does.
/// </summary>
public sealed record IngestOptions
{
    /// <summary>
    /// The terms request for the embedder: where it runs (<c>device</c>, <c>host</c> or <c>provider</c>) and the
    /// conditions it meets. The use is always <c>embed</c>, whatever this says. Null: every unit is ingested.
    /// </summary>
    public AiRequest? Terms { get; init; }

    /// <summary>The host's terms limits (Reader contract §15.3 step 4): floors, never values.</summary>
    public TermsLimits? TermsLimits { get; init; }

    /// <summary>The ingest's options for a provider's embedder with no conditions: the default of a binding a model calls.</summary>
    public static IngestOptions Provider { get; } = new() { Terms = new AiRequest(TermsDestinations.Provider) };
}

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
    public static IReadOnlyList<StoreUnit> From(Library library) => From(library, null, out _);

    /// <summary>
    /// The units to ingest under a terms request (Reader contract §9.4, moca-spec ADR-0041): every unit of every loaded
    /// package whose resolved terms pass the gate's <c>ai</c> test for the embedder's destination, with the use
    /// <c>embed</c>. A unit the gate removes is not returned, so it is never embedded or stored; the removals are
    /// counted in <c>U004_TERMS_EXCLUDED</c>, with its <c>excluded</c> and <c>hidden</c> counts.
    /// </summary>
    /// <param name="library">The packages to ingest.</param>
    /// <param name="options">The ingest's terms request and the host's limits; null or no request ingests every unit.</param>
    /// <param name="diagnostics">U004 when the request removed any unit; otherwise empty.</param>
    public static IReadOnlyList<StoreUnit> From(Library library, IngestOptions? options, out IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(library);
        var request = options?.Terms is { } ai ? new TermsRequest { Ai = ai with { Use = "embed" } } : null;
        var resolver = request is not null ? TermsResolver.For(library, options!.TermsLimits?.ToAi(), request) : null;
        var removed = new TermsCount();
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

                // Contract §9.4: a unit the gate removes is never embedded or stored.
                if (resolver is not null)
                {
                    var reasons = TermsRules.GateReasons(request!, resolver.NodeTerms(result.Digest!, u.Node.Path).Ai, null);
                    if (reasons.Count > 0)
                    {
                        removed.Add(reasons, resolver.Closure(result.Digest!).Hidden);
                        continue;
                    }
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

        var findings = new DiagnosticBag();
        removed.Report(findings, "units from the ingest");
        diagnostics = findings.Items;
        return units;
    }
}
