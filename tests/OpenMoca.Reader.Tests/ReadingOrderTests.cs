using System.Text.Json;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Package spec §4.10 and Reader contract §9: readingOrder and upTo. The corpus covers the rules; these cover what it
/// cannot.
/// </summary>
public sealed class ReadingOrderTests : IDisposable
{
    private const string B = "https://example.org/c#";
    private const string Head = "@prefix skos: <http://www.w3.org/2004/02/skos/core#> .\n@prefix c: <https://example.org/c#> .\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-reading-order-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Node(params string[] concepts) =>
        "---\ntype: Note\ntitle: A\n"
        + (concepts.Length > 0 ? "moca:\n  concepts:\n" + string.Concat(concepts.Select(c => $"    - iri: {B}{c}\n")) : "")
        + "---\n\n# A\n\nText.\n";

    private string Package(string structure, params (string Path, string Text)[] nodes)
    {
        File.WriteAllText(Path.Combine(_dir, "moca.json"), $$"""{ "id": "https://example.com/t", "version": "1.0.0", "title": "T", "readingOrder": "{{B}}Path" }""");
        File.WriteAllText(Path.Combine(_dir, "structure.ttl"), Head + structure);
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        foreach (var (path, text) in nodes.Length > 0 ? nodes : [("a.md", Node())])
        {
            File.WriteAllText(Path.Combine(_dir, "content", path), text);
        }

        return _dir;
    }

    [Fact]
    public async Task AnEmptyList_OrAListWithNoCollectionType_DoesNotResolve()
    {
        var empty = await MocaReader.ReadPackageAsync(Package("c:Path a skos:OrderedCollection ; skos:memberList () .\n"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([DiagnosticCodes.M014ReadingOrderUnresolved], empty.Diagnostics.Select(d => d.Code));
        Assert.Null(empty.ReadingOrder);

        var untyped = await MocaReader.ReadPackageAsync(Package("c:A a skos:Concept .\nc:Path skos:memberList ( c:A ) .\n"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(DiagnosticCodes.M014ReadingOrderUnresolved, untyped.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task ANarrowerCycle_DoesNotStopPlacement_AndEachConceptKeepsItsFirstPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await MocaReader.ReadPackageAsync(Package(
            "c:A a skos:Concept ; skos:narrower c:B .\nc:B a skos:Concept ; skos:narrower c:A .\nc:C a skos:Concept .\n"
            + "c:Path a skos:OrderedCollection ; skos:memberList ( c:A c:C c:B ) .\n",
            ("a.md", Node("A")), ("b.md", Node("B")), ("c.md", Node("C"))), cancellationToken: ct);
        Assert.Equal($"{B}Path", result.ReadingOrder);
        var library = new Library().Add(result);
        // B is a member at index 2, but sits under A at index 0, so it passes at upTo A.
        var records = await new MocaSearch(library, new LexicalBackend(library)).SearchAsync("text", new SearchOptions { Limit = 10, UpTo = $"{B}A" }, ct);
        Assert.Equal(["a.md", "b.md"], records.Select(r => r.Node.Path).Order());
    }

    [Fact]
    public async Task APackageState_CarriesTheReadingOrderAsJson_AndAStoreSearchWithPushdownAppliesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await MocaReader.ReadPackageAsync(Package(
            "c:A a skos:Concept .\nc:B a skos:Concept .\nc:Off a skos:Concept .\nc:Path a skos:OrderedCollection ; skos:memberList ( c:A c:B ) .\n",
            ("a.md", Node("A")), ("b.md", Node("B")), ("p.md", Node("Off"))), cancellationToken: ct);
        var json = JsonSerializer.Serialize(PackageState.From(result));
        using (var doc = JsonDocument.Parse(json))
        {
            var order = doc.RootElement.GetProperty("readingOrder");
            Assert.Equal($"{B}Path", order.GetProperty("iri").GetString());
            Assert.Equal(1, order.GetProperty("positions").GetProperty($"{B}B").GetInt32());
        }

        var state = JsonSerializer.Deserialize<PackageState>(json)!;
        var store = await new MemoryStoreBackend(filterPushdown: true).IngestAsync(new Library().Add(result), ct);
        // With pushdown the Reader would fetch only the limit; upTo has no pushdown filter, so it must fetch more.
        var records = await new MocaSearch([state], () => DateTimeOffset.UtcNow, store).SearchAsync("text", new SearchOptions { Limit = 2, UpTo = $"{B}A" }, ct);
        Assert.Equal(["a.md", "p.md"], records.Select(r => r.Node.Path).Order());
    }
}
