using CommunityToolkit.VectorData.InMemory;
using OpenMoca.Reader.Extensions.AI;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sidecars;
using OpenMoca.Reader.VectorData;
using Xunit;

namespace OpenMoca.Reader.Tests;

public sealed class VectorStoreBackendTests
{
    public static TheoryData<string, bool> Cases() => new()
    {
        { "cosine", false }, { "dot", false }, { "euclidean", false }, { "cosine", true },
    };

    /// <summary>
    /// The store backend must return what the reference store backend returns, with the same scores, whatever the
    /// connector reports. Equal scores may come in another order: each backend breaks ties its own way.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Search_MatchesMemoryStoreBackend(string distance, bool sidecar)
    {
        var ct = TestContext.Current.CancellationToken;
        var library = await SupportKb(sidecar, ct);
        var embedder = new EmbeddingGeneratorEmbedder(new HashingGenerator());
        var memory = await new MemoryStoreBackend(embedder, distance: distance).IngestAsync(library, ct);
        var store = await new VectorStoreBackend<string>(Collection<string>(distance), embedder, distance).IngestAsync(library, ct);
        string[][] queries = [["refund window"], ["domestic express", "public"], ["delete my account", "internal"], ["remboursement"]];

        foreach (var q in queries)
        {
            IReadOnlyList<string>? audiences = q.Length > 1 ? [q[1]] : null;
            var expected = TiesSorted(await Ranked(new MocaSearch(library, memory, audiences), q[0], ct));
            var actual = TiesSorted(await Ranked(new MocaSearch(library, store, audiences), q[0], ct));

            Assert.NotEmpty(expected);
            Assert.True(expected.SequenceEqual(actual), $"{q[0]}\nexpected:\n{string.Join("\n", expected)}\nactual:\n{string.Join("\n", actual)}");
        }
    }

    [Fact]
    public async Task Search_WithoutFilterPushdown_ReturnsTheSameAfterTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = await SupportKb(false, ct);
        var embedder = new EmbeddingGeneratorEmbedder(new HashingGenerator());
        var pushed = await new VectorStoreBackend<string>(Collection<string>(), embedder).IngestAsync(library, ct);
        var gated = await new VectorStoreBackend<string>(Collection<string>(), embedder, filterPushdown: false).IngestAsync(library, ct);

        Assert.Equal(BackendFeatures.Dense, gated.Features);
        Assert.Equal(
            await Ranked(new MocaSearch(library, pushed, ["public"]), "account", ct),
            await Ranked(new MocaSearch(library, gated, ["public"]), "account", ct));
        Assert.NotEmpty(await Ranked(new MocaSearch(library, gated, ["public"]), "account", ct));
    }

    [Fact]
    public async Task Search_ConceptsAndScope_ArePushedDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = new Library().Add(await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "handbook", "incident-response"), cancellationToken: ct));
        var embedder = new EmbeddingGeneratorEmbedder(new HashingGenerator());
        var memory = await new MemoryStoreBackend(embedder).IngestAsync(library, ct);
        var store = await new VectorStoreBackend<Guid>(Collection<Guid>(), embedder).IngestAsync(library, ct);
        var options = new SearchOptions { Limit = 10, Scope = "https://example.org/handbook/incident#IncidentResponse" };

        var expected = TiesSorted(await Ranked(new MocaSearch(library, memory), "severity", ct, options));
        var actual = TiesSorted(await Ranked(new MocaSearch(library, store), "severity", ct, options));
        var unscoped = await Ranked(new MocaSearch(library, store), "severity", ct, new SearchOptions { Limit = 10, Concepts = ["https://example.org/nothing"] });

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        Assert.Empty(unscoped);
    }

    [Fact]
    public async Task IngestAgain_Replaces_AndRemoveDeletesAPackage()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = await SupportKb(false, ct);
        var digests = library.Packages.Select(p => PackageState.From(p.Result)).ToList();
        var embedder = new EmbeddingGeneratorEmbedder(new HashingGenerator());
        var store = new VectorStoreBackend<string>(Collection<string>(), embedder);
        var options = new SearchOptions { Limit = 50, IncludeAll = true };

        await store.IngestAsync(library, ct);
        var once = await new MocaSearch(digests, library.Clock, store).SearchAsync("refund", options, ct);
        await store.IngestAsync(library, ct);
        var twice = await new MocaSearch(digests, library.Clock, store).SearchAsync("refund", options, ct);
        await store.RemoveAsync(digests[0].Digest, ct);
        var removed = await new MocaSearch(digests, library.Clock, store).SearchAsync("refund", options, ct);

        Assert.NotEmpty(once);
        Assert.Equal(once.Count, twice.Count);
        Assert.Empty(removed);
    }

    [Fact]
    public async Task TheStoreNeverEmbeds_TheHostsEmbedderDoes_ByRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = await SupportKb(false, ct);
        var generator = new HashingGenerator();
        var embedder = new EmbeddingGeneratorEmbedder(generator, queryPrefix: "query: ", documentPrefix: "passage: ");
        var store = await new VectorStoreBackend<string>(Collection<string>(), embedder, batchSize: 2).IngestAsync(library, ct);

        await new MocaSearch(library, store).SearchAsync("refund", cancellationToken: ct);

        Assert.All(generator.Inputs.SkipLast(1), i => Assert.StartsWith("passage: ", i, StringComparison.Ordinal));
        Assert.Equal("query: refund", generator.Inputs[^1]);
    }

    [Fact]
    public void Keys_OtherThanStringOrGuid_NeedAKeyFunction()
    {
        var embedder = new EmbeddingGeneratorEmbedder(new HashingGenerator());

        Assert.Throws<ArgumentException>(() => new VectorStoreBackend<int>(Collection<int>(), embedder));
        Assert.Throws<ArgumentException>(() => new VectorStoreBackend<string>(Collection<string>(), embedder, distance: "manhattan"));
        _ = new VectorStoreBackend<int>(Collection<int>(), embedder, key: id => id.GetHashCode(StringComparison.Ordinal));
    }

    private static InMemoryCollection<TKey, MocaStoreRecord<TKey>> Collection<TKey>(string distance = "cosine")
        where TKey : notnull =>
        new InMemoryVectorStore().GetCollection<TKey, MocaStoreRecord<TKey>>($"moca-{Guid.NewGuid():N}", MocaStore.Definition<TKey>(HashingGenerator.Size, distance));

    private static async Task<Library> SupportKb(bool sidecar, CancellationToken ct)
    {
        var package = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct);
        return sidecar
            ? new Library().Add(package, SidecarBinder.Bind(Path.Combine(MocaSpec.Examples, "sidecars", "support-kb.moca.idx"), package))
            : new Library().Add(package);
    }

    private static async Task<string[]> Ranked(MocaSearch search, string query, CancellationToken ct, SearchOptions? options = null) =>
        [.. (await search.SearchAsync(query, options ?? new SearchOptions { Limit = 10 }, ct))
            .Select(r => $"{r.Score}|{r.Node.Ref} {r.Node.Locale} {r.Span?.Start} {r.Text?.Length}")];

    /// <summary>The same results with equal scores in one order: best score first, then by the rest of the line.</summary>
    private static string[] TiesSorted(string[] ranked) =>
        [.. ranked.Select((line, i) => (line, i))
            .GroupBy(x => x.line.Split('|')[0])
            .OrderBy(g => g.Min(x => x.i))
            .SelectMany(g => g.Select(x => x.line).Order(StringComparer.Ordinal))];
}
