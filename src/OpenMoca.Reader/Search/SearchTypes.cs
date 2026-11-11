using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader.Search;

/// <summary>A backend hit (<c>search-hit.schema.json</c>).</summary>
/// <param name="Digest">The package digest the hit was indexed from.</param>
/// <param name="Node">The node id, <c>&lt;package id&gt;#&lt;path&gt;</c>.</param>
/// <param name="Path">The node path.</param>
/// <param name="Locale">The representation's locale, or null for the default.</param>
/// <param name="Span">UTF-8 byte offsets into the node file, for a chunk.</param>
/// <param name="Score">Higher is better.</param>
/// <param name="Record">For store backends: the citation record written at ingest.</param>
public sealed record SearchHit(string Digest, string Node, string Path, string? Locale, CitationSpan? Span, double Score, CitationRecord? Record = null);

/// <summary>A node excluded by the retrieval policy: package digest and node id (contract §9.5).</summary>
public sealed record ExcludedNode(string Digest, string Node);

/// <summary>Filters a backend with <c>filterPushdown</c> should apply (Reader contract §9.5).</summary>
public sealed record SearchFilters(
    IReadOnlyList<string> Digests,
    IReadOnlyList<ExcludedNode> Exclude,
    IReadOnlyList<string>? Audiences = null,
    string? Locale = null,
    IReadOnlyList<string>? Concepts = null,
    bool AllowUnlabelled = false);

/// <summary>Backend features (Reader interface §5.2).</summary>
[Flags]
public enum BackendFeatures
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>A lexical backend.</summary>
    Lexical = 1,

    /// <summary>A dense (vector) backend.</summary>
    Dense = 2,

    /// <summary>The backend applies <see cref="SearchFilters"/> itself.</summary>
    FilterPushdown = 4,
}

/// <summary>A search backend the host chooses (Reader contract §9.1). A package never chooses one.</summary>
public interface ISearchBackend
{
    /// <summary>What the backend is.</summary>
    BackendFeatures Features { get; }

    /// <summary>Findings about the backend itself, such as S006 or S007.</summary>
    IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Returns hits for a query.</summary>
    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken);
}

/// <summary>What a text is embedded as (Reader interface §5.3). The embedder applies its model's convention.</summary>
public enum EmbeddingRole
{
    /// <summary>A search query.</summary>
    Query,

    /// <summary>Content, at store ingest or when a producer builds an index.</summary>
    Document,
}

/// <summary>A host-supplied embedder (Reader interface §5.3). A Reader never chooses one, and never adds text to what it embeds.</summary>
public interface IEmbedder
{
    /// <summary>The model name, compared with an index's <c>model.name</c>.</summary>
    string Name { get; }

    /// <summary>The model version, compared when both sides give one.</summary>
    string? Version { get; }

    /// <summary>Vector dimensions.</summary>
    int Dimensions { get; }

    /// <summary>The text this embedder puts before each query, declared for comparison with an index. Never applied by the Reader.</summary>
    string? QueryPrefix => null;

    /// <summary>The text this embedder puts before each document, declared for comparison with an index. Never applied by the Reader.</summary>
    string? DocumentPrefix => null;

    /// <summary>One vector per text, embedded with the model's convention for <paramref name="role"/>.</summary>
    ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken);
}

/// <summary>Options for one search (Reader contract §9).</summary>
public sealed record SearchOptions
{
    /// <summary>Most records to return.</summary>
    public int Limit { get; init; } = 5;

    /// <summary>The host's opt-in to content the default retrieval policy excludes.</summary>
    public bool IncludeAll { get; init; }

    /// <summary>Keep only the representation package spec §5.4 selects for this locale.</summary>
    public string? Locale { get; init; }

    /// <summary>Keep records bound to at least one of these concept IRIs.</summary>
    public IReadOnlyList<string>? Concepts { get; init; }

    /// <summary>Keep records bound to this concept or its descendants.</summary>
    public string? Scope { get; init; }
}

/// <summary>What an ingress hook may change: the query, and the concepts or scope the backend searches.</summary>
public sealed record IngressResult(string? Query = null, IReadOnlyList<string>? Concepts = null, string? Scope = null);

/// <summary>Read-only context for retrieval hooks (Reader interface §5.4).</summary>
public sealed record HookContext(
    SearchOptions Request,
    Library? Library,
    StructureIndex? Structure,
    IReadOnlyList<string>? Audiences,
    DateTimeOffset Now);

/// <summary>
/// Retrieval hooks: host code, never package content (moca-spec ADR-0010). The gate runs after both, so no hook
/// can return what the retrieval policy excludes.
/// </summary>
public sealed record SearchHooks
{
    /// <summary>May rewrite the query and narrow what the backend searches.</summary>
    public Func<string, HookContext, CancellationToken, ValueTask<IngressResult?>>? Ingress { get; init; }

    /// <summary>May reorder candidates or add records found through structure operations.</summary>
    public Func<IReadOnlyList<CitationRecord>, HookContext, CancellationToken, ValueTask<IReadOnlyList<CitationRecord>>>? Egress { get; init; }
}
