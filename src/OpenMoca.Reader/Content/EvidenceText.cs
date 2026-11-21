using OpenMoca.Reader.Json;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenMoca.Reader.Content;

/// <summary>The kinds of source a Reader can check evidence against (Reader contract §7).</summary>
public enum SourceKind
{
    /// <summary><c>.txt</c>, <c>.text</c>: the text, unchanged.</summary>
    Text,

    /// <summary><c>.md</c>, <c>.markdown</c>.</summary>
    Markdown,

    /// <summary><c>.html</c>, <c>.htm</c>.</summary>
    Html,

    /// <summary><c>.vtt</c> (WebVTT captions and transcripts).</summary>
    WebVtt,
}

/// <summary>A WebVTT cue: its time range in seconds and its payload text.</summary>
public sealed record VttCue(double Start, double End, string Text);

/// <summary>
/// Evidence checks against sources inside the package (Reader contract §7). W3C Web Annotation says
/// selector text "MUST be normalized"; these are MOCA's exact normalisation rules per source type, so
/// every Reader reaches the same answer. Positions count Unicode code points of the normalised text.
/// </summary>
public static partial class EvidenceText
{
    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = "\u00a0",
    };

    [GeneratedRegex(@"&(#x[0-9a-f]+|#[0-9]+|[a-z]+);", RegexOptions.IgnoreCase)]
    private static partial Regex Entity();

    [GeneratedRegex(@"^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*#{1,6}[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*(?:[-*+]|[0-9]+[.)])[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+")]
    private static partial Regex ListMarker();

    [GeneratedRegex("[*_`]")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"<(script|style)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningScriptOrStyle();

    [GeneratedRegex(@"</script[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingScript();

    [GeneratedRegex(@"</style[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingStyle();

    [GeneratedRegex(@"^(?:([0-9]+):)?([0-9]{1,2}):([0-9]{2}(?:\.[0-9]+)?)\z")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\z")]
    private static partial Regex PlainSeconds();

    [GeneratedRegex(@"(?:^|&)t=(?:npt:)?([^,&]*)(?:,([^&]*))?")]
    private static partial Regex TemporalFragment();

    [GeneratedRegex(@"^https?://www\.w3\.org/TR/media-frags/?\z")]
    private static partial Regex MediaFragments();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+")]
    private static partial Regex Whitespace();

    /// <summary>The kind of a source path, or null when it is not checkable.</summary>
    public static SourceKind? KindOf(string path)
    {
        var extension = UnicodeText.AsciiLower(Path.GetExtension(path));
        return extension switch
        {
            ".txt" or ".text" => SourceKind.Text,
            ".md" or ".markdown" => SourceKind.Markdown,
            ".html" or ".htm" => SourceKind.Html,
            ".vtt" => SourceKind.WebVtt,
            _ => null,
        };
    }

    /// <summary>Decodes numeric character references and <c>&amp;amp; &amp;lt; &amp;gt; &amp;quot; &amp;apos; &amp;nbsp;</c>.</summary>
    public static string DecodeEntities(string text) =>
        Entity().Replace(text, m =>
        {
            var e = m.Groups[1].Value;
            if (e[0] == '#')
            {
                var isHex = e.Length > 1 && (e[1] == 'x' || e[1] == 'X');
                var digits = isHex ? e[2..] : e[1..];
                if (long.TryParse(digits, isHex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var cp)
                    && cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF))
                {
                    return char.ConvertFromUtf32((int)cp);
                }

                return m.Value;
            }

            return NamedEntities.TryGetValue(e, out var named) ? named : m.Value;
        });

    /// <summary>
    /// Markdown to text: 1. <c>![alt](url)</c> becomes <c>alt</c> and <c>[text](url)</c> becomes <c>text</c>;
    /// 2. per line, a leading heading marker and a leading list marker are removed;
    /// 3. every <c>*</c>, <c>_</c> and backtick is removed.
    /// </summary>
    public static string MarkdownText(string source)
    {
        var linksDone = LinkText(source);
        // Steps 2 and 3: anchored line markers and a one-character class, one pass each.
        TextWork.Add(3L * linksDone.Length);
        var lines = linksDone.Split('\n').Select(line => ListMarker().Replace(HeadingMarker().Replace(line, ""), ""));
        return Emphasis().Replace(string.Join('\n', lines), "");
    }

    /// <summary>HTML to text: script and style elements removed, tags removed, character references decoded.</summary>
    public static string HtmlText(string source)
    {
        var stripped = StripTags(StripScriptAndStyle(source));
        // Each reference starts at "&" and stops at the next character outside its set: one pass.
        TextWork.Add(stripped.Length);
        return DecodeEntities(stripped);
    }

    /// <summary>
    /// A forward search for one character that remembers its last answer, so searches from rising positions cost one
    /// pass over the text in all (contract §3: work is bounded by the size of what is read).
    /// </summary>
    internal sealed class ForwardFinder(string text, char needle)
    {
        private int _at = -2; // the last index found; -1 once there is none left

        public int From(int from)
        {
            if (_at != -1 && _at < from)
            {
                _at = from >= text.Length ? -1 : text.IndexOf(needle, from);
                TextWork.Add((_at < 0 ? text.Length : _at + 1) - Math.Min(from, text.Length));
            }

            return _at;
        }
    }

    /// <summary>
    /// Step 1 of the Markdown rules, in one pass: what replacing <c>!?\[([^\]]*)\]\([^)]*\)</c> with its first group
    /// gives, without that expression's rescans from every <c>[</c>.
    /// </summary>
    internal static string LinkText(string source)
    {
        var open = new ForwardFinder(source, '[');
        var close = new ForwardFinder(source, ']');
        var paren = new ForwardFinder(source, ')');
        var output = new StringBuilder(source.Length);
        var pos = 0;
        while (true)
        {
            TextWork.Add(1);
            var i = open.From(pos);
            if (i < 0)
            {
                break;
            }

            var j = close.From(i + 1);
            if (j < 0)
            {
                break;
            }

            var opensTarget = j + 1 < source.Length && source[j + 1] == '(';
            var end = opensTarget ? paren.From(j + 2) : -1;
            if (end < 0)
            {
                // No bracket between i and j opens a link either: they all close at j. With no ")" left after a "](",
                // nothing later is a link.
                if (opensTarget)
                {
                    break;
                }

                TextWork.Add(j + 1 - pos);
                output.Append(source, pos, j + 1 - pos);
                pos = j + 1;
                continue;
            }

            var start = i > pos && source[i - 1] == '!' ? i - 1 : i;
            TextWork.Add(end + 1 - pos);
            output.Append(source, pos, start - pos).Append(source, i + 1, j - i - 1);
            pos = end + 1;
        }

        TextWork.Add(source.Length - pos);
        return output.Append(source, pos, source.Length - pos).ToString();
    }

    /// <summary>Every <c>&lt;...&gt;</c> removed, in one pass: what replacing <c>&lt;[^&gt;]*&gt;</c> with nothing gives.</summary>
    internal static string StripTags(string text)
    {
        var output = new StringBuilder(text.Length);
        var pos = 0;
        while (true)
        {
            var i = text.IndexOf('<', pos);
            if (i < 0)
            {
                break;
            }

            var j = i + 1 < text.Length ? text.IndexOf('>', i + 1) : -1;
            if (j < 0)
            {
                break;
            }

            TextWork.Add(j + 1 - pos);
            output.Append(text, pos, i - pos);
            pos = j + 1;
        }

        // The last searches covered the rest of the text once.
        TextWork.Add(2 * (text.Length - pos));
        return output.Append(text, pos, text.Length - pos).ToString();
    }

    /// <summary>
    /// script and style elements removed, in one pass: what replacing
    /// <c>&lt;(script|style)\b[^&gt;]*&gt;[\s\S]*?&lt;/\1\s*&gt;</c> (ignoring case) with nothing gives.
    /// </summary>
    internal static string StripScriptAndStyle(string source)
    {
        var gt = new ForwardFinder(source, '>');
        var unclosed = new Dictionary<string, bool>(StringComparer.Ordinal) { ["script"] = false, ["style"] = false };
        var output = new StringBuilder(source.Length);
        var pos = 0;
        while (true)
        {
            var m = OpeningScriptOrStyle().Match(source, pos);
            TextWork.Add((m.Success ? m.Index + m.Length : source.Length) - pos);
            if (!m.Success)
            {
                break;
            }

            var name = m.Groups[1].Value.ToLowerInvariant();
            var afterName = m.Index + m.Length;
            var tagEnd = gt.From(afterName);
            if (tagEnd < 0)
            {
                break;
            }

            var end = -1;
            if (!unclosed[name])
            {
                var closing = (name == "script" ? ClosingScript() : ClosingStyle()).Match(source, tagEnd + 1);
                end = closing.Success ? closing.Index + closing.Length : -1;
                // A closing tag is searched for past each opening once at most: one found is consumed, and one not
                // found is never searched for again.
                TextWork.Add((closing.Success ? end : source.Length) - tagEnd);
                unclosed[name] = !closing.Success;
            }

            if (end < 0)
            {
                // Not closed: the opening tag stays, and the search goes on after its name.
                if (unclosed["script"] && unclosed["style"])
                {
                    break;
                }

                output.Append(source, pos, afterName - pos);
                pos = afterName;
                continue;
            }

            output.Append(source, pos, m.Index - pos);
            pos = end;
        }

        return output.Append(source, pos, source.Length - pos).ToString();
    }

    /// <summary>
    /// WebVTT cues and their text: every cue's payload lines, cue tags removed and character references
    /// decoded, joined with LF. The header, NOTE and other blocks without a timing line are skipped.
    /// </summary>
    public static (IReadOnlyList<VttCue> Cues, string Text) WebVtt(string source)
    {
        var normalised = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = BlankLines().Split(normalised);
        var cues = new List<VttCue>();
        foreach (var block in blocks.Skip(1))
        {
            var lines = block.Split('\n');
            var at = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (at < 0)
            {
                continue;
            }

            var timing = lines[at];
            var arrow = timing.IndexOf("-->", StringComparison.Ordinal);
            var start = Seconds(timing[..arrow]);
            var end = Seconds(Whitespace().Split(UnicodeText.JsTrim(timing[(arrow + 3)..]))[0]);
            var text = string.Join('\n', lines.Skip(at + 1).Select(l => DecodeEntities(StripTags(l))));
            if (double.IsFinite(start) && double.IsFinite(end))
            {
                cues.Add(new VttCue(start, end, text));
            }
        }

        return (cues, string.Join('\n', cues.Select(c => c.Text)));
    }

    /// <summary>The normalised text of a source, by kind.</summary>
    public static string NormalisedText(string raw, SourceKind kind) => kind switch
    {
        SourceKind.Markdown => MarkdownText(raw),
        SourceKind.Html => HtmlText(raw),
        SourceKind.WebVtt => WebVtt(raw).Text,
        _ => raw,
    };

    /// <summary>
    /// Parses a W3C Media Fragments temporal value <c>t=start[,end]</c> (seconds, or <c>hh:mm:ss</c>
    /// with an optional <c>npt:</c> prefix). Returns null when there is none or it is malformed.
    /// </summary>
    public static (double Start, double End)? MediaTimeRange(string? value)
    {
        var m = TemporalFragment().Match(value ?? "");
        if (!m.Success)
        {
            return null;
        }

        var start = m.Groups[1].Value.Length == 0 ? 0 : Seconds(m.Groups[1].Value);
        var end = m.Groups[2].Success ? Seconds(m.Groups[2].Value) : double.PositiveInfinity;
        if (!double.IsFinite(start) || double.IsNaN(end) || end < start)
        {
            return null;
        }

        return (start, end);
    }

    /// <summary>
    /// Whether a text selector matches normalised text: <c>true</c>, <c>false</c>, or null when the
    /// selector type is not a text selector.
    /// </summary>
    public static bool? SelectorMatches(JsonObject selector, string text)
    {
        var type = JsonValues.StringOf(selector["type"]);
        if (type == "TextQuoteSelector")
        {
            var exact = JsonValues.StringOf(selector["exact"]);
            if (string.IsNullOrEmpty(exact))
            {
                return false;
            }

            var prefix = JsonValues.StringOf(selector["prefix"]) ?? "";
            var suffix = JsonValues.StringOf(selector["suffix"]) ?? "";
            // No whitespace or case folding (contract §7): one ordinal search for prefix + exact + suffix, the same
            // answer as checking each occurrence of exact, without rescanning the text for every one.
            return ContainsLinear(text, prefix + exact + suffix);
        }

        // Contract §7: a TextPositionSelector on its own proves only that its offsets are in range, so it is not
        // checked: matched stays absent.
        if (type == "TextPositionSelector")
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Checks one selector against a source's raw text. Returns null when this selector cannot be
    /// checked against this kind of source, so <c>matched</c> stays absent.
    /// </summary>
    public static bool? CheckSelector(JsonObject selector, string raw, SourceKind kind) => CheckSelector(selector, raw, kind, null);

    /// <summary>As <see cref="CheckSelector(JsonObject, string, SourceKind)"/>, with the source's normalised text
    /// already computed (it is computed once per source and package).</summary>
    public static bool? CheckSelector(JsonObject selector, string raw, SourceKind kind, string? normalised) =>
        CheckSelector(selector, raw, kind, normalised, null);

    /// <summary>A FragmentSelector that conforms to Media Fragments: a time range in audio or video.</summary>
    public static bool IsMediaTimeRange(JsonNode? selector) =>
        selector is JsonObject o && JsonValues.StringOf(o["type"]) == "FragmentSelector"
        && MediaFragments().IsMatch(JsonValues.StringOf(o["conformsTo"]) ?? "");

    /// <summary>As <see cref="CheckSelector(JsonObject, string, SourceKind, string?)"/>, with the declared duration of
    /// the media, which bounds a time range with the latest cue end (Reader contract §7).</summary>
    public static bool? CheckSelector(JsonObject selector, string raw, SourceKind kind, string? normalised, double? duration) =>
        CheckSelector(selector, raw, kind, normalised, duration, null);

    /// <summary>As <see cref="CheckSelector(JsonObject, string, SourceKind, string?, double?)"/>, with a mapping from the
    /// cited alternate's timeline to its reference's (package spec §5.7); a range that does not map does not match.</summary>
    public static bool? CheckSelector(
        JsonObject selector,
        string raw,
        SourceKind kind,
        string? normalised,
        double? duration,
        Func<(double Start, double End), (double Start, double End)?>? toReference) =>
        CheckSelectorLocated(selector, raw, kind, normalised, duration, toReference).Matched;

    /// <summary>As <see cref="CheckSelector(JsonObject, string, SourceKind, string?, double?, Func{ValueTuple{double, double}, ValueTuple{double, double}?}?)"/>,
    /// with, for a time range refined by a quote that matched, where the quote is spoken on the cited source's
    /// timeline (Reader contract §7).</summary>
    public static (bool? Matched, (double Start, double End)? Located) CheckSelectorLocated(
        JsonObject selector,
        string raw,
        SourceKind kind,
        string? normalised,
        double? duration,
        Func<(double Start, double End), (double Start, double End)?>? toReference,
        IReadOnlyList<VttCue>? cues = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (JsonValues.StringOf(selector["type"]) == "FragmentSelector")
        {
            // Contract §7: a media time range is checked only against WebVTT, by overlap with a cue.
            if (!MediaFragments().IsMatch(JsonValues.StringOf(selector["conformsTo"]) ?? "") || kind != SourceKind.WebVtt)
            {
                return (null, null);
            }

            var parsed = MediaTimeRange(JsonValues.StringOf(selector["value"]));
            var range = parsed is { } p && toReference is not null ? toReference(p) : parsed;
            if (range is null || parsed is null)
            {
                return (false, null);
            }

            var (start, end) = range.Value;
            cues ??= WebVtt(raw).Cues;
            if (!cues.Any(c => start < c.End && end > c.Start))
            {
                return (false, null);
            }

            // Contract §7: the range lies within the media, bounded by the latest cue end or the declared duration.
            if (!double.IsPositiveInfinity(end))
            {
                var bound = cues.Max(c => c.End);
                if (duration is { } d && d > bound)
                {
                    bound = d;
                }

                if (end > bound)
                {
                    return (false, null);
                }
            }

            // Contract §7: a range refined by a quote matches only when the quote is in the cues it covers.
            if (selector["refinedBy"] is not JsonObject refined || JsonValues.StringOf(refined["type"]) != "TextQuoteSelector")
            {
                return (true, null);
            }

            if (LocateQuote(cues, range.Value, refined) is not { } located)
            {
                return (false, null);
            }

            // On an alternate, back to the cited source's timeline by the stretch's offset.
            var offset = parsed.Value.Start - start;
            return (true, (located.Start + offset, located.End + offset));
        }

        // The quote is normalised as the source is, so markup in either never decides a match.
        var quote = JsonValues.StringOf(selector["type"]) == "TextQuoteSelector" ? NormaliseQuote(selector, kind) : selector;
        return (SelectorMatches(quote, normalised ?? NormalisedText(raw, kind)), null);
    }

    private static bool IsQuoteSpace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        var inSpace = false;
        foreach (var c in s)
        {
            if (IsQuoteSpace(c))
            {
                if (!inSpace)
                {
                    sb.Append(' ');
                }

                inSpace = true;
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>Every start index of <paramref name="needle"/> in <paramref name="text"/>, overlapping ones included,
    /// in one linear pass (Knuth-Morris-Pratt).</summary>
    public static List<int> Occurrences(string text, string needle) => Occurrences(text, needle, int.MaxValue);

    /// <summary>Whether <paramref name="needle"/> occurs in <paramref name="text"/>, in linear time however they repeat.</summary>
    internal static bool ContainsLinear(string text, string needle) => needle.Length == 0 || Occurrences(text, needle, 1).Count > 0;

    private static List<int> Occurrences(string text, string needle, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(needle);
        var output = new List<int>();
        var m = needle.Length;
        if (m == 0)
        {
            return output;
        }

        var fail = new int[m];
        for (int i = 1, k = 0; i < m; i++)
        {
            while (k > 0 && needle[i] != needle[k])
            {
                k = fail[k - 1];
            }

            if (needle[i] == needle[k])
            {
                k++;
            }

            fail[i] = k;
        }

        for (int i = 0, k = 0; i < text.Length; i++)
        {
            TextWork.Add(1);
            while (k > 0 && text[i] != needle[k])
            {
                TextWork.Add(1);
                k = fail[k - 1];
            }

            if (text[i] == needle[k])
            {
                k++;
            }

            if (k == m)
            {
                output.Add(i - m + 1);
                if (output.Count >= limit)
                {
                    break;
                }

                k = fail[k - 1];
            }
        }

        return output;
    }

    /// <summary>A range-minimum (or maximum) table: a query over values[lo..hi] in O(1).</summary>
    private static Func<int, int, double> SparseTable(double[] values, Func<double, double, double> pick)
    {
        var levels = new List<double[]> { values };
        for (var w = 1; 2 * w <= values.Length; w *= 2)
        {
            var prev = levels[^1];
            var next = new double[prev.Length - w];
            for (var i = 0; i < next.Length; i++)
            {
                next[i] = pick(prev[i], prev[i + w]);
            }

            levels.Add(next);
        }

        return (lo, hi) =>
        {
            var k = System.Numerics.BitOperations.Log2((uint)(hi - lo + 1));
            return pick(levels[k][lo], levels[k][hi - (1 << k) + 1]);
        };
    }

    /// <summary>
    /// Contract §7: where a refining quote is spoken in a time range. The covered text is the payloads of the cues the
    /// range overlaps, in file order, joined with LF, with each run of white space (the five ASCII characters) as one
    /// space, the quote's parts too. A cue is touched when a non-space character of <c>exact</c> comes from it. Of
    /// every occurrence, the one whose touched cues span the shortest time wins, the earliest on a tie. Contract §3:
    /// linear in the covered text and the quote.
    /// </summary>
    /// <returns>The earliest start and the latest end of the touched cues; null when the quote is not in the covered
    /// text.</returns>
    public static (double Start, double End)? LocateQuote(IReadOnlyList<VttCue> cues, (double Start, double End) range, JsonObject quote)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(quote);
        if (JsonValues.StringOf(quote["exact"]) is not { } rawExact)
        {
            return null;
        }

        var exact = CollapseSpaces(rawExact);
        if (exact.All(IsQuoteSpace))
        {
            return null;
        }

        var prefix = CollapseSpaces(JsonValues.StringOf(quote["prefix"]) ?? "");
        var needle = prefix + exact + CollapseSpaces(JsonValues.StringOf(quote["suffix"]) ?? "");
        var covered = cues.Where(c => range.Start < c.End && range.End > c.Start).ToList();
        var text = new StringBuilder();
        var owner = new List<int>();
        for (var i = 0; i < covered.Count; i++)
        {
            foreach (var c in (i > 0 ? "\n" : "") + covered[i].Text)
            {
                if (IsQuoteSpace(c))
                {
                    if (owner.Count > 0 && owner[^1] == -1)
                    {
                        continue;
                    }

                    text.Append(' ');
                    owner.Add(-1);
                }
                else
                {
                    text.Append(c);
                    owner.Add(i);
                }
            }
        }

        var n = owner.Count;
        // The owner of the first non-space character at or after each position, and of the last at or before it.
        var next = new int[n + 1];
        next[n] = -1;
        for (var i = n - 1; i >= 0; i--)
        {
            next[i] = owner[i] >= 0 ? owner[i] : next[i + 1];
        }

        var prev = new int[n];
        for (var i = 0; i < n; i++)
        {
            prev[i] = owner[i] >= 0 ? owner[i] : i > 0 ? prev[i - 1] : -1;
        }

        // Cues with text, in order: the touched cues of a span are those between its first and last owner.
        var rank = Enumerable.Repeat(-1, covered.Count).ToArray();
        var filled = new List<int>();
        foreach (var o in owner)
        {
            if (o >= 0 && rank[o] < 0)
            {
                rank[o] = filled.Count;
                filled.Add(o);
            }
        }

        if (filled.Count == 0)
        {
            return null;
        }

        var earliest = SparseTable([.. filled.Select(c => covered[c].Start)], Math.Min);
        var latest = SparseTable([.. filled.Select(c => covered[c].End)], Math.Max);
        (double Start, double End)? best = null;
        foreach (var at in Occurrences(text.ToString(), needle))
        {
            var a = at + prefix.Length;
            var b = a + exact.Length;
            var (first, last) = (next[a], prev[b - 1]);
            if (first < 0 || last < 0 || rank[first] > rank[last])
            {
                continue;
            }

            (double Start, double End) span = (earliest(rank[first], rank[last]), latest(rank[first], rank[last]));
            if (best is not { } bs || span.End - span.Start < bs.End - bs.Start
                || (span.End - span.Start == bs.End - bs.Start && span.Start < bs.Start))
            {
                best = span;
            }
        }

        return best;
    }

    // A quote from captions is cue text already; Markdown and HTML quotes lose markup as their sources do.
    private static JsonObject NormaliseQuote(JsonObject selector, SourceKind kind)
    {
        if (kind is not (SourceKind.Markdown or SourceKind.Html))
        {
            return selector;
        }

        var copy = (JsonObject)selector.DeepClone();
        foreach (var key in new[] { "exact", "prefix", "suffix" })
        {
            if (JsonValues.StringOf(copy[key]) is { } part)
            {
                copy[key] = NormalisedText(part, kind);
            }
        }

        return copy;
    }

    private static double Seconds(string ts)
    {
        var trimmed = UnicodeText.JsTrim(ts);
        var clock = ClockTime().Match(trimmed);
        if (clock.Success)
        {
            var hours = clock.Groups[1].Success ? double.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return (hours * 3600)
                   + (double.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                   + double.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        var plain = PlainSeconds().Match(trimmed);
        return plain.Success ? double.Parse(plain.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    private static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>
/// The work the text scanners do, counted in characters examined or copied, per thread (contract §3: work is bounded by
/// the size of what is read). The tests check that it stays within a constant times the length of the input; it is
/// never used to decide anything.
/// </summary>
internal static class TextWork
{
    [ThreadStatic]
    private static long _steps;

    /// <summary>The steps counted on this thread so far.</summary>
    internal static long Steps => _steps;

    internal static void Add(long steps) => _steps += steps;
}
