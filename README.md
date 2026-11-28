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
  returned; with one, the representation package spec §5.4 selects. A locale
  is a priority list of language tags (`Locale = ["fr-CA", "en"]`); one tag is
  a list of one. `library.Get(id, ["fr-CA", "en"])` and `library.Citations(...)`
  take the same list.

Characters added in very recent Unicode versions can tokenise differently
across Readers, because each language runtime ships its own Unicode data.

## Languages

A node's translations (`page.fr.md` beside `page.md`) are its representations,
and a locale chooses one of them (package spec §5.4). A composite package can
also group whole packages as editions of one work, one per language
(package spec §7.4).

```csharp
// Each node in French where there is a French file, else in English, else in its default language.
var records = await search.SearchAsync("bread", new SearchOptions { Locale = ["fr-CA", "en"] });

// A composite's slot: its editions, and the one a locale chooses.
IReadOnlyList<EditionItem> editions = library.Structure.Editions("https://example.com/course#bread");
string? chosen = library.Structure.ChooseEdition("https://example.com/course#bread", ["fr"]);
```

- A translation that does not write `moca.concepts`, `status`,
  `moca.valid_from`, `moca.valid_until` or `moca.audience` has its default
  file's, so it is found by topic and retired with the original.
- A search chooses only among the representations it may return, so a retired
  translation falls back as a missing one does.
- `LocaleMode.Only` leaves out records in no language of the list (a node
  with no French file drops out of a French search); `LocaleMode.First` puts
  records in the first language first, then the next, and leaves nothing out.
- With a locale, a search keeps one edition per slot
  (`Editions = EditionsMode.All` keeps every one), but only where the
  composite's signers speak for the edition's package, or the library allows
  unsigned relations. Package states carry the slots, so a store search does
  the same.

## Conformance

