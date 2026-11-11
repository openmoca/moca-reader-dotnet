using System.IO.Compression;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Sources;

/// <summary>
/// Opens package sources: directories, <c>.moca</c> Zip archives and host-supplied sources, each
/// checked against the safety rules and limits of Reader contract §3.
/// </summary>
public static class PackageSources
{
    /// <summary>Opens a directory or a <c>.moca</c> / <c>.zip</c> archive path.</summary>
    /// <exception cref="TargetException">The target does not exist, or the archive breaks a rule.</exception>
    public static IPackageSource Open(string path, ReaderLimits? limits = null)
    {
        if (Directory.Exists(path))
        {
            return new DirectorySource(path, limits);
        }

        if (File.Exists(path) && (path.EndsWith(".moca", StringComparison.OrdinalIgnoreCase)
                                  || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            return new ArchiveSource(File.ReadAllBytes(path), path, limits ?? ReaderLimits.Default);
        }

        if (File.Exists(path))
        {
            throw new TargetException(DiagnosticCodes.T002ManifestNotFound,
                $"target exists but is neither a directory nor a .moca/.zip archive: {path}");
        }

        throw new TargetException(DiagnosticCodes.T001TargetNotFound, $"target does not exist: {path}");
    }

    /// <summary>Opens a <c>.moca</c> archive held in memory.</summary>
    /// <exception cref="TargetException">The archive cannot be opened or breaks a rule.</exception>
    public static IPackageSource OpenArchive(byte[] archive, ReaderLimits? limits = null) =>
        new ArchiveSource(archive, null, limits ?? ReaderLimits.Default);

    /// <summary>Wraps a host source so the Reader's rules apply to it.</summary>
    public static IPackageSource Checked(IPackageSource host, ReaderLimits? limits = null) =>
        host is DirectorySource or ArchiveSource or CheckedHostSource
            ? host
            : new CheckedHostSource(host, limits ?? ReaderLimits.Default);

    /// <summary>Entries whose name begins with <c>.</c> are not part of a package (package spec §6.2).</summary>
    internal static bool IsHidden(string segment) => segment.StartsWith('.');

    internal static bool HasHiddenSegment(string path) => path.Split('/').Any(IsHidden);

    /// <summary>Contract §3: absolute names, backslashes and <c>..</c> segments are rejected before reading.</summary>
    internal static void AssertSafeArchivePath(string name)
    {
        var absolute = name.StartsWith('/')
                       || (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':');
        if (name.Contains('\\') || absolute)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                $"archive entry \"{name}\" is absolute or uses backslashes");
        }

        if (name.Split('/').Contains(".."))
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                $"archive entry \"{name}\" contains a \"..\" segment");
        }

        // Contract §3: no NUL, and no empty or "." segments (a trailing "/" marks a directory).
        if (name.Contains('\0') || name.TrimEnd('/').Split('/').Any(s => s.Length == 0 || s == "."))
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                $"archive entry \"{name.Replace("\0", "\\0", StringComparison.Ordinal)}\" has an empty, \".\" or NUL segment");
        }
    }

    /// <summary>
    /// Contract §3: entry names are UTF-8. Reads the central directory's raw names and flags: a name with bytes outside
    /// ASCII and without the UTF-8 flag (general purpose bit 11), or with a NUL, rejects the archive.
    /// </summary>
    internal static void AssertNamesAreUtf8(byte[] archive)
    {
        // End of central directory: signature 0x06054b50, searched backwards over the comment.
        var eocd = -1;
        for (var i = archive.Length - 22; i >= Math.Max(0, archive.Length - 22 - 0xFFFF); i--)
        {
            if (archive[i] == 0x50 && archive[i + 1] == 0x4b && archive[i + 2] == 0x05 && archive[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            return; // ZipArchive reports a damaged archive.
        }

        var count = BitConverter.ToUInt16(archive, eocd + 10);
        long offset = BitConverter.ToUInt32(archive, eocd + 16);
        for (var n = 0; n < count && offset + 46 <= archive.Length; n++)
        {
            var at = (int)offset;
            if (BitConverter.ToUInt32(archive, at) != 0x02014b50)
            {
                return;
            }

            var flags = BitConverter.ToUInt16(archive, at + 8);
            var nameLength = BitConverter.ToUInt16(archive, at + 28);
            var extraLength = BitConverter.ToUInt16(archive, at + 30);
            var commentLength = BitConverter.ToUInt16(archive, at + 32);
            if (at + 46 + nameLength > archive.Length)
            {
                return;
            }

            var name = archive.AsSpan(at + 46, nameLength);
            if (name.Contains((byte)0))
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected, "archive entry name contains NUL");
            }

            if ((flags & 0x800) == 0 && name.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0)
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected, "archive entry name is not marked as UTF-8");
            }

            offset += 46 + nameLength + extraLength + commentLength;
        }
    }

    internal static int CompareOrdinal(PackageEntry a, PackageEntry b) => string.CompareOrdinal(a.Path, b.Path);
}

