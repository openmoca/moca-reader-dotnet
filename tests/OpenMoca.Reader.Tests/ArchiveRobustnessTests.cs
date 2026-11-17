using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sources;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Damaged input is reported, never thrown (contract §3). Each case was found by fuzzing (fuzz/).</summary>
public sealed class ArchiveRobustnessTests
{
    private const string Manifest = "{\"id\":\"https://example.com/damaged\",\"version\":\"1.0.0\",\"title\":\"Damaged\"}";

    // Frontmatter on which YamlDotNet's scanner throws InvalidOperationException rather than a YamlException.
    private const string BadYaml =
        "LS0tCnR5cHM6CltbW1vg39/cLSBpaHR0cHM6Ly9leGFtcGxlLm9yZy9oYW5kYm8gkHJpbWFyeQotLS12ZFJldpWaiPUgICAgICByb2xlOiBwcmltYXJ5Ci0t//8tdlJlc6ZvbHZlQW5k//9SZXZpZXcKICAgICAgcm9sZTogcHJpIHByaW1hcnkKLS0tCgojIFJlb3JraW5nIGRhAAB5cy4K";

    [Fact]
    public async Task EndRecordCountingMoreEntriesThanTheCentralDirectory_RejectsTheArchive()
    {
        var archive = Archive(("moca.json", Manifest), ("content/a.md", "---\ntype: Note\ntitle: A\n---\n\nA.\n"));
        var end = archive.AsSpan().LastIndexOf("PK\u0005\u0006"u8);
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(end + 8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(end + 10), 3);

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(archive), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Valid);
        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task FrontmatterTheYamlScannerCannotRead_IsInvalidFrontmatter()
    {
        var source = new MemorySource(new Dictionary<string, byte[]>
        {
            ["moca.json"] = Encoding.UTF8.GetBytes(Manifest),
            ["content/a.md"] = Convert.FromBase64String(BadYaml),
        });

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Valid);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.C002FrontmatterInvalid);
    }

    private static byte[] Archive(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return buffer.ToArray();
    }

    private sealed class MemorySource(Dictionary<string, byte[]> files) : IPackageSource
    {
        public string Kind => "host";

        public string? Location => null;

        public IReadOnlyList<PackageEntry> List() => [.. files.Keys.Select(p => new PackageEntry(p))];

        public byte[] Read(string path) => files[path];
    }
}
