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
        var docs = new List<(SearchHit Item, Bm25.TermCounts Counts, string Key)>();
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

            string? Keep(Content.ContentNode node, Content.Representation rep)
            {
                var id = $"{result.Id}#{node.Path}";
                if (excluded.Contains($"{result.Digest} {id}"))
                {
                    return null;
                }

                var (label, invalid) = Audience.Of(node, rep);
                if (!Audience.Allowed(label, invalid, filters.Audiences, filters.AllowUnlabelled))
                {
                    return null;
                }

                if (filters.Concepts is not null && !(rep.Concepts ?? []).Any(filters.Concepts.Contains))
                {
                    return null;
                }

                return id;
            }

            if (chunks is not null)
            {
                var byPath = result.Nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
                foreach (var c in chunks)
                {
                    if (!byPath.TryGetValue(c.Path, out var node) || node.Representations.FirstOrDefault(r => r.Locale == c.Locale) is not { } rep)
                    {
                        continue;
                    }

                    if (filters.Locale is not null && Citations.Citations.PickRepresentation(node, filters.Locale) != rep)
                    {
                        continue;
                    }

                    if (Keep(node, rep) is not { } id)
                    {
                        continue;
                    }

                    var key = $"{id}\u0000{c.Start:D12}";
                    // The cache key adds the locale: chunks of two representations can start at the same offset.
                    var counts = counted.GetOrAdd($"{key}\u0000{c.Locale}", _ =>
                        Bm25.Count($"{JsonValues.StringOf(rep.Frontmatter["title"]) ?? ""} {Sidecars.SidecarBinder.ChunkText(c, rep)}"));
                    docs.Add((new SearchHit(result.Digest!, id, node.Path, rep.Locale, new CitationSpan(c.Start, c.End), 0), counts, key));
                }
            }
            else
            {
                foreach (var node in result.Nodes)
                {
                    var reps = filters.Locale is not null ? [Citations.Citations.PickRepresentation(node, filters.Locale)] : node.Representations;
                    foreach (var rep in reps)
                    {
                        if (Keep(node, rep) is not { } id)
                        {
                            continue;
                        }

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
                        docs.Add((new SearchHit(result.Digest!, id, node.Path, rep.Locale, null, 0), counts, key));
                    }
                }
            }
        }

        // Node ids repeat across versions of a package, so key on the digest too.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<SearchHit> hits = Bm25.RankCounted(docs, query)
            .Where(r => r.Item.Span is not null || seen.Add($"{r.Item.Digest} {r.Item.Node}"))
            .Take(limit)
            .Select(r => r.Item with { Score = r.Score })
            .ToList();
        return ValueTask.FromResult(hits);
    }
}
