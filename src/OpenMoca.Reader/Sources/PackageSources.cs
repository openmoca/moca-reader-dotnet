using System.IO.Compression;
using System.Text;
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

        // Encrypted packages §2: an envelope (conventionally .mocae) is recognised by content, so any file that starts
        // with a Zip signature is read as an archive.
        if (File.Exists(path) && (path.EndsWith(".moca", StringComparison.OrdinalIgnoreCase)
                                  || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                  || StartsWithZipSignature(path)))
        {
            return new ArchiveSource(ReadArchiveFile(path), path, limits ?? ReaderLimits.Default);
        }

        if (File.Exists(path))
        {
            throw new TargetException(DiagnosticCodes.T002ManifestNotFound,
                $"target exists but is neither a directory nor a .moca/.zip archive: {path}");
        }

        throw new TargetException(DiagnosticCodes.T001TargetNotFound, $"target does not exist: {path}");
    }

    // An archive is read into memory whole; one too large to hold is rejected before reading (T003), and one that
    // cannot be read, or that changes while it is read, is T003 too.
    private static byte[] ReadArchiveFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > Array.MaxLength)
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"archive is {stream.Length} bytes, too large to read");
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected, $"archive could not be read: {ex.Message}");
        }
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
    private static bool StartsWithZipSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
                && head[0] == 0x50 && head[1] == 0x4b && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One central-directory entry of an archive, as its records state it.</summary>
    internal sealed record ZipEntryRecord(
        string Name, int Flags, int Method, uint Crc, long CompressedSize, long Size, uint ExternalAttributes, long LocalOffset);

    /// <summary>
    /// Contract §3, "Directory": the records every Zip reader must agree on, checked before any entry is read. There is
    /// exactly one end-of-central-directory record, and it ends at the end of the file; it is not part of a multi-disk
    /// or Zip64 archive; the central directory holds exactly the entries it declares; each entry's local header is where
    /// the directory says, with the same name and compression method; and every name is UTF-8 with no NUL. Anything else
    /// is T003. This is the Reader's only parse of the directory: entries are read from what it returns.
    /// </summary>
    /// <exception cref="TargetException">The archive breaks one of the rules.</exception>
    internal static (List<ZipEntryRecord> Entries, long Offset) ReadDirectory(byte[] buf)
    {
        static TargetException Reject(string why) => new(DiagnosticCodes.T003ArchiveRejected, $"archive {why}");
        uint U32(long at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)at, 4));
        int U16(long at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan((int)at, 2));

        const int End = 22;
        long end = -1;
        for (long p = buf.Length - End; p >= Math.Max(0, buf.Length - End - 0xFFFF); p--)
        {
            if (U32(p) == 0x06054b50 && p + End + U16(p + 20) == buf.Length)
            {
                if (end != -1)
                {
                    throw Reject("has more than one end-of-central-directory record");
                }

                end = p;
            }
        }

        if (end == -1)
        {
            throw Reject("has no end-of-central-directory record at its end");
        }

        var count = U16(end + 10);
        long size = U32(end + 12);
        long offset = U32(end + 16);
        if (U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 8) != count)
        {
            throw Reject("is part of a multi-disk archive");
        }

        if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF || (end >= 20 && U32(end - 20) == 0x07064b50))
        {
            throw Reject("uses Zip64, which this version does not read");
        }

        var stop = offset + size;
        if (stop > end)
        {
            throw Reject("has a central directory that runs into its end record");
        }

        var entries = new List<ZipEntryRecord>(count);
        var at = offset;
        for (var i = 0; i < count; i++)
        {
            if (at + 46 > stop || U32(at) != 0x02014b50)
            {
                throw Reject("has a central directory that does not hold the entries it declares");
            }

            var nameLength = U16(at + 28);
            var next = at + 46 + nameLength + U16(at + 30) + U16(at + 32);
            if (next > stop)
            {
                throw Reject("has a central directory that does not hold the entries it declares");
            }

            long local = U32(at + 42);
            if (U16(at + 34) != 0 || local + 30 + nameLength > offset || U32(local) != 0x04034b50)
            {
                throw Reject("has an entry whose local header is not where the central directory says");
            }

            var name = buf.AsSpan((int)(at + 46), nameLength);
            var sameName = U16(local + 26) == nameLength && buf.AsSpan((int)(local + 30), nameLength).SequenceEqual(name);
            var method = U16(at + 10);
            if (!sameName || U16(local + 8) != method)
            {
                throw Reject("has an entry whose local header does not match the central directory");
            }

            // Contract §3: entry names are UTF-8. A name with bytes outside ASCII and without the UTF-8 flag (general
            // purpose bit 11) is rejected, not guessed; so is a NUL.
            var flags = U16(at + 8);
            if (name.Contains((byte)0))
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected, "archive entry name contains NUL");
            }

            if ((flags & 0x800) == 0 && name.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0)
            {
                throw new TargetException(DiagnosticCodes.T003ArchiveRejected, "archive entry name is not marked as UTF-8");
            }

            entries.Add(new ZipEntryRecord(Encoding.UTF8.GetString(name), flags, method, U32(at + 16), U32(at + 20), U32(at + 24), U32(at + 38), local));
            at = next;
        }

        if (at != stop)
        {
            throw Reject("has a central directory that does not hold the entries it declares");
        }

        return (entries, offset);
    }

    /// <summary>
    /// Contract §3: an entry's bytes. Only stored (0) and deflated (8) entries without the encryption flag are read; the
    /// result is exactly the declared size and matches the declared CRC-32. Anything else throws
    /// <see cref="InvalidDataException"/>, so the entry is unreadable (P002). A deflated entry never inflates far past
    /// its declared size.
    /// </summary>
    internal static byte[] ReadEntry(byte[] buf, ZipEntryRecord entry, long directoryOffset)
    {
        if ((entry.Flags & 1) != 0)
        {
            throw new InvalidDataException("the entry is encrypted");
        }

        if (entry.Method is not (0 or 8))
        {
            throw new InvalidDataException($"the entry uses compression method {entry.Method}; only stored (0) and deflated (8) are read");
        }

        var nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan((int)entry.LocalOffset + 26, 2));
        var extraLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan((int)entry.LocalOffset + 28, 2));
        var start = entry.LocalOffset + 30 + nameLength + extraLength;
        if (start + entry.CompressedSize > directoryOffset)
        {
            throw new InvalidDataException("the entry's data runs past the central directory");
        }

        var compressed = new ReadOnlyMemory<byte>(buf, (int)start, (int)entry.CompressedSize);
        byte[] data;
        if (entry.Method == 0)
        {
            data = compressed.ToArray();
        }
        else
        {
            using var inflater = new DeflateStream(new MemoryStream(buf, (int)start, (int)entry.CompressedSize, writable: false), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = inflater.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (output.Length + read > entry.Size)
                {
                    throw new InvalidDataException("the entry inflates past its declared size");
                }

                output.Write(chunk, 0, read);
            }

            data = output.ToArray();
        }

        if (data.Length != entry.Size)
        {
            throw new InvalidDataException($"the entry holds {data.Length} bytes, not the {entry.Size} its header declares");
        }

        if (Crc32.Compute(data) != entry.Crc)
        {
            throw new InvalidDataException("the entry fails its CRC-32 check");
        }

        return data;
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
        // Contract §3: every record is checked, and every limit applied, before any entry is read.
        var (records, directoryOffset) = PackageSources.ReadDirectory(archive);
        Methods = records.GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Method, StringComparer.Ordinal);
        if (records.Count > limits.MaxEntries)
        {
            throw new TargetException(DiagnosticCodes.T003ArchiveRejected,
                $"archive has {records.Count} entries, over the {limits.MaxEntries} limit");
        }

        long total = 0;
        var files = new List<(string Path, PackageSources.ZipEntryRecord Record)>();
        foreach (var entry in records)
        {
            var name = entry.Name;
            PackageSources.AssertSafeArchivePath(name);
            total += entry.Size;
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
                HiddenEntries.Add(path);
                continue;
            }

            var mode = (int)(entry.ExternalAttributes >>> 16) & TypeMask;
            var kind = mode switch
            {
                SymbolicLink => PackageEntryKind.Symlink,
                0 or RegularFile => PackageEntryKind.File,
                _ => PackageEntryKind.Other,
            };
            _entries.Add(new PackageEntry(path, kind, entry.Size));
            if (kind == PackageEntryKind.File)
            {
                files.Add((path, entry));
            }
        }

        foreach (var (path, entry) in files)
        {
            // Contract §3: an entry is exactly its declared size, stored or deflated; anything else is unreadable (P002).
            try
            {
                _files[path] = PackageSources.ReadEntry(archive, entry, directoryOffset);
            }
            catch (InvalidDataException ex)
            {
                _unreadable[path] = ex.Message;
            }
        }

        _entries.Sort(PackageSources.CompareOrdinal);
    }

    public string Kind => "archive";

    public string? Location { get; }

    /// <summary>Each entry's Zip compression method (0 stored, 8 deflated), by stored name.</summary>
    internal IReadOnlyDictionary<string, int> Methods { get; }

    /// <summary>Hidden entries, which are not part of a package but make an envelope invalid (encrypted packages §2).</summary>
    internal List<string> HiddenEntries { get; } = [];

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
