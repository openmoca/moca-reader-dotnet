using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace OpenMoca.Reader.Json;

/// <summary>The spec's JSON Schemas (<c>schemas/v1</c>), embedded, and validation against them.</summary>
public static partial class MocaSchemas
{
    /// <summary>Schema names, as in <c>schemas/v1/&lt;name&gt;.schema.json</c>.</summary>
    public const string Manifest = "manifest";

    /// <summary>Node frontmatter.</summary>
    public const string Node = "node";

    /// <summary>Citation records.</summary>
    public const string CitationRecord = "citation-record";

    /// <summary>Sidecar index.json.</summary>
    public const string SidecarIndex = "sidecar-index";

    /// <summary>Review attestation predicates.</summary>
    public const string ReviewPredicate = "review-predicate";

    /// <summary>Host trust roots.</summary>
    public const string TrustRoot = "trust-root";

    /// <summary>Search backend hits.</summary>
    public const string SearchHit = "search-hit";

    /// <summary>The derived structure view.</summary>
    public const string Structure = "structure";

    /// <summary>An envelope's moca-enc.json (encrypted packages §3).</summary>
    public const string MocaEnc = "moca-enc";

    /// <summary>The creator's terms (package spec §4.14).</summary>
    public const string Terms = "terms";

    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(Load);

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.Hierarchical,
        // ajv-formats in the reference Reader checks formats (date, date-time); so do we.
        RequireFormatValidation = true,
    };

    /// <summary>The raw text of a schema, as embedded.</summary>
    public static string Text(string name)
    {
        using var stream = typeof(MocaSchemas).Assembly.GetManifestResourceStream($"OpenMoca.Reader.Schemas.{name}.schema.json")
            ?? throw new ArgumentException($"No embedded schema named {name}.", nameof(name));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Validates a value; returns human-readable errors, empty when valid.</summary>
    public static IReadOnlyList<string> Validate(string name, JsonNode? value)
    {
        var element = value is null
            ? JsonDocument.Parse("null").RootElement
            : JsonSerializer.SerializeToElement(value, JsonNodeContext.Default.JsonNode);
        var results = Schemas.Value[name].Evaluate(element, Options);
        if (results.IsValid)
        {
            return [];
        }

        // Only the deepest failing places: a nested schema repeats its failure at every level above it. Of the
        // failing branches of a oneOf or anyOf, only the one that got furthest into the value is reported.
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (where, message) in Failures(results))
        {
            var text = $"{(where.Length > 0 ? where : "(root)")} {message}";
            if (seen.Add(text))
            {
                errors.Add(text);
            }
        }

        return errors.Count > 0 ? errors : ["(root) does not match the schema"];
    }

    /// <summary>
    /// Every failing keyword, flat, as (instance location as a JSON Pointer, evaluation path, keyword, message). A false
    /// schema (as under <c>additionalProperties: false</c>) fails with the keyword <c>""</c> at the rejected member.
    /// For callers that classify failures, such as the terms check (package spec §4.14).
    /// </summary>
    internal static List<(string Where, string Path, string Keyword, string Message)> Failures(string name, JsonNode? value)
    {
        var element = value is null
            ? JsonDocument.Parse("null").RootElement
            : JsonSerializer.SerializeToElement(value, JsonNodeContext.Default.JsonNode);
        var results = Schemas.Value[name].Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        var output = new List<(string, string, string, string)>();
        if (results.IsValid)
        {
            return output;
        }

        void Walk(EvaluationResults node)
        {
            foreach (var (keyword, message) in node.Errors ?? new Dictionary<string, string>())
            {
                output.Add((node.InstanceLocation.ToString(), node.EvaluationPath.ToString(), keyword, message));
            }

            foreach (var detail in node.Details ?? [])
            {
                Walk(detail);
            }
        }

        Walk(results);
        return output;
    }

    // Keywords whose error only says that a subschema below failed; that failure is reported where it happened.
    private static readonly HashSet<string> Summaries = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "additionalProperties", "items", "prefixItems", "contains", "allOf", "anyOf",
        "oneOf", "not", "$ref", "$dynamicRef", "if", "then", "else", "dependentSchemas", "propertyNames",
        "unevaluatedProperties", "unevaluatedItems",
    };

    // A oneOf or anyOf branch at the end of an evaluation path: the keyword and the branch's index.
    [System.Text.RegularExpressions.GeneratedRegex(@"/(oneOf|anyOf)/(\d+)$")]
    private static partial System.Text.RegularExpressions.Regex Branch();

    /// <summary>The deepest failing places below a failing node, as (instance location, message).</summary>
    private static List<(string Where, string Message)> Failures(EvaluationResults node)
    {
        var below = (node.Details ?? []).Where(d => !d.IsValid).ToList();
        var output = new List<(string Where, string Message)>();
        var own = node.Errors ?? new Dictionary<string, string>();

        // Branches of a combinator are reported only when the combinator itself failed, and then only one of them.
        var branches = below
            .Select(d => (Detail: d, Match: Branch().Match(d.EvaluationPath.ToString())))
            .Where(x => x.Match.Success)
            .ToList();
        var others = below.Except(branches.Select(x => x.Detail)).ToList();
        foreach (var combinator in branches.GroupBy(x => x.Match.Groups[1].Value, StringComparer.Ordinal))
        {
            if (!own.ContainsKey(combinator.Key))
            {
                continue;
            }

            var chosen = combinator
                .Select(x => (x.Match, Found: Failures(x.Detail)))
                .OrderByDescending(x => x.Found.Count == 0 ? -1 : x.Found.Max(f => f.Where.Count(c => c == '/')))
                .ThenBy(x => int.Parse(x.Match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                .First();
            output.AddRange(chosen.Found);
        }

        foreach (var d in others)
        {
            output.AddRange(Failures(d));
        }

        var where = node.InstanceLocation.ToString();
        foreach (var (keyword, message) in own)
        {
            // A summary keyword says only that something below failed; keep it when nothing below was reported.
            if (!Summaries.Contains(keyword) || output.Count == 0)
            {
                output.Add((where, message));
            }
        }

        return output;
    }

    private static Dictionary<string, JsonSchema> Load()
    {
        // Each schema gets its own registry, so loading twice (tests, several readers) never collides.
        string[] names = [Manifest, Node, CitationRecord, SidecarIndex, ReviewPredicate, TrustRoot, SearchHit, Structure, MocaEnc, Terms];
        return names.ToDictionary(
            n => n,
            n => JsonSchema.FromText(Text(n), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
    }
}