/// <summary>A package stored as a directory. Symbolic links are listed, never followed.</summary>
internal sealed class DirectorySource(string root, ReaderLimits? limits = null) : IPackageSource
{
    private readonly ReaderLimits _limits = limits ?? ReaderLimits.Default;
    private IReadOnlyList<PackageEntry>? _entries;
    private long _read;

    public string Kind => "directory";

    public string? Location => root;

    public IReadOnlyList<PackageEntry> List() => _entries ??= Walk();

    public byte[] Read(string path)
    {
        var file = new FileInfo(Path.Combine([root, .. path.Split('/')]));

        // Never follow a link swapped in after listing.
        if (file.LinkTarget is not null || !file.Exists)
        {
            throw new IOException($"{path} is no longer a regular file");
        }

        var bytes = File.ReadAllBytes(file.FullName);
        _read += bytes.Length;
        if (_read > _limits.MaxBytes)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"directory exceeds the {_limits.MaxBytes}-byte limit");
        }

        return bytes;
    }

    private List<PackageEntry> Walk()
    {
        var entries = new List<PackageEntry>();
        Visit(root, "", entries);

        // Contract §3: the entry and byte limits apply to every source, directories included.
        if (entries.Count > _limits.MaxEntries)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"directory has {entries.Count} entries, over the {_limits.MaxEntries} limit");
        }

        if (entries.Sum(e => e.Size ?? 0) > _limits.MaxBytes)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"directory exceeds the {_limits.MaxBytes}-byte limit");
        }

        return entries;
    }

    private static void Visit(string absoluteDir, string relativeDir, List<PackageEntry> entries)
    {
        string[] names;
        try
        {
            names = Directory.GetFileSystemEntries(absoluteDir).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries.Add(new PackageEntry(relativeDir.Length > 0 ? relativeDir : ".", PackageEntryKind.Unreadable));
            return;
        }

        Array.Sort(names, string.CompareOrdinal);
        foreach (var name in names)
        {
            if (PackageSources.IsHidden(name))
            {
                continue;
            }

            var absolute = Path.Combine(absoluteDir, name);
            var relative = relativeDir.Length > 0 ? $"{relativeDir}/{name}" : name;
            FileSystemInfo info;
            try
            {
                info = new FileInfo(absolute);
                if (!info.Exists)
                {
                    info = new DirectoryInfo(absolute);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add(new PackageEntry(relative, PackageEntryKind.Unreadable));
                continue;
            }

            if (info.LinkTarget is not null)
            {
                entries.Add(new PackageEntry(relative, PackageEntryKind.Symlink));
            }
            else if (info is DirectoryInfo)
            {
                Visit(absolute, relative, entries);
            }
            else if (info is FileInfo file && (file.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) == 0)
            {
                entries.Add(new PackageEntry(relative, PackageEntryKind.File, file.Length));
            }
            else
            {
                entries.Add(new PackageEntry(relative, PackageEntryKind.Other));
            }
        }
    }
}

/// <summary>A <c>.moca</c> Zip archive, read in memory without extracting it.</summary>
internal sealed class ArchiveSource : IPackageSource
{
    // Unix file-type bits in the high 16 bits of a Zip entry's external attributes.
    private const int TypeMask = 0xF000;
    private const int RegularFile = 0x8000;
    private const int SymbolicLink = 0xA000;

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unreadable = new(StringComparer.Ordinal);
    private readonly List<PackageEntry> _entries = [];