As [Reader contract §14](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#14-demonstrating-conformance)
asks:

| | |
| --- | --- |
| Class | Reader |
| Contract version | 0.7.0 |
| Implemented | Archives, host sources, DSSE attestations, sidecars (lexical and dense), the ontology profile, the creator's terms (§15) |
| Encryption | With the `OpenMoca.Reader.Age` package (`ReadOptions.EnvelopeDecryptor = AgeEnvelopeDecryptor.Instance`, plus a `KeyProvider`). Without it, an envelope is recognised and reported with `E001`, and nothing inside it is exposed. Decrypted bytes stay in memory |
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
`result.Diagnostics` says why.

The creator's terms (`terms` in the manifest) are exposed as written in
`result.Terms`, with their problems and who stated them; reading a package
never applies them. A host about to hand records to a model, or to show them
under a rating filter, asks the search to apply them:

```csharp
var records = await search.SearchAsync("refund window", new SearchOptions
{
    // A third party's model, which promises not to train on what it is sent.
    Terms = new TermsRequest { Ai = new AiRequest(TermsDestinations.Provider, Conditions: ["noTraining"]) },
});
```

Records the terms do not allow are left out and counted in
`search.Diagnostics` (`U004`), and each record carries its resolved terms.
Without a terms request, terms change nothing. The Reader never reaches the network, or any
file outside the package, because a package names it.

Everything you hand to a model goes through the same gate, not only search
results ([Reader contract §15.4](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#154-the-terms-gate)):

- `library.StructureFor(terms, termsLimits)` answers the structure operations
  over the packages that are not closed for the request, so a closed
  package's labels and definitions never reach the model.
- `library.Catalogue(new CatalogueOptions { Terms = terms, ... })` says what
  a model may be told about the packages. An open package gets its full
  entry. A closed one gets only its id, version, title, publisher, whether it
  is signed, and `ClosedBy`. A package whose creator set
  `ai.allowed.mention: false` is hidden when closed: it has no entry at all.
  Use the catalogue for any list of packages, and answer a lookup of a
  package that has no entry exactly as for one that is not loaded.
- Ingest takes the embedder's terms request (below).
- `U004` carries `Excluded` (removed per reason) and `Hidden` (how many came
  from hidden packages). Take `Hidden` out of any count you tell a model.

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
| `OpenMoca.Reader.Age` | `AgeEnvelopeDecryptor`: opens encrypted packages (moca-spec encrypted packages). Decrypt-only age v1 on BouncyCastle: X25519 and scrypt recipients, no plugins. Keys come from the host's `ReadOptions.KeyProvider`; `SidecarBinder.BindEnvelopeAsync` opens an envelope's `index.age` the same way |
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
its audience filter. The creator's terms apply to everything the provider and
the tools hand the model
([Reader contract §15.4](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#154-the-terms-gate)):
by default as a third party's model with no conditions. Set
`MocaContextOptions.Terms` (or pass a `TermsRequest` to `MocaTools`) when the
model runs on the person's device or in your own service. The request always
has an `ai` part: its destination (`device`, `host` or `provider`) is your
setting, and a request without one is refused (`ArgumentException`). The
`moca_structure` tool answers from `StructureFor`.

```csharp
using OpenMoca.Reader.VectorData;

// Any connector: CommunityToolkit.VectorData.InMemory, PgVector, Qdrant, SqliteVec, ...
var collection = vectorStore.GetCollection<string, MocaStoreRecord<string>>(
    "moca", MocaStore.Definition<string>(embedder.Dimensions));
// Embedding sends the text to a model: name where your embedder runs (here a third party's), and units the
// creator closed to it are never embedded or stored.
var store = await new VectorStoreBackend<string>(collection, embedder)
    .IngestAsync(library, new IngestOptions { Terms = new AiRequest(TermsDestinations.Provider) });

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

**Ingest applies the creator's terms when you give it a terms request.**
Embedding is a use of the text
([Reader contract §9.4, §15.4](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#94-store-backends)).
`VectorStoreBackend.IngestAsync` and `MemoryStoreBackend.IngestAsync` take
`IngestOptions`: the embedder's destination (`device`, `host` or `provider`),
the conditions it meets, and your terms limits. A unit the creator closed to
that destination is never sent to the embedder or written to your store, and
`IngestDiagnostics` reports `U004`. `VectorStoreBackend` also deletes any
record an earlier ingest stored for it, so if you ingested before 0.7.0,
ingest again with a request (or call `RemoveAsync` for the closed packages).
Without a request, ingest takes every unit, as before. `DenseBackend` embeds
only the person's query, never package text.

## Stores that keep packages broken apart

Three calls, new in 0.8.0, for a host whose store keeps packages as parts
(manifest, file digests, attestation files, records) rather than as packages.

**Trust from what the store keeps: `PackageTrust.Evaluate`.** Give it the
manifest's bytes, every file's SHA-256, the attestation files, a trust root
and a time. It runs the same checks as opening the package, in the same code,
and returns the same signers, keys, reviews, issued copy, relation scopes and
diagnostics (with the reason for any attestation that does not count), plus
the window the state holds for: `TrustedFrom` (the package's own `validFrom`)
and `TrustedUntil` (the earliest of the package's `validUntil` and the expiry
of each key behind a valid attestation). It needs no file contents. Use it
when your trust root changes, such as a key removed before its expiry, and to
store trust as dated fields you can filter on.

```csharp
var trust = PackageTrust.Evaluate(manifestBytes, fileDigests, attestationFiles, trustRoot, DateTimeOffset.UtcNow);
// trust.Signed, trust.Signers, trust.Keys (each with Expires), trust.Diagnostics, trust.TrustedUntil
var state = storedState.WithTrust(trust);   // the package state a store search uses, re-established
```

Inputs that do not describe one package (a manifest whose bytes do not match
its digest, a path in the wrong map) are your store's error and throw
`ArgumentException`; what the package itself holds never throws.

**Package states on demand: `IPackageStateSource`.** `new MocaSearch(states,
clock, backend)` with your own source asks only for the states a search needs:
the packages its candidates come from, the packages composing them, and, with
a terms request, the packages their records name. What a search needs about
the whole store (relation effects, who composes whom, the concepts on reading
orders) comes from a `StoreIndex` you build once with `StoreIndex.Build` and
rebuild when a state changes. Results are the same as with every state in
memory. Memory per search does not grow with the store: over synthetic stores
of 1,000 and 10,000 packages, a search allocates the same. Use it once your
store is too large to keep every state in memory; `MemoryPackageStates` is the
in-memory source, shared between searches. With a source of your own, call
`ApplyTermsAsync` rather than `ApplyTerms`.

**A candidate pool: `SearchOptions.Pool`.** From 200 to 2,000 records that pass
the gate, best first, for a layer of your own that fuses, groups or pages
them. Every record is still the gate's; the default (no pool) is unchanged.
Bound the asking again with `MaxCandidates`.

```csharp
var search = new MocaSearch(myStates, () => DateTimeOffset.UtcNow, store) { MaxCandidates = SearchOptions.MaxPool };
var pool = await search.SearchAsync(query, new SearchOptions { Pool = 200 });
```

**Traces.** The Reader starts OpenTelemetry spans through .NET's
`ActivitySource`, named `OpenMoca.Reader` (`ReaderTelemetry.SourceName`): add
it to your tracer. `moca.reader.package_trust` carries attestation and signer
counts; `moca.reader.package_states.load` carries the states asked for, loaded
and missing, and the store's size. Spans never carry package content.

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
4. Merge to main, tag `vX.Y.Z` on main and push the tag. The release workflow
   (`.github/workflows/release.yml`) checks that the tag matches the version,
   packs every package in `src/`, and publishes them to NuGet through trusted
   publishing: `OpenMoca.Reader` first, then the packages that depend on it.
   No NuGet API key is kept anywhere.
5. Publish a GitHub release whose notes are the changelog section.

## Standards

What this Reader uses, and how strongly. **Follow** means we implement it and
the tests check it where they can; **map to** means only an informative
mapping, in the specification.

| Standard | Where | How strongly |
| --- | --- | --- |
| [Open Knowledge Format](https://github.com/open-knowledge-format/open-knowledge-format/blob/ad30107c31c06aec8a7d5636e0d1058118604e6f/SPEC.md) v0.2, pinned at commit `ad30107` | `content/` is an OKF bundle | follow (pinned) |
| [JSON Schema 2020-12](https://json-schema.org/draft/2020-12) | the embedded schemas, byte-identical to moca-spec's | follow |
| [Ed25519](https://www.rfc-editor.org/rfc/rfc8032) (RFC 8032) and [in-toto Statement v1](https://github.com/in-toto/attestation) in DSSE envelopes | package and review attestations | follow |
| [Sigstore](https://www.sigstore.dev/) bundles | `OpenMoca.Reader.Sigstore`: offline only; issuer and subject compared exactly | follow |
| [age v1](https://age-encryption.org/v1) (X25519 and scrypt recipients) | `OpenMoca.Reader.Age`: encrypted packages, decrypt only | follow |
| [SKOS](https://www.w3.org/TR/skos-reference/) and [Dublin Core terms](https://www.dublincore.org/specifications/dublin-core/dcmi-terms/) in RDF 1.1 Turtle | `structure.ttl` | follow (the named terms) |
| [schema.org](https://schema.org/) `workExample` | edition groups in a composite's `structure.ttl` | follow (that term) |
| [WebVTT](https://www.w3.org/TR/webvtt1/) | transcripts that evidence cites | follow |
| [BCP 47](https://www.rfc-editor.org/info/bcp47) | `language`, `locales`, search locales | follow |
| [RFC 3339](https://www.rfc-editor.org/rfc/rfc3339) | dates, validity windows, `datePackaged` | follow |
| [SPDX licence expressions](https://spdx.org/licenses/) | `license`, and a node's licence in citation records | follow (exposed as written) |
| [MARC relator codes](https://id.loc.gov/vocabulary/relators.html) | credit `roles` | follow (exposed as written) |
| [schema.org](https://schema.org/) | `datePublished`, `contentRating`, `typicalAgeRange`, copyright | map to |
| [ONNX](https://onnx.ai/) via ONNX Runtime | `OpenMoca.Reader.Onnx`: the standard embedding models | follow |
| [OpenTelemetry](https://opentelemetry.io/docs/specs/otel/) traces, through .NET's `ActivitySource` | spans for the trust call and loading package states (`OpenMoca.Reader`); counts and durations, never content | follow |

Built with the help of AI assistants; every change is reviewed and accepted by the maintainer.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

OpenMOCA™ is a trade mark of PI & Mash Solutions Ltd. The licence covers the
code, not the name: see the
[trade mark policy](https://github.com/openmoca/moca-spec/blob/main/TRADEMARKS.md).
