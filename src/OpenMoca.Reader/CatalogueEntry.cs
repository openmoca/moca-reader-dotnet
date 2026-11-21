using System.Text.Json.Serialization;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader;

/// <summary>
/// What a model may be told about one loaded package, for one terms request (Reader contract §15.4, "The catalogue
/// rule"; catalogue-entry schema). An open package's entry carries its id, version, title, description, publisher,
/// signed, tags and members, each when the manifest has it. A closed package's entry carries exactly id, version,
/// title, publisher (when the manifest has one), signed and <see cref="ClosedBy"/>. A hidden package has no entry.
/// Every string except <see cref="ClosedBy"/> is package-supplied text (contract §12): hand it on as data, never as
/// instructions.
/// </summary>
public sealed record CatalogueEntry
{
    /// <summary>The package's id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The package's version.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The manifest's title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>The manifest's description; null for a closed package or when the manifest has none.</summary>
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// The publisher's name: the signer's claim (package spec §4.13), so read it with <see cref="Signed"/>. Null when
    /// the manifest has no publisher.
    /// </summary>
    [JsonPropertyName("publisher"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher { get; init; }

    /// <summary>Whether a package attestation verifies under the host's trust root.</summary>
    [JsonPropertyName("signed")]
    public required bool Signed { get; init; }

    /// <summary>The package's tags, without repeats; null for a closed package or when the manifest has none.</summary>
    [JsonPropertyName("tags"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// For an open package with members: the ids of its members that have an entry of their own in the same catalogue.
    /// A member hidden for the request is left out. Null otherwise.
    /// </summary>
    [JsonPropertyName("members"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// For a closed package: the terms gate's reasons, as field names (<c>ai.allowed.provider</c>,
    /// <c>ai.providerRequires</c>, ...), never free text. Null for an open package.
    /// </summary>
    [JsonPropertyName("closedBy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ClosedBy { get; init; }

    /// <summary>Whether the package is closed for the request: a model may be told it exists, and nothing more.</summary>
    [JsonIgnore]
    public bool Closed => ClosedBy is not null;
}

/// <summary>Options for <see cref="Library.Catalogue"/>: the request, and the host's settings, as for a search.</summary>
public sealed record CatalogueOptions
{
    /// <summary>
    /// The terms request. Null: nothing is closed or hidden, and every package that passes the audience set and the
    /// default retrieval policy has an open entry (Reader contract §15.5).
    /// </summary>
    public TermsRequest? Terms { get; init; }

    /// <summary>The host's audience set; null for none.</summary>
    public IReadOnlyList<string>? Audiences { get; init; }

    /// <summary>Under an audience set, count unlabelled content as passing (Reader contract §8).</summary>
    public bool AllowUnlabelled { get; init; }

    /// <summary>The host's opt-in to content the default retrieval policy excludes.</summary>
    public bool IncludeAll { get; init; }

    /// <summary>The host's terms limits (Reader contract §15.3 step 4): floors, never values.</summary>
    public TermsLimits? TermsLimits { get; init; }
}