    public ArchiveSource(byte[] archive, string? location, ReaderLimits limits)
    {
        Location = location;
        PackageSources.AssertNamesAreUtf8(archive);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"archive could not be opened: {ex.Message}");
        }

        using (zip)
        {
            if (zip.Entries.Count > limits.MaxEntries)
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                    $"archive has {zip.Entries.Count} entries, over the {limits.MaxEntries} limit");
            }

            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                PackageSources.AssertSafeArchivePath(name);
                total += entry.Length;
                if (total > limits.MaxBytes)
                {
                    throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                        $"archive expands beyond the {limits.MaxBytes}-byte limit");
                }

                if (name.EndsWith('/'))
                {
                    continue;
                }

                var path = name.TrimEnd('/');
                if (PackageSources.HasHiddenSegment(path))
                {
                    continue;
                }

                var mode = (entry.ExternalAttributes >>> 16) & TypeMask;
                var kind = mode switch
                {
                    SymbolicLink => PackageEntryKind.Symlink,
                    0 or RegularFile => PackageEntryKind.File,
                    _ => PackageEntryKind.Other,
                };
                _entries.Add(new PackageEntry(path, kind, entry.Length));
                if (kind == PackageEntryKind.File)
                {
                    // Contract §3: only stored and deflated entries are read (any other method is unreadable, P002),
                    // and an entry may not inflate past its declared size.
                    try
                    {
                        using var stream = entry.Open();
                        using var buffer = new MemoryStream();
                        var chunk = new byte[81920];
                        int read;
                        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            if (buffer.Length + read > entry.Length)
                            {
                                throw new InvalidDataException("the entry inflates past its declared size");
                            }

                            buffer.Write(chunk, 0, read);
                        }

                        _files[path] = buffer.ToArray();
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
                    {
                        _unreadable[path] = ex.Message;
                    }
                }
            }
        }

        _entries.Sort(PackageSources.CompareOrdinal);
    }

    public string Kind => "archive";

    public string? Location { get; }

    public IReadOnlyList<PackageEntry> List() => _entries;

    public byte[] Read(string path) =>
        _files.TryGetValue(path, out var bytes) ? bytes
        : _unreadable.TryGetValue(path, out var reason) ? throw new IOException(reason)
        : throw new FileNotFoundException($"no such entry: {path}");
}

/// <summary>A host source with the Reader's rules applied: limits, safe names, listed paths only.</summary>
internal sealed class CheckedHostSource(IPackageSource host, ReaderLimits limits) : IPackageSource
{
    private List<PackageEntry>? _entries;
    private HashSet<string>? _listed;
    private long _read;

    public string Kind => "host";

    public string? Location => host.Location;

    public IReadOnlyList<PackageEntry> List()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var raw = host.List();
        if (raw.Count > limits.MaxEntries)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                $"source lists {raw.Count} entries, over the {limits.MaxEntries} limit");
        }

        long total = 0;
        foreach (var entry in raw)
        {
            PackageSources.AssertSafeArchivePath(entry.Path);
            total += entry.Size ?? 0;
            if (total > limits.MaxBytes)
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                    $"source exceeds the {limits.MaxBytes}-byte limit");
            }
        }

        _entries = raw.Where(e => !PackageSources.HasHiddenSegment(e.Path)).ToList();
        _entries.Sort(PackageSources.CompareOrdinal);
        _listed = _entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        return _entries;
    }

    public byte[] Read(string path)
    {
        // Contract §3: never request a path the source did not list.
        if (_listed is null || !_listed.Contains(path))
        {
            throw new InvalidOperationException($"path was not listed: {path}");
        }

        var bytes = host.Read(path);

        // Contract §3: a host source need not list sizes, so the byte limit is also enforced on what is read.
        _read += bytes.Length;
        if (_read > limits.MaxBytes)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"source exceeds the {limits.MaxBytes}-byte limit");
        }

        return bytes;
    }
}
