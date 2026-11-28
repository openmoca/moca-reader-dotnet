using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.ConformanceAdapter;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// A store search with package states supplied on demand (<see cref="IPackageStateSource"/>, Reader contract §9.4): the
/// same results as with every state in memory, only the states a search needs loaded, the relation index built once,
/// a candidate pool from 200 to 2,000, and memory per search that does not grow with the store.
/// </summary>
public sealed class StoreStatesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Lazy<JsonObject> Corpus = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(MocaSpec.Conformance, "cases.json")))!.AsObject());

    public static TheoryData<string> StoreCaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Corpus.Value["cases"]!.AsArray().OfType<JsonObject>())
        {
            if (JsonValues.StringOf(c["kind"]) == "search" && JsonValues.StringOf(c["options"]?["backend"]) == "store")
            {
                data.Add(JsonValues.StringOf(c["id"])!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StoreCaseIds))]
    public async Task StoreCase_WithStatesOnDemand_GivesTheSameResultAsStatesInMemory(string id)
    {
        var c = Corpus.Value["cases"]!.AsArray().OfType<JsonObject>().Single(x => JsonValues.StringOf(x["id"]) == id);
        var inMemory = await ConformanceCases.ActualForAsync(MocaSpec.Conformance, c, TestContext.Current.CancellationToken);

        CountingStates? source = null;
        ConformanceCases.StateSource.Value = (states, allowUnsigned) => source = new CountingStates(states, allowUnsigned);
        try
        {
            var onDemand = await ConformanceCases.ActualForAsync(MocaSpec.Conformance, c, TestContext.Current.CancellationToken);
            Assert.Equal(inMemory.ToJsonString(), onDemand.ToJsonString());
            Assert.NotNull(source);
            Assert.True(source.Loaded.Count <= source.Digests.Count, "a state is loaded at most once per search, and only from the allowlist");
        }
        finally
        {
            ConformanceCases.StateSource.Value = null;
        }
    }

    [Fact]
    public async Task Search_OnDemand_LoadsOnlyTheHitsPackagesAndTheirComposers()
    {
        var store = Synthetic.Store(1_000, hitPackages: 5);
        var search = new MocaSearch(store.Source, () => Now, store.Backend);

        var records = await search.SearchAsync("q", new SearchOptions { Limit = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal(5, records.Count);
        Assert.Equal(5, store.Source.Loaded.Count);
        Assert.All(store.Source.Loaded, d => Assert.Contains(d, store.HitDigests));
    }

    [Fact]
    public void Pool_OutsideTheRange_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchOptions { Pool = SearchOptions.MinPool - 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchOptions { Pool = SearchOptions.MaxPool + 1 });
        Assert.Equal(200, new SearchOptions { Pool = 200 }.Pool);
        Assert.Equal(2000, new SearchOptions { Pool = 2000 }.Pool);
        Assert.Null(new SearchOptions().Pool);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public async Task Pool_ReturnsThatManyGatedRecords_AsTheSameLimitWould(int pool)
    {
        var store = Synthetic.Store(3_000, hitPackages: 3_000);
        var bySize = await new MocaSearch(store.Source, () => Now, store.Backend).SearchAsync("q", new SearchOptions { Pool = pool }, TestContext.Current.CancellationToken);
        var byLimit = await new MocaSearch(store.Source, () => Now, store.Backend).SearchAsync("q", new SearchOptions { Limit = pool }, TestContext.Current.CancellationToken);

        Assert.Equal(pool, bySize.Count);
        Assert.Equal(byLimit.Select(r => r.Node.Ref), bySize.Select(r => r.Node.Ref));
        Assert.Equal(bySize.Count, bySize.Select(r => r.Node.Ref).Distinct().Count());
    }

    [Fact]
    public async Task Pool_WithAnAllowlistSmallerThanTheBackend_KeepsOnlyAllowedRecords()
    {
        // The gate still runs on every record of the pool: hits from packages outside the allowlist never pass.
        var store = Synthetic.Store(1_000, hitPackages: 1_000, allowEvery: 2);
        var search = new MocaSearch(store.Source, () => Now, store.Backend) { MaxCandidates = SearchOptions.MaxPool };
        var records = await search.SearchAsync("q", new SearchOptions { Pool = 200 }, TestContext.Current.CancellationToken);

        Assert.Equal(200, records.Count);
        Assert.All(records, r => Assert.True(store.Source.Contains(r.Package.Digest)));
    }

    [Fact]
    public void RelationIndex_Linear_GivesThePairwiseScansEffectsInItsOrder()
    {
        var states = new List<PackageState>();
        for (var i = 0; i < 60; i++)
        {
            var relations = new JsonArray();
            if (i % 3 == 0)
            {
                relations.Add(new JsonObject { ["type"] = "supersedes", ["target"] = $"https://example.com/p/{i % 7}", ["version"] = i % 2 == 0 ? "1.0.0" : null });
            }

            if (i % 5 == 0)
            {
                relations.Add(new JsonObject { ["type"] = "conflictsWith", ["target"] = $"https://example.com/p/{(i + 3) % 7}" });
            }

            states.Add(new PackageState
            {
                Digest = $"sha256:{i:x64}",
                Id = $"https://example.com/p/{i % 7}",
                Version = $"{i % 3}.0.0",
                Relations = relations,
                RelationScopes = i % 4 == 0 ? [] : ["https://example.com/p/"],
            });
        }

        foreach (var allowUnsigned in new[] { false, true })
        {
            var linear = PackageState.RelationIndexOf(states, allowUnsigned);
            var pairwise = Pairwise(states, allowUnsigned);
            Assert.Equal(Canonical(pairwise.Superseded), Canonical(linear.Superseded));
            Assert.Equal(Canonical(pairwise.Contested), Canonical(linear.Contested));
        }
    }

    [Fact]
    public async Task ApplyTerms_WithAHostSource_IsAsync()
    {
        var store = Synthetic.Store(10, hitPackages: 2);
        var search = new MocaSearch(store.Source, () => Now, store.Backend);
        var records = await search.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        var request = new TermsRequest { Ai = new AiRequest(TermsDestinations.Provider) };

        Assert.Throws<InvalidOperationException>(() => search.ApplyTerms(records, request));
        var kept = await search.ApplyTermsAsync(records, request, TestContext.Current.CancellationToken);

        // No package states any terms, so each is at its strictest: nothing goes to a provider (contract §15.3).
        Assert.Empty(kept);
        Assert.Contains(search.Diagnostics, d => d.Code == DiagnosticCodes.U004TermsExcluded);
    }

    [Fact]
    public async Task Search_WhenTraced_RecordsEachLoadWithCountsAndNoContent()
    {
        var store = Synthetic.Store(100, hitPackages: 4);
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ReaderTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await new MocaSearch(store.Source, () => Now, store.Backend).SearchAsync("q", new SearchOptions { Limit = 4 }, TestContext.Current.CancellationToken);

        var load = Assert.Single(spans, s => s.OperationName == ReaderTelemetry.StateLoadSpan);
        Assert.Equal(4, load.GetTagItem("moca.states.requested"));
        Assert.Equal(4, load.GetTagItem("moca.states.loaded"));
        Assert.Equal(0, load.GetTagItem("moca.states.missing"));
        Assert.Equal(100, load.GetTagItem("moca.store.packages"));
        Assert.All(load.TagObjects, t => Assert.StartsWith("moca.", t.Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Brief item 4: memory per search does not grow with the store. The same search, over a store of 1,000 and of
    /// 10,000 packages, allocates about the same; the figures are written to the test output.
    /// </summary>
    [Fact]
    public async Task Search_OnDemand_MemoryPerSearchDoesNotGrowWithTheStore()
    {
        var small = await AllocatedPerSearch(1_000);
        var large = await AllocatedPerSearch(10_000);
        TestContext.Current.TestOutputHelper?.WriteLine($"bytes allocated per search: 1,000 packages {small:N0}; 10,000 packages {large:N0}");

        // Within 10% (and 16 KiB): the store is ten times larger.
        Assert.True(large <= small * 1.1 + 16_384, $"1,000 packages: {small:N0} bytes; 10,000 packages: {large:N0} bytes");
    }

    private static async Task<long> AllocatedPerSearch(int packages)
    {
        var store = Synthetic.Store(packages, hitPackages: 20);
        var search = new MocaSearch(store.Source, () => Now, store.Backend);
        var options = new SearchOptions { Limit = 20 };
        // Warm up, then measure several searches on this thread: the source and backend complete synchronously, so
        // the whole search runs here.
        await search.SearchAsync("q", options, TestContext.Current.CancellationToken);
        const int Runs = 20;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Runs; i++)
        {
            var records = search.SearchAsync("q", options, TestContext.Current.CancellationToken);
            Assert.True(records.IsCompleted);
            Assert.Equal(20, (await records).Count);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Runs;
    }

    // The scan over every pair of packages that PackageState.RelationIndexOf used before 0.8.0, kept here as the reference.
    private static RelationIndex Pairwise(IReadOnlyList<PackageState> packages, bool allowUnsignedRelations)
    {
        var superseded = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var contested = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void Push(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        foreach (var m in packages)
        {
            foreach (var relation in m.Relations.OfType<JsonObject>())
            {
                var targetId = JsonValues.StringOf(relation["target"]);
                if (!allowUnsignedRelations && !Attestations.TrustRoot.ScopeCovers(m.RelationScopes, targetId))
                {
                    continue;
                }

                var targetVersion = JsonValues.StringOf(relation["version"]);
                var type = JsonValues.StringOf(relation["type"]);
                foreach (var t in packages.Where(t => t.Id == targetId && (string.IsNullOrEmpty(targetVersion) || t.Version == targetVersion)))
                {
                    var key = $"{t.Id}@{t.Version}";
                    var self = $"{m.Id}@{m.Version}";
                    if (type == "supersedes")
                    {
                        Push(superseded, key, self);
                    }

                    if (type == "conflictsWith")
                    {
                        Push(contested, key, self);
                        Push(contested, self, key);
                    }
                }
            }
        }

        return new RelationIndex(superseded, contested);
    }

    private static string Canonical(IReadOnlyDictionary<string, List<string>> map) =>
        string.Join("\n", map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"));

    /// <summary>A host's state source that loads asynchronously and counts what it is asked for.</summary>
    private sealed class CountingStates(IEnumerable<PackageState> states, bool allowUnsigned) : IPackageStateSource
    {
        private readonly MemoryPackageStates _inner = new(states, allowUnsigned);

        public List<string> Loaded { get; } = [];

        public IReadOnlyList<string> Digests => _inner.Digests;

        public StoreIndex Index => _inner.Index;

        public bool Contains(string digest) => _inner.Contains(digest);

        public async ValueTask<IReadOnlyList<PackageState>> LoadAsync(IReadOnlyCollection<string> digests, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Assert.NotEmpty(digests);
            Assert.All(digests, d => Assert.DoesNotContain(d, Loaded));
            Loaded.AddRange(digests);
            return await _inner.LoadAsync(digests, cancellationToken);
        }
    }

    /// <summary>
    /// A synthetic store: <c>n</c> packages whose states are made when asked for and never kept, and a backend whose
    /// hits come from the first <c>hitPackages</c> of them, each with one stored record.
    /// </summary>
    private static class Synthetic
    {
        private static readonly Lazy<CitationRecord> Template = new(() =>
        {
            var result = MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Conformance, "fixtures", "valid-minimal")).GetAwaiter().GetResult();
            var library = new Library(new LibraryOptions { Clock = () => Now });
            library.Add(result);
            return StoreUnit.From(library)[0].Record;
        });

        public static (GeneratedStates Source, FixedBackend Backend, HashSet<string> HitDigests) Store(int n, int hitPackages, int allowEvery = 1)
        {
            var source = new GeneratedStates(n, allowEvery);
            var hits = new List<SearchHit>();
            for (var i = 0; i < hitPackages; i++)
            {
                var digest = GeneratedStates.DigestOf(i);
                var id = GeneratedStates.IdOf(i);
                var template = Template.Value;
                var record = template with
                {
                    Package = template.Package with { Id = id, Digest = digest },
                    Node = template.Node with { Id = $"{id}#{template.Node.Path}", Ref = $"{id}#{template.Node.Path}" },
                };
                hits.Add(new SearchHit(digest, record.Node.Id, record.Node.Path, null, null, 1.0 - (i * 1e-6), record));
            }

            return (source, new FixedBackend(hits), [.. hits.Select(h => h.Digest)]);
        }
    }

    private sealed class GeneratedStates : IPackageStateSource
    {
        private readonly int _allowEvery;
        private readonly List<string> _digests;

        public GeneratedStates(int n, int allowEvery)
        {
            _allowEvery = allowEvery;
            _digests = [.. Enumerable.Range(0, n).Where(i => i % allowEvery == 0).Select(DigestOf)];
            // Streamed: each state is made, indexed and dropped.
            Index = StoreIndex.Build(Enumerable.Range(0, n).Where(i => i % allowEvery == 0).Select(StateOf), allowUnsignedRelations: false);
        }

        public List<string> Loaded { get; } = [];

        public IReadOnlyList<string> Digests => _digests;

        public StoreIndex Index { get; }

        public static string DigestOf(int i) => $"sha256:{i:x64}";

        public static string IdOf(int i) => $"https://example.com/synthetic/{i}";

        public bool Contains(string digest) =>
            digest.StartsWith("sha256:", StringComparison.Ordinal)
            && int.TryParse(digest.AsSpan(7), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var i)
            && i < _digests.Count * _allowEvery && i % _allowEvery == 0;

        public ValueTask<IReadOnlyList<PackageState>> LoadAsync(IReadOnlyCollection<string> digests, CancellationToken cancellationToken)
        {
            Loaded.AddRange(digests);
            return ValueTask.FromResult<IReadOnlyList<PackageState>>([.. digests.Select(d => StateOf(int.Parse(d.AsSpan(7), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)))]);
        }

        private static PackageState StateOf(int i) => new()
        {
            Digest = DigestOf(i),
            Id = IdOf(i),
            Version = "1.0.0",
            // From the 500th on, each fifth package supersedes the next, so the relation index is not empty; the hits
            // come from the first packages, which nothing supersedes.
            Relations = i >= 500 && i % 5 == 0 ? [new JsonObject { ["type"] = "supersedes", ["target"] = IdOf(i + 1) }] : [],
            RelationScopes = ["https://example.com/synthetic/"],
            Signers = ["Example publisher"],
        };
    }

    /// <summary>A store backend with pushdown that returns its hits, best first, up to the limit.</summary>
    private sealed class FixedBackend(IReadOnlyList<SearchHit> hits) : ISearchBackend
    {
        public BackendFeatures Features => BackendFeatures.Lexical | BackendFeatures.FilterPushdown;

        public IReadOnlyList<Diagnostic> Diagnostics => [];

        public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SearchHit>>([.. hits.Take(limit)]);
    }
}
