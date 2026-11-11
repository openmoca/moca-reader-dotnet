using System.IO.Compression;
using System.Text;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sources;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Reader rules the conformance corpus cannot express as fixtures, because Git and some file systems
/// cannot store them (conformance/README.md): symbolic links, unreadable entries, and names that differ
/// only in Unicode normalisation or case. Also the archive and host-source safety rules of contract §3.
/// </summary>
public sealed class ReaderRulesTests : IDisposable
{
    private const string Manifest = """{ "id": "https://example.com/tests/rules", "version": "1.0.0", "title": "Rules" }""";
    private const string Node = "---\ntype: Note\ntitle: Hello\n---\n\nHello.\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-reader-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ReadPackage_WhenTargetMissing_ReportsT001()
    {
        var result = await MocaReader.ReadPackageAsync(Path.Combine(_dir, "nope"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.T001TargetNotFound);
        Assert.False(result.Valid);
    }

    [Fact]
    public async Task ReadPackage_WhenDirectoryHasNoManifest_ReportsT002NotT001()
    {
        var result = await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([DiagnosticCodes.T002ManifestNotFound], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task ReadPackage_WhenPackageHasSymlink_ReportsP001AndNoDigest()
    {
        WritePackage(_dir);
        File.CreateSymbolicLink(Path.Combine(_dir, "content", "link.md"), Path.Combine(_dir, "content", "hello.md"));

        var result = await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.P001UnsupportedEntry);
        Assert.Null(result.Digest);
        Assert.False(result.Valid);
    }

    [Fact]
    public async Task ReadPackage_WhenNamesDifferOnlyInNormalisation_ReportsP003()
    {
        // "é" precomposed (NFC) and "e" + combining acute (NFD) are the same path after NFC.
        var source = new MemorySource(new Dictionary<string, string>
        {
            ["moca.json"] = Manifest,
            ["content/café.md"] = Node,
            ["content/café.md"] = Node,
        });

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.P003PathCollision);
        Assert.Null(result.Digest);
    }

    [Fact]
    public async Task ReadPackage_WhenNamesDifferOnlyInCase_ReportsP003()
    {
        var source = new MemorySource(new Dictionary<string, string>
        {
            ["moca.json"] = Manifest,
            ["content/Hello.md"] = Node,
            ["content/hello.md"] = Node,
        });

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.P003PathCollision);
    }

    [Theory]
    [InlineData("content/\u0130.md", "content/i.md", true)]
    [InlineData("content/\u0130.md", "content/i\u0307.md", false)]
    public async Task ReadPackage_LowerCasesPathsBySimpleMapping(string first, string second, bool collides)
    {
        // Package spec §6.3: the simple lowercase mapping per code point. İ is i, not i + U+0307.
        var source = new MemorySource(new Dictionary<string, string> { ["moca.json"] = Manifest, [first] = Node, [second] = Node });

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(collides, result.Diagnostics.Any(d => d.Code == DiagnosticCodes.P003PathCollision));
    }

    [Fact]
    public async Task ReadPackage_WhenSourceListsUnreadableEntry_ReportsP002()
    {
        var source = new MemorySource(new Dictionary<string, string> { ["moca.json"] = Manifest, ["content/hello.md"] = Node },
            extra: [new PackageEntry("content/locked", PackageEntryKind.Unreadable)]);

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.P002UnreadableEntry);
        Assert.False(result.Valid);
    }

    [Fact]
    public async Task ReadPackage_WhenArchiveHasDotDotEntry_ReportsT003()
    {
        var archive = Zip(("moca.json", Manifest), ("content/../../evil.md", Node));

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(archive), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task ReadPackage_WhenArchiveOverEntryLimit_ReportsT003()
    {
        var archive = Zip(("moca.json", Manifest), ("content/hello.md", Node), ("content/other.md", Node));
        var options = new ReadOptions { Limits = ReaderLimits.Default with { MaxEntries = 2 } };

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(archive), options, TestContext.Current.CancellationToken);

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task ReadPackage_WhenSameBytesAsDirectoryArchiveAndHostSource_HasSameDigest()
    {
        WritePackage(_dir);
        var files = new Dictionary<string, string> { ["moca.json"] = Manifest, ["content/hello.md"] = Node };
        var token = TestContext.Current.CancellationToken;

        var fromDirectory = await MocaReader.ReadPackageAsync(_dir, cancellationToken: token);
        var fromArchive = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(Zip([.. files.Select(kv => (kv.Key, kv.Value))])), cancellationToken: token);
        var fromHost = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(new MemorySource(files)), cancellationToken: token);

        Assert.True(fromDirectory.Valid);
        Assert.NotNull(fromDirectory.Digest);
        Assert.Equal(fromDirectory.Digest, fromArchive.Digest);
        Assert.Equal(fromDirectory.Digest, fromHost.Digest);
    }

    [Fact]
    public async Task ReadPackage_WhenHostSourceUsed_OnlyReadsListedPaths()
    {
        var source = new MemorySource(new Dictionary<string, string> { ["moca.json"] = Manifest, ["content/hello.md"] = Node });

        await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(source.Reads, path => Assert.Contains(path, source.Listed));
    }

    [Fact]
    public async Task ReadPackage_WhenStrict_PromotesWarningsWithoutChangingCodes()
    {
        var source = new MemorySource(new Dictionary<string, string>
        {
            ["moca.json"] = Manifest,
            ["content/hello.md"] = "---\ntype: Note\n---\n\nSee [missing](missing.md).\n",
        });

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), new ReadOptions { Strict = true }, TestContext.Current.CancellationToken);

        var link = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.C007LinkUnresolved);
        Assert.Equal(DiagnosticSeverity.Error, link.Severity);
        Assert.False(result.Valid);
    }

    [Fact]
    public async Task ReadPackage_WhenFrontmatterHasTimestamp_KeepsItAsWritten()
    {
        var source = new MemorySource(new Dictionary<string, string>
        {
            ["moca.json"] = Manifest,
            ["content/hello.md"] = "---\ntype: Note\nstale_after: 2027-02-14\n---\n\nHello.\n",
        });

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(source), cancellationToken: TestContext.Current.CancellationToken);

        var fm = result.Nodes.Single().Representations.Single().Frontmatter;
        Assert.Equal("2027-02-14", fm["stale_after"]!.GetValue<string>());
    }

    private static void WritePackage(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "moca.json"), Manifest);
        File.WriteAllText(Path.Combine(dir, "content", "hello.md"), Node);
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A host source in memory, recording what the Reader asks for.</summary>
    private sealed class MemorySource(Dictionary<string, string> files, IReadOnlyList<PackageEntry>? extra = null) : IPackageSource
    {
        public List<string> Reads { get; } = [];

        public HashSet<string> Listed { get; } = [];

        public string Kind => "host";

        public string? Location => null;

        public IReadOnlyList<PackageEntry> List()
        {
            var entries = files.Select(kv => new PackageEntry(kv.Key, PackageEntryKind.File, Encoding.UTF8.GetByteCount(kv.Value)))
                .Concat(extra ?? [])
                .ToList();
            Listed.UnionWith(entries.Select(e => e.Path));
            return entries;
        }

        public byte[] Read(string path)
        {
            Reads.Add(path);
            return Encoding.UTF8.GetBytes(files[path]);
        }
    }
}
