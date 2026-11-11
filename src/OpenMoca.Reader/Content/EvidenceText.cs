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

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*#{1,6}[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*(?:[-*+]|[0-9]+[.)])[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+")]
    private static partial Regex ListMarker();

    [GeneratedRegex("[*_`]")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

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
        var linksDone = MarkdownLink().Replace(source, "$1");
        var lines = linksDone.Split('\n').Select(line => ListMarker().Replace(HeadingMarker().Replace(line, ""), ""));
        return Emphasis().Replace(string.Join('\n', lines), "");
    }

    /// <summary>HTML to text: script and style elements removed, tags removed, character references decoded.</summary>
    public static string HtmlText(string source) =>
        DecodeEntities(Tag().Replace(ScriptOrStyle().Replace(source, ""), ""));

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
            var text = string.Join('\n', lines.Skip(at + 1).Select(l => DecodeEntities(Tag().Replace(l, ""))));
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
            return text.Contains(prefix + exact + suffix, StringComparison.Ordinal);
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
    public static bool? CheckSelector(JsonObject selector, string raw, SourceKind kind, string? normalised)
    {
        if (JsonValues.StringOf(selector["type"]) == "FragmentSelector")
        {
            // Contract §7: a media time range is checked only against WebVTT, by overlap with a cue.
            if (!MediaFragments().IsMatch(JsonValues.StringOf(selector["conformsTo"]) ?? "") || kind != SourceKind.WebVtt)
            {
                return null;
            }

            var range = MediaTimeRange(JsonValues.StringOf(selector["value"]));
            if (range is null)
            {
                return false;
            }

            var (start, end) = range.Value;
            return WebVtt(raw).Cues.Any(c => start < c.End && end > c.Start);
        }

        // The quote is normalised as the source is, so markup in either never decides a match.
        var quote = JsonValues.StringOf(selector["type"]) == "TextQuoteSelector" ? NormaliseQuote(selector, kind) : selector;
        return SelectorMatches(quote, normalised ?? NormalisedText(raw, kind));
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
