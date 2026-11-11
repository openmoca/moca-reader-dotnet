using OpenMoca.Reader.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Structure;

/// <summary>A package's structure layer as read (package spec §5.6).</summary>
/// <param name="Present">The package has <c>structure.ttl</c>.</param>
/// <param name="Ok">Present, parsed, and every relation and binding resolved with no O diagnostic.</param>
/// <param name="Facts">The package-layer facts.</param>
/// <param name="Graph">The graph over those facts, or null when there is no <c>structure.ttl</c>.</param>
public sealed record PackageStructure(bool Present, bool Ok, IReadOnlyList<StructureFact> Facts, StructureGraph? Graph)
{
    /// <summary>The structure file.</summary>
    public const string StructureFile = "structure.ttl";

    internal static PackageStructure Read(
        IReadOnlyDictionary<string, byte[]> bytes,
        IReadOnlyList<ContentNode> nodes,
        DiagnosticBag diagnostics,
        ReaderLimits limits,
        string? origin)
    {
        bytes.TryGetValue(StructureFile, out var ttl);
        var bound = nodes.SelectMany(n => n.Representations).Where(r => r.Moca["concepts"] is JsonArray).ToList();
        if (ttl is null && bound.Count == 0)
        {
            return new PackageStructure(false, false, [], null);
        }

        var before = diagnostics.Count;
        if (ttl is null)
        {
            foreach (var rep in bound)
            {
                diagnostics.Add(DiagnosticCodes.O002ConceptUndeclared, $"concepts are bound but the package has no {StructureFile}", rep.File);
            }

            return new PackageStructure(false, false, [], null);
        }

        var parsed = StructureParser.Parse(ttl, StructureFile, StructureLayers.Package, origin ?? "", diagnostics, limits);
        var graph = new StructureGraph(parsed.Facts);
        foreach (var relation in new[] { "requires", "broader", "hasPart" })
        {
            if (graph.Cycle(relation) is { } loop)
            {
                diagnostics.Add(DiagnosticCodes.O005StructureCycle, $"{relation} forms a cycle: {string.Join(" -> ", loop)}", StructureFile);
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in StructureGraph.Relations)
        {
            foreach (var s in graph.Subjects(relation))
            {
                referenced.Add(s);
                foreach (var e in graph.Out(relation, s))
                {
                    referenced.Add(e.Iri);
                }
            }
        }

        foreach (var c in graph.Collections.Values)
        {
            referenced.UnionWith(c.Members);
        }

        foreach (var iri in referenced.Order(StringComparer.Ordinal))
        {
            if (!graph.IsDeclared(iri))
            {
                diagnostics.Add(DiagnosticCodes.O002ConceptUndeclared, $"{iri} is used in a relation but not declared as a skos:Concept", StructureFile);
            }
        }

        foreach (var rep in bound)
        {
            var concepts = new List<string>();
            foreach (var binding in (JsonArray)rep.Moca["concepts"]!)
            {
                var iri = JsonValues.StringOf((binding as JsonObject)?["iri"]);
                if (iri is null || !StructureParser.AbsoluteIri().IsMatch(iri))
                {
                    diagnostics.Add(DiagnosticCodes.O003RemoteReference, $"concept \"{iri}\" is not an absolute IRI", rep.File);
                    continue;
                }

                if (parsed.Prefixes.Contains(iri[..iri.IndexOf(':')]) && !graph.IsDeclared(iri))
                {
                    diagnostics.Add(DiagnosticCodes.O003RemoteReference, $"concept \"{iri}\" is a prefixed name; write the absolute IRI", rep.File);
                    continue;
                }

                if (!graph.IsDeclared(iri))
                {
                    diagnostics.Add(DiagnosticCodes.O002ConceptUndeclared, $"concept {iri} is not declared in {StructureFile}", rep.File);
                }

                concepts.Add(iri);
            }

            if (concepts.Count > 0)
            {
                rep.Concepts = concepts.Distinct(StringComparer.Ordinal).ToList();
            }
        }

        var ok = !diagnostics.Since(before).Any(d => d.Code.StartsWith('O'));
        return new PackageStructure(true, ok, parsed.Facts, graph);
    }
}
