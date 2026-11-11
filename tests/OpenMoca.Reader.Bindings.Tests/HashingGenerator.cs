using System.Text;
using Microsoft.Extensions.AI;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// A deterministic M.E.AI generator: the Reader's tokens, FNV-1a into 64 buckets, counts. The same vectors as the
/// parity check's hashing embedder. Records every input it is given.
/// </summary>
internal sealed class HashingGenerator(string? modelId = "hash", int? dimensions = HashingGenerator.Size) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Size = 64;

    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.ToList();
        Inputs.AddRange(list);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(t => new Embedding<float>(Vector(t)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("hash", null, modelId, dimensions) : null;

    public void Dispose()
    {
    }

    public static float[] Vector(string text)
    {
        var v = new float[Size];
        foreach (var token in Bm25.Tokens(text))
        {
            var h = 0x811c9dc5u;
            foreach (var b in Encoding.UTF8.GetBytes(token))
            {
                h = unchecked((h ^ b) * 0x01000193u);
            }

            v[h % Size] += 1;
        }

        return v;
    }
}
