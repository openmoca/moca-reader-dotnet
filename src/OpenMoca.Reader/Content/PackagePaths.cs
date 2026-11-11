namespace OpenMoca.Reader.Content;

/// <summary>Resolving package-relative references the way the Reader does (package spec §5).</summary>
public static class PackagePaths
{
    /// <summary>The content directory.</summary>
    public const string ContentDirectory = ContentReader.ContentDirectory;

    /// <summary>
    /// Resolves a reference (a link or a source <c>resource</c>) from a node file to a package path.
    /// </summary>
    /// <param name="fromFile">The package-relative node file, for example <c>content/guides/setup.md</c>.</param>
    /// <param name="reference">The reference as written, for example <c>../sources/policy.pdf#page=3</c>.</param>
    /// <returns>The NFC package path, or null; <c>Escapes</c> when it leaves the package root.</returns>
    public static (string? Path, bool Escapes) Resolve(string fromFile, string reference) =>
        ContentReader.ResolveReference(fromFile, reference);
}
