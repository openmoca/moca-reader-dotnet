using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3: reading frontmatter never depends on the call stack. Sequences and mappings nest at most
/// <see cref="CoreSchemaYaml.MaxDepth"/> levels; deeper frontmatter is invalid (C002), never a crash.
/// </summary>
public sealed class FrontmatterDepthTests
{
    // The frontmatter mapping is level 1; each flow sequence adds one.
    private static string Nested(int levels) => $"---\nn: {new string('[', levels - 1)}{new string(']', levels - 1)}\n---\n";

    [Fact]
    public void Split_AtTheDepthLimit_Reads()
    {
        var split = Frontmatter.Split(Nested(CoreSchemaYaml.MaxDepth));

        Assert.Null(split.Error);
        Assert.NotNull(split.Data);
        // What is read can be serialised for schema checks at the default JSON depth.
        JsonSerializer.SerializeToElement(split.Data, OpenMoca.Reader.Json.JsonNodeContext.Default.JsonNode);
    }

    [Fact]
    public void Split_PastTheDepthLimit_IsAnError()
    {
        var split = Frontmatter.Split(Nested(CoreSchemaYaml.MaxDepth + 1));

        Assert.Null(split.Data);
        Assert.Contains("levels deep", split.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_WithNestingAsDeepAsTheSizeLimitAllows_IsAnError()
    {
        // About 64 KiB of brackets: one level per byte.
        var split = Frontmatter.Split($"---\nn: {new string('[', 30_000)}{new string(']', 30_000)}\n---\n");

        Assert.Null(split.Data);
        Assert.NotNull(split.Error);
    }

    [Theory]
    [InlineData("? [a, b]\n: c\n")]
    [InlineData("? {a: b}\n: c\n")]
    [InlineData("? - a\n: c\n")]
    public void Split_WithACollectionAsAMappingKey_IsAnError(string yaml)
    {
        var split = Frontmatter.Split($"---\n{yaml}---\n");

        Assert.Null(split.Data);
        Assert.NotNull(split.Error);
    }

    [Fact]
    public async Task ReadPackage_WithDeeplyNestedFrontmatter_ReportsC002AndDoesNotThrow()
    {
        var source = new Dictionary<string, byte[]>
        {
            ["moca.json"] = """{ "id": "https://example.com/tests/deep", "version": "1.0.0", "title": "Deep" }"""u8.ToArray(),
            ["content/deep.md"] = System.Text.Encoding.UTF8.GetBytes($"---\ntype: Note\ntitle: Deep\nx: {new string('[', 30_000)}{new string(']', 30_000)}\n---\n\nDeep.\n"),
        };

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(ZipOf(source)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.C002FrontmatterInvalid);
    }

    private static byte[] ZipOf(Dictionary<string, byte[]> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }
}
