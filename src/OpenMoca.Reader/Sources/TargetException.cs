namespace OpenMoca.Reader.Sources;

/// <summary>
/// Raised inside the Reader when a target cannot be opened at all (T001, T003). <see cref="MocaReader"/>
/// turns it into a diagnostic; it never reaches the host from <c>ReadPackageAsync</c>.
/// </summary>
public sealed class TargetException(string code, string message) : Exception(message)
{
    /// <summary>The diagnostic code: <c>T001_TARGET_NOT_FOUND</c>, <c>T002_MANIFEST_NOT_FOUND</c> or <c>T003_ARCHIVE_REJECTED</c>.</summary>
    public string Code { get; } = code;
}
