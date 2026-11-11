using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Content;

/// <summary>The outcome of checking one <c>moca.evidence</c> entry.</summary>
/// <param name="Local">The source is a file under <c>sources/</c> or <c>media/</c> inside the package.</param>
/// <param name="Matched">Set only when the selector was checked against that file.</param>
public sealed record EvidenceCheck(bool Local, bool? Matched = null);

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

    /// <summary>The <c>moca</c> mapping, or an empty one.</summary>
    public JsonObject Moca => Frontmatter["moca"] as JsonObject ?? [];
}

/// <summary>An OKF concept document under <c>content/</c>, with its locale representations.</summary>
public sealed class ContentNode
{
    /// <summary>The node path relative to <c>content/</c> (the default-language file name), NFC.</summary>
    public required string Path { get; init; }

    /// <summary>Representations, default language first, then by locale.</summary>
    public List<Representation> Representations { get; } = [];
}
