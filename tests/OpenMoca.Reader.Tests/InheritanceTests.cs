using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Package spec §5.4: what a translation takes from its default file.</summary>
public sealed class InheritanceTests : IDisposable
{
    private const string Id = "https://example.com/t";

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-inherit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Page(string front) => $"---\ntype: Note\ntitle: A\n{front}---\n\nText.\n";

    private async Task<(PackageResult Result, Library Library)> LoadAsync(string english, string french)
    {
        File.WriteAllText(Path.Combine(_dir, "moca.json"), $$"""{ "id": "{{Id}}", "version": "1.0.0", "title": "T", "language": "en", "locales": ["fr"] }""");
        File.WriteAllText(Path.Combine(_dir, "structure.ttl"), $"<{Id}#topic> a <http://www.w3.org/2004/02/skos/core#Concept> .\n");
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        File.WriteAllText(Path.Combine(_dir, "content", "a.md"), english);
        File.WriteAllText(Path.Combine(_dir, "content", "a.fr.md"), french);
        var result = await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.Parse("2026-11-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return (result, new Library(new LibraryOptions { Clock = () => now }).Add(result));
    }

    [Fact]
    public async Task ATranslationWithNoneOfTheKeys_TakesEachFromItsDefaultFile()
    {
        var (_, library) = await LoadAsync(
            Page($"status: deprecated\nmoca:\n  valid_from: 2026-01-01T00:00:00Z\n  valid_until: 2027-01-01T00:00:00Z\n  concepts:\n    - iri: {Id}#topic\n"),
            Page(""));

        var fr = library.Get($"{Id}#a.md", ["fr"])!;

        Assert.Equal("fr", fr.Node.Locale);
        Assert.Equal("deprecated", fr.Trust.Status);
        Assert.Equal("2026-01-01T00:00:00Z", fr.Trust.ValidFrom);
        Assert.Equal("2027-01-01T00:00:00Z", fr.Trust.ValidUntil);
        Assert.Equal([$"{Id}#topic"], fr.Concepts);
    }

    [Fact]
    public async Task EachKeyOnItsOwn_AKeyTheTranslationWrites_IsItsOwnEvenWhenInvalid()
    {
        var (_, library) = await LoadAsync(
            Page($"status: deprecated\nmoca:\n  valid_until: 2027-01-01T00:00:00Z\n  concepts:\n    - iri: {Id}#topic\n"),
            Page("status: retired\nmoca:\n  valid_from: 2026-06-01T00:00:00Z\n  concepts: []\n"));

        var fr = library.Get($"{Id}#a.md", ["fr"])!;

        Assert.Equal("stable", fr.Trust.Status);
        Assert.Equal("2026-06-01T00:00:00Z", fr.Trust.ValidFrom);
        Assert.Equal("2027-01-01T00:00:00Z", fr.Trust.ValidUntil);
        Assert.Null(fr.Concepts);
    }

    [Fact]
    public async Task C010_IsOnTheWindowATranslationEndsUpWith_OnItsOwnFile()
    {
        var (result, _) = await LoadAsync(
            Page("moca:\n  valid_until: 2026-01-01T00:00:00Z\n"),
            Page("moca:\n  valid_from: 2026-06-01T00:00:00Z\n"));

        Assert.Equal(["content/a.fr.md"], result.Diagnostics.Where(d => d.Code == DiagnosticCodes.C010ValidityWindowInvalid).Select(d => d.File));
    }
}
