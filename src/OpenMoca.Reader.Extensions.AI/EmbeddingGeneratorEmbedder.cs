using Microsoft.Extensions.AI;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader.Extensions.AI;

/// <summary>
/// A Microsoft.Extensions.AI embedding generator as a Reader embedder (Reader interface §5.3; docs/adr/0004).
/// M.E.AI has no query or document role, so this embedder applies the model's convention itself: it puts
/// <see cref="QueryPrefix"/> or <see cref="DocumentPrefix"/> before each text. The Reader never adds text.
/// </summary>
public sealed class EmbeddingGeneratorEmbedder : IEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    /// <summary>Wraps a generator.</summary>
    /// <param name="generator">The host's embedding generator.</param>
    /// <param name="name">
    /// The model name, compared with an index's <c>model.name</c>. Defaults to the generator's
    /// <see cref="EmbeddingGeneratorMetadata.DefaultModelId"/>.
    /// </param>
    /// <param name="dimensions">Vector dimensions. Defaults to the generator's <see cref="EmbeddingGeneratorMetadata.DefaultModelDimensions"/>.</param>
    /// <param name="version">The model version, compared when an index gives one.</param>
    /// <param name="queryPrefix">The text the model expects before a query, such as e5's <c>"query: "</c>.</param>
    /// <param name="documentPrefix">The text the model expects before a document, such as e5's <c>"passage: "</c>.</param>
    /// <exception cref="ArgumentException">No name or dimensions were given and the generator does not report them.</exception>
    public EmbeddingGeneratorEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string? name = null,
        int? dimensions = null,
        string? version = null,
        string? queryPrefix = null,
        string? documentPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        _generator = generator;
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        Name = name ?? metadata?.DefaultModelId
            ?? throw new ArgumentException("Give the model name: the generator does not report one.", nameof(name));
        Dimensions = dimensions ?? metadata?.DefaultModelDimensions
            ?? throw new ArgumentException("Give the dimensions: the generator does not report them.", nameof(dimensions));
        Version = version;
        QueryPrefix = queryPrefix;
        DocumentPrefix = documentPrefix;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string? Version { get; }

    /// <inheritdoc/>
    public int Dimensions { get; }

    /// <inheritdoc/>
    public string? QueryPrefix { get; }

    /// <inheritdoc/>
    public string? DocumentPrefix { get; }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The generator returned the wrong number of vectors, or vectors of the wrong length.</exception>
    public async ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        var prefix = role == EmbeddingRole.Query ? QueryPrefix : DocumentPrefix;
        var input = string.IsNullOrEmpty(prefix) ? texts : texts.Select(t => prefix + t).ToList();
        var embeddings = await _generator.GenerateAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException($"The generator returned {embeddings.Count} vectors for {texts.Count} texts.");
        }

        var vectors = new float[embeddings.Count][];
        for (var i = 0; i < embeddings.Count; i++)
        {
            vectors[i] = embeddings[i].Vector.ToArray();
            if (vectors[i].Length != Dimensions)
            {
                throw new InvalidOperationException($"The generator returned a vector of {vectors[i].Length} dimensions; this embedder declares {Dimensions}.");
            }
        }

        return vectors;
    }
}
