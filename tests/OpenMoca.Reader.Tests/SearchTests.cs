using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sidecars;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Search behaviour the corpus does not pin down on its own.</summary>
public sealed class SearchTests
{
    /// <summary>
    /// The same vectors as the reference Reader's tests: every Reader's built-in lexical backend must tokenise
    /// identically.
    /// </summary>
    public static TheoryData<string, string, string[]> TokenVectors()
    {
        var data = new TheoryData<string, string, string[]>();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "token-vectors.json");
        foreach (var v in System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            data.Add(v!["why"]!.GetValue<string>(), v["text"]!.GetValue<string>(), [.. v["tokens"]!.AsArray().Select(t => t!.GetValue<string>())]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TokenVectors))]
    public void Tokens_MatchTheReferenceReader(string why, string text, string[] expected)
    {
        Assert.True(expected.SequenceEqual(Bm25.Tokens(text)), $"{why}: {string.Join(" ", Bm25.Tokens(text))}");
    }

    [Fact]
    public void Tokens_LoneSurrogate_SeparatesWordsAndNeverThrows()
    {
        Assert.Equal(["a", "b"], Bm25.Tokens("a\uD800b"));
    }

    [Fact]
    public async Task LexicalSearch_WithoutALocale_FindsContentInEveryLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = new Library().Add(await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct));
        var search = new MocaSearch(library, new LexicalBackend(library));

        var hit = Assert.Single(await search.SearchAsync("remboursement", new SearchOptions { Limit = 1 }, ct));
        var english = await search.SearchAsync("remboursement", new SearchOptions { Locale = "en" }, ct);

        Assert.Equal(("refund-window.md", "fr"), (hit.Node.Path, hit.Node.Locale));
        Assert.Empty(english);
    }

    [Fact]
    public async Task Search_SidecarChunksWithoutText_SearchesThePackageText()
    {
        var ct = TestContext.Current.CancellationToken;
        var package = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct);
        var bound = SidecarBinder.Bind(Path.Combine(MocaSpec.Examples, "sidecars", "support-kb.moca.idx"), package);
        Assert.True(bound.Usable);
        var library = new Library().Add(package, bound with { Chunks = [.. bound.Chunks.Select(c => c with { Text = null })] });
        var options = new SearchOptions { Limit = 1 };

        var lexical = await new MocaSearch(library, new LexicalBackend(library)).SearchAsync("domestic express", options, ct);
        var store = await new MemoryStoreBackend().IngestAsync(library, ct);
        var stored = await new MocaSearch([PackageState.From(package)], library.Clock, store).SearchAsync("domestic express", options, ct);

        Assert.Equal("shipping-estimates.md", Assert.Single(lexical).Node.Path);
        Assert.Contains("Domestic", lexical[0].Text, StringComparison.Ordinal);
        Assert.Equal("shipping-estimates.md", Assert.Single(stored).Node.Path);
        Assert.Contains("Domestic", stored[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DenseBackend_PackagesWithoutVectors_AreReportedAndNotSearched()
    {
        var ct = TestContext.Current.CancellationToken;
        var kb = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct);
        var lexicalSidecar = SidecarBinder.Bind(Path.Combine(MocaSpec.Examples, "sidecars", "support-kb.moca.idx"), kb);
        var minimal = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "minimal"), cancellationToken: ct);
        var library = new Library().Add(kb, lexicalSidecar).Add(minimal);

        var dense = new DenseBackend(library, new FixedEmbedder());
        var results = await new MocaSearch(library, dense).SearchAsync("refund", cancellationToken: ct);

        Assert.Equal([DiagnosticCodes.S007NoVectors, DiagnosticCodes.S007NoVectors], dense.Diagnostics.Select(d => d.Code));
        Assert.Empty(results);
    }

    [Fact]
    public async Task Embedders_GetARole_AndDeclaredPrefixesAreComparedNeverApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = new Library().Add(await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct));
        var recording = new FixedEmbedder();
        var store = await new MemoryStoreBackend(recording).IngestAsync(library, ct);
        await new MocaSearch(library.Packages.Select(p => PackageState.From(p.Result)), library.Clock, store).SearchAsync("refund", cancellationToken: ct);

        var model = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "fixed", ["dimensions"] = 2, ["queryPrefix"] = "query: ", ["documentPrefix"] = "passage: ",
        };

        Assert.Equal([EmbeddingRole.Document, EmbeddingRole.Query], recording.Roles);
        Assert.Null(DenseBackend.ModelMismatch(new FixedEmbedder("query: ", "passage: "), model, 2));
        // Contract §9.3 (0.6): a prefix declared on one side only is a mismatch.
        Assert.Contains("queryPrefix", DenseBackend.ModelMismatch(new FixedEmbedder(), model, 2), StringComparison.Ordinal);
        Assert.Contains("queryPrefix", DenseBackend.ModelMismatch(new FixedEmbedder("search_query: ", "passage: "), model, 2), StringComparison.Ordinal);
        Assert.Contains("documentPrefix", DenseBackend.ModelMismatch(new FixedEmbedder("query: ", "search_document: "), model, 2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HybridBackend_FusesRanks_WeightedByTheHost()
    {
        var ct = TestContext.Current.CancellationToken;
        static SearchHit Hit(string node) => new("sha256:x", $"p#{node}", node, null, new Citations.CitationSpan(0, 1), 9);
        var lexical = new FixedBackend(BackendFeatures.Lexical | BackendFeatures.FilterPushdown, [Hit("a.md"), Hit("b.md"), Hit("c.md")]);
        var dense = new FixedBackend(BackendFeatures.Dense | BackendFeatures.FilterPushdown, [Hit("b.md"), Hit("d.md")]);
        var filters = new SearchFilters([], []);
        static IEnumerable<string> Paths(IReadOnlyList<SearchHit> hits) => hits.Select(h => h.Path);

        var fused = await new HybridBackend(lexical, dense).SearchAsync("q", 10, filters, ct);
        var denseOnly = await new HybridBackend(lexical, dense, lexicalWeight: 0).SearchAsync("q", 10, filters, ct);
        var denseHeavy = await new HybridBackend(lexical, dense, denseWeight: 3).SearchAsync("q", 2, filters, ct);

        Assert.Equal(["b.md", "a.md", "d.md", "c.md"], Paths(fused));
        Assert.Equal(Math.Round(((1 / 62d) + (1 / 61d)) * 1e6, MidpointRounding.AwayFromZero) / 1e6, fused[0].Score);
        Assert.Equal(["b.md", "d.md"], Paths(denseOnly));
        Assert.Equal(["b.md", "d.md"], Paths(denseHeavy));
        Assert.Equal(BackendFeatures.Lexical | BackendFeatures.Dense | BackendFeatures.FilterPushdown, new HybridBackend(lexical, dense).Features);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridBackend(lexical, dense, lexicalWeight: 0, denseWeight: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridBackend(lexical, dense, k: -1));
    }

    private sealed class FixedBackend(BackendFeatures features, IReadOnlyList<SearchHit> hits) : ISearchBackend
    {
        public BackendFeatures Features => features;

        public IReadOnlyList<Diagnostic> Diagnostics => [];

        public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SearchHit>>([.. hits.Take(limit)]);
    }

    [Fact]
    public void Similarity_ScoresByTheIndexDistance_HigherAlwaysBetter()
    {
        Assert.Equal(0.7071, Math.Round(DenseBackend.Similarity(null, [1, 0], [3, 3]), 4));
        Assert.Equal(0.7071, Math.Round(DenseBackend.Similarity("cosine", [1, 0], [3, 3]), 4));
        Assert.Equal(3, DenseBackend.Similarity("dot", [1, 0], [3, 3]));
        Assert.Equal(0, DenseBackend.Similarity("euclidean", [1, 0], [1, 0]));
        Assert.True(DenseBackend.Similarity("euclidean", [1, 0], [0.9f, 0.1f]) > DenseBackend.Similarity("euclidean", [1, 0], [10, 0]));
    }

    [Fact]
    public async Task Diversify_BringsOtherNodesForward_AndSearchFetchesPastTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var kb = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct);
        var library = new Library().Add(kb);
        var backend = new ScriptedBackend(kb);
        static IEnumerable<string> Paths(IReadOnlyList<Citations.CitationRecord> records) => records.Select(r => r.Node.Path);

        var plain = await new MocaSearch(library, backend).SearchAsync("x", new SearchOptions { Limit = 2 }, ct);
        var spread = await new MocaSearch(library, backend, hooks: Diversify.Hooks()).SearchAsync("x", new SearchOptions { Limit = 2 }, ct);
        var two = await new MocaSearch(library, backend, hooks: Diversify.Hooks(perNode: 2)).SearchAsync("x", new SearchOptions { Limit = 3 }, ct);

        Assert.Equal(["refund-window.md", "refund-window.md"], Paths(plain));
        Assert.Equal(["refund-window.md", "shipping-estimates.md"], Paths(spread));
        Assert.Equal(["refund-window.md", "refund-window.md", "shipping-estimates.md"], Paths(two));
        Assert.Equal([2, 6, 9], backend.Asked);
        Assert.Throws<ArgumentOutOfRangeException>(() => Diversify.Hooks(perNode: 0));
    }

    /// <summary>A pushdown backend that returns three chunks of one node, then one of another, and records each limit.</summary>
    private sealed class ScriptedBackend(PackageResult package) : ISearchBackend
    {
        public List<int> Asked { get; } = [];

        public BackendFeatures Features => BackendFeatures.Lexical | BackendFeatures.FilterPushdown;

        public IReadOnlyList<Diagnostic> Diagnostics => [];

        public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
        {
            Asked.Add(limit);
            SearchHit Hit(string path, int at, double score)
            {
                var body = package.Nodes.First(n => n.Path == path).Representations[0].BodyOffset;
                return new SearchHit(package.Digest!, $"{package.Id}#{path}", path, null, new Citations.CitationSpan(body + at, body + at + 20), score);
            }

            IReadOnlyList<SearchHit> hits = [Hit("refund-window.md", 0, 9), Hit("refund-window.md", 30, 8), Hit("refund-window.md", 60, 7), Hit("shipping-estimates.md", 0, 6)];
            return ValueTask.FromResult<IReadOnlyList<SearchHit>>([.. hits.Take(limit)]);
        }
    }

    private sealed class FixedEmbedder(string? queryPrefix = null, string? documentPrefix = null) : IEmbedder
    {
        public List<EmbeddingRole> Roles { get; } = [];

        public string? QueryPrefix => queryPrefix;

        public string? DocumentPrefix => documentPrefix;

        public string Name => "fixed";

        public string? Version => null;

        public int Dimensions => 2;

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
        {
            Roles.Add(role);
            return ValueTask.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => new float[] { 1, 0 })]);
        }
    }
}
