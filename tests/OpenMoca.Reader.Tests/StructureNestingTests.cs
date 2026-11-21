using System.IO.Compression;
using System.Text;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Structure;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3: parsing structure.ttl never depends on the call stack. Blank-node property lists and collections nest
/// at most 64 levels, fixed, each one a level; deeper is O001, checked before the Turtle parser runs, on the parser's
/// own tokens, so Turtle's rules for names, IRIs, strings and comments decide what is a bracket.
/// </summary>
public sealed class StructureNestingTests
{
    [Theory]
    [InlineData("<https://e.org/a> <https://e.org/p> [ <https://e.org/q> [ <https://e.org/r> 1 ] ] .", 2)]
    [InlineData("<https://e.org/a> <https://e.org/p> ( ( 1 ) 2 ) .", 2)]
    [InlineData("<https://e.org/a(([[> <https://e.org/p> \"(([[\" .", 0)]
    [InlineData("<https://e.org/a> <https://e.org/p> \"\"\"multi\n((\nline\"\"\" .", 0)]
    [InlineData("<https://e.org/a> <https://e.org/p> 'it\\'s ((' .", 0)]
    [InlineData("# ((((\n<https://e.org/a> <https://e.org/p> ( 1 ) .", 1)]
    [InlineData("@prefix e: <https://e.org/> .\ne:a\\#b e:p ( ( 1 ) ) .", 2)]
    [InlineData("@prefix e: <https://e.org/> .\ne:a\\'b e:p [ e:q ( 1 ) ] .", 2)]
    [InlineData("@prefix e: <https://e.org/> .\ne:a\\(\\(\\) e:p 1 .", 0)]
    public void NestingDepth_CountsOnlyBrackets(string turtle, int depth) =>
        Assert.Equal(depth, StructureParser.NestingDepth(turtle));

    private static string Nested(int levels) =>
        "@prefix skos: <http://www.w3.org/2004/02/skos/core#> .\n"
        + "<https://e.org/c> a skos:Concept ; <https://e.org/p> "
        + string.Concat(Enumerable.Repeat("[ <https://e.org/q> ", levels)) + "1" + new string(']', levels) + " .\n";

    // The same nesting on one line after a name that holds an escaped '#', which is part of the name, not a comment.
    private static string NestedAfterEscapedName(int levels) =>
        "@prefix e: <https://e.org/> .\n"
        + "e:c\\#1 e:p " + string.Concat(Enumerable.Repeat("[ e:q ", levels)) + "1" + new string(']', levels) + " .\n";

    private static async Task<PackageResult> Read(string turtle)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
            {
                ("moca.json", """{ "id": "https://example.com/tests/nesting", "version": "1.0.0", "title": "Nesting" }"""),
                ("content/a.md", "---\ntype: Note\ntitle: A\n---\n\nA.\n"),
                ("structure.ttl", turtle),
            })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(buffer.ToArray()), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void MaxNesting_IsTheContractsFixedLimit() => Assert.Equal(64, StructureParser.MaxNesting);

    [Fact]
    public async Task ReadPackage_AtTheNestingLimit_Parses()
    {
        var result = await Read(Nested(64));

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable);
    }

    [Fact]
    public async Task ReadPackage_PastTheNestingLimit_ReportsO001()
    {
        var result = await Read(Nested(65));

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable && d.Message.Contains("levels deep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadPackage_WithNestingAsDeepAsTheSizeLimitAllows_ReportsO001()
    {
        var result = await Read($"<https://e.org/a> <https://e.org/p> {new string('(', 1_000_000)}{new string(')', 1_000_000)} .");

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable);
    }

    [Fact]
    public async Task ReadPackage_AtTheNestingLimitAfterAnEscapedName_Parses()
    {
        var result = await Read(NestedAfterEscapedName(64));

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable);
    }

    [Fact]
    public async Task ReadPackage_PastTheNestingLimitAfterAnEscapedName_ReportsO001()
    {
        var result = await Read(NestedAfterEscapedName(65));

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable && d.Message.Contains("levels deep", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\\#")]
    [InlineData("\\'")]
    [InlineData("\\)")]
    [InlineData("%23")]
    public async Task ReadPackage_NestedOneHundredThousandLevelsAfterAName_ReportsO001(string inName)
    {
        // The process survives: the depth is refused before the parser recurses.
        var turtle = "@prefix e: <https://e.org/> .\n"
            + $"e:c{inName}1 e:p " + string.Concat(Enumerable.Repeat("[ e:q ", 100_000)) + "1" + new string(']', 100_000) + " .\n";

        var result = await Read(turtle);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable && d.Message.Contains("levels deep", StringComparison.Ordinal));
    }
}
