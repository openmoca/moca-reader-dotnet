using System.Text.Json;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// What the terms gate covers (Reader contract §9.4, §10, §15.4; moca-spec ADR-0041): search counts and relation ids,
/// the structure for a terms request, the catalogue, and ingest.
/// </summary>
public sealed class TermsCoverageTests : IDisposable
{
    private const string Open = "https://example.com/open";
    private const string Named = "https://example.com/named";
    private const string Hidden = "https://example.com/hidden";

    private static readonly TermsRequest Provider = new() { Ai = new AiRequest(TermsDestinations.Provider, Use: "answer") };

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-terms-coverage-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Search: U004 counts and relation ids ----

    [Fact]
    public async Task Search_U004_CountsByReason_AndThoseFromHiddenPackages()
    {
        var library = await LibraryOfAsync(OpenPackage(), NamedPackage(), HiddenPackage());
        var search = new MocaSearch(library, new LexicalBackend(library));

        var records = await search.SearchAsync("lantern", new SearchOptions { Limit = 10, Terms = Provider }, Ct);

        Assert.Equal([$"{Open}@1.0.0#a.md"], records.Select(r => r.Node.Ref));
        var u004 = Assert.Single(search.Diagnostics);
        Assert.Equal(DiagnosticCodes.U004TermsExcluded, u004.Code);
        Assert.Equal(new Dictionary<string, int> { ["ai.allowed.provider"] = 2 }, u004.Excluded);
        Assert.Equal(1, u004.Hidden);
        var json = JsonSerializer.SerializeToNode(u004)!;
        Assert.Equal(1, json["hidden"]!.GetValue<int>());
        Assert.Equal(2, json["excluded"]!["ai.allowed.provider"]!.GetValue<int>());
    }

    [Fact]
    public async Task Search_ARecordNamesNoHiddenPackage_InSupersededBy_AndKeepsSuperseded()
    {
        var v1 = Package("v1", $$"""{ "mocaVersion": "0.7", "id": "{{Hidden}}", "version": "1.0.0", "title": "V1", "language": "en" }""");
        var v2 = Package("v2", $$"""
            { "mocaVersion": "0.7", "id": "{{Hidden}}", "version": "2.0.0", "title": "V2", "language": "en",
              "relations": [ { "type": "supersedes", "target": "{{Hidden}}", "version": "1.0.0" } ],
              "terms": { "ai": { "allowed": { "provider": false, "mention": false } } } }
            """);
        var library = await LibraryOfAsync(new LibraryOptions { AllowUnsignedRelations = true }, v1, v2);
        var search = new MocaSearch(library, new LexicalBackend(library));

        var device = Assert.Single(await search.SearchAsync("lantern", new SearchOptions { Limit = 10, IncludeAll = true, Terms = new TermsRequest { Ai = new AiRequest(TermsDestinations.Device) } }, Ct),
            r => r.Package.Version == "1.0.0");
        var provider = Assert.Single(await search.SearchAsync("lantern", new SearchOptions { Limit = 10, IncludeAll = true, Terms = Provider }, Ct));

        Assert.NotNull(device.Trust.SupersededBy);
        Assert.Equal("1.0.0", provider.Package.Version);
        Assert.True(provider.Trust.Superseded);
        Assert.Null(provider.Trust.SupersededBy);
    }

    [Fact]
    public async Task ApplyTerms_CountsHidden_AsSearchDoes()
    {
        var library = await LibraryOfAsync(OpenPackage(), HiddenPackage());
        var search = new MocaSearch(library, new LexicalBackend(library));

        var kept = search.ApplyTerms(library.Citations(includeText: true), Provider);

        Assert.Equal([Open], kept.Select(r => r.Package.Id));
        Assert.Equal(1, Assert.Single(search.Diagnostics).Hidden);
    }

    // ---- Structure for a terms request ----

    [Fact]
    public async Task StructureFor_LeavesOutWhatOnlyAClosedPackageStates()
    {
        var library = await LibraryOfAsync(
            Package("s-open", Manifest(Open, ""), """
                @prefix skos: <http://www.w3.org/2004/02/skos/core#> .
                @prefix t: <https://example.org/t#> .
                t:Topic a skos:Concept ; skos:prefLabel "Harbours"@en .
                """),
            Package("s-closed", Manifest(Named, """, "terms": { "ai": { "allowed": { "provider": false } } }"""), """
                @prefix skos: <http://www.w3.org/2004/02/skos/core#> .
                @prefix t: <https://example.org/t#> .
                t:Topic a skos:Concept ; skos:definition "Closed text."@en .
                t:Closed a skos:Concept ; skos:prefLabel "Closed"@en ; skos:broader t:Topic .
                """));

        var all = library.Structure;
        var provider = library.StructureFor(Provider);
        var device = library.StructureFor(new TermsRequest { Ai = new AiRequest(TermsDestinations.Device) });

        Assert.Equal("Closed text.", all.Concept("https://example.org/t#Topic")!.Definition);
        Assert.Null(provider.Concept("https://example.org/t#Topic")!.Definition);
        Assert.Null(provider.Concept("https://example.org/t#Closed"));
        Assert.Empty(provider.Narrower("https://example.org/t#Topic"));
        Assert.Same(all, library.StructureFor(new TermsRequest()));
        Assert.Same(all, library.StructureFor(null));
        Assert.Equal("Closed text.", device.Concept("https://example.org/t#Topic")!.Definition);
    }

