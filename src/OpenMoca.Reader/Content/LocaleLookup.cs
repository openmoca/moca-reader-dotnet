namespace OpenMoca.Reader.Content;

/// <summary>
/// Choosing by language (package spec §5.4): RFC 4647 §3.4 Lookup over a priority list, with MOCA's two additions. At
/// each truncation of a request, a tag equal to it is taken first, then the lowest tag that starts with it and
/// <c>-</c>; and the default representation is tagged with its node's language.
/// </summary>
public static class LocaleLookup
{
    /// <summary>
    /// A request for a locale as a priority list (package spec §5.4): the non-empty entries, in order. Null, or a list
    /// with no non-empty entry, is no request.
    /// </summary>
    /// <param name="locale">The caller's tags, most preferred first.</param>
    public static IReadOnlyList<string> List(IEnumerable<string?>? locale) =>
        locale is null ? [] : [.. locale.Where(l => !string.IsNullOrEmpty(l)).Select(l => l!)];

    /// <summary>
    /// Package spec §5.4: for each entry of the priority list, and each truncation of it, the candidate whose tag
    /// equals it, else the candidate with the lowest tag (lower-cased, in code-point order) that starts with it and
    /// <c>-</c>. Tags are compared case-insensitively. A candidate with no tag is never matched here.
    /// </summary>
    /// <typeparam name="T">What is chosen.</typeparam>
    /// <param name="candidates">Each candidate with its tag.</param>
    /// <param name="locale">The priority list.</param>
    /// <returns>The value chosen, or null when no entry finds one.</returns>
    public static T? Lookup<T>(IEnumerable<(string? Tag, T Value)> candidates, IReadOnlyList<string>? locale)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var tagged = candidates.Where(c => !string.IsNullOrEmpty(c.Tag)).Select(c => (Lower: c.Tag!.ToLowerInvariant(), c.Value)).ToList();
        foreach (var range in List(locale))
        {
            foreach (var prefix in Truncations(range))
            {
                foreach (var c in tagged)
                {
                    if (c.Lower == prefix)
                    {
                        return c.Value;
                    }
                }

                var longer = tagged.Where(c => c.Lower.StartsWith(prefix + "-", StringComparison.Ordinal))
                    .OrderBy(c => c.Lower, StringComparer.Ordinal)
                    .Select(c => c.Value)
                    .FirstOrDefault();
                if (longer is not null)
                {
                    return longer;
                }
            }
        }

        return null;
    }

    /// <summary>Whether a language tag matches a request: the lookup over that one tag selects it (Reader contract §9).</summary>
    /// <param name="tag">The tag, or null for none (which matches nothing).</param>
    /// <param name="locale">The priority list.</param>
    public static bool Matches(string? tag, IReadOnlyList<string>? locale) =>
        Lookup([(tag, (object)tag!)], locale) is not null;

    /// <summary>
    /// A representation's tag (package spec §5.4): a translation's own, or, for the default representation, its node's
    /// language. Null when the default representation has none.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="rep">One of its representations.</param>
    public static string? RepresentationTag(ContentNode node, Representation rep)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(rep);
        return rep.Locale ?? node.Language;
    }

    /// <summary>The range, then each RFC 4647 §3.4 truncation of it: a trailing single-character subtag goes with the one after it.</summary>
    private static List<string> Truncations(string range)
    {
        var output = new List<string>();
        var parts = range.ToLowerInvariant().Split('-').ToList();
        while (parts.Count > 0)
        {
            output.Add(string.Join('-', parts));
            parts.RemoveAt(parts.Count - 1);
            if (parts.Count > 1 && parts[^1].Length == 1)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        return output;
    }
}
