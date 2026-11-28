// Runs parity.mjs's scenarios through this Reader and prints the results as JSON, in the reference's shape.
// Usage: OpenMoca.Reader.Parity <scenarios.json> <moca-spec dir> <sidecar dir>
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sidecars;

var config = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
var spec = args[1];
var work = args[2];
var now = Dates.Parse(config["now"]!.GetValue<string>())!.Value;
var limit = config["limit"]!.GetValue<int>();
var cache = new Dictionary<string, PackageResult>(StringComparer.Ordinal);
var output = new JsonArray();

foreach (var sc in config["scenarios"]!.AsArray().Select(n => n!.AsObject()))
{
    var library = new Library(new LibraryOptions { Clock = () => now });
    var codes = new List<string>();
    foreach (var pair in sc["load"]!.AsArray().Select(p => p!.AsArray()))
    {
        var path = Path.Combine(spec, config["packages"]![pair[0]!.GetValue<string>()]!.GetValue<string>());
        if (!cache.TryGetValue(path, out var result))
        {
            cache[path] = result = await MocaReader.ReadPackageAsync(path);
        }

        SidecarBinding? bound = null;
        if (pair.Count > 1)
        {
            var b = SidecarBinder.Bind(Path.Combine(work, pair[1]!.GetValue<string>()), result);
            codes.AddRange(b.Diagnostics.Select(d => d.Code));
            bound = b.Usable ? b : null;
        }

        library.Add(result, bound);
    }

    var backendName = sc["backend"]!.GetValue<string>();
    var embedder = new HashingEmbedder(sc["embedder"]?.GetValue<string>() == "other" ? "another-model" : "parity-hash");
    var audiences = sc["audiences"]?.AsArray().Select(a => a!.GetValue<string>()).ToList();
    ISearchBackend backend;
    MocaSearch search;
    if (backendName.StartsWith("store", StringComparison.Ordinal))
    {
        var store = await new MemoryStoreBackend(backendName == "store-dense" ? embedder : null).IngestAsync(library);
        backend = store;
        search = new MocaSearch(library.Packages.Select(p => PackageState.From(p.Result)), () => now, store, audiences);
    }
    else
    {
        var weights = sc["hybrid"]?["weights"];
        backend = backendName switch
        {
            "dense" => new DenseBackend(library, embedder),
            "hybrid" => new HybridBackend(
                new LexicalBackend(library),
                new DenseBackend(library, embedder),
                lexicalWeight: weights?["lexical"]?.GetValue<double>() ?? 1,
                denseWeight: weights?["dense"]?.GetValue<double>() ?? 1),
            _ => new LexicalBackend(library),
        };
        var hooks = sc["diversify"] is { } perNode ? Diversify.Hooks(perNode.GetValue<int>()) : null;
        search = new MocaSearch(library, backend, audiences, hooks);
    }

    var options = new SearchOptions { Limit = limit, Locale = sc["locale"]?.GetValue<string>() };
    var queries = new JsonArray();
    foreach (var q in config["queries"]![sc["queries"]!.GetValue<string>()]!.AsArray().Select(x => x!.GetValue<string>()))
    {
        var results = new JsonArray();
        foreach (var r in await search.SearchAsync(q, options))
        {
            results.Add(new JsonObject
            {
                ["ref"] = r.Node.Ref,
                ["locale"] = r.Node.Locale,
                ["span"] = r.Span is { } s ? new JsonArray(s.Start, s.End) : null,
                ["score"] = r.Score,
                ["text"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Text ?? "")))[..16].ToLowerInvariant(),
            });
        }

        queries.Add(new JsonObject { ["query"] = q, ["results"] = results });
    }

    codes.AddRange(backend.Diagnostics.Select(d => d.Code));
    output.Add(new JsonObject
    {
        ["name"] = sc["name"]!.GetValue<string>(),
        ["codes"] = new JsonArray([.. codes.Distinct().Order(StringComparer.Ordinal).Select(c => (JsonNode)c)]),
        ["queries"] = queries,
    });
}

Console.WriteLine(output.ToJsonString());

/// <summary>The deterministic hashing embedder parity.mjs uses: the Reader's tokens, FNV-1a into 64 buckets, counts.</summary>
internal sealed class HashingEmbedder(string name) : IEmbedder
{
    private const int Size = 64;

    public string Name => name;

    public string? Version => "1";

    public int Dimensions => Size;

    public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> vectors = [.. texts.Select(text =>
        {
            var v = new float[Size];
            foreach (var token in Bm25.Tokens(text))
            {
                var h = 0x811c9dc5u;
                foreach (var b in Encoding.UTF8.GetBytes(token))
                {
                    h = unchecked((h ^ b) * 0x01000193u);
                }

                v[h % Size] += 1;
            }

            return v;
        })];
        return ValueTask.FromResult(vectors);
    }
}
