using OpenMoca.Reader.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sidecars;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader.ConformanceAdapter;

/// <summary>
/// What this Reader concludes for one case of moca-spec's <c>conformance/cases.json</c>, in the shape the
/// runner protocol expects (conformance/README.md). Mirrors moca-spec's scripts/conformance-adapter.mjs.
/// </summary>
public static class ConformanceCases
{
    private const string UnloadedDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Runs one case and returns the adapter's JSON output.</summary>
    public static async Task<JsonObject> ActualForAsync(string root, JsonObject c, CancellationToken cancellationToken = default)
    {
        var o = c["options"] as JsonObject ?? [];
        var readOptions = new ReadOptions
        {
            TrustRoot = Str(o["trustRoot"]) is { } trust ? TrustRoot.LoadFile(Path.Combine(root, trust)) : null,
            ResolveMember = o["members"] is JsonArray members
                ? MemberResolvers.Directories(members.Select(m => Path.Combine(root, Str(m)!)))
                : null,
            Limits = Limits(o["limits"] as JsonObject),
            RequireSignature = o["requireSignature"]?.GetValue<bool>() == true,
            Strict = o["strict"]?.GetValue<bool>() == true,
            SigstoreVerifier = new OpenMoca.Reader.Sigstore.SigstoreVerifier(),
        };

        // The host clock applies to reading too: keys expire (attestations §6).
        if (Dates.Parse(Str(o["now"])) is { } readNow)
        {
            readOptions = readOptions with { Clock = () => readNow };
        }
        var overlays = (o["overlays"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(v => new StructureOverlay(Str(v["id"])!, Str(v["layer"])!, File.ReadAllText(Path.Combine(root, Str(v["path"])!))))
            .ToList();

        return Str(c["kind"]) switch
        {
            "structure" => await StructureCaseAsync(root, c, readOptions, overlays, cancellationToken),
            "search" => await SearchCaseAsync(root, c, o, readOptions, overlays, cancellationToken),
            _ => await PackageCaseAsync(root, c, o, readOptions, cancellationToken),
        };
    }

    private static async Task<JsonObject> PackageCaseAsync(string root, JsonObject c, JsonObject o, ReadOptions readOptions, CancellationToken cancellationToken)
    {
        var result = await MocaReader.ReadPackageAsync(Path.Combine(root, Str(c["target"])!), readOptions, cancellationToken);
        var codes = result.Diagnostics.Select(d => d.Code).ToList();
        bool? sidecarUsable = null;
        if (Str(o["sidecar"]) is { } sidecar)
        {
            var bound = SidecarBinder.Bind(Path.Combine(root, sidecar), result);
            codes.AddRange(bound.Diagnostics.Select(d => d.Code));
            sidecarUsable = bound.Usable;
        }

        var evidenceMatched = new JsonObject();
        if (result.Valid)
        {
            foreach (var r in new Library().Add(result).Citations())
            {
                if (r.Evidence.Count > 0)
                {
                    evidenceMatched[r.Node.Path] = new JsonArray([.. r.Evidence.Select(e => e.Matched is { } m ? (JsonNode)JsonValue.Create(m) : null)]);
                }
            }
        }

        var output = new JsonObject
        {
            ["valid"] = result.Valid,
            ["codes"] = StructureJson.Strings(codes),
            ["capabilities"] = StructureJson.Strings(result.Capabilities),
            ["digest"] = result.Digest,
            ["evidenceMatched"] = evidenceMatched,
        };
        if (sidecarUsable is not null)
        {
            output["sidecarUsable"] = sidecarUsable;
        }

        return output;
    }

    private static async Task<JsonObject> StructureCaseAsync(string root, JsonObject c, ReadOptions readOptions, List<StructureOverlay> overlays, CancellationToken cancellationToken)
    {
        var library = new Library(new LibraryOptions { Overlays = overlays });
        foreach (var target in (c["packages"] as JsonArray ?? []).Select(Str).OfType<string>())
        {
            var result = await MocaReader.ReadPackageAsync(Path.Combine(root, target), readOptions, cancellationToken);
            if (result.Valid)
            {
                library.Add(result);
            }
        }

        var s = library.Structure;
        var args = c["args"] as JsonObject ?? [];
        var iri = Str(c["iri"])!;
        var transitive = args["transitive"] is JsonValue t && t.TryGetValue<bool>(out var tb) && tb;
        JsonNode? answer = Str(c["op"]) switch
        {
            "concept" => StructureJson.Concept(s.Concept(iri)),
            "requires" => StructureJson.Items(s.Requires(iri, transitive)),
            "requiredBy" => StructureJson.Items(s.RequiredBy(iri)),
            "parts" => StructureJson.Items(s.Parts(iri)),
            "narrower" => StructureJson.Items(s.Narrower(iri, transitive)),
            "broader" => StructureJson.Items(s.Broader(iri)),
            "related" => StructureJson.Items(s.Related(iri)),
            "sequence" => s.Sequence(iri) is { } seq ? StructureJson.Items(seq) : null,
            "nodes" => StructureJson.Strings(s.Nodes(iri, Str(args["include"]) ?? "none").Select(r => r.Node.Ref)),
            var op => throw new InvalidOperationException($"unknown structure op {op}"),
        };

        return new JsonObject { ["codes"] = StructureJson.Strings(library.Diagnostics.Select(d => d.Code)), ["result"] = answer };
    }

    private static async Task<JsonObject> SearchCaseAsync(string root, JsonObject c, JsonObject o, ReadOptions readOptions, List<StructureOverlay> overlays, CancellationToken cancellationToken)
    {
        var allowUnlabelled = o["allowUnlabelled"]?.GetValue<bool>() == true;
        var now = Dates.Parse(Str(o["now"])) ?? DateTimeOffset.UtcNow;
        var library = new Library(new LibraryOptions { Clock = () => now, Overlays = overlays, AllowUnsignedRelations = o["allowUnsignedRelations"]?.GetValue<bool>() == true });
        var codes = new List<string>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in (c["packages"] as JsonArray ?? []).Select(Str).OfType<string>())
        {
            var path = Path.Combine(root, target);
            var result = await MocaReader.ReadPackageAsync(path, readOptions, cancellationToken);
            if (!result.Valid)
            {
                continue;
            }

            SidecarBinding? bound = null;
            if (Str(o["sidecar"]) is { } sidecar)
            {
                bound = SidecarBinder.Bind(Path.Combine(root, sidecar), result);
                codes.AddRange(bound.Diagnostics.Select(d => d.Code));
            }

            library.Add(result, bound);
            sources[result.Digest!] = Path.GetFullPath(path);
        }

        var search = c["search"] as JsonObject ?? [];
        var options = new SearchOptions
        {
            Limit = (int)(JsonValues.IntegerOf(search["limit"]) ?? 50),
            IncludeAll = search["includeAll"] is JsonValue ia && ia.TryGetValue<bool>(out var iab) && iab,
            Locale = Str(search["locale"]),
            Concepts = search["concepts"] is JsonArray concepts ? concepts.Select(Str).OfType<string>().ToList() : null,
            Scope = Str(search["scope"]),
        };
        var audiences = o["audiences"] is JsonArray aud ? aud.Select(Str).OfType<string>().ToList() : null;
        var query = Str(search["query"]) ?? "";
        var backendName = Str(o["backend"]);

        IReadOnlyList<CitationRecord> records;
        if (backendName == "store")
        {
            // Everything listed is ingested (each package on its own with `ingestSeparately`); only the packages in
            // `allow` are "loaded", and the search gets their package states (contract §9.4).
            var store = new MemoryStoreBackend(filterPushdown: false);
            if (o["ingestSeparately"]?.GetValue<bool>() == true)
            {
                foreach (var p in library.Packages)
                {
                    var single = new Library(new LibraryOptions { Clock = () => now });
                    single.Add(p.Result);
                    await store.IngestAsync(single, cancellationToken);
                }
            }
            else
            {
                await store.IngestAsync(library, cancellationToken);
            }

            var allowPaths = (o["allow"] as JsonArray ?? []).Select(Str).OfType<string>().Select(t => Path.GetFullPath(Path.Combine(root, t))).ToHashSet();
            var allowedDigests = sources.Where(kv => allowPaths.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            var allowed = library.Packages.Where(p => allowedDigests.Contains(p.Result.Digest!)).Select(p => PackageState.From(p.Result));
            records = await new MocaSearch(allowed, () => now, store, audiences, allowUnsignedRelations: o["allowUnsignedRelations"]?.GetValue<bool>() == true) { AllowUnlabelled = allowUnlabelled }.SearchAsync(query, options, cancellationToken);
        }
        else
        {
            ISearchBackend backend = backendName switch
            {
                "all" => new EveryHitBackend(library),
                "dense" => new DenseBackend(library, new ConformanceEmbedder(o["embedder"] as JsonObject ?? [])),
                "hybrid" => Hybrid(library, o),
                _ => new LexicalBackend(library),
            };
            codes.AddRange(backend.Diagnostics.Select(d => d.Code));

            // Library diagnostics (L): for example two digests for one id@version.
            codes.AddRange(library.Diagnostics.Where(d => d.Code.StartsWith('L')).Select(d => d.Code));
            var hooks = Str(o["hooks"]) switch
            {
                "hostile" => HostileHooks(),
                "hostile-ingress" => HostileIngressHooks(),
                "ontology-guided" => OntologyGuided.Hooks(),
                _ => null,
            };
            records = await new MocaSearch(library, backend, audiences, hooks) { AllowUnlabelled = allowUnlabelled }.SearchAsync(query, options, cancellationToken);
        }

        return new JsonObject
        {
            ["codes"] = StructureJson.Strings(codes),
            ["records"] = new JsonArray([.. records.Select(CitationJson.ToJson)]),
        };
    }

    /// <summary>An egress hook that tries to return everything, plus a record from a package that is not loaded.</summary>
    /// <summary>The Reader's hybrid backend over its lexical backend and the dense backend, with the case's fusion options.</summary>
    private static HybridBackend Hybrid(Library library, JsonObject o)
    {
        var h = o["hybrid"] as JsonObject;
        var weights = h?["weights"] as JsonObject;
        double Number(JsonNode? n, double fallback) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;
        return new HybridBackend(
            new LexicalBackend(library),
            new DenseBackend(library, new ConformanceEmbedder(o["embedder"] as JsonObject ?? [])),
            Number(h?["k"], 60),
            Number(weights?["lexical"], 1),
            Number(weights?["dense"], 1));
    }

    /// <summary>
    /// An ingress hook that asks for more than the caller did. <see cref="IngressResult"/> cannot carry the opt-in,
    /// a limit or a locale, so the most it can do here is rewrite the query; the gate must follow the caller.
    /// </summary>
    private static SearchHooks HostileIngressHooks() => new()
    {
        Ingress = (query, _, _) => ValueTask.FromResult<IngressResult?>(new IngressResult(Query: query)),
    };

    private static SearchHooks HostileHooks() => new()
    {
        Egress = (candidates, ctx, _) =>
        {
            var all = ctx.Library!.Citations(includeText: true);
            var list = candidates.Concat(all).ToList();
            if (all.Count > 0)
            {
                list.Add(all[0] with { Package = all[0].Package with { Digest = UnloadedDigest } });
            }

            return ValueTask.FromResult<IReadOnlyList<CitationRecord>>(list);
        },
    };

    /// <summary>A backend that ignores every filter and returns every representation, plus a hit from an unloaded digest.</summary>
    private sealed class EveryHitBackend(Library library) : ISearchBackend
    {
        public BackendFeatures Features => BackendFeatures.None;

        public IReadOnlyList<Diagnostic> Diagnostics => [];

        public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken)
        {
            var hits = library.Packages
                .SelectMany(p => p.Result.Nodes.SelectMany(n => n.Representations.Select(r =>
                    new SearchHit(p.Result.Digest!, $"{p.Result.Id}#{n.Path}", n.Path, r.Locale, null, 1))))
                .Append(new SearchHit(UnloadedDigest, "https://example.com/not-loaded#x.md", "x.md", null, null, 2))
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<SearchHit>>(hits);
        }
    }

    /// <summary>The conformance embedder: the identity a case names, and [1, 0, 0, …] for every text.</summary>
    private sealed class ConformanceEmbedder(JsonObject identity) : IEmbedder
    {
        public string Name => Str(identity["name"]) ?? "";

        public string? Version => Str(identity["version"]);

        public int Dimensions => (int)(JsonValues.IntegerOf(identity["dimensions"]) ?? 0);

        public string? QueryPrefix => Str(identity["queryPrefix"]);

        public string? DocumentPrefix => Str(identity["documentPrefix"]);

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
        {
            IReadOnlyList<float[]> vectors = texts.Select(_ =>
            {
                var v = new float[Dimensions];
                if (v.Length > 0)
                {
                    v[0] = 1;
                }

                return v;
            }).ToList();
            return ValueTask.FromResult(vectors);
        }
    }

    private static ReaderLimits Limits(JsonObject? limits)
    {
        if (limits is null)
        {
            return ReaderLimits.Default;
        }

        int? Int(string key) => JsonValues.IntegerOf(limits[key]) is { } v ? (int)v : null;
        var d = ReaderLimits.Default;
        return d with
        {
            MaxEntries = Int("maxEntries") ?? d.MaxEntries,
            MaxBytes = JsonValues.IntegerOf(limits["maxBytes"]) ?? d.MaxBytes,
            MaxFrontmatterBytes = Int("maxFrontmatterBytes") ?? d.MaxFrontmatterBytes,
            MaxStructureBytes = Int("maxStructureBytes") ?? d.MaxStructureBytes,
            MaxTriples = Int("maxTriples") ?? d.MaxTriples,
            MaxEvidencePerNode = Int("maxEvidencePerNode") ?? d.MaxEvidencePerNode,
            MaxEvidenceChecksPerPackage = Int("maxEvidenceChecksPerPackage") ?? d.MaxEvidenceChecksPerPackage,
            MaxEvidenceBytesPerPackage = JsonValues.IntegerOf(limits["maxEvidenceBytesPerPackage"]) ?? d.MaxEvidenceBytesPerPackage,
            MaxSourceBytesChecked = Int("maxSourceBytesChecked") ?? d.MaxSourceBytesChecked,
        };
    }

    private static string? Str(JsonNode? node) => JsonValues.StringOf(node);
}
