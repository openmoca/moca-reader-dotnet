using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader.Search;

/// <summary>
/// The default ontology-guided retrieval strategy (moca-spec ADR-0010). Opt-in: pass its hooks to
/// <see cref="MocaSearch"/>. Applications and domain extensions may replace it.
/// </summary>
/// <remarks>
/// Ingress: when the query names concepts by their label, search those concepts and their descendants.
/// Egress: after each result, add the nodes of the concepts it requires (transitively), tagged
/// <c>retrieval.via = "requires"</c>. The gate still applies the retrieval policy to everything added.
/// </remarks>
public static class OntologyGuided
{
    /// <summary>The hooks.</summary>
    public static SearchHooks Hooks(bool expandRequires = true) => new()
    {
        Ingress = (query, ctx, _) => ValueTask.FromResult(Ingress(query, ctx)),
        Egress = (candidates, ctx, _) => ValueTask.FromResult(expandRequires ? Egress(candidates, ctx) : candidates),
    };

    private static IngressResult? Ingress(string query, HookContext ctx)
    {
        if (ctx.Structure is null)
        {
            return null;
        }

        var words = Bm25.Tokens(query).ToHashSet(StringComparer.Ordinal);
        var matched = new List<string>();
        foreach (var c in ctx.Structure.Graph.Concepts.Values)
        {
            if (c.Declared is null)
            {
                continue;
            }

            var label = StructureGraph.Preferred(c.Labels);
            var labelWords = label is null ? [] : Bm25.Tokens(label);
            if (labelWords.Count > 0 && labelWords.All(words.Contains))
            {
                matched.Add(c.Iri);
            }
        }

        if (matched.Count == 0)
        {
            return null;
        }

        var concepts = new HashSet<string>(matched, StringComparer.Ordinal);
        foreach (var iri in matched)
        {
            concepts.UnionWith(ctx.Structure.Descendants(iri));
        }

        return new IngressResult(Concepts: concepts.Order(StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<CitationRecord> Egress(IReadOnlyList<CitationRecord> candidates, HookContext ctx)
    {
        if (ctx.Structure is null)
        {
            return candidates;
        }

        var result = new List<CitationRecord>();
        var seen = candidates.Select(r => r.Node.Ref).ToHashSet(StringComparer.Ordinal);
        foreach (var record in candidates)
        {
            result.Add(record);
            foreach (var concept in record.Concepts ?? [])
            {
                foreach (var required in ctx.Structure.Requires(concept, transitive: true))
                {
                    foreach (var node in ctx.Structure.Nodes(required.Iri, locale: ctx.Request.Locale))
                    {
                        if (!seen.Add(node.Node.Ref))
                        {
                            continue;
                        }

                        result.Add(node with { Score = record.Score, Retrieval = new CitationRetrieval("requires", concept) });
                    }
                }
            }
        }

        return result;
    }
}
