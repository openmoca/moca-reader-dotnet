using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace OpenMoca.Reader.Json;

/// <summary>The spec's JSON Schemas (<c>schemas/v1</c>), embedded, and validation against them.</summary>
public static class MocaSchemas
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

    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(Load);

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
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

        var errors = new List<string>();
        foreach (var detail in (results.Details ?? []).Prepend(results))
        {
            if (detail.Errors is null)
            {
                continue;
            }

            var where = detail.InstanceLocation.ToString();
            foreach (var error in detail.Errors.Values)
            {
                var message = $"{(where.Length > 0 ? where : "(root)")} {error}";
                if (!errors.Contains(message))
                {
                    errors.Add(message);
                }
            }
        }

        return errors.Count > 0 ? errors : ["(root) does not match the schema"];
    }

    private static Dictionary<string, JsonSchema> Load()
    {
        // Each schema gets its own registry, so loading twice (tests, several readers) never collides.
        string[] names = [Manifest, Node, CitationRecord, SidecarIndex, ReviewPredicate, TrustRoot, SearchHit, Structure];
        return names.ToDictionary(
            n => n,
            n => JsonSchema.FromText(Text(n), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
    }
}
