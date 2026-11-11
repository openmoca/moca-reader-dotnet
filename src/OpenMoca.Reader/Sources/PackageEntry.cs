namespace OpenMoca.Reader.Sources;

/// <summary>What a package entry is. Only regular files may be part of a package (package spec §6.3).</summary>
public enum PackageEntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A symbolic link: reported as P001 and never followed.</summary>
    Symlink,

    /// <summary>Any other non-regular entry (device, socket, pipe): reported as P001.</summary>
    Other,

    /// <summary>An entry that could not be read: reported as P002.</summary>
    Unreadable,
}

/// <summary>One entry a package source lists.</summary>
/// <param name="Path">Package-relative path with <c>/</c> separators, as stored.</param>
/// <param name="Kind">What the entry is.</param>
/// <param name="Size">Its size in bytes, when the source knows it.</param>
public sealed record PackageEntry(string Path, PackageEntryKind Kind = PackageEntryKind.File, long? Size = null);