    [Fact]
    public async Task StructureFor_NodesComeOnlyFromOpenPackages()
    {
        var node = "---\ntype: Note\ntitle: A\nmoca:\n  concepts:\n    - iri: https://example.org/t#Topic\n---\n\n# A\n\nThe harbour lantern.\n";
        const string ttl = "@prefix skos: <http://www.w3.org/2004/02/skos/core#> .\n<https://example.org/t#Topic> a skos:Concept .\n";
        var library = await LibraryOfAsync(
            Package("n-open", Manifest(Open, ""), ttl, node),
            Package("n-closed", Manifest(Named, """, "terms": { "ai": { "allowed": { "provider": false } } }"""), ttl, node));

        Assert.Equal(2, library.Structure.Nodes("https://example.org/t#Topic").Count);
        Assert.Equal([Open], library.StructureFor(Provider).Nodes("https://example.org/t#Topic").Select(r => r.Package.Id));
    }

    // ---- The catalogue ----

    [Fact]
    public async Task Catalogue_OpenFull_ClosedExactlySixFields_HiddenAbsent()
    {
        var library = await LibraryOfAsync(OpenPackage(), NamedPackage(), HiddenPackage());

        var entries = library.Catalogue(new CatalogueOptions { Terms = Provider });

        Assert.Equal([Named, Open], entries.Select(e => e.Id));
        var named = JsonSerializer.SerializeToNode(entries[0], CitationJson.Options)!.AsObject();
        Assert.Equal(["id", "version", "title", "publisher", "signed", "closedBy"], named.Select(kv => kv.Key));
        Assert.Equal(["ai.allowed.provider"], entries[0].ClosedBy);
        Assert.True(entries[0].Closed);
        Assert.Equal("A description.", entries[1].Description);
        Assert.Equal(["harbours"], entries[1].Tags);
        Assert.False(entries[1].Closed);
        foreach (var e in entries)
        {
            Assert.Empty(MocaSchemas.Validate(MocaSchemas.CatalogueEntry, JsonSerializer.SerializeToNode(e, CitationJson.Options)));
        }
    }

    [Fact]
    public async Task Catalogue_NoRequest_EveryPackageIsOpen()
    {
        var library = await LibraryOfAsync(OpenPackage(), HiddenPackage());

        var entries = library.Catalogue();

        Assert.Equal([Hidden, Open], entries.Select(e => e.Id));
        Assert.All(entries, e => Assert.False(e.Closed));
    }

    [Fact]
    public async Task Catalogue_OutsideTheAudience_OrRemovedByTheRatingGate_HasNoEntry()
    {
        var explicitPackage = Package("explicit", Manifest("https://example.com/explicit", """, "terms": { "rating": { "explicit": true } }"""));
        var audienceNode = "---\ntype: Note\ntitle: A\nmoca:\n  audience: staff\n---\n\n# A\n\nThe harbour lantern.\n";
        var staff = Package("staff", Manifest("https://example.com/staff", """, "terms": { "ai": { "allowed": { "provider": false } } }"""), node: audienceNode);
        var library = await LibraryOfAsync(OpenPackage(), explicitPackage, staff);

        var rated = library.Catalogue(new CatalogueOptions { Terms = Provider with { Rating = new RatingRequest { Explicit = false, AllowUnrated = true } } });
        var audience = library.Catalogue(new CatalogueOptions { Terms = Provider, Audiences = ["public"], AllowUnlabelled = true });

        Assert.DoesNotContain(rated, e => e.Id == "https://example.com/explicit");
        Assert.DoesNotContain(audience, e => e.Id == "https://example.com/staff");
        Assert.Contains(library.Catalogue(new CatalogueOptions { Terms = Provider }), e => e.Id == "https://example.com/staff");
    }

    [Fact]
    public async Task Catalogue_AHostLimitOfMentionFalse_HidesEveryClosedPackage()
    {
        var library = await LibraryOfAsync(OpenPackage(), NamedPackage());

        var entries = library.Catalogue(new CatalogueOptions { Terms = Provider, TermsLimits = new TermsLimits { Mention = false } });

        Assert.Equal([Open], entries.Select(e => e.Id));
    }

