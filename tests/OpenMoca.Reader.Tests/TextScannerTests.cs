using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Content;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3: work on a node's links and on an evidence source stays roughly linear in the text. Each scanner gives
/// exactly what the regular expression it replaced gave, and stays fast on text that made that expression slow.
/// </summary>
public sealed class TextScannerTests
{
    private const string Ws = @"\t\n\v\f\r    -     　﻿";

    private static readonly Regex Link = new($"""!?\[[^\]]*\]\([{Ws}]*(<[^>]+>|[^){Ws}]+)(?:[{Ws}]+(?:"[^"]*"|'[^']*'))?[{Ws}]*\)""");
    private static readonly Regex MarkdownLink = new(@"!?\[([^\]]*)\]\([^)]*\)");
    private static readonly Regex Tag = new("<[^>]*>");
    private static readonly Regex ScriptOrStyle = new($@"<(script|style)\b[^>]*>[\s\S]*?</\1[{Ws}]*>", RegexOptions.IgnoreCase);

    // Short strings over the characters each scanner cares about, so every arrangement turns up.
    private static IEnumerable<string> Samples(string alphabet, string[] words, int count, int seed)
    {
        var random = new Random(seed);
        for (var n = 0; n < count; n++)
        {
            var sb = new StringBuilder();
            var length = random.Next(0, 24);
            for (var i = 0; i < length; i++)
            {
                sb.Append(random.Next(4) == 0 ? words[random.Next(words.Length)] : alphabet[random.Next(alphabet.Length)].ToString());
            }

            yield return sb.ToString();
        }
    }

    [Fact]
    public void LinkTargets_GiveWhatTheExpressionCaptures()
    {
        foreach (var s in Samples("[]()<>!\"' a.", ["](", "](<", ">)", " \"t\")", "a.md"], 20_000, 1))
        {
            Assert.Equal(Link.Matches(s).Select(m => m.Groups[1].Value), ContentReader.LinkTargets(s));
        }
    }

    [Fact]
    public void LinkText_GivesWhatTheExpressionGives()
    {
        foreach (var s in Samples("[]()!a\n", ["](", "![", ")"], 20_000, 2))
        {
            Assert.Equal(MarkdownLink.Replace(s, "$1"), EvidenceText.LinkText(s));
        }
    }

    [Fact]
    public void StripTags_GivesWhatTheExpressionGives()
    {
        foreach (var s in Samples("<>a/ ", ["<b>", "</", "<<"], 20_000, 3))
        {
            Assert.Equal(Tag.Replace(s, ""), EvidenceText.StripTags(s));
        }
    }

    [Fact]
    public void StripScriptAndStyle_GivesWhatTheExpressionGives()
    {
        foreach (var s in Samples("<>/ a", ["<script>", "<style ", "</script>", "</style >", "<SCRIPT", "</Style>", "<scripts>"], 20_000, 4))
        {
            Assert.Equal(ScriptOrStyle.Replace(s, ""), EvidenceText.StripScriptAndStyle(s));
        }
    }

    [Theory]
    [InlineData("a", "aaa", true)]
    [InlineData("aab", "aaab", true)]
    [InlineData("abc", "ab", false)]
    [InlineData("", "x", true)]
    public void ContainsLinear_FindsTheNeedle(string needle, string text, bool expected) =>
        Assert.Equal(expected, EvidenceText.ContainsLinear(text, needle));

    public static TheoryData<string, string> Hostile() => new()
    {
        { "brackets", new string('[', 200_000) },
        { "unclosed links", string.Concat(Enumerable.Repeat("[a](", 50_000)) },
        { "angle links", string.Concat(Enumerable.Repeat("[a](<", 50_000)) },
        { "tags", new string('<', 200_000) },
        { "scripts", string.Concat(Enumerable.Repeat("<script>", 30_000)) },
        { "styles", string.Concat(Enumerable.Repeat("<style><script>", 20_000)) },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Scanners_OnTextThatWasSlow_FinishQuickly(string name, string text)
    {
        var clock = Stopwatch.StartNew();
        ContentReader.LinkTargets(text);
        EvidenceText.MarkdownText(text);
        EvidenceText.HtmlText(text);
        EvidenceText.ContainsLinear(new string('a', 1_000_000), new string('a', 50_000) + "b");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{name} took {clock.Elapsed}");
    }
}
