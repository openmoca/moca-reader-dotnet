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

    /// <summary>
    /// Package spec §5.3: node path to language, for each node whose default file sets a <c>moca.language</c> other
    /// than the manifest's, so a stored record's file is known (contract §9.4). Null when there is none.
    /// </summary>
    [JsonPropertyName("nodeLanguages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? NodeLanguages { get; init; }

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

    /// <summary>The package's issued copy, when it has one (attestations §7.1), so a store search recomputes it.</summary>
    [JsonPropertyName("issuedCopy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IssuedCopy? IssuedCopy { get; init; }

    /// <summary>The package's reading order, when it resolves (package spec §4.10), so a store search applies upTo.</summary>
    [JsonPropertyName("readingOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenMoca.Reader.Structure.ReadingOrder? ReadingOrder { get; init; }

    /// <summary>The bindings by composition (package spec §5.6), when the package has valid members, so a store search filters and limits members as a library does.</summary>
    [JsonPropertyName("composition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenMoca.Reader.Structure.CompositionState? Composition { get; init; }

    /// <summary>
    /// What resolving the package's terms needs (Reader contract §15.3): its declared terms and their authority, so a store
    /// search applies a terms request too. Null when the package has no terms result.
    /// </summary>
    [JsonPropertyName("terms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PackageTermsState? Terms { get; init; }

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

        var language = JsonValues.StringOf(result.Manifest?["language"]);
        var nodeLanguages = result.Nodes.Where(n => n.Language is not null && n.Language != language)
            .ToDictionary(n => n.Path, n => n.Language!, StringComparer.Ordinal);
        return new PackageState
        {
            Digest = result.Digest ?? "",
            Id = result.Id,
            Version = result.Version,
            Language = language,
            NodeLanguages = nodeLanguages.Count > 0 ? nodeLanguages : null,
            Relations = result.Manifest?["relations"] is JsonArray relations ? (JsonArray)relations.DeepClone() : [],
            RelationScopes = [.. result.RelationScopes],
            Signers = [.. result.Signers],
            Reviews = result.ReviewsByFile.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AttestedReview>)[.. kv.Value], StringComparer.Ordinal),
            Contests = contests,
            ReadingOrder = result.ResolvedReadingOrder,
            IssuedCopy = result.IssuedCopy,
            Composition = OpenMoca.Reader.Structure.Composition.Of(result),
            Terms = result.Terms is { } terms ? new PackageTermsState(terms.Declared?.DeepClone() ?? new JsonObject(), terms.Authority) : null,
        };
    }

    /// <summary>
    /// This state with every trust-dependent field taken from a trust state (Reader contract §9.4): signers, relation
    /// scopes, current reviews, the issued copy and the terms' authority. A host re-establishes trust with
    /// <see cref="PackageTrust"/> after its trust root changes, and keeps this in place of the stored state.
    /// </summary>
    /// <exception cref="ArgumentException">The trust state is for another package digest.</exception>
    public PackageState WithTrust(TrustState trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (trust.Digest != Digest)
        {
            throw new ArgumentException($"the trust state is for {trust.Digest}, not {Digest}", nameof(trust));
        }

        return this with
        {
            Signers = [.. trust.Signers],
            RelationScopes = [.. trust.RelationScopes],
            Reviews = trust.Reviews.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AttestedReview>)[.. kv.Value], StringComparer.Ordinal),
            IssuedCopy = trust.IssuedCopy,
            Terms = Terms is { } terms ? terms with { Authority = trust.TermsAuthority } : null,
        };
    }

    /// <summary>
    /// Package-level relation effects (package spec §7.2) over a set of packages. Linear in the packages and their
    /// relations; a store search keeps the result in its <see cref="Search.StoreIndex"/> rather than building it per search.
    /// </summary>
    public static RelationIndex RelationIndexOf(IReadOnlyList<PackageState> packages, bool allowUnsignedRelations)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return Search.StoreIndex.Build(packages.DistinctBy(p => p.Digest, StringComparer.Ordinal), allowUnsignedRelations).Relations;
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
        // The default file when the locale is its node's language: the node's own moca.language, else the package's.
        var own = state.NodeLanguages is { } languages && languages.TryGetValue(record.Node.Path, out var nodeLanguage) ? nodeLanguage : state.Language;
        var file = locale is null || locale == own
            ? $"content/{record.Node.Path}"
            : $"content/{MdSuffix().Replace(record.Node.Path, "")}.{locale}.md";
        var supersededBy = index.Superseded.GetValueOrDefault(key) ?? [];
        var contestedBy = (state.Contests.GetValueOrDefault(file) ?? []).Concat(index.Contested.GetValueOrDefault(key) ?? []).ToList();
        var signed = state.Signers.Count > 0;
        var reviews = state.Reviews.GetValueOrDefault(file) ?? [];
        return record with
        {
            // Attestations §7.1: whether the copy is bound to its publisher depends on the trust root, so it comes from the state.
            Package = record.Package with
            {
                Signed = signed,
                Signers = signed ? [.. state.Signers] : null,
                IssuedCopy = state.IssuedCopy is { } issued ? new CitationIssuedCopy(issued.OriginalDigest, [.. issued.Issuers]) : null,
            },
            Node = record.Node with { IssuedChange = state.IssuedCopy?.ChangedFiles.Contains(file) == true ? true : null },
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

/// <summary>A package state's terms (Reader contract §9.4, §15.3): the declared terms, <c>{}</c> for none, and their authority.</summary>
/// <param name="Terms">The manifest's terms as written; an empty object when it has none.</param>
/// <param name="Authority"><c>packager</c> or <c>unverified</c>.</param>
public sealed record PackageTermsState(
    [property: JsonPropertyName("terms")] JsonNode Terms,
    [property: JsonPropertyName("authority")] string Authority);
