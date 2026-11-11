using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Structure;

/// <summary>Where an edge or declaration came from.</summary>
/// <param name="Layer">The layer that stated it.</param>
/// <param name="Origin">The package digest or overlay id.</param>
public sealed record FactSource(string Layer, string Origin);

/// <summary>A related IRI with the layer that stated the relation.</summary>
/// <param name="Iri">The other concept's IRI.</param>
/// <param name="Layer">The layer that stated the relation.</param>
/// <param name="Origin">The package digest or overlay id.</param>
public sealed record Edge(string Iri, string Layer, string Origin);

/// <summary>A concept as the merged structure knows it.</summary>
public sealed class ConceptEntry(string iri)
{
    /// <summary>The concept IRI.</summary>
    public string Iri { get; } = iri;

    /// <summary>Where it was first declared as a <c>skos:Concept</c>, or null when it never was.</summary>
    public FactSource? Declared { get; set; }

    /// <summary>Preferred labels by language tag ("" for none), in the order first stated.</summary>
    public OrderedDictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

    /// <summary>Definitions by language tag.</summary>
    public OrderedDictionary<string, string> Definitions { get; } = new(StringComparer.Ordinal);

    /// <summary><c>owl:deprecated true</c>.</summary>
    public bool Deprecated { get; set; }
}

/// <summary>An ordered collection (<c>skos:OrderedCollection</c>).</summary>
public sealed class CollectionEntry(string iri, FactSource source)
{
    /// <summary>The collection IRI.</summary>
    public string Iri { get; } = iri;

    /// <summary>Where it was first stated.</summary>
    public FactSource Source { get; } = source;

    /// <summary>Members in list order.</summary>
    public List<string> Members { get; } = [];

    internal SortedDictionary<int, string> Positions { get; } = [];
}

/// <summary>
/// An index over structure facts from any number of layers. The first layer to state a fact (package,
/// then application, then organisation) is the one reported (Reader contract §10).
/// </summary>
public sealed class StructureGraph
{
    /// <summary>The relations the structure core understands.</summary>
    public static readonly string[] Relations = ["broader", "related", "hasPart", "requires", "replaces"];

    private readonly Dictionary<string, Dictionary<string, OrderedDictionary<string, FactSource>>> _edges = [];
    private readonly Dictionary<string, Dictionary<string, OrderedDictionary<string, FactSource>>> _inverse = [];

    /// <summary>Builds the graph from facts in any order; facts are ranked by layer first.</summary>
    public StructureGraph(IEnumerable<StructureFact> facts)
    {
        foreach (var relation in Relations)
        {
            _edges[relation] = new Dictionary<string, OrderedDictionary<string, FactSource>>(StringComparer.Ordinal);
            _inverse[relation] = new Dictionary<string, OrderedDictionary<string, FactSource>>(StringComparer.Ordinal);
        }

        // OrderBy is stable, so facts from one layer keep their document order.
        Facts = facts.OrderBy(f => StructureLayers.Rank(f.Layer)).ToList();
        foreach (var f in Facts)
        {
            var source = new FactSource(f.Layer, f.Origin);
            switch (f.Kind)
            {
                case "concept":
                    Concept(f.S).Declared ??= source;
                    break;
                case "collection":
                    CollectionFor(f.S, source);
                    break;
                case "member":
                    var collection = CollectionFor(f.S, source);
                    if (collection.Source.Origin == f.Origin && f.O is not null && !collection.Positions.ContainsValue(f.O))
                    {
                        collection.Positions[f.Index] = f.O;
                    }

                    break;
                case "label":
                    Concept(f.S).Labels.TryAdd(f.Lang ?? "", f.Value ?? "");
                    break;
                case "definition":
                    Concept(f.S).Definitions.TryAdd(f.Lang ?? "", f.Value ?? "");
                    break;
                case "deprecated":
                    Concept(f.S).Deprecated = true;
                    break;
                default:
                    if (_edges.ContainsKey(f.Kind) && f.O is not null)
                    {
                        AddEdge(_edges[f.Kind], f.S, f.O, source);
                        AddEdge(_inverse[f.Kind], f.O, f.S, source);
                    }

                    break;
            }
        }

        foreach (var c in Collections.Values)
        {
            c.Members.AddRange(c.Positions.Values);
        }
    }

    /// <summary>The facts, ranked by layer.</summary>
    public IReadOnlyList<StructureFact> Facts { get; }

    /// <summary>Concepts by IRI, including ones only used, never declared.</summary>
    public Dictionary<string, ConceptEntry> Concepts { get; } = new(StringComparer.Ordinal);

    /// <summary>Ordered collections by IRI.</summary>
    public Dictionary<string, CollectionEntry> Collections { get; } = new(StringComparer.Ordinal);

    /// <summary>Subjects that have at least one edge of this relation.</summary>
    public IEnumerable<string> Subjects(string relation) => _edges[relation].Keys;

    /// <summary>The objects of a relation from <paramref name="iri"/>, sorted by IRI.</summary>
    public IReadOnlyList<Edge> Out(string relation, string iri) => Sorted(_edges[relation], iri);

