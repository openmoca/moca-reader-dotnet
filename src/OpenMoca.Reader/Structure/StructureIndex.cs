using OpenMoca.Reader.Citations;

namespace OpenMoca.Reader.Structure;

/// <summary>A concept item: its IRI, preferred label and the layer that stated the fact (Reader contract §10).</summary>
/// <param name="Iri">The concept IRI.</param>
/// <param name="Label">The preferred label, when there is one.</param>
/// <param name="Layer">package, application or organisation.</param>
public sealed record ConceptItem(string Iri, string? Label, string Layer);

/// <summary>A concept as <see cref="StructureIndex.Concept"/> returns it.</summary>
public sealed record ConceptDetails(
    string Iri,
    string? Label,
    IReadOnlyDictionary<string, string> Labels,
    string? Definition,
    bool Deprecated,
    IReadOnlyList<string> ReplacedBy,
    IReadOnlyList<string> Replaces,
    string Layer);

/// <summary>
/// Structure operations over the merged structure of every loaded package plus the host's overlays
/// (Reader contract §10). They are facts: every Reader answers them identically.
/// </summary>
public sealed class StructureIndex
{
    private readonly Library _library;

    internal StructureIndex(Library library, IReadOnlyList<StructureFact> overlayFacts)
    {
        _library = library;
        var facts = library.Packages.SelectMany(p => p.Result.Structure?.Facts ?? []).Concat(overlayFacts);
        Graph = new StructureGraph(facts);
    }

    /// <summary>The merged graph.</summary>
    public StructureGraph Graph { get; }

    /// <summary>The concept, or null when no layer declares it.</summary>
    public ConceptDetails? Concept(string iri)
    {
        if (!Graph.Concepts.TryGetValue(iri, out var c) || c.Declared is null)
        {
            return null;
        }

        return new ConceptDetails(
            iri,
            StructureGraph.Preferred(c.Labels),
            new Dictionary<string, string>(c.Labels),
            StructureGraph.Preferred(c.Definitions),
            c.Deprecated,
            Graph.In("replaces", iri).Select(e => e.Iri).ToList(),
            Graph.Out("replaces", iri).Select(e => e.Iri).ToList(),
            c.Declared.Layer);
    }

    /// <summary>
    /// What <paramref name="iri"/> requires. Direct: by IRI. Transitive: every concept after the concepts
    /// it requires (depth first, ties by IRI).
    /// </summary>
    public IReadOnlyList<ConceptItem> Requires(string iri, bool transitive = false)
    {
        if (!transitive)
        {
            return Graph.Out("requires", iri).Select(Item).ToList();
        }

        // Depth first and iterative, so a long chain cannot exhaust the call stack: each concept is
        // emitted after the concepts it requires.
        var result = new List<ConceptItem>();
        var done = new HashSet<string>(StringComparer.Ordinal) { iri };
        var frames = new Stack<(Edge? Edge, IEnumerator<Edge> Pending)>();
        frames.Push((null, Graph.Out("requires", iri).GetEnumerator()));
        while (frames.Count > 0)
        {
            var (edge, pending) = frames.Peek();
            if (!pending.MoveNext())
            {
                frames.Pop();
                if (edge is not null)
                {
                    result.Add(Item(edge));
                }

                continue;
            }

            var e = pending.Current;
            if (done.Add(e.Iri))
            {
                frames.Push((e, Graph.Out("requires", e.Iri).GetEnumerator()));
            }
        }

        return result;
    }

    /// <summary>What requires <paramref name="iri"/> (direct), by IRI.</summary>
    public IReadOnlyList<ConceptItem> RequiredBy(string iri) => Graph.In("requires", iri).Select(Item).ToList();

    /// <summary>Direct parts, by IRI.</summary>
    public IReadOnlyList<ConceptItem> Parts(string iri) => Graph.Out("hasPart", iri).Select(Item).ToList();

    /// <summary>Narrower concepts; transitively, breadth first (by depth, then IRI).</summary>
    public IReadOnlyList<ConceptItem> Narrower(string iri, bool transitive = false)
    {
        var result = new List<ConceptItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { iri };
        var level = new List<string> { iri };
        while (level.Count > 0)
        {
            var next = new List<ConceptItem>();
            foreach (var n in level)
            {
                foreach (var e in Graph.In("broader", n))
                {
                    if (seen.Add(e.Iri))
                    {
                        next.Add(Item(e));
                    }
                }
            }

            next.Sort((a, b) => string.CompareOrdinal(a.Iri, b.Iri));
            result.AddRange(next);
            if (!transitive)
            {
                break;
            }

            level = next.Select(x => x.Iri).ToList();
        }

        return result;
    }

    /// <summary>Direct broader concepts, by IRI.</summary>
    public IReadOnlyList<ConceptItem> Broader(string iri) => Graph.Out("broader", iri).Select(Item).ToList();

    /// <summary>Related concepts in either direction, by IRI.</summary>
    public IReadOnlyList<ConceptItem> Related(string iri)
    {
        var seen = new Dictionary<string, Edge>(StringComparer.Ordinal);
        foreach (var e in Graph.Out("related", iri).Concat(Graph.In("related", iri)))
        {
            seen.TryAdd(e.Iri, e);
        }

        return seen.Values.OrderBy(e => e.Iri, StringComparer.Ordinal).Select(Item).ToList();
    }

    /// <summary>The members of an ordered collection, in order; null when it isn't one.</summary>
    public IReadOnlyList<ConceptItem>? Sequence(string iri) =>
        Graph.Collections.TryGetValue(iri, out var c)
            ? c.Members.Select(m => Item(new Edge(m, c.Source.Layer, c.Source.Origin))).ToList()
            : null;

    /// <summary>Every concept below <paramref name="iri"/> by hierarchy or parts, transitively, by IRI.</summary>
    public IReadOnlyList<string> Descendants(string iri)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([iri]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var e in Graph.In("broader", n).Concat(Graph.Out("hasPart", n)))
            {
                if (e.Iri != iri && result.Add(e.Iri))
                {
                    stack.Push(e.Iri);
                }
            }
        }

        return result.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Citation records of the nodes bound to <paramref name="iri"/> and, with <paramref name="include"/>
    /// of narrower, parts or both, to its descendants. The default retrieval policy applies unless
    /// <paramref name="includeAll"/>. Ordered by <c>node.ref</c>.
    /// </summary>
    public IReadOnlyList<CitationRecord> Nodes(string iri, string include = "none", string? locale = null, bool includeAll = false)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal) { iri };
        if (include != "none")
        {
            var stack = new Stack<string>([iri]);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                var next = Enumerable.Empty<Edge>();
                if (include is "narrower" or "both")
                {
                    next = next.Concat(Graph.In("broader", n));
                }

                if (include is "parts" or "both")
                {
                    next = next.Concat(Graph.Out("hasPart", n));
                }

                foreach (var e in next)
                {
                    if (wanted.Add(e.Iri))
                    {
                        stack.Push(e.Iri);
                    }
                }
            }
        }

        return _library.Citations(locale, includeText: true)
            .Where(r => (r.Concepts ?? []).Any(wanted.Contains))
            .Where(r => includeAll || Citations.Citations.PassesDefaultPolicy(r))
            .OrderBy(r => r.Node.Ref, StringComparer.Ordinal)
            .ToList();
    }

    private ConceptItem Item(Edge e)
    {
        var label = Graph.Concepts.TryGetValue(e.Iri, out var c) ? StructureGraph.Preferred(c.Labels) : null;
        return new ConceptItem(e.Iri, label, e.Layer);
    }
}
