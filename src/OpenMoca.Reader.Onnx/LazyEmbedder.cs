using OpenMoca.Reader.Search;

namespace OpenMoca.Reader.Onnx;

/// <summary>
/// A standard model from a store, loaded (and its files checked) the first time something is embedded with it, so an
/// installed model that no package uses costs nothing.
/// </summary>
public sealed class LazyEmbedder(ModelStore store, StandardModel model) : IEmbedder, IDisposable
{
    private readonly Lock _lock = new();
    private OnnxEmbedder? _loaded;

    /// <inheritdoc/>
    public string Name => model.Name;

    /// <inheritdoc/>
    public string? Version => model.Version;

    /// <inheritdoc/>
    public int Dimensions => model.Dimensions;

    /// <inheritdoc/>
    public string? QueryPrefix => model.QueryPrefix;

    /// <inheritdoc/>
    public string? DocumentPrefix => model.DocumentPrefix;

    /// <summary>The standard model.</summary>
    public StandardModel Model => model;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
    {
        OnnxEmbedder embedder;
        lock (_lock)
        {
            embedder = _loaded ??= store.Load(model);
        }

        return embedder.EmbedAsync(texts, role, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => _loaded?.Dispose();
}
