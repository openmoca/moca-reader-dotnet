using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Search;

/// <summary>
/// A reference store backend kept in memory (Reader contract §9.4). It stands in for a host's vector
/// store in tests and shows what a store binding must record at ingest: the whole citation record,
/// including the package digest, so search resolves hits without loading every package.
/// </summary>
/// <param name="embedder">Without one, the store is searched lexically.</param>
/// <param name="filterPushdown">Whether the store applies the search filters itself.</param>
/// <param name="distance">How its vectors are compared: <c>cosine</c> (the default), <c>dot</c> or <c>euclidean</c>.</param>
public sealed class MemoryStoreBackend(IEmbedder? embedder = null, bool filterPushdown = true, string distance = "cosine") : ISearchBackend
{
    private readonly List<(SearchHit Hit, string Text, CitationRecord Record, IReadOnlyList<string> Concepts, float[]? Vector)> _records = [];

    /// <inheritdoc/>
    public BackendFeatures Features =>
        (embedder is null ? BackendFeatures.Lexical : BackendFeatures.Dense) | (filterPushdown ? BackendFeatures.FilterPushdown : BackendFeatures.None);

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> Diagnostics => [];

    /// <summary>Ingests every node representation (or sidecar chunk) of every package, through citation records.</summary>
    public async Task<MemoryStoreBackend> IngestAsync(Library library, CancellationToken cancellationToken = default)
    {
        var pending = StoreUnit.From(library)
            .Select(u => (u.Hit, u.Text, u.Record, Concepts: u.Record.Concepts ?? []))
            .ToList();

        IReadOnlyList<float[]>? vectors = null;
        if (embedder is not null && pending.Count > 0)
        {
            vectors = await embedder.EmbedAsync(pending.Select(p => p.Text).ToList(), EmbeddingRole.Document, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            _records.Add((p.Hit, p.Text, p.Record, p.Concepts, vectors?[i]));
        }

        return this;
    }

    /// <summary>Removes everything ingested from a package digest.</summary>
    public void Remove(string digest) => _records.RemoveAll(r => r.Hit.Digest == digest);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
    {
        IEnumerable<(SearchHit Hit, string Text, CitationRecord Record, IReadOnlyList<string> Concepts, float[]? Vector)> candidates = _records;
        if (filterPushdown)
        {
            var digests = filters.Digests.ToHashSet(StringComparer.Ordinal);
            var excluded = filters.Exclude.Select(x => $"{x.Digest} {x.Node}").ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(r => digests.Contains(r.Hit.Digest)
                                               && !excluded.Contains($"{r.Hit.Digest} {r.Hit.Node}")
                                               && Audience.Allowed(r.Record, filters.Audiences, filters.AllowUnlabelled)
                                               && (filters.Concepts is null || r.Concepts.Any(filters.Concepts.Contains)));
        }

        var list = candidates.ToList();
        if (embedder is null)
        {
            var docs = list.Select(r => (r.Hit, r.Text, $"{r.Hit.Node}\u0000{r.Hit.Locale ?? ""}\u0000{r.Hit.Span?.Start ?? 0}")).ToList();
            return Bm25.Rank(docs, query).Take(limit).Select(x => x.Item with { Score = x.Score }).ToList();
        }

        var q = (await embedder.EmbedAsync([query], EmbeddingRole.Query, cancellationToken).ConfigureAwait(false))[0];
        return list
            .Select(r => r.Hit with { Score = Math.Round(DenseBackend.Similarity(distance, q, r.Vector ?? []) * 1e4, MidpointRounding.AwayFromZero) / 1e4 })
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
    }
}
