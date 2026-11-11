# OpenMoca.Reader

The MOCA Reader for .NET: open a package from a directory, a `.moca` archive
or a host-supplied source; check it; compute its digest; verify attestations
and evidence; resolve members; answer structure operations; produce citation
records; and search with one entry point over lexical, dense-sidecar or store
backends.

It implements the Reader class of the
[Reader contract](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md),
follows the
[Reader interface](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-interface.md),
and passes the whole
[conformance corpus](https://github.com/openmoca/moca-spec/blob/main/conformance/README.md).
The reference Reader is
[moca-reader-typescript](https://github.com/openmoca/moca-reader-typescript).

## Install

```sh
dotnet add package OpenMoca.Reader
```

.NET 10. [Getting started](docs/getting-started.md) takes you from install to a
cited search result and a conformance run in five steps. The
[framework bindings](#framework-bindings) are separate packages:
add only the ones you use. Example packages are in
[moca-spec](https://github.com/openmoca/moca-spec/tree/main/examples), and
[moca-examples](https://github.com/openmoca/moca-examples#readme) has a
command line and a chat built on this Reader.

## Lexical search

The built-in lexical backend is BM25 (k1 1.2, b 0.75) over node text, or over
sidecar chunks when a sidecar is bound. Every MOCA Reader tokenises the same
way, so they return the same results:

- NFKC-normalise, then lower-case each code point (simple case mapping).
- Letters, digits and combining marks make words; anything else separates
  them. Words in scripts with vowel signs, such as Devanagari, stay whole.
- Chinese, Japanese, Thai, Lao, Khmer and Myanmar text is split into each
  character (with its marks) and each pair of neighbouring characters.
- No stemming. A host that needs it supplies its own lexical backend.
- Without a locale, every language of a node is searched and its best match
  returned; with one, the representation package spec §5 selects.

Characters added in very recent Unicode versions can tokenise differently
across Readers, because each language runtime ships its own Unicode data.

## Conformance

As [Reader contract §14](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#14-demonstrating-conformance)
asks:

| | |
| --- | --- |
| Class | Reader |
| Contract version | 0.6.0 |
| Implemented | Archives, host sources, DSSE attestations, sidecars (lexical and dense), the ontology profile |
| Sigstore | Offline only, with the `OpenMoca.Reader.Sigstore` package (`ReadOptions.SigstoreVerifier = new SigstoreVerifier()`), against the trust root's `sigstore.trustedRoot`. Without the package, or without a trusted root, a `*.sigstore.json` attestation is unverifiable (`A003`). There is no online mode: refresh the trusted root yourself (moca-spec attestations §6) |

```csharp
using OpenMoca.Reader;

var result = await MocaReader.ReadPackageAsync(PackageTarget.FromPath("support-kb"));
Console.WriteLine($"{result.Digest} [{string.Join(", ", result.Capabilities)}]");

var library = new Library().Add(result);
foreach (var record in await library.SearchAsync("refund window"))
    Console.WriteLine($"{record.Node.Title} ({record.Node.Ref})");
```

A malformed package never throws: `result.Valid` is false and
`result.Diagnostics` says why. The Reader never reaches the network, or any
file outside the package, because a package names it.

`src/OpenMoca.Reader/Schemas/` holds byte-identical copies of moca-spec's
`schemas/v1`, checked by the tests.

## Framework bindings

Separate packages, each a thin adapter over the Reader
([ADR-0004](docs/adr/0004-framework-bindings-as-thin-adapters.md)):

| Package | What it gives you |
| --- | --- |
| `OpenMoca.Reader.Onnx` | `OnnxEmbedder` and `ModelStore`: MOCA's four standard embedding models run locally, as the Reader's embedder, matching indexes built with `moca-index --model` exactly |
| `OpenMoca.Reader.Extensions.AI` | `EmbeddingGeneratorEmbedder`: any Microsoft.Extensions.AI embedding generator as the Reader's embedder |
| `OpenMoca.Reader.AgentFramework` | `MocaContextProvider`: grounds a Microsoft Agent Framework agent in your packages each turn, and offers `moca_search`, `moca_get_node` and `moca_structure`, as moca-mcp names them |
| `OpenMoca.Reader.Sigstore` | `SigstoreVerifier`: verifies Sigstore-signed attestations offline against the host's trusted root: certificate chain, certificate-transparency timestamp, transparency-log entry and checkpoint, and the DSSE signature. No dependency beyond .NET |
| `OpenMoca.Reader.VectorData` | `VectorStoreBackend<TKey>`: a store backend over any Microsoft.Extensions.VectorData collection, with the Reader's scores and retrieval policy |

```csharp
using OpenMoca.Reader.Onnx;

// A standard model from the model store (~/.moca/models; `moca-mcp models add bge-small-en-v1.5` installs it).
// Each file is checked against its pinned SHA-256 before loading; nothing is downloaded here.
var store = new ModelStore(ModelStore.DefaultFolder);
using var embedder = store.Load(StandardModel.Find("bge-small-en-v1.5")!);
var hybrid = new HybridBackend(new LexicalBackend(library), new DenseBackend(library, embedder));
```

```csharp
using OpenMoca.Reader.Extensions.AI;

// e5 models expect a prefix for each role; the embedder applies it, the Reader never does.
var embedder = new EmbeddingGeneratorEmbedder(generator, name: "multilingual-e5-small", dimensions: 384,
    queryPrefix: "query: ", documentPrefix: "passage: ");
var dense = new DenseBackend(library, embedder);
```

```csharp
using OpenMoca.Reader.AgentFramework;

var search = new MocaSearch(library, new LexicalBackend(library), audiences: ["public"]);
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatOptions = new() { Instructions = "Answer from the packages and cite each ref." },
    AIContextProviders = [new MocaContextProvider(search)],
});
```

Each turn the provider searches on the user's message and adds the results as
one user-role message: compact citation records as JSON, with a note that they
are reference material, never instructions
([Reader contract §12](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#12-handing-content-to-a-model)).
The whole records are in the message's `AdditionalProperties["moca.citations"]`.
Every tool call goes through the same `MocaSearch`, so the model cannot widen
its audience filter.

```csharp
using OpenMoca.Reader.VectorData;

// Any connector: CommunityToolkit.VectorData.InMemory, PgVector, Qdrant, SqliteVec, ...
var collection = vectorStore.GetCollection<string, MocaStoreRecord<string>>(
    "moca", MocaStore.Definition<string>(embedder.Dimensions));
var store = await new VectorStoreBackend<string>(collection, embedder).IngestAsync(library);

// Search without loading the packages: keep each loaded package's state (PackageState.From(result), plain JSON)
// beside the store. Only those packages are returned, with supersession, contest, signatures and reviews
// recomputed from the states at query time (Reader contract §9.4).
var search = new MocaSearch(packageStates, () => DateTimeOffset.UtcNow, store, audiences: ["public"]);
```

The store never embeds: the backend embeds through your `IEmbedder`, by role,
and scores each hit itself, so every connector gives the same scores. Each
record holds the whole citation record (`moca_record`) beside the flat fields
it filters on, named as in the consuming guide (`moca_package_digest`,
`moca_node_id`, `moca_audience`, `moca_concepts`, ...). If a connector cannot
translate the filters, pass `filterPushdown: false`; the gate still applies
them. Keys are strings or GUIDs made from each unit, so ingesting a package
again replaces it, and `RemoveAsync(digest)` drops it.

## Develop

You need the .NET 10 SDK. The tests read moca-spec's examples and run its
conformance corpus. Check out
[moca-spec](https://github.com/openmoca/moca-spec) beside this repository, or
set `MOCA_SPEC_DIR`. CI uses the tags pinned at the top of `.github/workflows/test.yml`
(`MOCA_SPEC_REF` and the like); check out the same tags to match it.

```sh
dotnet build -c Release
dotnet test --project tests/OpenMoca.Reader.Tests -c Release
dotnet test --project tests/OpenMoca.Reader.Bindings.Tests -c Release
```

moca-spec's runner can also drive this Reader through the runner protocol,
from a moca-spec checkout:

```sh
node scripts/run-conformance.mjs --reader "dotnet run --project ../moca-reader-dotnet/tests/OpenMoca.Reader.ConformanceAdapter -c Release"
```

The corpus checks what a search returns, never its order. This Reader also
promises to rank exactly as the reference Reader does. The parity check runs
search scenarios over moca-spec's examples (lexical, dense, store, hybrid,
diversified; sidecars with and without text, zipped; locales and audiences)
through both Readers and fails on any difference in records, order, spans,
text or scores. Dense sidecars use a deterministic hashing embedder, so no
model is needed. With moca-reader-typescript and moca-tools (after `npm ci`)
beside moca-spec:

```sh
node tests/OpenMoca.Reader.Parity/parity.mjs
```

## Release

1. Add a dated section to [CHANGELOG.md](CHANGELOG.md) and update `<Version>`
   in `src/Directory.Build.props`, which every package shares.
2. Point the `MOCA_*_REF` pins in `.github/workflows/test.yml` at the release tags
   ([moca-spec docs/releasing.md](https://github.com/openmoca/moca-spec/blob/main/docs/releasing.md#moving-the-pins)).
3. The tests pass.
4. Tag `vX.Y.Z` and publish a GitHub release whose notes are the changelog
   section.
5. `dotnet pack -c Release` from the tagged commit, and publish the four
   packages to NuGet: `OpenMoca.Reader` first, then its bindings.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

OpenMOCA™ is a trade mark of PI & Mash Solutions Ltd. The licence covers the
code, not the name: see the
[trade mark policy](https://github.com/openmoca/moca-spec/blob/main/TRADEMARKS.md).
