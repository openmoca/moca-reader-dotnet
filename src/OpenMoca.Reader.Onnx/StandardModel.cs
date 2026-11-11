namespace OpenMoca.Reader.Onnx;

/// <summary>How a model splits text into tokens.</summary>
public enum TokenizerKind
{
    /// <summary>BERT WordPiece, uncased, from <c>vocab.txt</c>.</summary>
    WordPiece,

    /// <summary>SentencePiece, from <c>sentencepiece.bpe.model</c>, with XLM-R's id offset.</summary>
    SentencePiece,
}

/// <summary>How token states become one vector.</summary>
public enum Pooling
{
    /// <summary>The first token's state.</summary>
    Cls,

    /// <summary>The average over real tokens.</summary>
    Mean,
}

/// <summary>
/// One of MOCA's standard embedding models: a local ONNX file pinned by Hugging Face commit and SHA-256, with the
/// convention every tool embeds it by (prefixes, pooling, length). An index built with it and a query embedded with
/// it agree, whichever tool did each.
/// </summary>
/// <param name="Id">The short name used on the command line and in index file names, such as <c>bge-small-en-v1.5</c>.</param>
/// <param name="Name">The model name an index records, such as <c>BAAI/bge-small-en-v1.5</c>.</param>
/// <param name="Version">The model version an index records: <c>onnx-q8</c>, the 8-bit ONNX file.</param>
/// <param name="Dimensions">Vector length.</param>
/// <param name="Tokenizer">How it tokenises.</param>
/// <param name="Pooling">How it pools.</param>
/// <param name="MaxTokens">Most tokens embedded, special tokens included; longer text is cut.</param>
/// <param name="QueryPrefix">Text put before a query.</param>
/// <param name="DocumentPrefix">Text put before a document.</param>
/// <param name="Repository">The Hugging Face repository the files come from.</param>
/// <param name="Commit">The pinned commit.</param>
/// <param name="Licence">The model's licence.</param>
/// <param name="Files">The files to fetch, with their SHA-256: the vocabulary and ONNX file the .NET embedder runs,
/// and the tokenizer and config files other tools (moca-index) read, so one store serves both.</param>
public sealed record StandardModel(
    string Id, string Name, string Version, int Dimensions, TokenizerKind Tokenizer, Pooling Pooling, int MaxTokens,
    string QueryPrefix, string DocumentPrefix, string Repository, string Commit, string Licence,
    IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The ONNX file, relative to the model's folder.</summary>
    public const string OnnxFile = "onnx/model_quantized.onnx";

    /// <summary>MOCA's standard models (sidecar index specification §5.1, standard embedders).</summary>
    public static IReadOnlyList<StandardModel> All { get; } =
    [
        new("bge-small-en-v1.5", "BAAI/bge-small-en-v1.5", "onnx-q8", 384, TokenizerKind.WordPiece, Pooling.Cls, 512,
            "Represent this sentence for searching relevant passages: ", "",
            "Xenova/bge-small-en-v1.5", "ea104dacec62c0de699686887e3f920caeb4f3e3", "MIT",
            new Dictionary<string, string>
            {
                ["vocab.txt"] = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                ["config.json"] = "fa73f90bf92c8cace1fbcb709626306f2bdbc9ea3e5b5f94b440df9b6aa56350",
                ["tokenizer.json"] = "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
                ["tokenizer_config.json"] = "9261e7d79b44c8195c1cada2b453e55b00aeb81e907a6664974b4d7776172ab3",
                ["special_tokens_map.json"] = "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3",
                [OnnxFile] = "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4",
            }),
        new("nomic-embed-text-v1.5", "nomic-ai/nomic-embed-text-v1.5", "onnx-q8", 768, TokenizerKind.WordPiece, Pooling.Mean, 2048,
            "search_query: ", "search_document: ",
            "nomic-ai/nomic-embed-text-v1.5", "e9b6763023c676ca8431644204f50c2b100d9aab", "Apache-2.0",
            new Dictionary<string, string>
            {
                ["vocab.txt"] = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                ["config.json"] = "9ab00bd92cee80a569f708140b7b6c1661a65891ff3765b1519e181ba2f2c92b",
                ["tokenizer.json"] = "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
                ["tokenizer_config.json"] = "d7e0000bcc80134debd2222220427e6bf5fa20a669f40a0d0d1409cc18e0a9bc",
                ["special_tokens_map.json"] = "5d5b662e421ea9fac075174bb0688ee0d9431699900b90662acd44b2a350503a",
                [OnnxFile] = "b4342336debaea79de872370664b0aaeb67dea4605513d00ee236ea871a81f27",
            }),
        new("all-MiniLM-L6-v2", "sentence-transformers/all-MiniLM-L6-v2", "onnx-q8", 384, TokenizerKind.WordPiece, Pooling.Mean, 256,
            "", "",
            "Xenova/all-MiniLM-L6-v2", "751bff37182d3f1213fa05d7196b954e230abad9", "Apache-2.0",
            new Dictionary<string, string>
            {
                ["vocab.txt"] = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                ["config.json"] = "7135149f7cffa1a573466c6e4d8423ed73b62fd2332c575bf738a0d033f70df7",
                ["tokenizer.json"] = "da0e79933b9ed51798a3ae27893d3c5fa4a201126cef75586296df9b4d2c62a0",
                ["tokenizer_config.json"] = "9261e7d79b44c8195c1cada2b453e55b00aeb81e907a6664974b4d7776172ab3",
                ["special_tokens_map.json"] = "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3",
                [OnnxFile] = "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1",
            }),
        new("multilingual-e5-small", "intfloat/multilingual-e5-small", "onnx-q8", 384, TokenizerKind.SentencePiece, Pooling.Mean, 512,
            "query: ", "passage: ",
            "Xenova/multilingual-e5-small", "761b726dd34fb83930e26aab4e9ac3899aa1fa78", "MIT",
            new Dictionary<string, string>
            {
                ["sentencepiece.bpe.model"] = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865",
                ["config.json"] = "cb99455288675345e1a4f411438d5d0adbba5fbd3a67ea4fb03c015433b996c1",
                ["tokenizer.json"] = "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39",
                ["tokenizer_config.json"] = "a1d6bc8734a6f635dc158508bef000f8e2e5a759c7d92f984b2c86e5ff53425b",
                ["special_tokens_map.json"] = "d05497f1da52c5e09554c0cd874037a083e1dc1b9cfd48034d1c717f1afc07a7",
                [OnnxFile] = "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193",
            }),
    ];

    /// <summary>A standard model by short id or recorded name, or null.</summary>
    public static StandardModel? Find(string idOrName) =>
        All.FirstOrDefault(m => string.Equals(m.Id, idOrName, StringComparison.OrdinalIgnoreCase) || m.Name == idOrName);
}
