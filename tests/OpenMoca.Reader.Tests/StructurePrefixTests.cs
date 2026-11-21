using System.Text;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Structure;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3: finding the prefixes a structure file declares is one pass over the text. The scanner gives exactly
/// what the regular expression it replaced gave, and its work grows with the text, not with the text squared.
/// </summary>
public sealed class StructurePrefixTests
{
    private static readonly Regex PrefixDeclaration = new(
        @"^[\t\n\v\f\r    -     　﻿]*(?:@prefix|PREFIX)[\t\n\v\f\r    -     　﻿]+([A-Za-z][A-Za-z0-9_.-]*)?:",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static IEnumerable<string> Expected(string text) =>
        PrefixDeclaration.Matches(text).Where(m => m.Groups[1].Success && m.Groups[1].Value.Length > 0).Select(m => m.Groups[1].Value);

    [Theory]
    [InlineData("@prefix e: <https://e.org/> .", "e")]
    [InlineData("PREFIX e: <https://e.org/>\n  @Prefix f.g-h_1: <https://f.org/> .", "e,f.g-h_1")]
    [InlineData("@prefix : <https://e.org/> .", "")]
    [InlineData("@prefix\n\n  e: <https://e.org/> .", "e")]
    [InlineData("@prefix\n\nPREFIX e: <https://e.org/>", "e")]
    [InlineData("x @prefix e: <https://e.org/> .", "")]
    [InlineData("　prefix e:", "e")]
    [InlineData("@prefixe: <https://e.org/> .", "")]
    public void DeclaredPrefixes_ReadsDeclarations(string turtle, string names) =>
        Assert.Equal(names.Length == 0 ? [] : names.Split(','), StructureParser.DeclaredPrefixes(turtle));

    [Fact]
    public void DeclaredPrefixes_GivesWhatTheExpressionGives()
    {
        string[] words = ["@prefix", "PREFIX", "prefix", "@PreFix", "e:", "ab.c-d:", ":", "\n", "\r\n", " ", "　"];
        const string alphabet = " \t\n:@e_1.x";
        var random = new Random(7);
        for (var n = 0; n < 40_000; n++)
        {
            var sb = new StringBuilder();
            var length = random.Next(0, 16);
            for (var i = 0; i < length; i++)
            {
                sb.Append(random.Next(2) == 0 ? words[random.Next(words.Length)] : alphabet[random.Next(alphabet.Length)].ToString());
            }

            var text = sb.ToString();
            Assert.Equal(Expected(text), StructureParser.DeclaredPrefixes(text));
        }
    }

    private static long StepsFor(string text)
    {
        var before = TextWork.Steps;
        StructureParser.DeclaredPrefixes(text);
        return TextWork.Steps - before;
    }

    public static TheoryData<string, string> Hostile() => new()
    {
        { "blank lines", new string('\n', 4 * 1024 * 1024) },
        { "keyword and blank lines", "@prefix" + new string('\n', 4 * 1024 * 1024) },
        { "keywords on blank lines", string.Concat(Enumerable.Repeat("@prefix\n\n", 512 * 1024)) },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void DeclaredPrefixes_OnFourMebibytes_IsLinear(string name, string text)
    {
        // Counted in steps (characters examined), never in time, so the check means the same on every machine.
        var whole = StepsFor(text);
        var half = StepsFor(text[..(text.Length / 2)]);

        Assert.True(whole <= 2L * text.Length, $"{name}: {whole} steps for {text.Length} characters");
        Assert.True(whole <= (2 * half) + 64, $"{name}: {half} steps for half the text, {whole} for all of it");
    }
}
