using Microsoft.Extensions.AI;
using OpenMoca.Reader.Extensions.AI;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

public sealed class EmbeddingGeneratorEmbedderTests
{
    [Fact]
    public async Task EmbedAsync_PutsTheRolesPrefixBeforeEachText()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = new HashingGenerator();
        var embedder = new EmbeddingGeneratorEmbedder(generator, queryPrefix: "query: ", documentPrefix: "passage: ");

        await embedder.EmbedAsync(["refund"], EmbeddingRole.Query, ct);
        await embedder.EmbedAsync(["a", "b"], EmbeddingRole.Document, ct);

        Assert.Equal(["query: refund", "passage: a", "passage: b"], generator.Inputs);
    }

    [Fact]
    public async Task EmbedAsync_WithoutPrefixes_PassesTheTextUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var generator = new HashingGenerator();
        var vectors = await new EmbeddingGeneratorEmbedder(generator).EmbedAsync(["refund window"], EmbeddingRole.Query, ct);

        Assert.Equal(["refund window"], generator.Inputs);
        Assert.Equal(HashingGenerator.Vector("refund window"), Assert.Single(vectors));
    }

    [Fact]
    public void NameAndDimensions_DefaultToTheGeneratorsMetadata_AndCanBeGiven()
    {
        var reported = new EmbeddingGeneratorEmbedder(new HashingGenerator());
        var given = new EmbeddingGeneratorEmbedder(new HashingGenerator(), name: "all-minilm", dimensions: 384, version: "2");

        Assert.Equal(("hash", 64, null), (reported.Name, reported.Dimensions, reported.Version));
        Assert.Equal(("all-minilm", 384, "2"), (given.Name, given.Dimensions, given.Version));
        Assert.Throws<ArgumentException>(() => new EmbeddingGeneratorEmbedder(new HashingGenerator(modelId: null)));
        Assert.Throws<ArgumentException>(() => new EmbeddingGeneratorEmbedder(new HashingGenerator(dimensions: null)));
    }

    [Fact]
    public async Task EmbedAsync_VectorsOfTheWrongLength_Throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new EmbeddingGeneratorEmbedder(new HashingGenerator(), dimensions: 384);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await embedder.EmbedAsync(["x"], EmbeddingRole.Query, ct));
    }

    [Fact]
    public void DeclaredPrefixes_AreComparedWithAnIndex()
    {
        var model = new System.Text.Json.Nodes.JsonObject { ["name"] = "hash", ["dimensions"] = 64, ["queryPrefix"] = "query: " };

        Assert.Null(DenseBackend.ModelMismatch(new EmbeddingGeneratorEmbedder(new HashingGenerator(), queryPrefix: "query: "), model, 64));
        Assert.NotNull(DenseBackend.ModelMismatch(new EmbeddingGeneratorEmbedder(new HashingGenerator(), queryPrefix: "search_query: "), model, 64));
    }

    [Fact]
    public async Task Search_ThroughTheAdapter_RanksAsTheSameVectorsGivenDirectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var package = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct);
        var library = new Library().Add(package);
        async Task<string[]> Ranked(IEmbedder embedder)
        {
            var store = await new MemoryStoreBackend(embedder).IngestAsync(library, ct);
            var records = await new MocaSearch([PackageState.From(package)], library.Clock, store).SearchAsync("domestic express", new SearchOptions { Limit = 10 }, ct);
            return [.. records.Select(r => $"{r.Node.Ref} {r.Node.Locale} {r.Score}")];
        }

        var adapted = await Ranked(new EmbeddingGeneratorEmbedder(new HashingGenerator()));
        var direct = await Ranked(new DirectEmbedder());

        Assert.NotEmpty(adapted);
        Assert.Equal(direct, adapted);
    }

    private sealed class DirectEmbedder : IEmbedder
    {
        public string Name => "hash";

        public string? Version => null;

        public int Dimensions => HashingGenerator.Size;

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<float[]>>([.. texts.Select(HashingGenerator.Vector)]);
    }
}
