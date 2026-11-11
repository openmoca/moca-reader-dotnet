using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader;

/// <summary>
/// A package's state (Reader contract §9.4): what a store search needs, per package, to recompute every field of a
/// stored citation record that depends on the library or the trust root, without loading the package. Plain JSON a
/// host can keep beside its store.
/// </summary>
public sealed partial record PackageState
{
    /// <summary>The package digest.</summary>
    [JsonPropertyName("digest")] public required string Digest { get; init; }

    /// <summary>The package id.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>The package version.</summary>
    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary>The manifest's default language, if any.</summary>
    [JsonPropertyName("language")] public string? Language { get; init; }

    /// <summary>The manifest's relations.</summary>
    [JsonPropertyName("relations")] public JsonArray Relations { get; init; } = [];

    /// <summary>Package ids the package's signers may speak for (package spec §7.2).</summary>
    [JsonPropertyName("relationScopes")] public IReadOnlyList<string> RelationScopes { get; init; } = [];

    /// <summary>Signers of the package's valid package attestations.</summary>
    [JsonPropertyName("signers")] public IReadOnlyList<string> Signers { get; init; } = [];

    /// <summary>Current reviews per node file, the latest from each signer.</summary>
    [JsonPropertyName("reviews")] public IReadOnlyDictionary<string, IReadOnlyList<AttestedReview>> Reviews { get; init; } = new Dictionary<string, IReadOnlyList<AttestedReview>>();

    /// <summary>Each node file's <c>moca.contested_by</c>.</summary>
    [JsonPropertyName("contests")] public IReadOnlyDictionary<string, IReadOnlyList<string>> Contests { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The state of a valid package result.</summary>
    public static PackageState From(PackageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var contests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var rep in result.Nodes.SelectMany(n => n.Representations))
        {
            if (rep.Moca["contested_by"] is JsonArray list && list.Count > 0)
            {
                contests[rep.File] = [.. list.Select(JsonValues.StringOf).OfType<string>()];
            }
        }

        return new PackageState
        {
            Digest = result.Digest ?? "",
            Id = result.Id,
            Version = result.Version,
            Language = JsonValues.StringOf(result.Manifest?["language"]),
            Relations = result.Manifest?["relations"] is JsonArray relations ? (JsonArray)relations.DeepClone() : [],
            RelationScopes = [.. result.RelationScopes],
            Signers = [.. result.Signers],
            Reviews = result.ReviewsByFile.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AttestedReview>)[.. kv.Value], StringComparer.Ordinal),
            Contests = contests,
        };
    }

    /// <summary>Package-level relation effects (package spec §7.2) over a set of packages.</summary>
    public static RelationIndex RelationIndexOf(IReadOnlyList<PackageState> packages, bool allowUnsignedRelations)
    {
        var superseded = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var contested = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void Push(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        foreach (var m in packages)
        {
            foreach (var relation in m.Relations.OfType<JsonObject>())
            {
                var targetId = JsonValues.StringOf(relation["target"]);

                // Package spec §7.2: only relations a signer of the package may make take effect.
                if (!allowUnsignedRelations && !TrustRoot.ScopeCovers(m.RelationScopes, targetId))
                {
                    continue;
                }

                var targetVersion = JsonValues.StringOf(relation["version"]);
                var type = JsonValues.StringOf(relation["type"]);
                foreach (var t in packages.Where(t => t.Id == targetId && (string.IsNullOrEmpty(targetVersion) || t.Version == targetVersion)))
                {
                    var key = $"{t.Id}@{t.Version}";
                    var self = $"{m.Id}@{m.Version}";
                    if (type == "supersedes")
                    {
                        Push(superseded, key, self);
                    }

                    if (type == "conflictsWith")
                    {
                        Push(contested, key, self);
                        Push(contested, self, key);
                    }
                }
            }
        }

        return new RelationIndex(superseded, contested);
    }

    [GeneratedRegex(@"\.[Mm][Dd]\z")]
    private static partial Regex MdSuffix();

    /// <summary>
    /// A stored record with every library- and trust-dependent field recomputed from the package states and the
    /// clock (Reader contract §9.4); null when its package is not among the states.
    /// </summary>
    public static CitationRecord? Refresh(CitationRecord record, DateTimeOffset now, IReadOnlyDictionary<string, PackageState> states, RelationIndex index)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!states.TryGetValue(record.Package.Digest, out var state))
        {
            return null;
        }

        var trust = record.Trust;
        var staleAt = Content.Dates.Parse(trust.StaleAfter);
        var from = Content.Dates.Parse(trust.ValidFrom);
        var until = Content.Dates.Parse(trust.ValidUntil);
        var key = $"{state.Id}@{state.Version}";
        var locale = record.Node.Locale;
        var file = locale is null || locale == state.Language
            ? $"content/{record.Node.Path}"
            : $"content/{MdSuffix().Replace(record.Node.Path, "")}.{locale}.md";
        var supersededBy = index.Superseded.GetValueOrDefault(key) ?? [];
        var contestedBy = (state.Contests.GetValueOrDefault(file) ?? []).Concat(index.Contested.GetValueOrDefault(key) ?? []).ToList();
        var signed = state.Signers.Count > 0;
        var reviews = state.Reviews.GetValueOrDefault(file) ?? [];
        return record with
        {
            Package = record.Package with { Signed = signed, Signers = signed ? [.. state.Signers] : null },
            Trust = trust with
            {
                Stale = trust.StaleAfter is not null && staleAt is not null && now >= staleAt,
                InForce = (trust.ValidFrom is null || (from is not null && now >= from)) && (trust.ValidUntil is null || (until is not null && now < until)),
                Superseded = supersededBy.Count > 0,
                SupersededBy = supersededBy.Count > 0 ? [.. supersededBy] : null,
                Contested = contestedBy.Count > 0,
                ContestedBy = contestedBy.Count > 0 ? contestedBy : null,
                AttestedReviews = [.. reviews.Select(r => CitationReview.From(r, signed))],
                ReviewStatus = OpenMoca.Reader.Attestations.Reviews.StatusOf([.. reviews], signed),
            },
        };
    }
}
