using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Reader contract §9 and §9.2 step 5.4: what a search's locale does.</summary>
public sealed class SearchLanguageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("moca-search-language-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<Library> LoadAsync(params (string Path, string Text)[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "moca.json"), """{ "id": "https://example.com/r", "version": "1.0.0", "title": "R", "language": "en", "locales": ["fr"] }""");
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        foreach (var (path, text) in files)
        {
            File.WriteAllText(Path.Combine(_dir, "content", path), text);
        }

        return new Library().Add(await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARetiredTranslation_FallsBackToTheDefault_WithPushdownToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = await LoadAsync(
            ("page.md", "---\ntype: Note\ntitle: Bread\n---\n\nBread.\n"),
            ("page.fr.md", "---\ntype: Note\ntitle: Pain\nstatus: deprecated\n---\n\nBread pain.\n"));
        var search = new MocaSearch(library, new LexicalBackend(library));

        var hits = await search.SearchAsync("bread", new SearchOptions { Locale = ["fr"] }, ct);
        var all = await search.SearchAsync("bread", new SearchOptions { Locale = ["fr"], IncludeAll = true }, ct);

        Assert.Equal(["en"], hits.Select(r => r.Node.Locale));
        Assert.Equal([("fr", "deprecated")], all.Select(r => (r.Node.Locale, r.Trust.Status)));
    }

    [Fact]
    public async Task LocaleModeOnlyAndFirst_LeaveOutAndReorder_ByTheLocaleList()
    {
        var ct = TestContext.Current.CancellationToken;
        // The English-only page matches best; the French page less well.
        var library = await LoadAsync(
            ("a.md", "---\ntype: Note\ntitle: A\n---\n\nbread bread bread bread.\n"),
            ("b.md", "---\ntype: Note\ntitle: B\n---\n\nbread.\n"),
            ("b.fr.md", "---\ntype: Note\ntitle: B\n---\n\nbread pain.\n"));

        async Task<List<(string Path, string? Locale)>> Search(SearchOptions options) =>
            [.. (await library.SearchAsync("bread", options, cancellationToken: ct)).Select(r => (r.Node.Path, r.Node.Locale))];

        Assert.Equal(["a.md", "b.md"], (await Search(new SearchOptions { Locale = ["fr"] })).Select(r => r.Path));
        Assert.Equal([("b.md", (string?)"fr")], await Search(new SearchOptions { Locale = ["fr"], LocaleMode = LocaleMode.Only }));
        Assert.Equal(["b.md", "a.md"], (await Search(new SearchOptions { Locale = ["fr"], LocaleMode = LocaleMode.First })).Select(r => r.Path));
        Assert.Equal(["b.md"], (await Search(new SearchOptions { Locale = ["fr"], LocaleMode = LocaleMode.First, Limit = 1 })).Select(r => r.Path));
        // Without a locale, localeMode has no effect: each node's best match, as the lexical backend gives it.
        Assert.Equal((await Search(new SearchOptions())).Count, (await Search(new SearchOptions { LocaleMode = LocaleMode.Only })).Count);
    }
}
