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

    /// <summary>
    /// The node's resolved terms (Reader contract §15.3), only in records from a search with a terms request
    /// (<c>SearchOptions.Terms</c>).
    /// </summary>
    [JsonPropertyName("terms")]
    public CitationTerms? Terms { get; init; }
}

/// <summary>The package part of a citation record.</summary>
public sealed record CitationPackage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("signed")] bool Signed,
    [property: JsonPropertyName("signers")] IReadOnlyList<string>? Signers = null,
    [property: JsonPropertyName("issuedCopy")] CitationIssuedCopy? IssuedCopy = null,
    [property: JsonPropertyName("datePublished")] string? DatePublished = null,
    [property: JsonPropertyName("datePackaged")] string? DatePackaged = null);

/// <summary>An issued copy in a citation record (attestations §7.1): the original digest and the issuers.</summary>
public sealed record CitationIssuedCopy(
    [property: JsonPropertyName("originalDigest")] string OriginalDigest,
    [property: JsonPropertyName("issuers")] IReadOnlyList<string> Issuers);

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

    /// <summary>True when the record comes from a file the issuer of an issued copy changed (attestations §7.1).</summary>
    [JsonPropertyName("issuedChange")]
    public bool? IssuedChange { get; init; }

    /// <summary>The node's own <c>moca.date_published</c>, as written, when it parses (package spec §4.12). Never the package's.</summary>
    [JsonPropertyName("datePublished")]
    public string? DatePublished { get; init; }

    /// <summary>The node's valid media descriptions, in order (package spec §5.7).</summary>
    [JsonPropertyName("media")]
    public IReadOnlyList<CitationMedia>? Media { get; init; }

    /// <summary>The node's own <c>moca.contributors</c>, as written, when they match the node schema (package spec §4.13). Never the package's.</summary>
    [JsonPropertyName("contributors")]
    public IReadOnlyList<Contributor>? Contributors { get; init; }

    /// <summary>
    /// The node's licence (Reader contract §7): its <c>terms.nodes</c> entry's <c>rights.license</c>, else the
    /// manifest's <c>license</c>. A value with a terms problem is skipped.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>
    /// The node's copyright statement, as written (Reader contract §7): its <c>terms.nodes</c> entry's
    /// <c>rights.copyright</c>, else <c>terms.rights.copyright</c>. A value with a terms problem is skipped.
    /// </summary>
    [JsonPropertyName("copyright")]
    public JsonObject? Copyright { get; init; }
}

/// <summary>A media description in a citation record (package spec §5.7): never fetched by the Reader.</summary>
public sealed record CitationMedia(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("duration")] double? Duration = null,
    [property: JsonPropertyName("digest")] string? Digest = null,
    [property: JsonPropertyName("transcript")] string? Transcript = null)
{
    /// <summary>The <c>source</c> of the reference, when this description is a valid alternate (package spec §5.7).</summary>
    [JsonPropertyName("alternateOf")]
    public string? AlternateOf { get; init; }

    /// <summary>The alternate's stretches; present with <see cref="AlternateOf"/>.</summary>
    [JsonPropertyName("sync")]
    public IReadOnlyList<OpenMoca.Reader.Content.SyncEntry>? Sync { get; init; }
}

/// <summary>UTF-8 byte offsets into the node file.</summary>
public sealed record CitationSpan(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End);

/// <summary>A declared generation or verification: <c>by</c> and optional <c>at</c>.</summary>
public sealed record DeclaredBy(
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("at")] string? At = null)
{
    /// <summary>For <c>generated</c> only: the model's name and version, when the node states it (package spec §4.14).</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

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
    [property: JsonPropertyName("packageVerified")] bool PackageVerified = false,
    [property: JsonPropertyName("fileChanged")] bool? FileChanged = null)
{
    internal static CitationReview From(AttestedReview r, bool packageVerified) =>
        new(r.Reviewer, r.ReviewedAt, r.Outcome, r.Signer, r.Scope, packageVerified, r.FileChanged);
}

