using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Json;

/// <summary>Reading loosely typed JSON (manifests, frontmatter, cases) the way JavaScript would.</summary>
public static class JsonValues
{
    /// <summary>Compact JSON with non-ASCII unescaped, as JavaScript's JSON.stringify writes it.</summary>
    public static JsonSerializerOptions CompactJson { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The node's string value, or null when it isn't a string.</summary>
    public static string? StringOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A JSON number that is an integer (as JavaScript's Number.isInteger sees it), or null.</summary>
    public static long? IntegerOf(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Floor(d) == d)
        {
            return (long)d;
        }

        return null;
    }
}
