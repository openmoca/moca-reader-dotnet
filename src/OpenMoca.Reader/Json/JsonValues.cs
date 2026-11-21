using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Json;

/// <summary>Reading loosely typed JSON (manifests, frontmatter, cases) the way JavaScript would.</summary>
public static class JsonValues
{
    /// <summary>Compact JSON with non-ASCII unescaped, as JavaScript's JSON.stringify writes it.</summary>
    public static JsonSerializerOptions CompactJson { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Parses JSON from a package or a sidecar. I-JSON (RFC 7493 §2.1): a string may not hold an unpaired surrogate, so
    /// an escape that makes one is a <see cref="JsonException"/>, as any other invalid JSON is. .NET would otherwise
    /// accept it and throw later, when the string is read.
    /// </summary>
    /// <summary>Contract §3: how deep JSON and YAML frontmatter in a package or a sidecar may nest. Fixed, not a default.</summary>
    internal const int MaxDepth = 64;

    internal static JsonNode? Parse(string text, JsonDocumentOptions options = default)
    {
        // Contract §3, "Nesting": at most 64 levels, the outermost object or array being level 1; deeper text is refused
        // by the reader as it goes, without recursion. Fixed here, never left to the library's default.
        var node = JsonNode.Parse(text, documentOptions: options with { MaxDepth = MaxDepth });
        if (HasUnpairedSurrogateEscape(text))
        {
            throw new JsonException("a string holds an unpaired surrogate (I-JSON, RFC 7493)");
        }

        return node;
    }

    /// <summary>
    /// Whether valid JSON text has a <c>\u</c> escape for a surrogate that is not one half of a pair. One pass; raw
    /// text cannot hold a surrogate, since it was decoded from UTF-8.
    /// </summary>
    internal static bool HasUnpairedSurrogateEscape(string text)
    {
        var pendingHigh = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                if (pendingHigh)
                {
                    return true;
                }

                continue;
            }

            if (text[i + 1] != 'u' || i + 5 >= text.Length
                || !int.TryParse(text.AsSpan(i + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var unit))
            {
                // Any other escape (\\, \", \n, ...) is two characters.
                if (pendingHigh)
                {
                    return true;
                }

                i++;
                continue;
            }

            if (unit is >= 0xDC00 and <= 0xDFFF)
            {
                if (!pendingHigh)
                {
                    return true;
                }

                pendingHigh = false;
            }
            else if (pendingHigh)
            {
                return true;
            }
            else
            {
                pendingHigh = unit is >= 0xD800 and <= 0xDBFF;
            }

            i += 5;
        }

        return pendingHigh;
    }

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
