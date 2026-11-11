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
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null);
