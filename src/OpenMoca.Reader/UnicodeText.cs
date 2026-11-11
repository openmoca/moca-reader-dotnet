using System.Text;

namespace OpenMoca.Reader;

/// <summary>Unicode normalisation that never throws on package text, and lower-casing defined once.</summary>
internal static class UnicodeText
{
    /// <summary>ECMAScript white space (package spec §2). <see cref="string.Trim()"/> uses another set.</summary>
    internal static readonly char[] JsWhitespace =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004',
        '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff',
    ];

    /// <summary>String.prototype.trim: removes ECMAScript white space from both ends.</summary>
    public static string JsTrim(string value) => value.Trim(JsWhitespace);

    /// <summary>
    /// Lower-cases A to Z only. Reserved file names and extensions are compared this way (package spec §5.1), so
    /// no other letter, such as a dotless ı or a capital İ, can make a name equal to <c>index.md</c>.
    /// </summary>
    public static string AsciiLower(string value) =>
        string.Create(value.Length, value, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    /// <summary>
    /// The simple lowercase mapping of one code point (package spec §6.3). Invariant lower-casing leaves
    /// İ (U+0130) as it is; its simple mapping is i.
    /// </summary>
    public static Rune LowerCodePoint(Rune rune) => rune.Value == 0x0130 ? new Rune('i') : Rune.ToLowerInvariant(rune);

    /// <summary>
    /// Lower-casing as package spec §6.3 defines it: each code point replaced with its Unicode simple lowercase
    /// mapping, one at a time, with no language or context rules.
    /// </summary>
    public static string SimpleLowercase(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            output.Append(LowerCodePoint(rune).ToString());
        }

        return output.ToString();
    }

    /// <summary>
    /// <see cref="string.Normalize(NormalizationForm)"/>, except that it never throws. .NET refuses strings that
    /// hold U+FFFE or a lone surrogate; JavaScript's <c>normalize</c> keeps them as they are, and so does this:
    /// the text around them is normalised, and they are kept in place.
    /// </summary>
    public static string Normalize(string text, NormalizationForm form)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException)
        {
            var output = new StringBuilder(text.Length);
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                if (pair)
                {
                    i++;
                    continue;
                }

                if (text[i] == '￾' || char.IsSurrogate(text[i]))
                {
                    output.Append(text.AsSpan(start, i - start).ToString().Normalize(form)).Append(text[i]);
                    start = i + 1;
                }
            }

            return output.Append(text[start..].Normalize(form)).ToString();
        }
    }
}
