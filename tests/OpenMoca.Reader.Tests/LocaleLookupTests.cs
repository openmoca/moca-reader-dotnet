using OpenMoca.Reader.Content;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Package spec §5.4: choosing a representation by language, with the same vectors as the reference Reader's.</summary>
public sealed class LocaleLookupTests
{
    private static ContentNode Node(string? language, params string[] tags)
    {
        var node = new ContentNode { Path = "a.md", Language = language };
        node.Representations.Add(Rep(null));
        foreach (var tag in tags)
        {
            node.Representations.Add(Rep(tag));
        }

        return node;
    }

    private static Representation Rep(string? locale) => new()
    {
        Locale = locale,
        File = locale is null ? "content/a.md" : $"content/a.{locale}.md",
        Frontmatter = [],
        Body = "",
        BodyOffset = 0,
    };

    private static string Pick(ContentNode node, params string[] locale) =>
        Citations.Citations.PickRepresentation(node, locale).Locale ?? "default";

    [Fact]
    public void Lookup_ASiblingRegion_IsFoundThroughTheTruncatedRequest()
    {
        Assert.Equal("pt-BR", Pick(Node("en", "pt-BR", "pt-PT"), "pt-AO"));
        Assert.Equal("es-ES", Pick(Node("en", "es-ES"), "es-419"));
        Assert.Equal("fr-CA", Pick(Node("en", "fr-CA"), "fr-BE"));
    }

    [Fact]
    public void Lookup_TheDefaultFile_IsTaggedWithTheManifestLanguage()
    {
        Assert.Equal("default", Pick(Node("en", "en-GB"), "en"));
        Assert.Equal("default", Pick(Node("en", "en-GB"), "en-US"));
        Assert.Equal("en-GB", Pick(Node("en", "en-GB"), "en-GB"));
        // With no manifest language the default is only the fallback.
        Assert.Equal("en-GB", Pick(Node(null, "en-GB"), "en"));
    }

    [Fact]
    public void Lookup_EqualComesBeforeLonger_AtEachTruncation()
    {
        Assert.Equal("fr", Pick(Node("en", "fr", "fr-BE"), "fr-CA"));
        Assert.Equal("zh-Hant-HK", Pick(Node("en", "zh-Hant-HK", "zh-Hans"), "zh-Hant-TW"));
        Assert.Equal("pt-BR", Pick(Node("en", "pt-PT", "pt-BR"), "pt"));
        Assert.Equal("PT-br", Pick(Node("en", "PT-br"), "pt-br"));
    }

    [Fact]
    public void Lookup_APriorityList_TriesEachEntryInFullBeforeTheNext()
    {
        Assert.Equal("de", Pick(Node("en", "fr", "de"), "it", "de", "fr"));
        Assert.Equal("fr", Pick(Node("en", "fr"), "fr-CA", "en"));
        Assert.Equal("default", Pick(Node("de", "fr"), "it", "de"));
        Assert.Equal("default", Pick(Node("en", "fr"), "it", "ja"));
        Assert.Equal("default", Pick(Node("en", "fr")));
    }

    [Fact]
    public void ListAndMatches_FollowTheLookup()
    {
        Assert.Equal(["fr", "en"], LocaleLookup.List(["fr", "", null, "en"]));
        Assert.Empty(LocaleLookup.List(null));
        Assert.True(LocaleLookup.Matches("fr", ["fr-CA"]));
        Assert.True(LocaleLookup.Matches("pt-BR", ["pt-AO"]));
        Assert.False(LocaleLookup.Matches("en", ["fr"]));
        Assert.False(LocaleLookup.Matches(null, ["fr"]));
        // RFC 4647 §3.4: a trailing single-character subtag goes with the one after it, so en-a-bbb truncates to en.
        Assert.Null(LocaleLookup.Lookup([("x", "x")], ["en-a-bbb"]));
        Assert.Equal("en", LocaleLookup.Lookup([("en", "en")], ["en-a-bbb"]));
    }

    [Fact]
    public async Task LibraryGet_WithAPriorityList_ChoosesByTheLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = new Library().Add(await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Examples, "support-kb"), cancellationToken: ct));
        var id = library.Packages[0].Result.Id + "#refund-window.md";

        Assert.Equal("fr", library.Get(id, ["fr-CA", "en"])!.Node.Locale);
        Assert.Equal(library.Packages[0].Result.Manifest!["language"]!.GetValue<string>(), library.Get(id, ["it"])!.Node.Locale);
    }
}
