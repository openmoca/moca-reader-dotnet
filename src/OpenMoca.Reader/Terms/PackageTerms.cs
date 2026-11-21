using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenMoca.Reader;

/// <summary>
/// A package's terms as the Reader exposes them (Reader contract §15.1): exactly as written, their problems, and who
/// stated them. Labels, not locks: reading a package never applies them.
/// </summary>
/// <param name="Declared">The manifest's <c>terms</c>, exactly as written; null when the manifest has none. No defaults are filled in.</param>
/// <param name="Problems">The <c>U001</c> and <c>U003</c> findings, each with the path of the field (for example <c>ai.allowed.provider</c>).</param>
/// <param name="Authority"><c>packager</c> when a package attestation verifies under the host's trust root, else <c>unverified</c> (contract §15.2).</param>
public sealed record PackageTerms(
    [property: JsonPropertyName("declared")] JsonNode? Declared,
    [property: JsonPropertyName("problems")] IReadOnlyList<TermsProblem> Problems,
    [property: JsonPropertyName("authority")] string Authority);

/// <summary>A problem with one field of <c>terms</c> (package spec §4.14).</summary>
/// <param name="Code"><c>U001_TERMS_INVALID</c> or <c>U003_TERMS_UNKNOWN_VALUE</c>.</param>
/// <param name="Path">The field, as a dotted path under <c>terms</c>, such as <c>nodes.0.rating</c>.</param>
public sealed record TermsProblem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("path")] string Path);

/// <summary>The authority values of Reader contract §15.2.</summary>
public static class TermsAuthority
{
    /// <summary>A package attestation verifies under the host's trust root.</summary>
    public const string Packager = "packager";

    /// <summary>No package attestation verifies.</summary>
    public const string Unverified = "unverified";
}