/// <summary>One evidence entry: the source (as the node declares it), the selector, and whether it matched.</summary>
public sealed record CitationEvidence(
    [property: JsonPropertyName("source")] JsonObject Source,
    [property: JsonPropertyName("selector")] JsonObject? Selector = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("matched")] bool? Matched = null)
{
    /// <summary>For a time range refined by a quote that matched: where the quote is spoken (Reader contract §7).</summary>
    [JsonPropertyName("located")]
    public EvidenceLocation? Located { get; init; }
}

/// <summary>Seconds on the cited source's timeline, from the start of the first cue a refined quote is in to the end
/// of the last (Reader contract §7).</summary>
public sealed record EvidenceLocation(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End);

/// <summary>How a search found a record: <c>search</c>, or a structure relation followed by a hook.</summary>
public sealed record CitationRetrieval(
    [property: JsonPropertyName("via")] string Via,
    [property: JsonPropertyName("from")] string? From = null);

/// <summary>One resolved terms field (Reader contract §15.3): its value and the level that decided it.</summary>
/// <typeparam name="T">The field's type.</typeparam>
/// <param name="Value">The resolved value.</param>
/// <param name="DecidedBy"><c>platform</c>, <c>package</c>, <c>node</c>, <c>composite</c> or <c>host</c>.</param>
public sealed record ResolvedTerm<T>(
    [property: JsonPropertyName("value")] T Value,
    [property: JsonPropertyName("decidedBy")] string DecidedBy);

/// <summary>A record's resolved terms (citation-record schema <c>terms</c>; Reader contract §15.3).</summary>
/// <param name="Authority"><c>packager</c> or <c>unverified</c> (contract §15.2).</param>
/// <param name="Ai">Each permission of <c>terms.ai</c>, in the same shape.</param>
/// <param name="Rating">The node's effective rating, as written; a rating that is not an object is shown as <c>{ "explicit": true }</c>.</param>
/// <param name="Rights">The resolved export permission.</param>
public sealed record CitationTerms(
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("ai")] CitationAiTerms Ai,
    [property: JsonPropertyName("rating")] JsonObject? Rating,
    [property: JsonPropertyName("rights")] CitationRightsTerms? Rights);

/// <summary>Resolved <c>terms.ai</c>.</summary>
/// <param name="Allowed">The five permissions.</param>
/// <param name="AnswerSources"><c>model</c> or <c>work</c>.</param>
/// <param name="ProviderRequires">The conditions a provider must meet, sorted. An unknown or malformed one is kept, so no host can meet it.</param>
public sealed record CitationAiTerms(
    [property: JsonPropertyName("allowed")] CitationAllowedTerms Allowed,
    [property: JsonPropertyName("answerSources")] ResolvedTerm<string> AnswerSources,
    [property: JsonPropertyName("providerRequires")] ResolvedTerm<IReadOnlyList<string>> ProviderRequires);

/// <summary>Resolved <c>terms.ai.allowed</c>.</summary>
/// <param name="Device">AI use on the person's own device.</param>
/// <param name="Host">AI use in the host's own service.</param>
/// <param name="Provider">AI use with a third party's model.</param>
/// <param name="Training">Training a model on the work.</param>
/// <param name="MachineTranslation">Translating the work by machine.</param>
public sealed record CitationAllowedTerms(
    [property: JsonPropertyName("device")] ResolvedTerm<bool> Device,
    [property: JsonPropertyName("host")] ResolvedTerm<bool> Host,
    [property: JsonPropertyName("provider")] ResolvedTerm<bool> Provider,
    [property: JsonPropertyName("training")] ResolvedTerm<bool> Training,
    [property: JsonPropertyName("machineTranslation")] ResolvedTerm<bool> MachineTranslation);

/// <summary>Resolved rights permissions (Reader contract §15.3).</summary>
/// <param name="Export">Whether an app may let people copy, save or export the work's text. Removes nothing from a search.</param>
public sealed record CitationRightsTerms(
    [property: JsonPropertyName("export")] ResolvedTerm<bool> Export);

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
