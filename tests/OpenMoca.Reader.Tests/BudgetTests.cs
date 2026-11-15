using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Reader contract §3: work stays bounded on hostile compositions and structures. The same tests as
/// moca-reader-typescript's test/budgets.test.js. Each sets a time budget, and the composition tests a memory budget,
/// that the quadratic versions broke by orders of magnitude.
/// </summary>
public sealed class BudgetTests : IDisposable
{
    private const string Id = "https://example.com/budget";

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-budget-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ADiamondOfSharedMembers_ReadsEachMemberOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const int n = 45;
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        Func<MemberReference, CancellationToken, ValueTask<PackageTarget?>> resolve =
            (m, _) => ValueTask.FromResult(paths.TryGetValue(m.Id, out var p) ? (PackageTarget?)p : null);

        var leaves = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var files = new Dictionary<string, string> { ["content/a.md"] = Node($"Leaf {i}") };
            if (i == 0)
            {
                files["sources/big.txt"] = new string('x', 2 * 1024 * 1024); // read 2,025 times without sharing
            }

            var dir = Package($"pkgs/leaf-{i}", new { id = $"{Id}/leaf-{i}", title = $"Leaf {i}" }, files);
            paths[$"{Id}/leaf-{i}"] = dir;
            leaves.Add(Pin($"{Id}/leaf-{i}", (await MocaReader.ReadPackageAsync(dir, cancellationToken: ct)).Digest!));
        }

        var mids = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var dir = Package($"pkgs/mid-{i}", new { id = $"{Id}/mid-{i}", title = $"Mid {i}", members = Members(leaves) }, []);
            paths[$"{Id}/mid-{i}"] = dir;
            mids.Add(Pin($"{Id}/mid-{i}", (await MocaReader.ReadPackageAsync(dir, new ReadOptions { ResolveMember = resolve }, ct)).Digest!));
        }

        var top = Package("top", new { id = $"{Id}/top", title = "Top", members = Members(mids) }, []);
        var calls = 0;
        Func<MemberReference, CancellationToken, ValueTask<PackageTarget?>> counting = (m, c) =>
        {
            Interlocked.Increment(ref calls);
            return resolve(m, c);
        };

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var result = await MocaReader.ReadPackageAsync(top, new ReadOptions { ResolveMember = counting }, ct);
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        Assert.True(result.Valid);
        Assert.Equal(2 * n, calls); // each distinct member is resolved once
        Assert.All(result.Members, m => Assert.Equal("resolved", m.Status));
        Assert.All(result.Members[0].Result!.Members, m => Assert.Equal("resolved", m.Status));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"read in {watch.Elapsed}");
        Assert.True(allocated < 2L * 1024 * 1024 * 1024, $"allocated {allocated / 1048576} MiB");

        // The byte budget: past it, no further member is resolved (R001).
        var capped = await MocaReader.ReadPackageAsync(top, new ReadOptions { ResolveMember = resolve, Limits = new ReaderLimits { MaxMemberBytes = 1024 * 1024 } }, ct);
        Assert.Contains(capped.Diagnostics, d => d.Code == DiagnosticCodes.R001MemberUnresolved && d.Message.Contains("bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALongReadingOrderOverASharedSubtree_ResolvesInLinearTime()
    {
        var ct = TestContext.Current.CancellationToken;
        const int n = 3000;
        var ttl = new StringBuilder("@prefix skos: <http://www.w3.org/2004/02/skos/core#> .\n@prefix dcterms: <http://purl.org/dc/terms/> .\n");
        ttl.Append($"<{Id}/order#list> a skos:OrderedCollection ; skos:memberList ( ")
            .AppendJoin(' ', Enumerable.Range(0, n).Select(i => $"<{Id}/order#m{i}>")).Append(" ) .\n");
        for (var i = 0; i < n; i++)
        {
            ttl.Append($"<{Id}/order#m{i}> a skos:Concept ; dcterms:hasPart <{Id}/order#c0> .\n");
        }

        for (var i = 0; i < n; i++)
        {
            ttl.Append($"<{Id}/order#c{i}> a skos:Concept").Append(i + 1 < n ? $" ; dcterms:hasPart <{Id}/order#c{i + 1}>" : "").Append(" .\n");
        }

        var dir = Package("order", new { id = $"{Id}/order", title = "Order", readingOrder = $"{Id}/order#list" },
            new Dictionary<string, string> { ["content/a.md"] = Node("A"), ["structure.ttl"] = ttl.ToString() });

        var watch = Stopwatch.StartNew();
        var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: ct);
        watch.Stop();

        Assert.True(result.Valid);
        var positions = result.ResolvedReadingOrder!.Positions;
        Assert.Equal(0, positions[$"{Id}/order#c{n - 1}"]); // the first member that reaches a concept places it
        Assert.Equal(n - 1, positions[$"{Id}/order#m{n - 1}"]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"read in {watch.Elapsed}");
    }

    [Fact]
    public async Task BindingsByComposition_StayLinearInWorkAndStateSize()
    {
        var ct = TestContext.Current.CancellationToken;
        const int n = 2000;
        var member = Package("pkgs/member", new { id = $"{Id}/member", title = "Member" },
            Enumerable.Range(0, n).ToDictionary(i => $"content/n{i}.md", i => Node($"Node {i}")));
        var pin = Pin($"{Id}/member", (await MocaReader.ReadPackageAsync(member, cancellationToken: ct)).Digest!);

        // Every node is bound, under a chain of n concepts: n × n ancestors if each node's were listed.
        var ttl = new StringBuilder("@prefix skos: <http://www.w3.org/2004/02/skos/core#> .\n");
        for (var i = 0; i < n; i++)
        {
            ttl.Append($"<{Id}/composer#c{i}> a skos:Concept").Append(i > 0 ? $" ; skos:broader <{Id}/composer#c{i - 1}>" : "").Append(" .\n");
        }

        for (var i = 0; i < n; i++)
        {
            ttl.Append($"<{Id}/member#n{i}.md> a skos:Concept ; skos:broader <{Id}/composer#c{n - 1}> .\n");
        }

        var composer = Package("composer", new { id = $"{Id}/composer", title = "Composer", members = Members([pin]) },
            new Dictionary<string, string> { ["structure.ttl"] = ttl.ToString() });
        Func<MemberReference, CancellationToken, ValueTask<PackageTarget?>> resolve =
            (_, _) => ValueTask.FromResult<PackageTarget?>(member);

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var result = await MocaReader.ReadPackageAsync(composer, new ReadOptions { ResolveMember = resolve }, ct);
        Assert.True(result.Valid);
        var size = JsonSerializer.Serialize(PackageState.From(result)).Length;
        var library = new Library().Add(result);
        var records = await new MocaSearch(library, new LexicalBackend(library)).SearchAsync("node", new SearchOptions { Scope = $"{Id}/composer#c0", Limit = 100 }, ct);
        var listed = library.Structure.Nodes($"{Id}/composer#c0");
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        Assert.Equal(100, records.Count); // search under the top of the chain finds bound nodes
        Assert.Equal(n, listed.Count); // nodes() lists every bound node under the top of the chain
        Assert.True(size < 1024 * 1024, $"package state is {size} bytes");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"read and searched in {watch.Elapsed}");
        Assert.True(allocated < 2L * 1024 * 1024 * 1024, $"allocated {allocated / 1048576} MiB");
    }

    private static string Node(string title) => $"---\ntype: Note\ntitle: {title}\n---\n# {title}\n";

    private static string Pin(string id, string digest) => JsonSerializer.Serialize(new { id, version = "1.0.0", digest });

    private static JsonElement Members(IEnumerable<string> pins) => JsonDocument.Parse($"[{string.Join(",", pins)}]").RootElement;

    private string Package(string name, object manifest, Dictionary<string, string> files)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        var fields = JsonSerializer.SerializeToNode(manifest)!.AsObject();
        fields["mocaVersion"] = "0.7";
        fields["version"] = "1.0.0";
        fields["language"] = "en";
        File.WriteAllText(Path.Combine(dir, "moca.json"), fields.ToJsonString());
        foreach (var (path, body) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }

        return dir;
    }
}
