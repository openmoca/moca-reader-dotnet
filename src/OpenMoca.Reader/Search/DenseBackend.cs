using OpenMoca.Reader.Json;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Search;

/// <summary>
/// Dense search over sidecar vectors (Reader contract §9.3), scored by the index's <c>model.distance</c> (cosine by
/// default). The host supplies the embedder. Every loaded
/// package it does not search is reported: a sidecar whose model does not match the embedder (S006), or no
/// vectors at all (S007).
/// </summary>
public sealed class DenseBackend : ISearchBackend
{
    private readonly Library _library;
    private readonly IEmbedder _embedder;
    private readonly DiagnosticBag _checks = new();
    private readonly Dictionary<string, bool> _checked = new(StringComparer.Ordinal);

    /// <summary>Creates the backend and checks every loaded sidecar's model against the embedder.</summary>
    public DenseBackend(Library library, IEmbedder embedder)
    {
        _library = library;
        _embedder = embedder;
        Check();
    }

    /// <inheritdoc/>
    public BackendFeatures Features => BackendFeatures.Dense | BackendFeatures.FilterPushdown;

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> Diagnostics => _checks.Items;

    /// <summary>Why an embedder cannot search vectors made by a model, or null when it can.</summary>
    public static string? ModelMismatch(IEmbedder embedder, System.Text.Json.Nodes.JsonObject? model, int vectorLength)
    {
        var name = JsonValues.StringOf(model?["name"]);
        if (string.IsNullOrEmpty(name))
        {
            return "the index names no model";
        }

        if (name != embedder.Name)
        {
            return $"index model \"{name}\" is not the host embedder \"{embedder.Name}\"";
        }

        var version = JsonValues.StringOf(model!["version"]);
        if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(embedder.Version) && version != embedder.Version)
        {
            return $"index model version {version} is not {embedder.Version}";
        }

        var dimensions = JsonValues.IntegerOf(model["dimensions"]) ?? vectorLength;
        if (dimensions != embedder.Dimensions)
        {
            return $"index has {dimensions} dimensions; the host embedder has {embedder.Dimensions}";
        }

        // Prefixes are compared, never applied (Reader contract §9.3).
        foreach (var (key, ours) in new[] { ("queryPrefix", embedder.QueryPrefix), ("documentPrefix", embedder.DocumentPrefix) })
        {
            // A prefix declared on one side only is a mismatch too.
            var theirs = JsonValues.StringOf(model[key]);
            if (theirs != ours)
            {
                return $"index {key} \"{theirs}\" is not the host embedder's \"{ours}\"";
            }
        }

        return null;
    }

    /// <summary>
    /// How well vector <paramref name="b"/> matches query <paramref name="a"/> under an index's <c>model.distance</c>
    /// (Reader contract §9.3): cosine similarity (the default), the dot product, or the negative Euclidean distance.
    /// A higher score is always a better match; 0 for vectors of different lengths.
    /// </summary>
    public static double Similarity(string? distance, float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        switch (distance)
        {
            case "dot":
                double dot = 0;
                for (var i = 0; i < a.Length; i++)
                {
                    dot += a[i] * (double)b[i];
                }

                return dot;
            case "euclidean":
                double sum = 0;
                for (var i = 0; i < a.Length; i++)
                {
                    var d = a[i] - (double)b[i];
                    sum += d * d;
                }

                return 0 - Math.Sqrt(sum);
            default:
                return Cosine(a, b);
        }
    }

    /// <summary>Cosine similarity; 0 for mismatched or zero vectors.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
    {
        Check();
        var usable = _library.Packages
            .Where(p => _checked.GetValueOrDefault(p.Result.Digest!) && filters.Digests.Contains(p.Result.Digest!))
            .ToList();
        if (usable.Count == 0)
        {
            return [];
        }

        var q = (await _embedder.EmbedAsync([query], EmbeddingRole.Query, cancellationToken).ConfigureAwait(false))[0];
        var excluded = filters.Exclude.Select(x => $"{x.Digest} {x.Node}").ToHashSet(StringComparer.Ordinal);
        var scored = new List<SearchHit>();
        foreach (var (result, chunks, index) in usable)
        {
            var distance = JsonValues.StringOf((index?["model"] as System.Text.Json.Nodes.JsonObject)?["distance"]);
            var byPath = result.Nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
            foreach (var c in chunks ?? [])
            {
                if (!byPath.TryGetValue(c.Path, out var node) || node.Representations.FirstOrDefault(r => r.Locale == c.Locale) is not { } rep || c.Vector is null)
                {
                    continue;
                }

                var id = $"{result.Id}#{node.Path}";
                if (excluded.Contains($"{result.Digest} {id}"))
                {
                    continue;
                }

                if (filters.Locale is not null && Citations.Citations.PickRepresentation(node, filters.Locale) != rep)
                {
                    continue;
                }

                var (label, invalid) = Audience.Of(node, rep);
                if (!Audience.Allowed(label, invalid, filters.Audiences, filters.AllowUnlabelled))
                {
                    continue;
                }

                if (filters.Concepts is not null && !(rep.Concepts ?? []).Any(filters.Concepts.Contains))
                {
                    continue;
                }

                var score = Math.Round(Similarity(distance, q, c.Vector) * 1e4, MidpointRounding.AwayFromZero) / 1e4;
                scored.Add(new SearchHit(result.Digest!, id, node.Path, rep.Locale, new CitationSpan(c.Start, c.End), score));
            }
        }

        return scored.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    /// <summary>Compares the embedder with each loaded sidecar's model, once per package digest.</summary>
    private void Check()
    {
        foreach (var (result, chunks, index) in _library.Packages)
        {
            if (_checked.ContainsKey(result.Digest!))
            {
                continue;
            }

            var vectors = (chunks ?? []).Where(c => c.Vector is not null).ToList();
            if (index is null || vectors.Count == 0)
            {
                _checks.Add(DiagnosticCodes.S007NoVectors, $"{result.Id}@{result.Version}: no sidecar vectors; the package is not searched");
                _checked[result.Digest!] = false;
                continue;
            }

            var problem = ModelMismatch(_embedder, index["model"] as System.Text.Json.Nodes.JsonObject, vectors[0].Vector!.Length);
            if (problem is not null)
            {
                _checks.Add(DiagnosticCodes.S006ModelMismatch, $"{result.Id}@{result.Version}: {problem}");
            }

            _checked[result.Digest!] = problem is null;
        }
    }
}
