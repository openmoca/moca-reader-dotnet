using System.Globalization;
using System.Text;

namespace OpenMoca.Reader.Search;

/// <summary>
/// BM25 over plain text (k1 = 1.2, b = 0.75), shared by the lexical and in-memory store backends.
/// </summary>
/// <remarks>
/// score(d) = Σ idf(t) · f·(k1+1) / (f + k1·(1 − b + b·|d|/avg|d|)), with idf(t) = ln(1 + (N − df + 0.5)/(df + 0.5)).
/// Scores are rounded to four decimals, like the reference Reader, so ties break the same way.
/// </remarks>
public static class Bm25
{
    /// <summary>Code point ranges written without spaces between words.</summary>
    private static readonly (int From, int To)[] Unspaced =
    [
        (0x0E00, 0x0EFF), // Thai, Lao
        (0x1000, 0x109F), // Myanmar
        (0x1780, 0x17FF), // Khmer
        (0x3040, 0x30FF), // Hiragana, Katakana
        (0x31F0, 0x31FF), // Katakana phonetic extensions
        (0x3400, 0x4DBF), // CJK extension A
        (0x4E00, 0x9FFF), // CJK unified ideographs
        (0xF900, 0xFAFF), // CJK compatibility ideographs
        (0x20000, 0x3FFFF), // CJK extensions B and later
    ];

    /// <summary>
    /// The tokens of a text, the same in every Reader's built-in lexical backend:
    /// <list type="number">
    /// <item>NFKC-normalise, then lower-case as package spec §6.3 does (<see cref="UnicodeText.SimpleLowercase"/>).</item>
    /// <item>Letters, digits and combining marks are word characters; anything else separates words.</item>
    /// <item>Scripts written without spaces (the ranges above) are split into units, each character with the
    /// combining marks after it, and indexed as each unit and each pair of neighbouring units.</item>
    /// </list>
    /// No stemming: hosts that need it supply their own lexical backend.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var run = new List<string>();
        var runUnspaced = false;

        void Flush()
        {
            if (run.Count == 0)
            {
                return;
            }

            if (!runUnspaced)
            {
                tokens.Add(string.Concat(run));
            }
            else
            {
                for (var i = 0; i < run.Count; i++)
                {
                    tokens.Add(run[i]);
                    if (i + 1 < run.Count)
                    {
                        tokens.Add(run[i] + run[i + 1]);
                    }
                }
            }

            run.Clear();
        }

        foreach (var rune in UnicodeText.Normalize(text, NormalizationForm.FormKC).EnumerateRunes())
        {
            var c = UnicodeText.LowerCodePoint(rune);
            var category = Rune.GetUnicodeCategory(c);
            if (!IsWordCharacter(category))
            {
                Flush();
                continue;
            }

            var unspaced = Unspaced.Any(r => c.Value >= r.From && c.Value <= r.To);
            if (unspaced && runUnspaced && run.Count > 0 && IsMark(category))
            {
                run[^1] += c.ToString();
                continue;
            }

            if (run.Count > 0 && unspaced != runUnspaced)
            {
                Flush();
            }

            runUnspaced = unspaced;
            run.Add(c.ToString());
        }

        Flush();
        return tokens;
    }

    private static bool IsMark(UnicodeCategory category) =>
        category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsWordCharacter(UnicodeCategory category) => category switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => IsMark(category),
    };

    /// <summary>Scores documents against a query; best first, zero scores dropped, ties by key.</summary>
    public static IReadOnlyList<(T Item, double Score)> Rank<T>(IReadOnlyList<(T Item, string Text, string Key)> docs, string query, double k1 = 1.2, double b = 0.75) =>
        RankCounted([.. docs.Select(d => (d.Item, Count(d.Text), d.Key))], query, k1, b);

    /// <summary>A document's term frequencies and length in tokens: all BM25 needs of it.</summary>
    internal sealed record TermCounts(Dictionary<string, int> Counts, int Length);

    /// <summary>The term frequencies of a text, from <see cref="Tokens"/>.</summary>
    internal static TermCounts Count(string text)
    {
        var tokens = Tokens(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        return new TermCounts(counts, tokens.Count);
    }

    /// <summary>
    /// <see cref="Rank{T}"/> over documents already counted, so a backend can count each document once and keep it.
    /// The same arithmetic in the same order, so scores are identical.
    /// </summary>
    internal static IReadOnlyList<(T Item, double Score)> RankCounted<T>(IReadOnlyList<(T Item, TermCounts Counts, string Key)> docs, string query, double k1 = 1.2, double b = 0.75)
    {
        var terms = Tokens(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || docs.Count == 0)
        {
            return [];
        }

        var average = docs.Sum(d => d.Counts.Length) / (double)docs.Count;
        if (average == 0)
        {
            average = 1;
        }

        var df = terms.ToDictionary(t => t, t => docs.Count(d => d.Counts.Counts.ContainsKey(t)), StringComparer.Ordinal);
        var scored = new List<(T Item, string Key, double Score)>();
        for (var i = 0; i < docs.Count; i++)
        {
            var counts = docs[i].Counts;
            double score = 0;
            foreach (var term in terms)
            {
                var f = counts.Counts.GetValueOrDefault(term);
                if (f == 0)
                {
                    continue;
                }

                var idf = Math.Log(1 + ((docs.Count - df[term] + 0.5) / (df[term] + 0.5)));
                score += idf * (f * (k1 + 1) / (f + (k1 * (1 - b + (b * counts.Length / average)))));
            }

            scored.Add((docs[i].Item, docs[i].Key, Math.Round(score * 1e4, MidpointRounding.AwayFromZero) / 1e4));
        }

        return scored
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => (s.Item, s.Score))
            .ToList();
    }
}
