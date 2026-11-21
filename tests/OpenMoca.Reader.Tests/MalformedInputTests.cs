using System.Text;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Sources;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §11: a read never throws for what a package holds. Text that .NET would refuse later is refused where it is
/// read, and anything unexpected fails closed as one P002.
/// </summary>
public sealed class MalformedInputTests
{
    private const string Node = "---\ntype: Note\ntitle: Hello\n---\n\nHello.\n";

    private static Task<PackageResult> Read(Dictionary<string, string> files) =>
        MocaReader.ReadPackageAsync(PackageTarget.FromSource(new MemorySource(files)), cancellationToken: TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("\"a\"", false)]
    [InlineData("\"\\ud83d\\ude00\"", false)] // a pair
    [InlineData("\"\\u00e9\\n\\\\ud800\"", false)] // an escaped backslash, then text
    [InlineData("\"\\ud800\"", true)]
    [InlineData("\"\\udc00\"", true)]
    [InlineData("\"\\ud800x\"", true)]
    [InlineData("\"\\ud800\\n\"", true)]
    [InlineData("\"\\ud800\\ud800\"", true)]
    public void HasUnpairedSurrogateEscape_FindsOnlyUnpairedHalves(string json, bool expected) =>
        Assert.Equal(expected, JsonValues.HasUnpairedSurrogateEscape(json));

    [Fact]
    public async Task ReadPackage_WhenManifestHasAnUnpairedSurrogate_ReportsM001()
    {
        var result = await Read(new()
        {
            ["moca.json"] = """{ "id": "https://example.com/tests/x", "version": "1.0.0", "title": "A \ud800 title" }""",
            ["content/a.md"] = Node,
        });

        Assert.Equal([DiagnosticCodes.M001ManifestInvalidJson], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void ParseAttestation_WithAnUnpairedSurrogate_IsMalformed()
    {
        var (parsed, reason) = AttestationFormats.Parse("attestations/a.dsse.json", """{ "payloadType": "x\ud800", "payload": "", "signatures": [] }"""u8.ToArray());

        Assert.Null(parsed);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task ReadPackage_WhenImageSourceIsANoncharacter_DoesNotThrow()
    {
        var result = await Read(new()
        {
            ["moca.json"] = """{ "id": "https://example.com/tests/x", "version": "1.0.0", "title": "X", "images": [{ "src": "media/a\uFFFE.png", "type": "image/png", "role": "icon" }] }""",
            ["content/a.md"] = Node,
        });

        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task ReadPackage_WhenTheSourceFailsUnexpectedly_ReportsP002AndDoesNotThrow()
    {
        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromSource(new FailingSource()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([DiagnosticCodes.P002UnreadableEntry], result.Diagnostics.Select(d => d.Code));
        Assert.False(result.Valid);
        Assert.DoesNotContain("secret", result.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPackage_WhenCancelled_StillThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MocaReader.ReadPackageAsync(PackageTarget.FromSource(new CancellingSource(cts.Token)), cancellationToken: cts.Token));
    }

    private sealed class MemorySource(Dictionary<string, string> files) : IPackageSource
    {
        public string Kind => "host";

        public string? Location => null;

        public IReadOnlyList<PackageEntry> List() =>
            files.Select(kv => new PackageEntry(kv.Key, PackageEntryKind.File, Encoding.UTF8.GetByteCount(kv.Value))).ToList();

        public byte[] Read(string path) => Encoding.UTF8.GetBytes(files[path]);
    }

    private sealed class FailingSource : IPackageSource
    {
        public string Kind => "host";

        public string? Location => null;

        public IReadOnlyList<PackageEntry> List() => [new PackageEntry("moca.json", PackageEntryKind.File, 2)];

        public byte[] Read(string path) => throw new NotSupportedException("secret host detail");
    }

    private sealed class CancellingSource(CancellationToken token) : IPackageSource
    {
        public string Kind => "host";

        public string? Location => null;

        public IReadOnlyList<PackageEntry> List()
        {
            token.ThrowIfCancellationRequested();
            return [];
        }

        public byte[] Read(string path) => [];
    }
}
