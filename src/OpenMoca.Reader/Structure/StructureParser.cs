using System.Text;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Diagnostics;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Parsing.Handlers;
using VDS.RDF.Parsing.Tokens;

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
        [$"{Skos}altLabel"] = ("altLabel", false),
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


    /// <summary>The facts a Turtle document states, the prefixes it declares, and whether it had no O problem.</summary>
    public sealed record ParseResult(IReadOnlyList<StructureFact> Facts, IReadOnlySet<string> Prefixes, bool Ok);

    /// <summary>
    /// How deeply blank-node property lists (<c>[ ]</c>) and collections (<c>( )</c>) may nest in a structure file, each
    /// one a level, the outermost level 1. Fixed, not a default, as for JSON and YAML (contract §3, "Nesting").
    /// </summary>
    internal const int MaxNesting = 64;

    /// <summary>
    /// The deepest nesting of blank-node property lists and collections in Turtle text. The text is split into tokens
    /// by the tokeniser the parser itself uses, so names, IRIs, strings and comments are read by Turtle's own rules
    /// (escaped characters in a name included), and the count never depends on the call stack. Unbalanced text is
    /// left to the parser to report. Throws <see cref="RdfParseException"/> where the parser would.
    /// </summary>
    internal static int NestingDepth(string text)
    {
        var tokeniser = new TurtleTokeniser(new StringReader(text), TurtleSyntax.W3C, false, false);
        int depth = 0, deepest = 0;
        for (var token = tokeniser.GetNextToken(); token.TokenType != Token.EOF; token = tokeniser.GetNextToken())
        {
            switch (token.TokenType)
            {
                case Token.LEFTSQBRACKET or Token.LEFTBRACKET or Token.STARTQUOTE:
                    deepest = Math.Max(deepest, ++depth);
                    if (deepest > MaxNesting)
                    {
                        // Deep enough to refuse; the rest of the text need not be read.
                        return deepest;
                    }

                    break;
                case Token.RIGHTSQBRACKET or Token.RIGHTBRACKET or Token.ENDQUOTE:
                    depth = Math.Max(0, depth - 1);
                    break;
            }
        }

        return deepest;
    }

    /// <summary>
    /// The prefixes a Turtle text declares: a line that starts, after white space, with <c>@prefix</c> or
    /// <c>PREFIX</c> (any case), white space, then an optional name and a colon. One pass over the text; white space
    /// that runs across several lines is read once, however many lines it covers.
    /// </summary>
    internal static List<string> DeclaredPrefixes(string text)
    {
        var names = new List<string>();
        var n = text.Length;
        var i = 0;

        // True when a line start reaches position i through white space alone.
        var lineStart = true;
        long steps = 0;
        while (i < n)
        {
            if (!lineStart)
            {
                var newline = text.IndexOf('\n', i);
                steps += (newline < 0 ? n : newline + 1) - i;
                if (newline < 0)
                {
                    break;
                }

                i = newline + 1;
                lineStart = true;
                continue;
            }

            for (; i < n && IsSpace(text[i]); i++)
            {
                steps++;
            }

            var keyword = KeywordAt(text, i);
            if (keyword == 0)
            {
                lineStart = false;
                continue;
            }

            var afterKeyword = i + keyword;
            var name = afterKeyword;
            var crossesLine = false;
            for (; name < n && IsSpace(text[name]); name++)
            {
                crossesLine |= text[name] == '\n';
                steps++;
            }

            if (name == afterKeyword)
            {
                i = afterKeyword;
                lineStart = false;
                continue;
            }

            var colon = name;
            if (colon < n && char.IsAsciiLetter(text[colon]))
            {
                for (colon++; colon < n && (char.IsAsciiLetterOrDigit(text[colon]) || text[colon] is '_' or '.' or '-'); colon++)
                {
                    steps++;
                }
            }

            if (colon < n && text[colon] == ':')
            {
                if (colon > name)
                {
                    names.Add(text[name..colon]);
                }

                i = colon + 1;
                lineStart = false;
                continue;
            }

            // No declaration here. A line that starts inside the white space after the keyword reaches the same
            // place, so the search goes on from there.
            i = name;
            lineStart = crossesLine;
        }

        Content.TextWork.Add(steps);
        return names;
    }

    // White space as the reference Reader's \s reads it (ECMAScript white space and line terminators).
    private static bool IsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680'
        or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';

    private static int KeywordAt(string text, int i) =>
        text.AsSpan(i).StartsWith("@prefix", StringComparison.OrdinalIgnoreCase) ? 7
        : text.AsSpan(i).StartsWith("prefix", StringComparison.OrdinalIgnoreCase) ? 6
        : 0;

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
        prefixes.UnionWith(DeclaredPrefixes(source));

        var handler = new OrderedTripleHandler();
        try
        {
            // Contract §3: the parser follows nested blank nodes and collections by recursion, so their depth is
            // bounded before it runs, counted on the parser's own tokens.
            if (NestingDepth(source) > MaxNesting)
            {
                diagnostics.Add(DiagnosticCodes.O001OntologyUnparseable, $"{file} nests blank nodes or collections more than {MaxNesting} levels deep", file);
                return new ParseResult(facts, prefixes, false);
            }

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

        // Contract §3: a member list is walked once, however many collections name it, and the list cells reached from
        // every skos:memberList statement, added together, stay within the triple limit.
        var walked = new Dictionary<string, (List<(string Member, int Index)> Items, int Cells)>(StringComparer.Ordinal);
        (List<(string Member, int Index)> Items, int Cells) MembersOf(INode head)
        {
            if (head is not IBlankNode first)
            {
                return ([], 0);
            }

            if (walked.TryGetValue(first.InternalID, out var known))
            {
                return known;
            }

            var items = new List<(string, int)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var node = head;
            var index = 0;
            for (; node is IBlankNode cellNode && seen.Add(cellNode.InternalID); index++)
            {
                if (!lists.TryGetValue(cellNode.InternalID, out var cell) || cell.Head is null)
                {
                    break;
                }

                if (cell.Head is IUriNode member)
                {
                    items.Add((Iri(member), index));
                }

                node = cell.Tail;
            }

            walked[first.InternalID] = (items, index);
            return (items, index);
        }

        long cells = 0;

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
                // A list reached again costs its cells again: a walk that starts part-way down another list is new work.
                var (items, listCells) = MembersOf(t.Object);
                cells += Math.Max(listCells, 1);
                if (cells > limits.MaxTriples)
                {
                    diagnostics.Add(DiagnosticCodes.O001OntologyUnparseable,
                        $"{file} has more than {limits.MaxTriples} collection members, counting each collection's member list in full", file);
                    return new ParseResult([], prefixes, false);
                }

                foreach (var (member, index) in items)
                {
                    Add(new StructureFact(s, "member", O: member, Index: index));
                }

                continue;
            }

            if (!Predicates.TryGetValue(predicate, out var known))
            {
                continue;
            }

            var (kind, flip) = known;
            if (kind is "label" or "altLabel" or "definition")
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
