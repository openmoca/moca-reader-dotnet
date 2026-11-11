using OpenMoca.Reader.Onnx;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Bindings.Tests;

/// <summary>
/// The ONNX binding. The model tests run when <c>MOCA_TEST_MODELS</c> names a model store holding bge-small-en-v1.5;
/// otherwise they are skipped.
/// </summary>
public sealed class OnnxTests
{
    private static readonly string? Models = Environment.GetEnvironmentVariable("MOCA_TEST_MODELS");

    [Fact]
    public void The_four_standard_models_are_pinned_with_their_convention()
    {
        Assert.Equal(["bge-small-en-v1.5", "nomic-embed-text-v1.5", "all-MiniLM-L6-v2", "multilingual-e5-small"], StandardModel.All.Select(m => m.Id));
        Assert.All(StandardModel.All, m =>
        {
            Assert.Matches("^[0-9a-f]{40}$", m.Commit);
            Assert.Equal("onnx-q8", m.Version);
            Assert.Contains(StandardModel.OnnxFile, m.Files.Keys);
            Assert.All(m.Files.Values, sha => Assert.Matches("^[0-9a-f]{64}$", sha));
        });
        Assert.Equal("bge-small-en-v1.5", StandardModel.Find("BAAI/bge-small-en-v1.5")?.Id);
        Assert.Null(StandardModel.Find("nope"));
    }

    [Fact]
    public void A_store_reports_missing_and_changed_files()
    {
        var folder = Directory.CreateTempSubdirectory("moca-models-").FullName;
        try
        {
            var store = new ModelStore(folder);
            var bge = StandardModel.Find("bge-small-en-v1.5")!;
            Assert.Empty(store.Installed());
            Assert.Contains("missing", store.Check(bge), StringComparison.Ordinal);

            foreach (var file in bge.Files.Keys)
            {
                var path = Path.Combine(folder, bge.Id, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "not the model");
            }

            Assert.Equal([bge], store.Installed());
            Assert.Contains("does not match", store.Check(bge), StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => store.Load(bge));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Bge_small_embeds_with_its_convention()
    {
        var bge = StandardModel.Find("bge-small-en-v1.5")!;
        if (string.IsNullOrEmpty(Models) || !new ModelStore(Models).Installed().Contains(bge))
        {
            Assert.Skip("MOCA_TEST_MODELS does not name a model store holding bge-small-en-v1.5");
        }

        using var embedder = new ModelStore(Models!).Load(bge);
        Assert.Equal(("BAAI/bge-small-en-v1.5", "onnx-q8", 384), (embedder.Name, embedder.Version, embedder.Dimensions));
        var ct = TestContext.Current.CancellationToken;
        var doc = (await embedder.EmbedAsync(["Customers may request a full refund within 30 days of delivery."], EmbeddingRole.Document, ct))[0];
        var query = (await embedder.EmbedAsync(["how long do I have to get a refund"], EmbeddingRole.Query, ct))[0];
        var other = (await embedder.EmbedAsync(["The engine needs oil checked before each flight."], EmbeddingRole.Document, ct))[0];

        Assert.Equal(1, Math.Sqrt(doc.Sum(x => (double)x * x)), 4);
        Assert.True(DenseBackend.Cosine(query, doc) > DenseBackend.Cosine(query, other));
    }
}
