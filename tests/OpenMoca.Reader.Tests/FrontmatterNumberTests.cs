using System.Globalization;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Package spec §5.1: octal and hex integers of any length, rounded once to the nearest double.</summary>
public sealed class FrontmatterNumberTests
{
    private static JsonNode? Value(string scalar) => Frontmatter.Split($"---\nn: {scalar}\n---\n").Data!["n"];

    [Theory]
    [InlineData("0x7fffffffffffffff", "9223372036854775807")]
    [InlineData("0x8000000000000000", "9.223372036854776E+18")]
    // 2^64 + 2049 is past the halfway point to the next double, 2^64 + 4096; rounding twice gave 2^64.
    [InlineData("0x10000000000000801", "1.8446744073709556E+19")]
    [InlineData("0x10000000000000800", "1.8446744073709552E+19")] // a tie rounds to even
    [InlineData("0o2000000000000000004001", "1.8446744073709556E+19")] // the same number in octal
    public void LongIntegers_RoundOnce(string scalar, string expected) =>
        Assert.Equal(expected, Value(scalar) is JsonValue v && v.TryGetValue<long>(out var l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : Value(scalar)!.GetValue<double>().ToString("R", CultureInfo.InvariantCulture));

    [Fact]
    public void AnIntegerTooLargeForADouble_IsNonFinite() =>
        Assert.Equal("0x" + new string('f', 300), Value("0x" + new string('f', 300))!["$nonFinite"]!.GetValue<string>());
}

/// <summary>YAML 1.2 §7.4: an implicit key, from its start to its colon, is at most 1,024 characters.</summary>
public sealed class FrontmatterKeyTests
{
    [Theory]
    [InlineData(1024, false, true)]
    [InlineData(1025, false, false)]
    [InlineData(1022, true, true)] // two quotes and 1,022 characters: 1,024 in all
    [InlineData(1023, true, false)]
    public void ImplicitKeys_AreAtMost1024Characters(int length, bool quoted, bool read)
    {
        var key = new string('k', length);
        var split = Frontmatter.Split($"---\ntype: N\n{(quoted ? $"\"{key}\"" : key)}: v\n---\n");
        Assert.Equal(read, split.Error is null);
    }

    [Fact]
    public void AnExplicitKey_MayBeLonger() =>
        Assert.Null(Frontmatter.Split($"---\ntype: N\n? {new string('k', 3000)}\n: v\n---\n").Error);
}
