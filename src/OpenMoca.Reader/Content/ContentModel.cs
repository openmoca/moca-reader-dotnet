using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Content;

/// <summary>The outcome of checking one <c>moca.evidence</c> entry.</summary>
/// <param name="Local">The source is a file under <c>sources/</c> or <c>media/</c> inside the package.</param>
/// <param name="Matched">Set only when the selector was checked against that file.</param>
/// <param name="Located">For a time range refined by a quote that matched: where the quote is spoken, in seconds on
/// the cited source's timeline (Reader contract §7).</param>
public sealed record EvidenceCheck(bool Local, bool? Matched = null, (double Start, double End)? Located = null);

/// <summary>Package spec §5.7: one valid media description, with its source's resource as written.</summary>
public sealed record MediaDescription(string Source, string Resource, string Type, double? Duration, string? Digest, string? Transcript)
{
    /// <summary>The package path of the transcript, when it names one.</summary>
    internal string? TranscriptPath { get; init; }

    /// <summary>The <c>source</c> of the reference this description is a valid alternate of (§5.7, Alternates).</summary>
    public string? AlternateOf { get; init; }

    /// <summary>The stretches of a valid alternate: its <c>sync</c>, or the default <c>[{ at: 0, offset: 0 }]</c>.</summary>
    public IReadOnlyList<SyncEntry>? Sync { get; init; }
}

/// <summary>
/// One entry of an alternate's <c>sync</c> (package spec §5.7): from <paramref name="At"/> on the reference, the
/// alternate is <paramref name="Offset"/> seconds later, or, when it is null, does not hold that stretch.
/// </summary>
/// <param name="At">A time on the reference, in seconds.</param>
/// <param name="Offset">Seconds to add to reach the alternate, or null.</param>
public sealed record SyncEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("at")] double At,
    [property: System.Text.Json.Serialization.JsonPropertyName("offset"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] double? Offset);

/// <summary>One language version of a node (package spec §5.4).</summary>
public sealed class Representation
{
    /// <summary>The locale tag, or null for the default-language file.</summary>
    public required string? Locale { get; init; }

    /// <summary>Package-relative path of the file, for example <c>content/guides/setup.fr.md</c>.</summary>
    public required string File { get; init; }

    /// <summary>The parsed frontmatter mapping.</summary>
    public required JsonObject Frontmatter { get; init; }

    /// <summary>The Markdown body after the frontmatter.</summary>
    public required string Body { get; init; }

    /// <summary>UTF-8 byte offset of the body in the file.</summary>
    public required int BodyOffset { get; init; }

    /// <summary>Lowercase hex SHA-256 of the file's bytes, when the digest was computed.</summary>
    public string? Sha256 { get; init; }

    /// <summary>One entry per <c>moca.evidence</c> item, in order.</summary>
    public IReadOnlyList<EvidenceCheck> EvidenceChecks { get; init; } = [];

    /// <summary>Absolute concept IRIs the node is bound to, set when the package structure is read.</summary>
    public IReadOnlyList<string>? Concepts { get; internal set; }

    /// <summary>Package spec §5.7: the valid media descriptions, in order.</summary>
    public IReadOnlyList<MediaDescription> Media { get; init; } = [];

    /// <summary>Package spec §4.13: the node's own credits, when they match the node schema; null otherwise.</summary>
    public IReadOnlyList<Contributor>? Contributors { get; init; }

    /// <summary>The <c>moca</c> mapping, or an empty one.</summary>
    public JsonObject Moca => Frontmatter["moca"] as JsonObject ?? [];
}

/// <summary>An OKF concept document under <c>content/</c>, with its locale representations.</summary>
public sealed class ContentNode
{
    /// <summary>The node path relative to <c>content/</c> (the default-language file name), NFC.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The default representation's language (package spec §5.4): the manifest's <c>language</c>, or null when it has
    /// none. Its tag in the locale lookup, and its records' <c>node.locale</c>.
    /// </summary>
    public string? Language { get; internal set; }

    /// <summary>Representations, default language first, then by locale.</summary>
    public List<Representation> Representations { get; } = [];
}
