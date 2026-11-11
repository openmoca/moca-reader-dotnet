using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Structure;

/// <summary>
/// Structure answers as JSON, in the shapes the conformance corpus and the MCP server use: concept items
/// as <c>{ iri, label?, layer }</c>, a concept as <c>{ iri, label?, labels, definition?, deprecated,
/// replacedBy, replaces, layer }</c>.
/// </summary>
public static class StructureJson
{
    /// <summary>A concept, or null.</summary>
    public static JsonNode? Concept(ConceptDetails? c)
    {
        if (c is null)
        {
            return null;
        }

        var obj = new JsonObject { ["iri"] = c.Iri };
        if (c.Label is not null)
        {
            obj["label"] = c.Label;
        }

        var labels = new JsonObject();
        foreach (var (lang, value) in c.Labels)
        {
            labels[lang] = value;
        }

        obj["labels"] = labels;
        if (c.Definition is not null)
        {
            obj["definition"] = c.Definition;
        }

        obj["deprecated"] = c.Deprecated;
        obj["replacedBy"] = Strings(c.ReplacedBy);
        obj["replaces"] = Strings(c.Replaces);
        obj["layer"] = c.Layer;
        return obj;
    }

    /// <summary>Concept items.</summary>
    public static JsonArray Items(IEnumerable<ConceptItem> items) => new([.. items.Select(x =>
    {
        var obj = new JsonObject { ["iri"] = x.Iri };
        if (x.Label is not null)
        {
            obj["label"] = x.Label;
        }

        obj["layer"] = x.Layer;
        return (JsonNode)obj;
    })]);

    /// <summary>A list of strings.</summary>
    public static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);
}
