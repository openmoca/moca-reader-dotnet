using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenMoca.Reader.Attestations;

namespace OpenMoca.Reader.Citations;

/// <summary>
/// What a Reader hands to an application for one node or chunk (Reader contract §7), valid against
/// <c>citation-record.schema.json</c>. Serialise with <see cref="CitationJson.Options"/>.
/// </summary>
public sealed record CitationRecord
{
    /// <summary>The package the node belongs to.</summary>
    [JsonPropertyName("package")]
    public required CitationPackage Package { get; init; }

    /// <summary>The node.</summary>
    [JsonPropertyName("node")]
    public required CitationNode Node { get; init; }

    /// <summary>UTF-8 byte offsets into the node file.</summary>
    [JsonPropertyName("span")]
    public CitationSpan? Span { get; init; }

    /// <summary>The text: the node body, or a chunk of it.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Trust signals, declared and attested kept apart.</summary>
    [JsonPropertyName("trust")]
    public required CitationTrust Trust { get; init; }

    /// <summary>Evidence entries, each with <c>matched</c> when it was checked.</summary>
    [JsonPropertyName("evidence")]
    public IReadOnlyList<CitationEvidence> Evidence { get; init; } = [];

    /// <summary>Absolute concept IRIs the node is bound to.</summary>
    [JsonPropertyName("concepts")]
    public IReadOnlyList<string>? Concepts { get; init; }

    /// <summary>The node's audience label.</summary>
    [JsonPropertyName("audience")]
    public string? Audience { get; init; }

    /// <summary>True when the node's <c>moca.audience</c> is malformed: the record never matches an audience set.</summary>
    [JsonPropertyName("audienceInvalid")]
    public bool? AudienceInvalid { get; init; }

    /// <summary>Profile URIs the package declares.</summary>
    [JsonPropertyName("profiles")]
    public IReadOnlyList<string>? Profiles { get; init; }

    /// <summary>The search score, for search results. Higher is better; scores from different backends are not comparable.</summary>
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    /// <summary>How a search found this record (moca-spec ADR-0010).</summary>
    [JsonPropertyName("retrieval")]
    public CitationRetrieval? Retrieval { get; init; }
}

/// <summary>The package part of a citation record.</summary>
public sealed record CitationPackage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("signed")] bool Signed,
    [property: JsonPropertyName("signers")] IReadOnlyList<string>? Signers = null);

/// <summary>The node part of a citation record.</summary>
public sealed record CitationNode
{
    /// <summary><c>&lt;package id&gt;#&lt;path&gt;</c>; the same across versions.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary><c>&lt;package id&gt;@&lt;version&gt;#&lt;path&gt;</c>: use it as the citation key.</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>The node path relative to <c>content/</c>.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The representation's locale, or the package language.</summary>
    [JsonPropertyName("locale")]
    public string? Locale { get; init; }

    /// <summary>The OKF type.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>The title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary><c>sha256:</c> digest of the node file.</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

/// <summary>UTF-8 byte offsets into the node file.</summary>
public sealed record CitationSpan(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End);

/// <summary>A declared generation or verification: <c>by</c> and optional <c>at</c>.</summary>
public sealed record DeclaredBy(
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("at")] string? At = null);

/// <summary>Trust signals (Reader contract §7).</summary>
public sealed record CitationTrust
{
    /// <summary>OKF status: draft, stable or deprecated.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>OKF <c>generated</c>, or null. Always written, as the reference Reader does.</summary>
    [JsonPropertyName("generated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DeclaredBy? Generated { get; init; }

    /// <summary>OKF <c>verified</c>: self-declared, not cryptographically checked.</summary>
    [JsonPropertyName("declaredVerified")]
    public IReadOnlyList<DeclaredBy> DeclaredVerified { get; init; } = [];

    /// <summary>Reviews that verified against the trust root and match the current file.</summary>
    [JsonPropertyName("attestedReviews")]
    public IReadOnlyList<CitationReview> AttestedReviews { get; init; } = [];

    /// <summary><c>approved</c>, <c>disputed</c>, <c>unverified</c> or <c>none</c> (Reader contract §7, moca-spec ADR-0017).</summary>
    [JsonPropertyName("reviewStatus")]
    public string ReviewStatus { get; init; } = "none";

    /// <summary>OKF <c>stale_after</c>.</summary>
    [JsonPropertyName("staleAfter")]
    public string? StaleAfter { get; init; }

    /// <summary>The effective validity window start.</summary>
    [JsonPropertyName("validFrom")]
    public string? ValidFrom { get; init; }

    /// <summary>The effective validity window end.</summary>
    [JsonPropertyName("validUntil")]
    public string? ValidUntil { get; init; }

    /// <summary>now ≥ <c>stale_after</c>.</summary>
    [JsonPropertyName("stale")]
    public bool Stale { get; init; }

    /// <summary>Inside the validity window.</summary>
    [JsonPropertyName("inForce")]
    public bool InForce { get; init; }

    /// <summary>A loaded package supersedes this one.</summary>
    [JsonPropertyName("superseded")]
    public bool Superseded { get; init; }

    /// <summary>Which packages supersede it.</summary>
    [JsonPropertyName("supersededBy")]
    public IReadOnlyList<string>? SupersededBy { get; init; }

    /// <summary>A loaded package, or <c>moca.contested_by</c>, disputes it.</summary>
    [JsonPropertyName("contested")]
    public bool Contested { get; init; }

    /// <summary>What contests it.</summary>
    [JsonPropertyName("contestedBy")]
    public IReadOnlyList<string>? ContestedBy { get; init; }
}

/// <summary>An attested review in a citation record.</summary>
public sealed record CitationReview(
    [property: JsonPropertyName("reviewer")] string Reviewer,
    [property: JsonPropertyName("reviewedAt")] string ReviewedAt,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("signer")] string Signer,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("packageVerified")] bool PackageVerified = false)
{
    internal static CitationReview From(AttestedReview r, bool packageVerified) =>
        new(r.Reviewer, r.ReviewedAt, r.Outcome, r.Signer, r.Scope, packageVerified);
}

/// <summary>One evidence entry: the source (as the node declares it), the selector, and whether it matched.</summary>
public sealed record CitationEvidence(
    [property: JsonPropertyName("source")] JsonObject Source,
    [property: JsonPropertyName("selector")] JsonObject? Selector = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("matched")] bool? Matched = null);

/// <summary>How a search found a record: <c>search</c>, or a structure relation followed by a hook.</summary>
public sealed record CitationRetrieval(
    [property: JsonPropertyName("via")] string Via,
    [property: JsonPropertyName("from")] string? From = null);

/// <summary>JSON options for citation records: nulls omitted (except <c>trust.generated</c>), non-ASCII unescaped.</summary>
public static class CitationJson
{
    /// <summary>Serialiser options.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A record as a JSON node.</summary>
    public static JsonNode ToJson(CitationRecord record) => JsonSerializer.SerializeToNode(record, Options)!;
}