    /// <summary>The subjects of a relation to <paramref name="iri"/>, sorted by IRI.</summary>
    public IReadOnlyList<Edge> In(string relation, string iri) => Sorted(_inverse[relation], iri);

    /// <summary>True when the IRI is declared as a concept or is an ordered collection.</summary>
    public bool IsDeclared(string iri) =>
        (Concepts.TryGetValue(iri, out var c) && c.Declared is not null) || Collections.ContainsKey(iri);

    /// <summary>The first cycle found in a relation, as a list of IRIs, or null.</summary>
    /// <remarks>Iterative, so a long chain cannot exhaust the call stack.</remarks>
    public IReadOnlyList<string>? Cycle(string relation)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var start in _edges[relation].Keys.Order(StringComparer.Ordinal))
        {
            if (state.ContainsKey(start))
            {
                continue;
            }

            state[start] = 1;
            path.Add(start);
            var frames = new Stack<IEnumerator<Edge>>();
            frames.Push(Out(relation, start).GetEnumerator());
            while (frames.Count > 0)
            {
                var pending = frames.Peek();
                if (!pending.MoveNext())
                {
                    frames.Pop();
                    state[path[^1]] = 2;
                    path.RemoveAt(path.Count - 1);
                    continue;
                }

                var target = pending.Current.Iri;
                if (state.TryGetValue(target, out var s) && s == 1)
                {
                    return [.. path.Skip(path.IndexOf(target)), target];
                }

                if (!state.ContainsKey(target))
                {
                    state[target] = 1;
                    path.Add(target);
                    frames.Push(Out(relation, target).GetEnumerator());
                }
            }
        }

        return null;
    }

    /// <summary>Preferred value: no language tag, then <c>en</c>, then the first language in code order.</summary>
    public static string? Preferred(OrderedDictionary<string, string> byLanguage)
    {
        if (byLanguage.TryGetValue("", out var plain))
        {
            return plain;
        }

        if (byLanguage.TryGetValue("en", out var english))
        {
            return english;
        }

        var first = byLanguage.Keys.Order(StringComparer.Ordinal).FirstOrDefault();
        return first is null ? null : byLanguage[first];
    }

    /// <summary>
    /// A JSON view of the structure, sorted so any two Readers produce the same JSON. Key order matches the
    /// reference Reader. It is not a package file (moca-spec ADR-0019).
    /// </summary>
    public JsonObject View()
    {
        var concepts = new JsonArray();
        foreach (var c in Concepts.Values.Where(c => c.Declared is not null).OrderBy(c => c.Iri, StringComparer.Ordinal))
        {
            var item = new JsonObject { ["iri"] = c.Iri };
            if (Preferred(c.Labels) is { } label)
            {
                item["label"] = label;
            }

            if (Preferred(c.Definitions) is { } definition)
            {
                item["definition"] = definition;
            }

            if (c.Deprecated)
            {
                item["deprecated"] = true;
            }

            JsonArray Ids(string relation) => new([.. Out(relation, c.Iri).Select(e => (JsonNode)e.Iri)]);
            var related = Out("related", c.Iri).Select(e => e.Iri).Concat(In("related", c.Iri).Select(e => e.Iri))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            item["broader"] = Ids("broader");
            item["related"] = new JsonArray([.. related.Select(r => (JsonNode)r)]);
            item["parts"] = Ids("hasPart");
            item["requires"] = Ids("requires");
            item["replaces"] = Ids("replaces");
            concepts.Add(item);
        }

        var sequences = new JsonArray();
        foreach (var c in Collections.Values.OrderBy(c => c.Iri, StringComparer.Ordinal))
        {
            sequences.Add(new JsonObject
            {
                ["iri"] = c.Iri,
                ["members"] = new JsonArray([.. c.Members.Select(m => (JsonNode)m)]),
            });
        }

        return new JsonObject { ["structureVersion"] = 1, ["concepts"] = concepts, ["sequences"] = sequences };
    }

    private ConceptEntry Concept(string iri)
    {
        if (!Concepts.TryGetValue(iri, out var c))
        {
            c = new ConceptEntry(iri);
            Concepts[iri] = c;
        }

        return c;
    }

    private CollectionEntry CollectionFor(string iri, FactSource source)
    {
        if (!Collections.TryGetValue(iri, out var c))
        {
            c = new CollectionEntry(iri, source);
            Collections[iri] = c;
        }

        return c;
    }

    private static void AddEdge(Dictionary<string, OrderedDictionary<string, FactSource>> map, string s, string o, FactSource source)
    {
        if (!map.TryGetValue(s, out var targets))
        {
            targets = new OrderedDictionary<string, FactSource>(StringComparer.Ordinal);
            map[s] = targets;
        }

        targets.TryAdd(o, source);
    }

    private static List<Edge> Sorted(Dictionary<string, OrderedDictionary<string, FactSource>> map, string iri) =>
        map.TryGetValue(iri, out var targets)
            ? targets.Select(kv => new Edge(kv.Key, kv.Value.Layer, kv.Value.Origin)).OrderBy(e => e.Iri, StringComparer.Ordinal).ToList()
            : [];
}
