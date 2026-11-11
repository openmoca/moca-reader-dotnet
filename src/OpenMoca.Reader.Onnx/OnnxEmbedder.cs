using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader.Onnx;

/// <summary>
/// A standard model run locally with ONNX Runtime: tokenise with the model's convention, run one text at a time (the
/// 8-bit models quantise activations per run, so batching would change each vector), pool, and L2-normalise. Nothing
/// is fetched: the files come from a <see cref="ModelStore"/>.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly StandardModel _model;
    private readonly InferenceSession _session;
    private readonly Func<string, List<int>> _tokenize;

    private OnnxEmbedder(StandardModel model, InferenceSession session, Func<string, List<int>> tokenize)
    {
        _model = model;
        _session = session;
        _tokenize = tokenize;
    }

    /// <inheritdoc/>
    public string Name => _model.Name;

    /// <inheritdoc/>
    public string? Version => _model.Version;

    /// <inheritdoc/>
    public int Dimensions => _model.Dimensions;

    /// <inheritdoc/>
    public string? QueryPrefix => _model.QueryPrefix;

    /// <inheritdoc/>
    public string? DocumentPrefix => _model.DocumentPrefix;

    /// <summary>The standard model this embedder runs.</summary>
    public StandardModel Model => _model;

    /// <summary>Loads a model's files from <paramref name="folder"/> (already checked by the store).</summary>
    public static OnnxEmbedder Load(StandardModel model, string folder)
    {
        ArgumentNullException.ThrowIfNull(model);
        Func<string, List<int>> tokenize;
        if (model.Tokenizer == TokenizerKind.WordPiece)
        {
            var wordPiece = new WordPieceTokenizer(Path.Combine(folder, "vocab.txt"));
            tokenize = text => wordPiece.Encode(text, model.MaxTokens);
        }
        else
        {
            using var stream = File.OpenRead(Path.Combine(folder, "sentencepiece.bpe.model"));
            var sentencePiece = SentencePieceTokenizer.Create(stream, false, false);
            tokenize = text =>
            {
                // XLM-R's Hugging Face ids: <s>=0, </s>=2, <unk>=3, then each SentencePiece id + 1 (fairseq's offset).
                var ids = new List<int> { 0 };
                ids.AddRange(sentencePiece.EncodeToIds(text).Take(model.MaxTokens - 2).Select(id => id == 0 ? 3 : id + 1));
                ids.Add(2);
                return ids;
            };
        }

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        var session = new InferenceSession(Path.Combine(folder, StandardModel.OnnxFile), options);
        return new OnnxEmbedder(model, session, tokenize);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var prefix = role == EmbeddingRole.Query ? _model.QueryPrefix : _model.DocumentPrefix;
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(Embed(prefix + text));
        }

        return ValueTask.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private float[] Embed(string text)
    {
        var ids = _tokenize(text);
        var length = ids.Count;
        var shape = new[] { 1, length };
        var inputIds = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), shape);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), shape);
        var types = new DenseTensor<long>(new long[length], shape);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types),
        };
        using var results = _session.Run(inputs);
        var hidden = results[0].AsTensor<float>();
        var dims = _model.Dimensions;
        var vector = new float[dims];
        if (_model.Pooling == Pooling.Cls)
        {
            for (var d = 0; d < dims; d++)
            {
                vector[d] = hidden[0, 0, d];
            }
        }
        else
        {
            for (var t = 0; t < length; t++)
            {
                for (var d = 0; d < dims; d++)
                {
                    vector[d] += hidden[0, t, d];
                }
            }

            for (var d = 0; d < dims; d++)
            {
                vector[d] /= length;
            }
        }

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm > 0)
        {
            for (var d = 0; d < dims; d++)
            {
                vector[d] = (float)(vector[d] / norm);
            }
        }

        return vector;
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();
}
