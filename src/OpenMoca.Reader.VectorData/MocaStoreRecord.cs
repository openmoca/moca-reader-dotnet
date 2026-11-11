using Microsoft.Extensions.VectorData;

namespace OpenMoca.Reader.VectorData;

/// <summary>
/// One stored unit: a node representation or sidecar chunk, with its whole citation record (Reader contract §9.4) and
/// the flat fields a store filters on, named as the consuming guide's metadata table names them. Create the
/// collection with <see cref="MocaStore.Definition{TKey}"/>, so the dimensions and distance match the embedder.
/// </summary>
/// <typeparam name="TKey">The store's key type: <see cref="string"/> or <see cref="System.Guid"/>.</typeparam>
public sealed class MocaStoreRecord<TKey>
    where TKey : notnull
{
    /// <summary>A key derived from the unit, so ingesting a package again replaces its records.</summary>
    public TKey Key { get; set; } = default!;

    /// <summary>The package digest (<c>moca_package_digest</c>).</summary>
    public string Digest { get; set; } = "";

    /// <summary>The node id, <c>&lt;package id&gt;#&lt;path&gt;</c> (<c>moca_node_id</c>).</summary>
    public string Node { get; set; } = "";

    /// <summary>The node's versioned ref, the key to cite by (<c>moca_node_ref</c>).</summary>
    public string NodeRef { get; set; } = "";

    /// <summary>The digest and node id, for excluding nodes (<c>moca_node_key</c>).</summary>
    public string NodeKey { get; set; } = "";

    /// <summary>The representation's locale; empty for the package's default (<c>moca_locale</c>).</summary>
    public string Locale { get; set; } = "";

    /// <summary>The node's audience label; empty for none (<c>moca_audience</c>).</summary>
    public string Audience { get; set; } = "";

    /// <summary>The concept IRIs the node is bound to (<c>moca_concepts</c>).</summary>
    public List<string> Concepts { get; set; } = [];

    /// <summary>The whole citation record as JSON (<c>moca_record</c>).</summary>
    public string Record { get; set; } = "";

    /// <summary>The unit's vector, embedded by the host's embedder as a document (<c>moca_vector</c>).</summary>
    public ReadOnlyMemory<float>? Vector { get; set; }
}

/// <summary>Helpers for creating a collection of <see cref="MocaStoreRecord{TKey}"/>.</summary>
public static class MocaStore
{
    /// <summary>The collection definition for <see cref="MocaStoreRecord{TKey}"/>.</summary>
    /// <typeparam name="TKey">The store's key type: <see cref="string"/> or <see cref="System.Guid"/>.</typeparam>
    /// <param name="dimensions">The embedder's dimensions.</param>
    /// <param name="distance">The Reader's distance: <c>cosine</c> (the default), <c>dot</c> or <c>euclidean</c>.</param>
    /// <param name="distanceFunction">
    /// The store's distance function, when the connector does not support the default for <paramref name="distance"/>
    /// (<see cref="DistanceFunction.CosineSimilarity"/>, <see cref="DistanceFunction.DotProductSimilarity"/> or
    /// <see cref="DistanceFunction.EuclideanDistance"/>). Any function that orders as <paramref name="distance"/> does
    /// will do: the backend scores each hit itself.
    /// </param>
    public static VectorStoreCollectionDefinition Definition<TKey>(int dimensions, string distance = "cosine", string? distanceFunction = null)
        where TKey : notnull => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(MocaStoreRecord<TKey>.Key), typeof(TKey)) { StorageName = "moca_key" },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.Digest), typeof(string)) { StorageName = "moca_package_digest", IsIndexed = true },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.Node), typeof(string)) { StorageName = "moca_node_id", IsIndexed = true },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.NodeRef), typeof(string)) { StorageName = "moca_node_ref" },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.NodeKey), typeof(string)) { StorageName = "moca_node_key", IsIndexed = true },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.Locale), typeof(string)) { StorageName = "moca_locale" },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.Audience), typeof(string)) { StorageName = "moca_audience", IsIndexed = true },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.Concepts), typeof(List<string>)) { StorageName = "moca_concepts", IsIndexed = true },
            new VectorStoreDataProperty(nameof(MocaStoreRecord<TKey>.Record), typeof(string)) { StorageName = "moca_record" },
            new VectorStoreVectorProperty(nameof(MocaStoreRecord<TKey>.Vector), typeof(ReadOnlyMemory<float>?), dimensions)
            {
                StorageName = "moca_vector",
                DistanceFunction = distanceFunction ?? StoreDistance(distance),
            },
        ],
    };

    /// <summary>The VectorData distance function that orders as the Reader's <paramref name="distance"/> does.</summary>
    /// <exception cref="ArgumentException">An unknown distance.</exception>
    public static string StoreDistance(string distance) => distance switch
    {
        "cosine" => DistanceFunction.CosineSimilarity,
        "dot" => DistanceFunction.DotProductSimilarity,
        "euclidean" => DistanceFunction.EuclideanDistance,
        _ => throw new ArgumentException($"Unknown distance {distance}: use cosine, dot or euclidean.", nameof(distance)),
    };
}