    [Fact]
    public async Task ClosureOf_ALoadedPackage_AndNullForOneNotLoaded()
    {
        var library = await LibraryOfAsync(OpenPackage(), HiddenPackage());
        var hidden = library.Packages.Single(p => p.Result.Id == Hidden).Result.Digest!;

        var closure = library.ClosureOf(hidden, Provider)!;
        Assert.True(closure.Closed);
        Assert.True(closure.Hidden);
        Assert.Equal(["ai.allowed.provider"], closure.ClosedBy);
        Assert.Null(library.ClosureOf("sha256:none", Provider));
    }

    // ---- Ingest ----

    [Fact]
    public async Task Ingest_ForAProvider_NeverEmbedsOrStoresClosedText_AndReportsU004()
    {
        var library = await LibraryOfAsync(OpenPackage(), NamedPackage(), HiddenPackage());
        var embedder = new RecordingEmbedder();
        var store = new MemoryStoreBackend(embedder);

        await store.IngestAsync(library, IngestOptions.Provider, Ct);

        Assert.All(embedder.Documents, t => Assert.DoesNotContain("closed", t, StringComparison.OrdinalIgnoreCase));
        Assert.Single(embedder.Documents);
        var u004 = Assert.Single(store.IngestDiagnostics);
        Assert.Equal(2, u004.Excluded!["ai.allowed.provider"]);
        Assert.Equal(1, u004.Hidden);
        var states = library.Packages.Select(p => PackageState.From(p.Result));
        var all = await new MocaSearch(states, library.Clock, store).SearchAsync("lantern", new SearchOptions { Limit = 10 }, Ct);
        Assert.Equal([Open], all.Select(r => r.Package.Id));
    }

    [Fact]
    public async Task Ingest_WithoutARequest_OrForTheDevice_TakesEveryUnit()
    {
        var library = await LibraryOfAsync(OpenPackage(), NamedPackage(), HiddenPackage());
        var plain = new RecordingEmbedder();
        var device = new RecordingEmbedder();
        var deviceStore = new MemoryStoreBackend(device);

        await new MemoryStoreBackend(plain).IngestAsync(library, Ct);
        await deviceStore.IngestAsync(library, new IngestOptions { Terms = new AiRequest(TermsDestinations.Device) }, Ct);

        Assert.Equal(3, plain.Documents.Count);
        Assert.Equal(3, device.Documents.Count);
        Assert.Empty(deviceStore.IngestDiagnostics);
    }

    [Fact]
    public async Task Ingest_TheUseIsAlwaysEmbed()
    {
        var library = await LibraryOfAsync(Package("t", Manifest(Open, """, "terms": { "ai": { "allowed": { "machineTranslation": false } } }""")));

        var units = StoreUnit.From(library, new IngestOptions { Terms = new AiRequest(TermsDestinations.Provider, Use: "translate") }, out var findings);

        Assert.Single(units);
        Assert.Empty(findings);
    }

    // ---- Fixtures ----

    private string OpenPackage() => Package("open", Manifest(Open, """, "description": "A description.", "tags": ["harbours"], "publisher": { "name": "Example" }"""));

    private string NamedPackage() => Package("named", Manifest(Named, """, "description": "Closed description.", "publisher": { "name": "Example" }, "terms": { "ai": { "allowed": { "provider": false } } }"""), node: Note("Closed lantern text."));

    private string HiddenPackage() => Package("hidden", Manifest(Hidden, """, "terms": { "ai": { "allowed": { "provider": false, "mention": false } } }"""), node: Note("Closed lantern words."));

    private static string Manifest(string id, string extra) =>
        $$"""{ "mocaVersion": "0.7", "id": "{{id}}", "version": "1.0.0", "title": "T {{id}}", "language": "en"{{extra}} }""";

    private static string Note(string text) => $"---\ntype: Note\ntitle: A\n---\n\n# A\n\nThe harbour lantern. {text}\n";

    private string Package(string name, string manifest, string? structure = null, string? node = null)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "moca.json"), manifest);
        File.WriteAllText(Path.Combine(dir, "content", "a.md"), node ?? Note(""));
        if (structure is not null)
        {
            File.WriteAllText(Path.Combine(dir, "structure.ttl"), structure);
        }

        return dir;
    }

    private static Task<Library> LibraryOfAsync(params string[] dirs) => LibraryOfAsync(new LibraryOptions(), dirs);

    private static async Task<Library> LibraryOfAsync(LibraryOptions options, params string[] dirs)
    {
        var library = new Library(options);
        foreach (var dir in dirs)
        {
            var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: Ct);
            Assert.True(result.Valid, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
            library.Add(result);
        }

        return library;
    }

    /// <summary>An embedder that records every document text it is sent.</summary>
    private sealed class RecordingEmbedder : IEmbedder
    {
        public List<string> Documents { get; } = [];

        public string Name => "recording";

        public string? Version => null;

        public int Dimensions => 2;

        public string? QueryPrefix => null;

        public string? DocumentPrefix => null;

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
        {
            if (role == EmbeddingRole.Document)
            {
                Documents.AddRange(texts);
            }

            return ValueTask.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => new float[] { 1, 0 })]);
        }
    }
}
