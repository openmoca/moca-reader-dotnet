using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Search;

/// <summary>
/// A lexical and a dense backend merged by Reciprocal Rank Fusion (Reader contract §9.6):
/// <c>score(hit) = Σ weight_b / (k + rank_b(hit))</c>, with <c>rank_b</c> the hit's 1-based position in backend b's
/// whole ranking of the loaded packages, before any filter. <c>k</c> (default 60) and the weights (default 1) are the
/// host's; a package never sets them. A package the dense backend cannot search (S006, S007) still comes back through
/// the lexical side.
/// Contract §9.2: filters never change a score. A rank within a filtered or shortened list would, so filters pushed
/// down only decide which fused hits pass. Within one search call (the same filters) it keeps the fused ranking, so a
/// Reader that asks again for more hits does not search again.
/// </summary>
public sealed class HybridBackend : ISearchBackend
{
    private readonly ISearchBackend _lexical;
    private readonly ISearchBackend _dense;
    private readonly double _k;
    private readonly double _lexicalWeight;
    private readonly double _denseWeight;
    private (SearchFilters Filters, string Query, List<SearchHit> Fused)? _latest;

    /// <summary>Creates the backend.</summary>
    /// <param name="lexical">The lexical backend.</param>
    /// <param name="dense">The dense backend.</param>
    /// <param name="k">The fusion constant; at least 0.</param>
    /// <param name="lexicalWeight">The lexical side's weight; at least 0.</param>
    /// <param name="denseWeight">The dense side's weight; at least 0. At least one weight must be above 0.</param>
    public HybridBackend(ISearchBackend lexical, ISearchBackend dense, double k = 60, double lexicalWeight = 1, double denseWeight = 1)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(dense);
        foreach (var (name, value) in new[] { (nameof(k), k), (nameof(lexicalWeight), lexicalWeight), (nameof(denseWeight), denseWeight) })
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(name, value, "must be a number of at least 0");
            }
        }

        if (lexicalWeight == 0 && denseWeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denseWeight), "at least one weight must be above 0");
        }

        (_lexical, _dense, _k, _lexicalWeight, _denseWeight) = (lexical, dense, k, lexicalWeight, denseWeight);
    }

    /// <inheritdoc/>
    public BackendFeatures Features =>
        BackendFeatures.Lexical | BackendFeatures.Dense
        | (_lexical.Features.HasFlag(BackendFeatures.FilterPushdown) && _dense.Features.HasFlag(BackendFeatures.FilterPushdown) ? BackendFeatures.FilterPushdown : BackendFeatures.None);

    /// <summary>Both backends' findings, such as S006 and S007 from the dense side.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => [.. _lexical.Diagnostics, .. _dense.Diagnostics];

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
    {
        // One search call passes the same filters each time it asks for more.
        if (_latest is not { } latest || !ReferenceEquals(latest.Filters, filters) || latest.Query != query)
        {
            _latest = latest = (filters, query, await FuseAsync(query, filters, cancellationToken).ConfigureAwait(false));
        }

        return latest.Fused.Take(limit).ToList();
    }

    private static string KeyOf(SearchHit hit) => $"{hit.Digest}\u0000{hit.Node}\u0000{hit.Locale}\u0000{hit.Span?.Start}\u0000{hit.Span?.End}";

    private async Task<List<SearchHit>> FuseAsync(string query, SearchFilters filters, CancellationToken cancellationToken)
    {
        var all = new SearchFilters(filters.Digests, []);
        var lexical = _lexical.SearchAsync(query, int.MaxValue, all, cancellationToken).AsTask();
        var dense = _dense.SearchAsync(query, int.MaxValue, all, cancellationToken).AsTask();
        await Task.WhenAll(lexical, dense).ConfigureAwait(false);

        // With pushdown, the same search with the filters says which hits pass them.
        HashSet<string>? passing = null;
        if (Features.HasFlag(BackendFeatures.FilterPushdown)
            && (filters.Exclude.Count > 0 || filters.Audiences is not null || filters.Locale is not null || filters.Concepts is not null || filters.Accept is not null))
        {
            var l = _lexical.SearchAsync(query, int.MaxValue, filters, cancellationToken).AsTask();
            var d = _dense.SearchAsync(query, int.MaxValue, filters, cancellationToken).AsTask();
            await Task.WhenAll(l, d).ConfigureAwait(false);
            passing = [.. l.Result.Concat(d.Result).Select(KeyOf)];
        }

        var fused = new Dictionary<string, (SearchHit Hit, double Score)>(StringComparer.Ordinal);
        void Add(IReadOnlyList<SearchHit> hits, double weight)
        {
            if (weight == 0)
            {
                return;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                var key = KeyOf(hit);
                (SearchHit Hit, double Score) entry = fused.TryGetValue(key, out var e) ? e : (hit, 0d);
                fused[key] = (entry.Hit, entry.Score + (weight / (_k + i + 1)));
            }
        }

        Add(lexical.Result, _lexicalWeight);
        Add(dense.Result, _denseWeight);

        // Ties: node id, then locale, then span start; the same order in every Reader.
        return fused.Values
            .Where(e => e.Score > 0 && (passing is null || passing.Contains(KeyOf(e.Hit))))
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Hit.Node, StringComparer.Ordinal)
            .ThenBy(e => e.Hit.Locale ?? "", StringComparer.Ordinal)
            .ThenBy(e => e.Hit.Span?.Start ?? -1)
            .Select(e => e.Hit with { Score = Math.Round(e.Score * 1e6, MidpointRounding.AwayFromZero) / 1e6 })
            .ToList();
    }
}
