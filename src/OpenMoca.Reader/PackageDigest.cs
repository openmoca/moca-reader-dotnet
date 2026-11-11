using System.Security.Cryptography;
using System.Text;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sources;

namespace OpenMoca.Reader;

/// <summary>
/// The package digest, <c>moca-digest-v2</c> (package spec §6.4): the SHA-256 of a BagIt-style payload
/// manifest (RFC 8493 <c>manifest-sha256.txt</c>) listing every file except <c>attestations/</c>.
/// </summary>
/// <remarks>
/// The digest is computed, never declared inside the package (§6.5): a digest a package states about
/// itself proves nothing, so it is only compared with a reference held elsewhere.
/// </remarks>
public static class PackageDigest
{
    /// <summary>The algorithm name.</summary>
    public const string Algorithm = "moca-digest-v2";

    /// <summary>The package manifest file.</summary>
    public const string ManifestFile = "moca.json";

    /// <summary>The attestations directory, outside the digest.</summary>
    public const string AttestationsDirectory = "attestations";

    /// <summary>Lowercase hexadecimal SHA-256.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>True when a path is in the attestations area, which the digest excludes.</summary>
    public static bool IsAttestationPath(string path) =>
        // Only entries under the attestations/ directory; a top-level file named `attestations` is
        // ordinary package content and is digested.
        path.StartsWith(AttestationsDirectory + "/", StringComparison.Ordinal);

    /// <summary>
    /// Checks every entry and returns the regular files that make up the package, keyed by their
    /// NFC-normalised path (package spec §6.2-§6.3). Symbolic links, unreadable entries and paths that
    /// collide after NFC normalisation or case folding make the package's identity ambiguous.
    /// </summary>
    /// <returns><c>ok</c> is false when any entry was refused; <c>files</c> maps NFC path to stored path.</returns>
    public static (bool Ok, Dictionary<string, string> Files) PackageFiles(IPackageSource source, DiagnosticBag diagnostics)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var folded = new Dictionary<string, string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var entry in source.List())
        {
            if (entry.Kind is PackageEntryKind.Symlink or PackageEntryKind.Other)
            {
                var what = entry.Kind == PackageEntryKind.Symlink ? "symbolic link" : "non-regular file";
                diagnostics.Add(DiagnosticCodes.P001UnsupportedEntry,
                    $"\"{entry.Path}\" is a {what}; packages may contain regular files only", entry.Path);
                ok = false;
                continue;
            }

            if (entry.Kind == PackageEntryKind.Unreadable)
            {
                diagnostics.Add(DiagnosticCodes.P002UnreadableEntry, $"\"{entry.Path}\" could not be read", entry.Path);
                ok = false;
                continue;
            }

            var nfc = UnicodeText.Normalize(entry.Path, NormalizationForm.FormC);
            var key = UnicodeText.SimpleLowercase(nfc);
            if (folded.TryGetValue(key, out var other))
            {
                diagnostics.Add(DiagnosticCodes.P003PathCollision,
                    $"\"{entry.Path}\" collides with \"{other}\" after NFC normalisation or case folding", entry.Path);
                ok = false;
                continue;
            }

            folded[key] = entry.Path;
            files[nfc] = entry.Path;
        }

        return (ok, files);
    }

    /// <summary>A path as it appears in a BagIt manifest line: <c>%</c>, CR and LF percent-encoded (RFC 8493 §2.1.3).</summary>
    public static string ManifestPath(string path) =>
        path.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>
    /// The payload manifest: one <c>&lt;hex&gt;  &lt;path&gt;\n</c> line per file, sorted by the UTF-8
    /// bytes of the path.
    /// </summary>
    public static string ManifestText(IReadOnlyDictionary<string, string> fileDigests)
    {
        var lines = fileDigests
            .OrderBy(kv => Encoding.UTF8.GetBytes(kv.Key), Utf8ByteComparer.Instance)
            .Select(kv => $"{kv.Value}  {ManifestPath(kv.Key)}\n");
        return string.Concat(lines);
    }

    /// <summary>Computes <c>moca-digest-v2</c> over the files <see cref="PackageFiles"/> returned.</summary>
    /// <param name="files">NFC path to stored path.</param>
    /// <param name="read">Returns a file's bytes by NFC path.</param>
    public static (string Digest, Dictionary<string, string> FileDigests, string Manifest) Compute(
        IReadOnlyDictionary<string, string> files, Func<string, byte[]> read)
    {
        var fileDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var nfc in files.Keys)
        {
            if (IsAttestationPath(nfc))
            {
                continue;
            }

            fileDigests[nfc] = Sha256Hex(read(nfc));
        }

        var manifest = ManifestText(fileDigests);
        return ($"sha256:{Sha256Hex(Encoding.UTF8.GetBytes(manifest))}", fileDigests, manifest);
    }

    private sealed class Utf8ByteComparer : IComparer<byte[]>
    {
        public static readonly Utf8ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
