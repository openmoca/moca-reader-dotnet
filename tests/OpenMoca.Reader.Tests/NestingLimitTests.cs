using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Sidecars;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3, "Nesting": JSON and YAML frontmatter in a package or a sidecar nest at most 64 levels, the outermost
/// object, array or mapping being level 1. One level more is malformed, with the code that kind of file already has for
/// text that does not parse. The corpus checks moca.json, frontmatter and an attestation file; these check the rest.
/// </summary>
public sealed class NestingLimitTests : IDisposable
{
    private const int Limit = 64;

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-nesting-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A JSON value that is <paramref name="levels"/> arrays deep.</summary>
    private static string Arrays(int levels) => $"{new string('[', levels)}1{new string(']', levels)}";

    /// <summary>A JSON object, level 1, with a member <paramref name="levels"/> levels deep in all.</summary>
    private static string ObjectDeep(string members, int levels) => $"{{{members}\"x-deep\":{Arrays(levels - 1)}}}";

    [Theory]
    [InlineData(Limit, true)]
    [InlineData(Limit + 1, false)]
    public void Json_NestsAtMost64Levels(int levels, bool parses)
    {
        var parse = () => JsonValues.Parse(Arrays(levels));

        if (parses)
        {
            Assert.NotNull(parse());
        }
        else
        {
            Assert.ThrowsAny<JsonException>(parse);
        }
    }

    [Fact]
    public void YamlAndJson_ShareOneLimit() => Assert.Equal(JsonValues.MaxDepth, OpenMoca.Reader.Content.CoreSchemaYaml.MaxDepth);

    [Theory]
    [InlineData(Limit, false)]
    [InlineData(Limit + 1, true)]
    public async Task ReservedFileFrontmatter_PastTheLimit_IsC004(int levels, bool c004)
    {
        var dir = Package("reserved");
        File.WriteAllText(Path.Combine(dir, "content", "index.md"), $"---\nokf_version: {Arrays(levels - 1)}\n---\n\n# Index\n");

        var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(c004, result.Diagnostics.Any(d => d.Code == DiagnosticCodes.C004ReservedFileInvalid));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.C002FrontmatterInvalid);
    }

    [Theory]
    [InlineData(Limit, DiagnosticCodes.A003AttestationUnverifiable)]
    [InlineData(Limit + 1, DiagnosticCodes.A001AttestationMalformed)]
    public async Task Statement_PastTheLimit_IsA001(int levels, string code)
    {
        var dir = Package("statement");
        var statement = ObjectDeep("\"_type\":\"https://in-toto.io/Statement/v1\",\"subject\":[],\"predicateType\":\"https://w3id.org/moca/attestation/package/v1\",", levels);
        Directory.CreateDirectory(Path.Combine(dir, "attestations"));
        File.WriteAllText(Path.Combine(dir, "attestations", "a.dsse.json"),
            $"{{\"payloadType\":\"application/vnd.in-toto+json\",\"payload\":\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(statement))}\",\"signatures\":[]}}");

        var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == code);
        Assert.True(result.Valid);
    }

    [Theory]
    [InlineData(Limit + 1)]
    public async Task EnvelopeMarker_PastTheLimit_IsE005(int levels)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("moca-enc.json", CompressionLevel.NoCompression).Open()))
            {
                writer.Write(ObjectDeep("\"format\":\"age\",", levels));
            }

            // The marker is checked once the envelope has its payload entry.
            using var payload = zip.CreateEntry("package.age", CompressionLevel.NoCompression).Open();
            payload.Write("age-encryption.org/v1\n"u8);
        }

        var result = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(buffer.ToArray()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.E005EnvelopeInvalid && d.Message.Contains("JSON", StringComparison.Ordinal)),
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
    }

    [Theory]
    [InlineData(Limit, false)]
    [InlineData(Limit + 1, true)]
    public async Task SidecarIndexAndPayloadLine_PastTheLimit_AreS001AndS004(int levels, bool refused)
    {
        var package = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: TestContext.Current.CancellationToken);
        var index = Path.Combine(_dir, $"index-{levels}");
        var payload = Path.Combine(_dir, $"payload-{levels}");
        Copy(Path.Combine(MocaSpec.Examples, "sidecars", "support-kb.moca.idx"), index);
        Copy(Path.Combine(MocaSpec.Examples, "sidecars", "support-kb.moca.idx"), payload);
        var indexJson = File.ReadAllText(Path.Combine(index, "index.json")).TrimEnd();
        File.WriteAllText(Path.Combine(index, "index.json"), $"{indexJson[..^1]},\"x-deep\":{Arrays(levels - 1)}}}");
        var items = Path.Combine(payload, "payload", "items.jsonl");
        File.AppendAllText(items, ObjectDeep("", levels) + "\n");

        var badIndex = SidecarBinder.Bind(index, package);
        var badLine = SidecarBinder.Bind(payload, package);

        Assert.Equal(refused, badIndex.Diagnostics.Any(d => d.Code == DiagnosticCodes.S001SidecarInvalid && d.Message.Contains("not valid JSON", StringComparison.Ordinal)));
        Assert.Equal(refused, badLine.Diagnostics.Any(d => d.Code == DiagnosticCodes.S004SidecarItemInvalid && d.Message.Contains("not JSON", StringComparison.Ordinal)));
    }

    private string Package(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "moca.json"), $"{{\"id\":\"https://example.com/{name}\",\"version\":\"1.0.0\",\"title\":\"N\"}}");
        File.WriteAllText(Path.Combine(dir, "content", "a.md"), "---\ntype: Note\ntitle: A\n---\n\nText.\n");
        return dir;
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
