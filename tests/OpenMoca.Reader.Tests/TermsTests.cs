using System.Text.Json.Nodes;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>The creator's terms (package spec §4.14, Reader contract §15), rule by rule.</summary>
public sealed class TermsTests : IDisposable
{
    private const string Node = "---\ntype: Note\ntitle: A\n---\n\n# A\n\nThe harbour lantern.\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-terms-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    private static HashSet<string> Problems(string terms) =>
        [.. TermsRules.Check(Json(terms)).Select(p => $"{p.Problem.Code} {p.Problem.Path}")];

    [Fact]
    public void Check_SortsProblemsIntoInvalidAndUnknown_AtTheFieldsPath()
    {
        var problems = Problems("""
            { "ai": { "allowed": { "edge": true, "training": "no" }, "answerSources": "other", "providerRequires": ["x", 1] },
              "nodes": [ { "paths": ["a"] } ] }
            """);

        Assert.Equal(
            [
                "U001_TERMS_INVALID ai.allowed.training", "U001_TERMS_INVALID ai.providerRequires.1", "U001_TERMS_INVALID nodes.0.paths.0",
                "U003_TERMS_UNKNOWN_VALUE ai.allowed.edge", "U003_TERMS_UNKNOWN_VALUE ai.answerSources", "U003_TERMS_UNKNOWN_VALUE ai.providerRequires.0",
            ],
            problems.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Check_NullWhereTheSchemaDoesNotDeclareIt_IsInvalid()
    {
        Assert.Equal(["U001_TERMS_INVALID ai.allowed.provider"], Problems("""{ "ai": { "allowed": { "provider": null } } }"""));
    }

    [Fact]
    public void Check_RangesOutOfOrderAndRepeatedSchemes_AreInvalid()
    {
        var problems = Problems("""
            { "rating": { "typicalAgeRange": "12-8", "contentRating": [ { "scheme": "a", "value": "1" }, { "scheme": "a", "value": "2" } ] },
              "rights": { "copyright": { "year": "2026-2024" } },
              "nodes": [ { "paths": ["content/a.md"], "rights": { "copyright": { "year": "2020-2021" } } } ] }
            """);

        Assert.Equal(
            ["U001_TERMS_INVALID rating.contentRating", "U001_TERMS_INVALID rating.typicalAgeRange", "U001_TERMS_INVALID rights.copyright.year"],
            problems.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Loosened_ReportsANodeValueLooserThanThePackages()
    {
        var found = TermsRules.Loosened(Json("""
            { "ai": { "allowed": { "provider": false }, "providerRequires": ["noTraining"] },
              "nodes": [ { "paths": ["content/a.md"], "ai": { "allowed": { "provider": true }, "providerRequires": [] } } ] }
            """));

        Assert.Equal(2, found.Count);
        Assert.Contains("allowed.provider", found[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "ai": { "allowed": false } }""", false)]
    [InlineData("""{ "ai": "none" }""", true)]
    [InlineData("""{ "nodes": {} }""", true)]
    [InlineData("""{ "nodes": [ "content/a.md" ] }""", true)]
    [InlineData("""{ "nodes": [ { "paths": "content/a.md" } ] }""", true)]
    [InlineData("""{ "nodes": [ { "paths": ["content/a.md"], "ai": { "allowed": [] } } ] }""", false)]
    public void ResolveAi_AMalformedContainer_SetsEveryPermissionUnderItToItsStrictest(string terms, bool wholeAi)
    {
        var ai = TermsRules.ResolveAi(Json(terms), "a.md", null, []);

        Assert.False(ai.Allowed.Device.Value);
        Assert.False(ai.Allowed.Host.Value);
        Assert.False(ai.Allowed.Provider.Value);
        Assert.False(ai.Allowed.MachineTranslation.Value);
        // Only a malformed ai (or a nodes entry that counts for every node) reaches answerSources and providerRequires.
        Assert.Equal(wholeAi ? "work" : "model", ai.AnswerSources.Value);
        Assert.Equal(wholeAi, ai.ProviderRequires.Value.Count > 0);
    }

    [Fact]
    public void ResolveAi_NoLevelLoosensThePlatform_AndEachFieldSaysWhoDecided()
    {
        var terms = Json("""
            { "ai": { "allowed": { "training": true, "provider": false } },
              "nodes": [ { "paths": ["content/notes/"], "ai": { "allowed": { "host": false } } } ] }
            """);
        var limits = new TermsLimits { Provider = true, AnswerSources = "work" }.ToAi();

        var notes = TermsRules.ResolveAi(terms, "notes/a.md", limits, []);
        var other = TermsRules.ResolveAi(terms, "a.md", limits, []);

        Assert.Equal(new ResolvedTerm<bool>(false, "platform"), notes.Allowed.Training);
        Assert.Equal(new ResolvedTerm<bool>(false, "package"), notes.Allowed.Provider);
        Assert.Equal(new ResolvedTerm<bool>(false, "node"), notes.Allowed.Host);
        Assert.Equal(new ResolvedTerm<bool>(true, "platform"), other.Allowed.Host);
        Assert.Equal(new ResolvedTerm<string>("work", "host"), other.AnswerSources);
    }

    [Fact]
    public void ResolveAi_ACompositeOnlyTightens()
    {
        var member = Json("""{ "ai": { "allowed": { "host": false } } }""");
        var composite = Json("""{ "ai": { "allowed": { "host": true, "device": false } } }""");

        var ai = TermsRules.ResolveAi(member, "a.md", null, [composite]);

        Assert.Equal(new ResolvedTerm<bool>(false, "package"), ai.Allowed.Host);
        Assert.Equal(new ResolvedTerm<bool>(false, "composite"), ai.Allowed.Device);
    }

    [Theory]
    [InlineData("""{}""", true, "platform")]
    [InlineData("""{ "rights": { "export": { "allowed": false } } }""", false, "package")]
    [InlineData("""{ "rights": { "export": { "allowed": true } } }""", true, "package")]
    [InlineData("""{ "rights": { "export": { "allowed": "no" } } }""", false, "package")]
    [InlineData("""{ "rights": { "export": false } }""", false, "package")]
    [InlineData("""{ "rights": [] }""", false, "package")]
    public void ResolveExport_DefaultsToAllowed_AndAMalformedValueCountsAsNot(string terms, bool value, string decidedBy)
    {
        Assert.Equal(new ResolvedTerm<bool>(value, decidedBy), TermsRules.ResolveExport(Json(terms), []));
    }

    [Fact]
    public void Gate_ARatingUnderAnUnlistedScheme_IsUnrated()
    {
        var ai = TermsRules.ResolveAi(Json("{}"), "a.md", null, []);
        var rating = Json("""{ "contentRating": [ { "scheme": "other", "value": "18" } ] }""");
        var listed = new TermsRequest { Rating = new RatingRequest { Schemes = new Dictionary<string, IReadOnlyList<string>?> { ["bbfc"] = ["U", "PG"] } } };
        var allowUnrated = listed with { Rating = listed.Rating! with { AllowUnrated = true } };

        Assert.Equal(["rating.unrated"], TermsRules.GateReasons(listed, ai, rating));
        Assert.Empty(TermsRules.GateReasons(allowUnrated, ai, rating));
    }

    [Theory]
    [InlineData("""{ "explicit": false, "mode": "strict-path" }""")]
    [InlineData("""{ "schemes": [] }""")]
    [InlineData("""true""")]
    public void Gate_ARatingRequestItCannotHonour_RemovesEverything(string request)
    {
        var ai = TermsRules.ResolveAi(Json("{}"), "a.md", null, []);

        Assert.Equal(["rating.request"], TermsRules.GateReasons(new TermsRequest { Rating = RatingRequest.FromJson(Json(request)) }, ai, null));
    }

    [Fact]
    public void Gate_AMalformedRating_CountsAsExplicitAndOutsideEveryScheme()
    {
        var ai = TermsRules.ResolveAi(Json("{}"), "a.md", null, []);
        var request = new TermsRequest { Rating = new RatingRequest { Explicit = false, AllowUnrated = true } };

        Assert.Equal(["rating.explicit"], TermsRules.GateReasons(request, ai, JsonValue.Create("PG")));
        Assert.Equal(["rating.explicit"], TermsRules.GateReasons(request, ai, Json("""{ "explicit": "no" }""")));
    }

    [Fact]
    public void Gate_AnUnknownDestination_IsTreatedAsAProvider()
    {
        var ai = TermsRules.ResolveAi(Json("""{ "ai": { "allowed": { "provider": false } } }"""), "a.md", null, []);

        Assert.Equal(["ai.allowed.provider"], TermsRules.GateReasons(new TermsRequest { Ai = new AiRequest("satellite") }, ai, null));
    }

    [Fact]
    public void RightsOf_TakesTheNodesEntryFirst_AndSkipsAValueWithAProblem()
    {
        var terms = Json("""
            { "rights": { "copyright": { "year": "2026", "notice": "© 2026 Example" } },
              "nodes": [ { "paths": ["content/a.md"], "rights": { "license": "CC0-1.0", "copyright": { "year": "2030-2020" } } } ] }
            """);
        var problems = TermsRules.Check(terms).Select(p => p.Problem).ToList();

        var (license, copyright) = TermsRules.RightsOf(terms, problems, "CC-BY-4.0", "a.md");
        var (otherLicense, _) = TermsRules.RightsOf(terms, problems, "CC-BY-4.0", "b.md");

        Assert.Equal("CC0-1.0", license);
        Assert.Equal("2026", copyright!["year"]!.GetValue<string>());
        Assert.Equal("CC-BY-4.0", otherLicense);
    }

    [Fact]
    public async Task Read_ExposesTermsAsWritten_WithTheirProblems_AndNeverFailsTheSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Package("p", """
            { "id": "https://example.com/p", "version": "1.0.0", "title": "P", "datePackaged": "2026-11-19T18:30:00+01:00",
              "terms": { "ai": { "allowed": { "provider": false, "edge": true } } } }
            """);

        var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: ct);

        Assert.True(result.Valid);
        Assert.Equal("2026-11-19T18:30:00+01:00", result.DatePackaged);
        Assert.Equal(TermsAuthority.Unverified, result.Terms!.Authority);
        Assert.Equal([new TermsProblem(DiagnosticCodes.U003TermsUnknownValue, "ai.allowed.edge")], result.Terms.Problems);
        Assert.Equal("""{"ai":{"allowed":{"provider":false,"edge":true}}}""", result.Terms.Declared!.ToJsonString());
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.M004UnknownKey);
    }

    [Fact]
    public async Task Search_WithATermsRequest_RemovesAndCounts_AndWithoutOneChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Package("q", """
            { "id": "https://example.com/q", "version": "1.0.0", "title": "Q", "terms": { "ai": { "allowed": { "provider": false } } } }
            """);
        var library = new Library().Add(await MocaReader.ReadPackageAsync(dir, cancellationToken: ct));
        var search = new MocaSearch(library, new LexicalBackend(library));

        var plain = await search.SearchAsync("lantern", cancellationToken: ct);
        var provider = await search.SearchAsync("lantern", new SearchOptions { Terms = new TermsRequest { Ai = new AiRequest(TermsDestinations.Provider) } }, ct);
        var excluded = Assert.Single(search.Diagnostics);
        var device = await search.SearchAsync("lantern", new SearchOptions { Terms = new TermsRequest { Ai = new AiRequest(TermsDestinations.Device) } }, ct);

        Assert.Null(Assert.Single(plain).Terms);
        Assert.Empty(provider);
        Assert.Equal(DiagnosticCodes.U004TermsExcluded, excluded.Code);
        Assert.Contains("ai.allowed.provider: 1", excluded.Message, StringComparison.Ordinal);
        var terms = Assert.Single(device).Terms!;
        Assert.Equal(new ResolvedTerm<bool>(true, "platform"), terms.Rights!.Export);
        Assert.Equal(new ResolvedTerm<bool>(false, "package"), terms.Ai.Allowed.Provider);
    }

    [Fact]
    public async Task Search_TwoChunksOfOneNodeThatShareAStart_AreBothKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var library = new Library().Add(await MocaReader.ReadPackageAsync(Package("r", """{ "id": "https://example.com/r", "version": "1.0.0", "title": "R" }"""), cancellationToken: ct));
        var rep = library.Packages[0].Result.Nodes[0].Representations[0];
        var start = rep.BodyOffset;
        SearchHit Hit(long end) => new(library.Packages[0].Result.Digest!, "https://example.com/r#a.md", "a.md", null, new CitationSpan(start, end), 1);
        var search = new MocaSearch(library, new FixedBackend([Hit(start + 4), Hit(start + 8), Hit(start + 8)]));

        var records = await search.SearchAsync("lantern", new SearchOptions { Limit = 10 }, ct);

        Assert.Equal([start + 4, start + 8], records.Select(r => r.Span!.End).Order());
    }

    private sealed class FixedBackend(IReadOnlyList<SearchHit> hits) : ISearchBackend
    {
        public BackendFeatures Features => BackendFeatures.Lexical;

        public IReadOnlyList<Diagnostic> Diagnostics => [];

        public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken) =>
            ValueTask.FromResult(hits);
    }

    private string Package(string name, string manifest)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "moca.json"), manifest);
        File.WriteAllText(Path.Combine(dir, "content", "a.md"), Node);
        return dir;
    }
}
