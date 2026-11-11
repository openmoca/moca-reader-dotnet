using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenMoca.Reader.Content;

/// <summary>
/// Dates and date-times as package fields and trust roots write them (package spec §4.4, attestations §6). A full
/// date (YYYY-MM-DD) is 00:00:00Z on that day. A date-time is RFC 3339 and carries an offset (Z or ±hh:mm); without
/// one it does not parse, so no Reader guesses a time zone. Anything else does not parse.
/// </summary>
public static partial class Dates
{
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})\z")]
    private static partial Regex FullDate();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})\z")]
    private static partial Regex DateTimeWithOffset();

    /// <summary>Parses a string node as a UTC instant, or returns null when it isn't a date.</summary>
    public static DateTimeOffset? Parse(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? Parse(s) : null;

    /// <summary>Parses a string as a UTC instant, or returns null when it isn't a date.</summary>
    public static DateTimeOffset? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (FullDate().IsMatch(text))
        {
            return DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date
                : null;
        }

        if (!DateTimeWithOffset().IsMatch(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text.ToUpperInvariant(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The node's validity window (package spec §4.4): the intersection of the package's window and its own. A bound
    /// that does not parse, or a window whose end is not after its start, is ignored.
    /// </summary>
    /// <returns>The bounds as written (or null) and as instants (or null).</returns>
    public static (string? From, string? Until, DateTimeOffset? FromAt, DateTimeOffset? UntilAt) EffectiveWindow(
        string? packageFrom, string? packageUntil, string? nodeFrom, string? nodeUntil)
    {
        static (DateTimeOffset? F, DateTimeOffset? U) Window(string? from, string? until)
        {
            var f = Parse(from);
            var u = Parse(until);
            return f is not null && u is not null && u <= f ? (null, null) : (f, u);
        }

        var (pf, pu) = Window(packageFrom, packageUntil);
        var (nf, nu) = Window(nodeFrom, nodeUntil);
        var (from, fromAt) = nf is not null && (pf is null || nf >= pf) ? (nodeFrom, nf) : (packageFrom, pf);
        var (until, untilAt) = nu is not null && (pu is null || nu <= pu) ? (nodeUntil, nu) : (packageUntil, pu);
        return (fromAt is null ? null : from, untilAt is null ? null : until, fromAt, untilAt);
    }
}
