using System.Text;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Diagnostics;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Parsing.Handlers;

namespace OpenMoca.Reader.Structure;

/// <summary>
/// Parses Turtle (a package's <c>structure.ttl</c> or a host overlay) into core structure facts
/// (package spec §5.6). Only the SKOS, DCMI and OWL terms the spec names are understood; others are
/// ignored. Nothing is fetched: <c>owl:imports</c> and non-absolute IRIs are reported (O003), never followed.
/// </summary>
public static partial class StructureParser
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Skos = "http://www.w3.org/2004/02/skos/core#";
    private const string Dct = "http://purl.org/dc/terms/";
    private const string Owl = "http://www.w3.org/2002/07/owl#";

    /// <summary>Core predicates, normalised to one direction: (kind, flip).</summary>
    private static readonly Dictionary<string, (string Kind, bool Flip)> Predicates = new(StringComparer.Ordinal)
    {
        [$"{Skos}prefLabel"] = ("label", false),
        [$"{Skos}definition"] = ("definition", false),
        [$"{Skos}broader"] = ("broader", false),
        [$"{Skos}narrower"] = ("broader", true),
        [$"{Skos}related"] = ("related", false),
        [$"{Dct}hasPart"] = ("hasPart", false),
        [$"{Dct}isPartOf"] = ("hasPart", true),
        [$"{Dct}requires"] = ("requires", false),
        [$"{Dct}isRequiredBy"] = ("requires", true),
        [$"{Dct}replaces"] = ("replaces", false),
        [$"{Dct}isReplacedBy"] = ("replaces", true),
        [$"{Owl}deprecated"] = ("deprecated", false),
    };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:[^\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+\z")]
    internal static partial Regex AbsoluteIri();

    [GeneratedRegex(@"^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*(?:@prefix|PREFIX)[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+([A-Za-z][A-Za-z0-9_.-]*)?:", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PrefixDeclaration();

    /// <summary>The facts a Turtle document states, the prefixes it declares, and whether it had no O problem.</summary>
    public sealed record ParseResult(IReadOnlyList<StructureFact> Facts, IReadOnlySet<string> Prefixes, bool Ok);

    /// <summary>Parses Turtle into facts; problems are reported as O diagnostics.</summary>
    public static ParseResult Parse(byte[] turtle, string file, string layer, string origin, DiagnosticBag diagnostics, ReaderLimits limits)
    {
        var before = diagnostics.Count;
        var facts = new List<StructureFact>();
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        if (turtle.Length > limits.MaxStructureBytes)
        {
            diagnostics.Add(DiagnosticCodes.O001OntologyUnparseable, $"{file} is larger than the {limits.MaxStructureBytes}-byte limit", file);
            return new ParseResult(facts, prefixes, false);
        }

        var source = Encoding.UTF8.GetString(turtle);
        foreach (Match m in PrefixDeclaration().Matches(source))
        {
            if (m.Groups[1].Success && m.Groups[1].Value.Length > 0)
            {
                prefixes.Add(m.Groups[1].Value);
            }
        }

        var handler = new OrderedTripleHandler();
        try
        {
            new TurtleParser(TurtleSyntax.W3C, false).Load(handler, new StringReader(source));
        }
        catch (Exception ex) when (ex is RdfException or UriFormatException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(DiagnosticCodes.O001OntologyUnparseable, $"{file} is not parseable Turtle: {ex.Message}", file);
            return new ParseResult(facts, prefixes, false);
        }

        var triples = handler.Triples;
        if (triples.Count > limits.MaxTriples)
        {
            diagnostics.Add(DiagnosticCodes.O001OntologyUnparseable, $"{file} has {triples.Count} triples, over the {limits.MaxTriples} limit", file);
            return new ParseResult(facts, prefixes, false);
        }

        // RDF lists (for skos:memberList): blank node -> (first, rest).
        var lists = new Dictionary<string, (INode? Head, INode? Tail)>(StringComparer.Ordinal);
        foreach (var t in triples)
        {
            if (t.Subject is IBlankNode blank && t.Predicate is IUriNode p
                && (Iri(p) == $"{Rdf}first" || Iri(p) == $"{Rdf}rest"))
            {
                lists.TryGetValue(blank.InternalID, out var cell);
                lists[blank.InternalID] = Iri(p) == $"{Rdf}first" ? (t.Object, cell.Tail) : (cell.Head, t.Object);
            }
        }

        void Add(StructureFact fact) => facts.Add(fact with { Layer = layer, Origin = origin });

        foreach (var t in triples)
        {
            var predicate = t.Predicate is IUriNode pu ? Iri(pu) : "";
            if (predicate == $"{Owl}imports")
            {
                diagnostics.Add(DiagnosticCodes.O003RemoteReference,
                    $"owl:imports {NodeValue(t.Object)} is not allowed; structure must be self-contained", file);
            }

            foreach (var term in new[] { t.Subject, t.Predicate, t.Object })
            {
                if (term is IUriNode named && !AbsoluteIri().IsMatch(Iri(named)))
                {
                    diagnostics.Add(DiagnosticCodes.O003RemoteReference, $"IRI <{Iri(named)}> is not absolute", file);
                }
            }

            if (t.Subject is not IUriNode subject)
            {
                continue;
            }

            var s = Iri(subject);
            if (predicate == $"{Rdf}type")
            {
                var type = NodeValue(t.Object);
                if (type == $"{Skos}Concept")
                {
                    Add(new StructureFact(s, "concept"));
                }

                if (type == $"{Skos}OrderedCollection")
                {
                    Add(new StructureFact(s, "collection"));
                }

                continue;
            }

            if (predicate == $"{Skos}memberList")
            {
                var node = t.Object;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; node is IBlankNode cellNode && seen.Add(cellNode.InternalID); index++)
                {
                    if (!lists.TryGetValue(cellNode.InternalID, out var cell) || cell.Head is null)
                    {
                        break;
                    }

                    if (cell.Head is IUriNode member)
                    {
                        Add(new StructureFact(s, "member", O: Iri(member), Index: index));
                    }

                    node = cell.Tail;
                }

                continue;
            }

            if (!Predicates.TryGetValue(predicate, out var known))
            {
                continue;
            }

            var (kind, flip) = known;
            if (kind is "label" or "definition")
            {
                if (t.Object is ILiteralNode literal)
                {
                    Add(new StructureFact(s, kind, Value: literal.Value, Lang: literal.Language ?? ""));
                }
            }
            else if (kind == "deprecated")
            {
                if (t.Object is ILiteralNode literal && literal.Value == "true")
                {
                    Add(new StructureFact(s, kind));
                }
            }
            else if (t.Object is IUriNode obj)
            {
                Add(flip ? new StructureFact(Iri(obj), kind, O: s) : new StructureFact(s, kind, O: Iri(obj)));
            }
        }

        var ok = !diagnostics.Since(before).Any(d => d.Code.StartsWith('O'));
        return new ParseResult(facts, prefixes, ok);
    }

    /// <summary>The IRI exactly as written (resolved), without System.Uri canonicalisation.</summary>
    internal static string Iri(IUriNode node) => node.Uri.OriginalString;

    private static string NodeValue(INode node) => node switch
    {
        IUriNode u => Iri(u),
        ILiteralNode l => l.Value,
        IBlankNode b => b.InternalID,
        _ => node.ToString() ?? "",
    };

    /// <summary>Keeps triples in document order, which the reference Reader's parser also does.</summary>
    private sealed class OrderedTripleHandler : BaseRdfHandler
    {
        public List<Triple> Triples { get; } = [];

        public override bool AcceptsAll => true;

        protected override bool HandleTripleInternal(Triple t)
        {
            Triples.Add(t);
            return true;
        }

        protected override bool HandleQuadInternal(Triple t, IRefNode graph)
        {
            Triples.Add(t);
            return true;
        }
    }
}
