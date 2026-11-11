using System.Globalization;
using System.Text;

namespace OpenMoca.Reader.Onnx;

/// <summary>
/// BERT tokenisation, uncased, as Hugging Face's BertNormalizer, BertPreTokenizer and WordPiece define it: clean the
/// text, space out CJK characters, strip accents, lower-case, split on whitespace and punctuation, then greedy
/// longest-match WordPiece. Written out here so its ids match the reference exactly, underscores and accents included.
/// </summary>
internal sealed class WordPieceTokenizer
{
    readonly Dictionary<string, int> _vocab;
    public WordPieceTokenizer(string vocabPath)
    {
        _vocab = new(StringComparer.Ordinal);
        var i = 0;
        foreach (var line in File.ReadLines(vocabPath)) _vocab[line] = i++;
    }

    /// <summary>[CLS], the first tokens that fit, [SEP]: cut as Hugging Face cuts, to <paramref name="maxTokens"/> in all.</summary>
    public List<int> Encode(string text, int maxTokens)
    {
        var ids = new List<int> { _vocab["[CLS]"] };
        foreach (var id in PreTokenize(Normalize(text)).SelectMany(WordPiece))
        {
            if (ids.Count == maxTokens - 1)
            {
                break;
            }

            ids.Add(id);
        }

        ids.Add(_vocab["[SEP]"]);
        return ids;
    }

    static bool IsControl(int cp) => cp is not ('\t' or '\n' or '\r') && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.Control or UnicodeCategory.Format;
    static bool IsWhitespace(int cp) => cp is ' ' or '\t' or '\n' or '\r' || CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.SpaceSeparator;
    static bool IsCjk(int cp) => cp is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x20000 and <= 0x2A6DF) or (>= 0x2A700 and <= 0x2B73F)
        or (>= 0x2B740 and <= 0x2B81F) or (>= 0x2B820 and <= 0x2CEAF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x2F800 and <= 0x2FA1F);
    static bool IsPunctuation(int cp) => cp is (>= 33 and <= 47) or (>= 58 and <= 64) or (>= 91 and <= 96) or (>= 123 and <= 126)
        || CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp == 0 || cp == 0xFFFD || IsControl(cp)) continue;
            if (IsWhitespace(cp)) { sb.Append(' '); continue; }
            if (IsCjk(cp)) { sb.Append(' ').Append(rune.ToString()).Append(' '); continue; }
            sb.Append(rune.ToString());
        }
        var nfd = sb.ToString().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder();
        foreach (var rune in nfd.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark) stripped.Append(rune.ToString());
        }
        return stripped.ToString().ToLowerInvariant();
    }

    static IEnumerable<string> PreTokenize(string text)
    {
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsWhitespace(rune.Value))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
            }
            else if (IsPunctuation(rune.Value))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return rune.ToString();
            }
            else word.Append(rune.ToString());
        }
        if (word.Length > 0) yield return word.ToString();
    }

    IEnumerable<int> WordPiece(string word)
    {
        var runes = word.EnumerateRunes().Select(r => r.ToString()).ToArray();
        if (runes.Length > 100) return [_vocab["[UNK]"]];
        var pieces = new List<int>();
        var start = 0;
        while (start < runes.Length)
        {
            var end = runes.Length;
            int? found = null;
            while (start < end)
            {
                var sub = string.Concat(runes[start..end]);
                if (start > 0) sub = "##" + sub;
                if (_vocab.TryGetValue(sub, out var id)) { found = id; break; }
                end--;
            }
            if (found is null) return [_vocab["[UNK]"]];
            pieces.Add(found.Value);
            start = end;
        }
        return pieces;
    }
}
