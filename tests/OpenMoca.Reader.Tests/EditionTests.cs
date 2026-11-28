using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Structure;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Package spec §7.4: edition groups, with the same cases as the reference Reader's.</summary>
public sealed class EditionTests
{
    private const string En = "https://example.com/en";
    private const string Fr = "https://example.com/fr";

    // A course composite whose two editions are composites of lessons, both composing one shared handout.
    private static readonly CompositionIndex Index = new([("d-course", new CompositionState
    {
        Members =
        [
            Member("d-en", En, "en", direct: true),
            Member("d-fr", Fr, "fr", direct: true),
            Member("d-en-1", $"{En}/1", null, "d-en"),
            Member("d-fr-1", $"{Fr}/1", null, "d-fr"),
            Member("d-handout", "https://example.com/handout", null, "d-en", "d-fr"),
        ],
        Up = new Dictionary<string, IReadOnlyList<string>>(),
        Slots = [new EditionSlot { Iri = "https://example.com/course#c", Editions = [En, Fr], Default = En }],
    })]);

    private static CompositionMember Member(string digest, string id, string? language, params string[] parents) =>
        Member(digest, id, language, false, parents);

    private static CompositionMember Member(string digest, string id, string? language, bool direct, params string[] parents) => new()
    {
        Digest = digest,
        Id = id,
        Direct = direct,
        Language = language,
        Bound = parents.Length == 0,
        Nodes = [],
        Parents = parents,
    };

    private static CitationRecord RecordOf(string digest) => new()
    {
        Package = new CitationPackage("https://example.com/x", "1.0.0", digest, false),
        Node = new CitationNode { Id = "https://example.com/x#a.md", Ref = "https://example.com/x@1.0.0#a.md", Path = "a.md", Type = "Note" },
        Trust = new CitationTrust { Status = "stable" },
    };

    private static bool Keeps(string digest, string locale) =>
        Index.ComposersOf(digest).All(c => c.KeepsEdition(RecordOf(digest), [locale]));

    [Fact]
    public void TheChosenEditionAndItsMembers_AreKept_TheOtherEditionAndItsMembersAreNot()
    {
        Assert.True(Keeps("d-fr", "fr"));
        Assert.True(Keeps("d-fr-1", "fr"));
        Assert.False(Keeps("d-en", "fr"));
        Assert.False(Keeps("d-en-1", "fr"));
    }

    [Fact]
    public void AMemberSharedByTwoEditions_IsTheChosenOnesToo()
    {
        Assert.True(Keeps("d-handout", "fr"));
        Assert.True(Keeps("d-handout", "en"));
    }

    [Fact]
    public void Choose_ByLookup_ThenTheDefault_ThenTheLowestId()
    {
        var slot = new EditionSlot { Iri = "https://example.com/course#c", Editions = ["https://example.com/b", "https://example.com/a", "https://example.com/c"], Default = "https://example.com/c" };
        var available = new Dictionary<string, string?>
        {
            ["https://example.com/a"] = "pt-PT",
            ["https://example.com/b"] = "pt-BR",
            ["https://example.com/c"] = "en",
        };

        Assert.Equal("https://example.com/b", Editions.Choose(slot, available, ["pt-AO"]));
        Assert.Equal("https://example.com/c", Editions.Choose(slot, available, ["de"]));
        available.Remove("https://example.com/c");
        Assert.Equal("https://example.com/a", Editions.Choose(slot, available, ["de"]));
        Assert.Null(Editions.Choose(slot, new Dictionary<string, string?>(), ["de"]));
    }

    [Fact]
    public void AnEditionTheCompositeListsItself_CanBeChosen_ThoughAnotherMemberAlsoComposesIt()
    {
        var nested = new CompositionIndex([("d-course", new CompositionState
        {
            Members = [Member("d-en", En, "en", direct: true), Member("d-fr", Fr, "fr", true, "d-en")],
            Up = new Dictionary<string, IReadOnlyList<string>>(),
            Slots = [new EditionSlot { Iri = "https://example.com/course#c", Editions = [En, Fr], Default = En }],
        })]);

        var composer = Assert.Single(nested.ComposersOf("d-fr"));

        Assert.True(composer.KeepsEdition(RecordOf("d-fr"), ["fr"]));
        Assert.False(composer.KeepsEdition(RecordOf("d-en"), ["fr"]));
    }
}
