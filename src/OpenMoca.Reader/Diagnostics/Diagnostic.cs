using System.Text.Json.Serialization;

namespace OpenMoca.Reader.Diagnostics;

/// <summary>
/// One finding about a package, sidecar or backend (Reader contract §11). <see cref="Code"/> is the
/// contract; <see cref="Message"/> is for people and hosts must not match on it.
/// </summary>
/// <param name="Code">A code from <see cref="DiagnosticCodes"/>, such as <c>C007_LINK_UNRESOLVED</c>.</param>
/// <param name="Severity">The severity, after strict mode has been applied.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="File">The package-relative file the finding is about, when there is one.</param>
/// <param name="Line">The 1-based line in <paramref name="File"/>, when known.</param>
public sealed record Diagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] DiagnosticSeverity Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("file"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null)
{
    /// <summary>
    /// For <c>U004_TERMS_EXCLUDED</c> (Reader contract §15.4): the number of records or units the terms request removed,
    /// for each reason (one removed for two reasons counts under each). Null on every other finding.
    /// </summary>
    [JsonPropertyName("excluded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Excluded { get; init; }

    /// <summary>
    /// For <c>U004_TERMS_EXCLUDED</c> (Reader contract §15.4): how many of the removed records or units belong to
    /// packages hidden for the request. A host takes these out of every count it hands to a model. Null on every other
    /// finding.
    /// </summary>
    [JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Hidden { get; init; }
}
