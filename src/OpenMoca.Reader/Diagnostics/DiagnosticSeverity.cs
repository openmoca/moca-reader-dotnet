using System.Text.Json.Serialization;

namespace OpenMoca.Reader.Diagnostics;

/// <summary>How serious a diagnostic is (Reader contract §11).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    /// <summary>In the T, P, M or C families, makes the package invalid; elsewhere, disables the feature concerned.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>A finding the host should see; the package stays usable.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>Informational only.</summary>
    [JsonStringEnumMemberName("info")]
    Info,
}
