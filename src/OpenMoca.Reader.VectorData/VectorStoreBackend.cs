using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader.VectorData;

/// <summary>
/// A store backend over any Microsoft.Extensions.VectorData collection (Reader interface §7; docs/adr/0004). It
/// records the whole citation record of each unit at ingest, and returns it with each hit (Reader contract §9.4), so
/// search needs no package loaded. The store never embeds: the host's <see cref="IEmbedder"/> embeds units as
/// documents and queries as queries. Each hit is scored by the Reader, not the store, so every connector scores as
/// <see cref="MemoryStoreBackend"/> does; equal scores are ordered by node ref, locale and span. An approximate
/// (ANN) index may still miss a hit an exact search finds. Dense only: for hybrid search, put this in a
/// <see cref="HybridBackend"/>.
/// </summary>
/// <typeparam name="TKey">The store's key type: <see cref="string"/> or <see cref="Guid"/>, or any type with a <c>key</c> function.</typeparam>
public sealed class VectorStoreBackend<TKey> : ISearchBackend
    where TKey : notnull
{
    private const int MaxFetch = 200;

    private readonly VectorStoreCollection<TKey, MocaStoreRecord<TKey>> _collection;
    private readonly IEmbedder _embedder;
    private readonly string _distance;
    private readonly bool _filterPushdown;
    private readonly int _batchSize;
    private readonly Func<string, TKey> _key;

    /// <summary>A backend over a collection created with <see cref="MocaStore.Definition{TKey}"/>.</summary>
    /// <param name="collection">The host's collection.</param>
    /// <param name="embedder">The host's embedder; the collection's dimensions must match it.</param>
    /// <param name="distance">How vectors are compared: <c>cosine</c> (the default), <c>dot</c> or <c>euclidean</c>.</param>
    /// <param name="filterPushdown">
    /// Whether the store applies the search filters itself. Turn it off for a connector that cannot translate them;
    /// the gate still applies every filter, at the cost of fetching more hits.
    /// </param>
    /// <param name="batchSize">How many units to embed and write at a time during ingest.</param>
    /// <param name="key">Turns a unit's identity into a key; needed only when <typeparamref name="TKey"/> is neither string nor Guid.</param>
    public VectorStoreBackend(
        VectorStoreCollection<TKey, MocaStoreRecord<TKey>> collection,
        IEmbedder embedder,
        string distance = "cosine",
        bool filterPushdown = true,
        int batchSize = 64,
        Func<string, TKey>? key = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        MocaStore.StoreDistance(distance);
        _collection = collection;
        _embedder = embedder;
        _distance = distance;
        _filterPushdown = filterPushdown;
        _batchSize = batchSize;
        _key = key ?? DefaultKey();
    }

    /// <inheritdoc/>
    public BackendFeatures Features => BackendFeatures.Dense | (_filterPushdown ? BackendFeatures.FilterPushdown : BackendFeatures.None);

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> Diagnostics => [];

    /// <summary>
    /// Ingests every node representation (or sidecar chunk) of every loaded package. Keys come from each unit's
    /// digest, node, locale and span, so ingesting a package again replaces its records.
    /// </summary>
    public async Task<VectorStoreBackend<TKey>> IngestAsync(Library library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        await _collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var batch in StoreUnit.From(library).Chunk(_batchSize))
        {
            var vectors = await _embedder.EmbedAsync([.. batch.Select(u => u.Text)], EmbeddingRole.Document, cancellationToken).ConfigureAwait(false);
            var records = batch.Select((u, i) => ToRecord(u, vectors[i])).ToList();
            await _collection.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
        }

        return this;
    }

    /// <summary>Removes everything ingested from a package digest.</summary>
    public async Task RemoveAsync(string digest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digest);
        while (true)
        {
            var keys = new List<TKey>();
            await foreach (var record in _collection.GetAsync(r => r.Digest == digest, MaxFetch, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                keys.Add(record.Key);
            }

            if (keys.Count == 0)
            {
                return;
            }

            await _collection.DeleteAsync(keys, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (limit < 1)
        {
            return [];
        }

        var q = (await _embedder.EmbedAsync([query], EmbeddingRole.Query, cancellationToken).ConfigureAwait(false))[0];
        var options = new VectorSearchOptions<MocaStoreRecord<TKey>>
        {
            IncludeVectors = true,
            Filter = _filterPushdown ? Filter(filters) : null,
        };
        var hits = new List<SearchHit>();
        await foreach (var result in _collection.SearchAsync(new ReadOnlyMemory<float>(q), Math.Min(limit, MaxFetch), options, cancellationToken).ConfigureAwait(false))
        {
            var r = result.Record;
            var record = JsonSerializer.Deserialize<CitationRecord>(r.Record, CitationJson.Options)!;
            // Scored as DenseBackend and MemoryStoreBackend score, whatever the store reports (docs/adr/0004).
            var score = Math.Round(DenseBackend.Similarity(_distance, q, r.Vector?.ToArray() ?? []) * 1e4, MidpointRounding.AwayFromZero) / 1e4;
            hits.Add(new SearchHit(r.Digest, r.Node, record.Node.Path, r.Locale.Length == 0 ? null : r.Locale, record.Span, score, record));
        }

        // A store's order for equal scores is its own; break ties the same way for every connector.
        return [.. hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Record!.Node.Ref, StringComparer.Ordinal)
            .ThenBy(h => h.Locale ?? "", StringComparer.Ordinal)
            .ThenBy(h => h.Span?.Start ?? -1)
            .Take(limit)];
    }

    /// <summary>The unit's identity, from which its key is made.</summary>
    public static string Identity(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return $"{hit.Digest}\u0000{hit.Node}\u0000{hit.Locale ?? ""}\u0000{hit.Span?.Start.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}";
    }

    private MocaStoreRecord<TKey> ToRecord(StoreUnit unit, float[] vector) => new()
    {
        Key = _key(Identity(unit.Hit)),
        Digest = unit.Hit.Digest,
        Node = unit.Hit.Node,
        NodeRef = unit.Record.Node.Ref,
        NodeKey = NodeKey(unit.Hit.Digest, unit.Hit.Node),
        Locale = unit.Hit.Locale ?? "",
        Audience = unit.Record.Audience ?? "",
        Concepts = [.. unit.Record.Concepts ?? []],
        Record = JsonSerializer.Serialize(unit.Record, CitationJson.Options),
        Vector = vector,
    };

    private static string NodeKey(string digest, string node) => $"{digest} {node}";

    /// <summary>
    /// The filters as one expression of the simple forms connectors translate: equality, <c>Contains</c> on a list,
    /// <c>&amp;&amp;</c>, <c>||</c> and <c>!</c>. Locale is left to the gate, as in <see cref="MemoryStoreBackend"/>.
    /// </summary>
    private static Expression<Func<MocaStoreRecord<TKey>, bool>> Filter(SearchFilters filters)
    {
        var digests = filters.Digests.ToList();
        Expression<Func<MocaStoreRecord<TKey>, bool>> filter = r => digests.Contains(r.Digest);
        if (filters.Exclude.Count > 0)
        {
            var excluded = filters.Exclude.Select(x => NodeKey(x.Digest, x.Node)).ToList();
            filter = And(filter, r => !excluded.Contains(r.NodeKey));
        }

        if (filters.Audiences is { } audiences)
        {
            var allowed = audiences.Append("").ToList();
            filter = And(filter, r => allowed.Contains(r.Audience));
        }

        if (filters.Concepts is { Count: > 0 } concepts)
        {
            Expression<Func<MocaStoreRecord<TKey>, bool>>? any = null;
            foreach (var concept in concepts)
            {
                var c = concept;
                Expression<Func<MocaStoreRecord<TKey>, bool>> one = r => r.Concepts.Contains(c);
                any = any is null ? one : Or(any, one);
            }

            filter = And(filter, any!);
        }

        return filter;
    }

    private static Expression<Func<MocaStoreRecord<TKey>, bool>> And(Expression<Func<MocaStoreRecord<TKey>, bool>> a, Expression<Func<MocaStoreRecord<TKey>, bool>> b) =>
        Expression.Lambda<Func<MocaStoreRecord<TKey>, bool>>(Expression.AndAlso(a.Body, Rebind(b, a.Parameters[0])), a.Parameters);

    private static Expression<Func<MocaStoreRecord<TKey>, bool>> Or(Expression<Func<MocaStoreRecord<TKey>, bool>> a, Expression<Func<MocaStoreRecord<TKey>, bool>> b) =>
        Expression.Lambda<Func<MocaStoreRecord<TKey>, bool>>(Expression.OrElse(a.Body, Rebind(b, a.Parameters[0])), a.Parameters);

    private static Expression Rebind(LambdaExpression lambda, ParameterExpression parameter) =>
        new ParameterSwap(lambda.Parameters[0], parameter).Visit(lambda.Body);

    private static Func<string, TKey> DefaultKey()
    {
        if (typeof(TKey) == typeof(string))
        {
            return id => (TKey)(object)Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        }

        if (typeof(TKey) == typeof(Guid))
        {
            return id => (TKey)(object)new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16));
        }

        throw new ArgumentException($"Give a key function for {typeof(TKey).Name} keys; string and Guid keys are made for you.", "key");
    }

    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
