using System.Text.Json.Nodes;

namespace OpenMoca.Reader.Content;

/// <summary>
/// What a translation takes from its default file (package spec §5.4). A translation that does not write
/// <c>moca.audience</c>, <c>moca.concepts</c>, OKF <c>status</c>, <c>moca.valid_from</c> or <c>moca.valid_until</c> has
/// its default representation's value for each, key by key. A key the translation writes is its own, even when its
/// value is invalid.
/// </summary>
internal static class Inheritance
{
    /// <summary>The node's default-language representation, or null for an orphan translation (C009).</summary>
    public static Representation? DefaultOf(ContentNode node) => node.Representations.FirstOrDefault(r => r.Locale is null);

    /// <summary>
    /// The representation whose value of <paramref name="key"/> applies to <paramref name="rep"/>: its own when it
    /// writes the key, else, for a translation, its default representation's. <c>status</c> is a top-level key; every
    /// other key is under <c>moca</c>.
    /// </summary>
    public static Representation SourceOf(ContentNode node, Representation rep, string key)
    {
        if (rep.Locale is null || Writes(rep.Frontmatter, key))
        {
            return rep;
        }

        return DefaultOf(node) ?? rep;
    }

    /// <summary>The value of <paramref name="key"/> that applies to <paramref name="rep"/> (package spec §5.4).</summary>
    public static JsonNode? Inherited(ContentNode node, Representation rep, string key)
    {
        var source = SourceOf(node, rep, key);
        return key == "status" ? source.Frontmatter["status"] : (source.Frontmatter["moca"] as JsonObject)?[key];
    }

    private static bool Writes(JsonObject frontmatter, string key) =>
        key == "status" ? frontmatter.ContainsKey("status") : frontmatter["moca"] is JsonObject moca && moca.ContainsKey(key);
}
