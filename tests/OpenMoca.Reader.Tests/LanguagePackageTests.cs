using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Reading a package whose content comes in more than one language: what the corpus does not pin down.</summary>
public sealed class LanguagePackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("moca-language-package-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<PackageResult> ReadAsync(string manifest, params (string Path, string Text)[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "moca.json"), manifest);
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_dir, path))!);
            File.WriteAllText(Path.Combine(_dir, path), text);
        }

        return await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("translationOf")]
    [InlineData("editionOf")]
    public async Task ATranslationOrEditionRelation_IsACoreType_AndNeedsNoSigner(string type)
    {
        var result = await ReadAsync(
            $$"""{ "id": "https://example.com/fr", "version": "1.0.0", "title": "T", "relations": [{ "type": "{{type}}", "target": "https://example.com/en" }] }""",
            ("content/a.md", "---\ntype: Note\ntitle: A\n---\n\nA.\n"));

        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is DiagnosticCodes.R005RelationTypeUnknown or DiagnosticCodes.R004RelationUnauthorised);
    }
}
