namespace OpenMoca.Reader.Sources;

/// <summary>
/// A package source (Reader interface §3): lists a package's entries and returns the bytes of any
/// listed file. Hosts implement it to read packages from memory, blob storage or a database; the
/// Reader provides implementations for directories and <c>.moca</c> archives.
/// </summary>
/// <remarks>
/// A Reader never asks for a path the source did not list (Reader contract §3). Entries whose name
/// starts with <c>.</c> are not part of a package; a source may list them, and the Reader skips them.
/// </remarks>
public interface IPackageSource
{
    /// <summary>A short name for the kind of source: <c>directory</c>, <c>archive</c> or <c>host</c>.</summary>
    string Kind { get; }

    /// <summary>Where the package lives, when that means something to the host (a path); otherwise null.</summary>
    string? Location { get; }

    /// <summary>Lists every entry in the package.</summary>
    IReadOnlyList<PackageEntry> List();

    /// <summary>Returns the bytes of a listed file.</summary>
    byte[] Read(string path);
}
