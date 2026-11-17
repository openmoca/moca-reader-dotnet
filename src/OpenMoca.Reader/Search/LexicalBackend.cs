using System.Collections.Concurrent;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Search;

/// <summary>
/// The lexical (BM25) backend every Reader provides (Reader contract §9.1), over each package's sidecar
/// chunks when it was added with them, otherwise its nodes. With a locale, the representation package spec
/// §5 selects for it; without one, every representation, so content in any language can be found, and the
/// best-scoring one for each node is returned.
/// Each document's term counts are kept per package digest after its first search, so later searches only score;
/// they are dropped when the package leaves the library. Scores are the same as counting afresh.
/// Contract §9.2: filters remove records, they never change a score. BM25 counts every representation of every loaded
/// package, then the filters apply, so a record scores the same whether or not a filter was pushed down. The search's
/// own candidate check (<see cref="SearchFilters.Accept"/>) is applied the same way: after scoring.
/// </summary>
public sealed class LexicalBackend(Library library) : ISearchBackend
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Bm25.TermCounts>> _counts = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public BackendFeatures Features => BackendFeatures.Lexical | BackendFeatures.FilterPushdown;

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> Diagnostics => [];

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
    {
        var excluded = filters.Exclude.Select(x => $"{x.Digest} {x.Node}").ToHashSet(StringComparer.Ordinal);
        var digests = filters.Digests.ToHashSet(StringComparer.Ordinal);
        var docs = new List<((SearchHit Hit, Content.ContentNode Node, Content.Representation Rep) Item, Bm25.TermCounts Counts, string Key)>();
        foreach (var gone in _counts.Keys.Except(library.Packages.Select(p => p.Result.Digest!)))
        {
            _counts.TryRemove(gone, out _);
        }

        foreach (var (result, chunks, _) in library.Packages)
        {
            if (!digests.Contains(result.Digest!))
            {
                continue;
            }

            var counted = _counts.GetOrAdd(result.Digest!, _ => new ConcurrentDictionary<string, Bm25.TermCounts>(StringComparer.Ordinal));

            if (chunks is not null)
            {
                var byPath = result.Nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
                foreach (var c in chunks)
                {
                    if (!byPath.TryGetValue(c.Path, out var node) || node.Representations.FirstOrDefault(r => r.Locale == c.Locale) is not { } rep)
                    {
                        continue;
                    }

                    var id = $"{result.Id}#{node.Path}";
                    var key = $"{id}\u0000{c.Start:D12}";
                    // The cache key adds the locale: chunks of two representations can start at the same offset.
                    var counts = counted.GetOrAdd($"{key}\u0000{c.Locale}", _ =>
                        Bm25.Count($"{JsonValues.StringOf(rep.Frontmatter["title"]) ?? ""} {Sidecars.SidecarBinder.ChunkText(c, rep)}"));
                    docs.Add(((new SearchHit(result.Digest!, id, node.Path, rep.Locale, new CitationSpan(c.Start, c.End), 0), node, rep), counts, key));
                }
            }
            else
            {
                foreach (var node in result.Nodes)
                {
                    foreach (var rep in node.Representations)
                    {
                        var id = $"{result.Id}#{node.Path}";
                        var key = $"{id}\u0000{rep.Locale ?? ""}";
                        var counts = counted.GetOrAdd(key, _ =>
                        {
                            var fm = rep.Frontmatter;
                            var parts = new List<string?> { JsonValues.StringOf(fm["title"]), JsonValues.StringOf(fm["description"]) };
                            if (fm["tags"] is System.Text.Json.Nodes.JsonArray tags)
                            {
                                parts.AddRange(tags.Select(JsonValues.StringOf));
                            }

                            parts.Add(rep.Body);
                            return Bm25.Count(string.Join(' ', parts.OfType<string>()));
                        });
                        docs.Add(((new SearchHit(result.Digest!, id, node.Path, rep.Locale, null, 0), node, rep), counts, key));
                    }
                }
            }
        }

        bool Passes(SearchHit hit, Content.ContentNode node, Content.Representation rep)
        {
            if (excluded.Contains($"{hit.Digest} {hit.Node}"))
            {
                return false;
            }

            if (filters.Locale is not null && Citations.Citations.PickRepresentation(node, filters.Locale) != rep)
            {
                return false;
            }

            var (label, invalid) = Audience.Of(node, rep);
            if (!Audience.Allowed(label, invalid, filters.Audiences, filters.AllowUnlabelled))
            {
                return false;
            }

            if (filters.Concepts is not null && !(rep.Concepts ?? []).Any(filters.Concepts.Contains))
            {
                return false;
            }

            return filters.Accept is null || filters.Accept(hit);
        }

        // Node ids repeat across versions of a package, so key on the digest too.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<SearchHit> hits = Bm25.RankCounted(docs, query)
            .Where(r => Passes(r.Item.Hit, r.Item.Node, r.Item.Rep))
            .Where(r => r.Item.Hit.Span is not null || seen.Add($"{r.Item.Hit.Digest} {r.Item.Hit.Node}"))
            .Take(limit)
            .Select(r => r.Item.Hit with { Score = r.Score })
            .ToList();
        return ValueTask.FromResult(hits);
    }
}
