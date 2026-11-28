# Changelog

All notable changes to OpenMoca.Reader and its bindings. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased: 0.8.0

For moca-spec 0.8.0: content in more than one language.

### Changed

- **The locale lookup** (package spec §5.4): the default representation is tagged with the manifest's `language`,
  and each truncation of the request also finds tags that start with it, so `pt-AO` finds `pt-BR` and `en` finds the
  default file before `en-GB`. A new public class, `LocaleLookup` (`List`, `Lookup`, `Matches`,
  `RepresentationTag`); `ContentNode` carries its default `Language`. Unit tests in `LocaleLookupTests`.
- **`locale` is a priority list** (breaking): `SearchOptions.Locale`, `SearchFilters.Locale`, `Library.Citations`,
  `Library.Get` and `StructureIndex.Nodes` take an `IReadOnlyList<string>` (`["fr-CA", "en"]`; one tag is a list of
  one) instead of a string. The pushdown filter carries the list.

## Unreleased: 0.8.0

Pre-release `0.8.0-preview.1`, from the `release-0.8.0` branch. Nothing here changes what a Reader must do (the Reader
contract): each item is an addition to this Reader's API, and every existing call returns what it returned in 0.7.0.

### Added

- **`PackageTrust.Evaluate`** and **`TrustState`**: a package's trust state from its manifest bytes, file digests and
  attestation files, under a trust root at a time (attestations §6–§7). The same code as opening the package, so the
  same signers, relation scopes, reviews, issued copy, attestation records and diagnostics, for every package in the
  conformance corpus (tested). With the keys behind each valid attestation (`TrustKey`, with `Expires`) and the window
  the state holds for: `TrustedFrom` (the package's `validFrom`) and `TrustedUntil` (the earliest of its `validUntil`,
  each such key's expiry, and the date of a review ignored as future-dated). `PackageState.WithTrust` puts it into a
  stored package state.
- **`IPackageStateSource`**, **`StoreIndex`** and **`MemoryPackageStates`** (Reader contract §9.4): a store search that
  loads package states on demand, only for its candidates' packages, their composers and, with a terms request, the
  packages their records name. The relation index, composers, bindings and reading-order concepts are built once into
  the `StoreIndex` and kept. `new MocaSearch(IPackageStateSource, ...)`, and `MocaSearch.ApplyTermsAsync`.
- **`SearchOptions.Pool`** (`MinPool` 200 to `MaxPool` 2,000): a candidate pool of gated records for a host's own
  layer, in place of `Limit`.
- **Traces**: `ReaderTelemetry.SourceName` (`OpenMoca.Reader`), with spans `moca.reader.package_trust` and
  `moca.reader.package_states.load` (counts and durations, never content).
- `AttestationRecord.KeyId`, `SignatureCheck.KeyId` and `TrustRoot.Expires(keyId)`.

### Changed

- **`PackageState.RelationIndexOf`** is linear in the packages and their relations (it compared every pair). Same
  effects, in the same order (tested against the old scan).
- **A store search over states in memory** (`new MocaSearch(IEnumerable<PackageState>, ...)`) now runs through
  `MemoryPackageStates`: a search no longer copies the allowlist or walks every state for composition and terms. Over
  10,000 synthetic packages, memory per search went from about 114 KB to 42 KB, the same as over 1,000; results are
  identical to 0.7.0 on the whole corpus. A host that makes a `MocaSearch` per request can share one
  `MemoryPackageStates` (or its own source), so the relation index is built once, not with every `MocaSearch`.
- `MocaSearch.ApplyTerms` throws `InvalidOperationException` for a search made with a host's own
  `IPackageStateSource` (use `ApplyTermsAsync`); with states in memory, or a library, it is unchanged.

## [0.7.0] - 2026-11-15

For moca-spec 0.7.0 ([moca-spec#5](https://github.com/openmoca/moca-spec/issues/5) and
[moca-spec#7](https://github.com/openmoca/moca-spec/issues/7) and
[moca-spec#8](https://github.com/openmoca/moca-spec/issues/8) and
[moca-spec#9](https://github.com/openmoca/moca-spec/issues/9)).

### Added (what the terms gate covers: moca-spec ADR-0041; Reader contract §9.4, §10, §15.3, §15.4)

Everything the Reader hands to a model goes through the creator's terms, not only search records.

- **`ai.allowed.mention`**: whether a model that may not receive the work may be told it exists. Platform default
  `true`; resolved like every `allowed.*` field (the stricter value applies whatever its authority); package level
  only (in a `nodes` entry it is `U003` and has no effect); a malformed value or container fails closed.
  `CitationAllowedTerms` gains `Mention` (a new positional parameter) and `TermsLimits` gains `Mention`.
- **Closed and hidden**: `TermsRules.ClosureOf` and `PackageClosure` (contract §15.4), from a package's package-level
  resolved terms for the request's destination and conditions. A use never closes a package. `Library.ClosureOf`
  answers it for a loaded package.
- **`Library.Catalogue(CatalogueOptions)`** and **`CatalogueEntry`** (`catalogue-entry.schema.json`): an open
  package's full entry; a closed one's exactly id, version, title, publisher, signed and `ClosedBy`; no entry for a
  hidden package, one outside the audience set or the default retrieval policy, or one whose own rating the rating
  gate removes. An open composite lists only members with their own entry.
- **`Library.StructureFor(terms, termsLimits)`**: the structure operations over the packages not closed for the
  request, and the overlays (contract §10).
- **Ingest with a terms request** (`IngestOptions`): `MemoryStoreBackend` and `VectorStoreBackend`
  (`OpenMoca.Reader.VectorData`) never embed or store a unit the gate removes for the embedder's destination, and
  report `U004` in `IngestDiagnostics`; `VectorStoreBackend` also deletes what an earlier ingest stored for it.
  `StoreUnit.From(library, options, out diagnostics)` for other store bindings. Without a request, nothing changes.
- **`U004` counts**: `Diagnostic.Excluded` (removed per reason) and `Diagnostic.Hidden` (how many came from packages
  hidden for the request). In a record from a search with a terms request, `supersededBy` and `contestedBy` name no
  hidden package.
- **Agent Framework**: `MocaTools` and `MocaContextProvider` always pass a terms request with an `ai` part (contract
  §15.4): the destination is the host's setting, default `provider`, and a request with no `ai` part is refused
  (`ArgumentException`); there is no way to drop it. `moca_structure` answers from `StructureFor`.
- Schemas re-vendored: `terms`, `citation-record` and the new `catalogue-entry`. The conformance adapter supports the
  `catalogue` case kind, `options.terms` on structure cases, `options.ingestTerms` and `termsExcluded`.

### Changed (limits, exact rules and signer binding)

For moca-spec's hardening pass before 0.7.0; each rule has corpus cases there and unit tests here.

- **`ids` bind a signer to package ids** (moca-spec ADR-0040). A trust-root key or Sigstore identity with `ids` signs
  only packages whose `id` its `ids` cover; otherwise `A010_SIGNER_NOT_FOR_PACKAGE` (error), the attestation is
  `invalid`, and the package is not `signed` and does not meet `RequireSignature`. An entry without `ids` signs any id.
- A signature counts only when its outcome is exactly `valid`; any other outcome from a verifier is `A002`.
- **New limits** (contract §3): `ReaderLimits.MaxAttestations` (256) and `MaxAttestationBytes` (1 MiB), `A001` past
  either; at most 16 signature entries in one DSSE envelope; and the members of every collection in a structure file,
  added together, counted against `MaxTriples` (`O001`), a shared list walked once.
- **Archives** (contract §3): one hand-written central-directory reader replaces `ZipArchive`. Exactly one end record,
  no multi-disk or Zip64 archive, a directory that holds what it declares, and local headers whose name and method
  match it, all checked before any entry is read (`T003`); declared sizes are summed before anything is inflated. An
  entry is read only with method 0 or 8 and no encryption flag, and must give exactly its declared length and CRC-32
  (`P002`). An archive too large to hold in memory is `T003`.
- **Nesting** (contract §3): JSON and YAML front matter nest at most 64 levels, the outermost object, array or mapping
  being level 1, fixed rather than left to a parser's default. Deeper text is refused as it is read, without
  recursion, with the code its kind of file has for text that does not parse: `M001` for `moca.json`, `C002` for a
  node's front matter (`C004` for a reserved file), `A001` for an attestation file or its statement, `E005` for
  `moca-enc.json`, `S001` for a sidecar's `index.json` and `S004` for a payload line. A `structure.ttl` is nested at
  most 64 levels too, each blank-node property list or collection being one level; deeper is `O001`, checked in one
  pass before the Turtle parser runs. The check counts the parser's own tokens, so it follows Turtle's rules for names
  (escaped characters included), IRIs, strings and comments.
- **A read never throws for what a package holds**: a JSON string with an unpaired surrogate escape is invalid JSON
  (I-JSON, RFC 7493): `M001`, `A001`, `E005` or `S001` by file; an unreadable envelope entry is `P002` and an unreadable
  sidecar entry `S001`; image paths are normalised safely. Anything else unexpected becomes one `P002` and an invalid
  result; only cancellation and programmer errors throw.
- **Text work is linear**: links, HTML tags and quotes in node bodies and evidence sources are scanned in one pass
  (with a Knuth-Morris-Pratt quote search), giving the same results as before; a link's target is copied only once
  the link is known to close. The tests count the scanners' steps and bytes, not time. A link target in angle brackets loses
  them, as in the reference Reader. The prefixes a `structure.ttl` declares are found by a one-pass line scanner,
  giving the same prefixes as before.
- **Directory sources** read regular files only: a named pipe, device or socket is `P001`, and an entry that reports
  no bytes is never opened.

### Added (the creator's terms: package spec §4.14, Reader contract §15)

- `PackageResult.Terms` (`PackageTerms`): the manifest's `terms` exactly as written (`Declared`), their problems
  (`U001_TERMS_INVALID`, `U003_TERMS_UNKNOWN_VALUE`, each with the field's path; warnings, never `M002`), and the
  `Authority`, `packager` or `unverified`, from the package's signatures only. `U002_TERMS_LOOSENED` for a node value
  looser than the package's. The embedded terms schema does the checking; an age range or copyright year range out of
  order, and two ratings under one scheme, are `U001` too.
- Permissions are booleans under `ai.allowed` (`device`, `host`, `provider`, `training`, `machineTranslation`); provider
  conditions are `noTraining` and `noRetention`. Every level only makes the platform defaults stricter: in 0.7.0 no
  package can loosen them, so `ai.allowed.training: true` has no effect.
- `SearchOptions.Terms` (`TermsRequest`: `AiRequest`, `RatingRequest`) applies the terms gate and gives each record its
  resolved `terms` (`CitationTerms`: each field's value and the level that decided it, `platform`, `package`, `node`,
  `composite` or `host`). `MocaSearch.TermsLimits` are the host's floors. Records removed are counted in
  `U004_TERMS_EXCLUDED` (info). Without a terms request nothing changes. Package states carry the terms, so store
  searches apply a request too.
- The gate fails closed (contract §15.3, §15.4): a container in `terms` present with the wrong type (`ai`,
  `ai.allowed`, `rights`, `rights.export`, `nodes`, a `nodes` entry) sets everything under it to its strictest; a
  `nodes` value or entry that does not say which nodes it means counts for every node; a record rated only under a
  scheme the host did not list is unrated; a malformed rating counts as explicit and outside every listed scheme; a
  rating request with a member this version does not define removes every record (`RatingRequest.Unhonourable`). An
  unknown destination is treated as `provider`.
- Composition (contract §15.3 step 3): a composing package's stricter `ai` and export terms apply to a member's
  records only when its signers' ids cover the member's id (as for `upTo`), or the host allows unsigned relations.
- `MocaSearch.ApplyTerms`: the same gate for records found outside a search, such as a node fetched by its id.
- OpenMoca.Reader.AgentFramework applies the creator's terms to everything it hands a model, on each turn and in
  every tool (contract §15.4): `MocaContextOptions.Terms` and `MocaTools(search, terms)`, by default
  `TermsRequest.Provider` (a third party's model, no conditions).
- The embedding backends (`DenseBackend`, OpenMoca.Reader.Extensions.AI, OpenMoca.Reader.VectorData) do not apply the
  terms yet; the README says so, and that a host embedding with a third party's model checks the terms first. The
  gate for embedding comes in 0.7.x.

### Added (rights and dates: package spec §4.12, §4.14; Reader contract §7, §15.3)

- `PackageResult.DatePackaged`, as written, when the manifest passes its schema; citation records carry
  `package.datePackaged`. Informative, and not a trusted time.
- Terms gain `rights.export.allowed`, `rights.copyright`, `rights.notice` and `rights` (`license`, `copyright`) in
  `terms.nodes` entries, checked with the rest of `terms`.
- Citation records carry `node.license` and `node.copyright`: the node's `terms.nodes` entry first, else the manifest's
  `license` and `terms.rights.copyright`; a value with a terms problem is skipped.
- Records from a search with a terms request carry `terms.rights.export`, resolved from the package and composing
  packages (platform default `true`; a malformed value counts as `false`). Export removes nothing from a search.
- Embedded manifest, terms, node, citation-record, envelope and trust-root schemas match moca-spec's release branch.

### Fixed

- Citation records' `trust.generated` keeps `model` (the model's name and version) when the node states it (contract §7).
- Search de-duplication keys on the node, its locale and the whole span (start and end), so two chunks of one node
  that share a start are both kept (contract §9.2).

### Added (credits: package spec §4.13)

- `PackageResult.Publisher` (`PackagePublisher`), `Url` and `Contributors` (`Contributor`), as written, when the
  package is valid; `CitationNode.Contributors` from a node's `moca.contributors`. A credit's image is checked as a
  manifest image (`M010`) and left out when it fails. Credits are labels, not proof. Embedded manifest, node and
  citation-record schemas updated; `publisher` as a string now fails the schema (`M002`).

### Changed (alternative labels and ingress narrowing: package spec §5.6, Reader contract §9.2, §9.4)

- `skos:altLabel` is read: concepts carry `altLabels` by language, in the order read, each once; the default
  ontology-guided ingress matches a preferred or an alternative label, as written.
- Concepts or a scope an ingress hook sets now narrow the search under composition and with backends that have no
  pushdown: resolved hits are narrowed after the backend, before the egress hook and the gate, on every round.
- A store search takes a structure source (`MocaSearch.StructureSource`): a library whose structure the hooks see and `scope` follows.

### Changed (filters come before the limit: Reader contract §9.2)

- `MocaSearch` returns the best `limit` records that pass the gate, however far down the unfiltered ranking they are.
  The fixed cap of 200 candidates is gone: it asks the backend again, doubling the request, until `limit` records pass
  or the backend returns fewer hits than asked. An egress hook runs again on each larger candidate list.
- New property `MocaSearch.MaxCandidates`: the host's bound on one call. A call it stops reports
  `S010_SEARCH_TRUNCATED` (warning) in `Diagnostics`. Null by default.
- `LexicalBackend` and `MemoryStoreBackend` score over every representation of the loaded packages, then apply the
  filters, so a pushed-down filter never changes a score. `SearchFilters.Accept` carries the search's own check for
  `upTo` and for concepts bound by composition; the lexical and dense backends apply it, so such a search usually
  needs one request.
- `HybridBackend` fuses each backend's whole ranking before filters, and keeps it for the rest of one search call;
  `DenseBackend` embeds a query once however often it is asked again.
- OpenMoca.Reader.VectorData: `VectorStoreBackend` asks the store for up to 10,000 results (was 200) and reports
  `S010_SEARCH_TRUNCATED` in its `Diagnostics` when a search needs more.

### Added (podcast and video series content: package spec §4.12, §5.6, §5.7, §7.1–§7.3; sidecar spec §5)

- Media descriptions (`moca.media`): validated (`C005`, `C006`), exposed as `node.media`, never fetched. A time range
  that cites hosted media is checked against its `transcript` and counts toward `self-contained-evidence`.
- Time ranges must also end within the media: at or before the latest cue end, or the declared duration when later.
- `datePublished` on the result and `package.datePublished` on records; the node's own `node.datePublished`.
- Binding by composition: a composing package's structure binds its resolved members' records for `concepts`,
  `scope`, `upTo` and the structure operation `nodes()`; `upTo` limits the members of a package whose reading
  order places it. Package states carry `composition`, so store searches agree. With bindings loaded, the `concepts` filter is applied in the gate only.
- `isPartOf` is a core relation type; `R005_RELATION_TYPE_UNKNOWN` (info) for other undefined types.
- An issued copy matches a member pin, and binds the publisher's sidecar, through its verified original digest. Items of
  changed files are replaced by whole-body chunks without vectors (`S009_SIDECAR_ITEMS_REPLACED`).
- Alternate media (package spec §5.7): `alternateOf` and `sync` in `moca.media`, validated (`C005` for an
  invalid alternate) and carried in `node.media`. A time range on an alternate with no transcript is checked through its
  reference's transcript. `Alternates.MapTime(record, from, to, t)` maps a moment between alternates.
- `maxMembers` defaults to 4,096 (was 256).
- A refined quote is located in linear time (one Knuth-Morris-Pratt pass, constant-time spans); `located` runs from
  the earliest start to the latest end of the touched cues; white space means the five ASCII characters.
- An open-ended range on an alternate runs to the end of its stretch, and a range must end within the alternate's own
  duration. A repeat of a member id (M005) is not resolved. `.inf`, `.nan` and numbers too large to be finite are
  neither numbers nor strings.
- A media time range may carry a `TextQuoteSelector` as its `refinedBy` (contract §7): it matches when the range does
  and the quote is in the cues the range covers, white space collapsed. `evidence[].located` gives the cue-precise
  span, the shortest of several occurrences, also through alternates.
- A composer's reading order limits a member only when the composer's signers' `ids` cover the member's `id`, or the
  host sets `allowUnsignedRelations` (contract §9).
- Contract §3: a member met more than once in one read is resolved and read once, and shared; it counts once.
  New limit `maxMemberBytes` (1 GiB) on the bytes of all the members one read reads. A reading order is walked once,
  and bindings by composition keep only direct bindings and the links above them, so both take linear work. Package
  states carry that form.
- `upTo` limits a member's records by their own concepts on every composer's reading order, bound by composition
  or not.
- A source whose `id` is not a string has no `id` in every lookup.
- Frontmatter whose `sources` has two entries with the same `id` is `C002_FRONTMATTER_INVALID` (package spec
  §5.1), so the node is not read. Before, this Reader used the first entry.
- Contract §3: the evidence budgets (`maxEvidenceChecksPerPackage`, `maxEvidenceBytesPerPackage`) cover the whole
  read, so a package and its members share them. Each transcript's cues are parsed once per package, not once per
  time range checked.
- An octal or hex integer beyond 64 bits is read as a number, or as `.inf` is when too large for a finite double
  (package spec §5.1); reading it never throws, and `0x8000000000000000` is positive. A number that is not finite
  may be a mapping key: its text is the key, and the node is read. An integer beyond 64 bits is rounded once to
  the nearest double. Schema errors name only the deepest failing places, and of a failing `oneOf` or `anyOf`
  only the branch that got furthest; a branch of one that passed is never reported.

### Changed (encrypted packages: hardening)

- Opening an envelope never throws to the caller: an unexpected failure is
  `E006` while parsing the header and `E002` after. Only the host's own
  cancellation escapes, decided by the host's token: an
  `OperationCanceledException` from a key provider or decryptor (its own
  timeout) is a failure like any other.
- `ReaderLimits.MaxScryptWorkFactor` must be from 1 to 22
  (`ArgumentOutOfRangeException` when the limits are made, before any read);
  `AgeDecryptor.ParseHeader`
  refuses any other value too, so the work-factor shift cannot overflow.
- `AgeDecryptor.FindFileKey` tries a key's kinds in order (identities,
  passphrase, file key) and verifies the header MAC for each; a null identity
  or a file key that is not 16 bytes is `E002`. `UnwrapFileKey` is gone.
- The plaintext length is computed from the payload length before any
  decryption (`AgeDecryptor.PlaintextLength`: `T003` over `MaxBytes`, `E003`
  when impossible), and the payload is decrypted straight into one array of
  that size. The header is parsed from its first 64 KiB + 16 bytes only.
- A key provider's error message never appears in a diagnostic; only its type.
- Key material and partial plaintext are zeroed after use.
- At most 256 recipient stanzas (`E006`).
- Zip64: the raw central-directory parse reads the Zip64 end record, and an
  envelope entry it cannot find is `E005`, never assumed to be stored.
- Tests: the hybrid CCTV vectors run parse-only; key results; limits; the
  header size; a Zip64 envelope; CI installs the Go age CLI and requires it on
  Linux.

### Added (attestations §3a and §7.1: issued copies)

- Issued-copy attestations (`AttestationFormats.PredicateIssuedCopy`) are
  verified first, with the trust-root role `issued-copy` (which needs `ids`)
  and the publisher's consent in `issuedCopies` (`IssuedCopies.Check`). The
  Reader rebuilds the original digest (`IssuedCopies.RebuiltDigest`), and
  package attestations are then checked against it. Every failure is `A002`.
- `PackageResult.IssuedCopy` (`OriginalDigest`, `Issuers`, `ChangedFiles`).
  Citation records carry `package.issuedCopy` and `node.issuedChange`;
  reviews of changed files are listed with `fileChanged: true` and never
  count, and such a copy is not `reviewed`.
- `Library.Add`: a second issued copy of the same original is
  `L002_ISSUED_COPY_DUPLICATE` (info), not `L001`.
- `PackageState.IssuedCopy`, so a store search recomputes it.
- `AttestationFormats.CheckSignature` returns the verifying key or Sigstore
  identity; `Dsse.Ed25519KeyBytes` compares keys as bytes. The manifest,
  trust-root and citation-record schemas are copied from moca-spec.

### Added (package spec §4.10, Reader contract §9)

- `PackageResult.ReadingOrder` when the manifest's `readingOrder` names an
  ordered collection with a member list in the package's own
  `structure.ttl`; otherwise `M014_READING_ORDER_UNRESOLVED` (warning) and
  it is ignored.
- `SearchOptions.UpTo`: in a package whose reading order has it as a member,
  records bound to a concept placed after it are left out. Records off the
  reading order, and other packages, are not limited. It runs in the gate
  and follows the caller only.
- `PackageState.ReadingOrder` (`Structure.ReadingOrder`), so a store search
  applies `UpTo` too.

### Added (package spec §4.9)

- `PackageResult.Apps`, as declared, when the manifest passes its schema.
  Advice only: the Reader never fetches, resolves or opens an app URI.
  `apps` may be an outline property of an envelope.

### Added (package spec §4.6–4.8)

- `PackageResult.Images` (`ManifestImage`), `Translations`
  (`ManifestTranslation`) and `Tags`, set when the package is valid. Each
  image is checked for presence, `ReaderLimits.MaxImageBytes` (default
  1 MiB) and the PNG, JPEG or WebP signature of its type, never decoded
  (`M010`). Translation keys must be declared locales (`M011`); repeated
  tags are dropped (`M012`); translated alt text for an unknown image is
  dropped (`M013`).
- Envelopes: the opt-in cover copy (`outline/cover.png`, `.jpg` or
  `.webp`), at most one and within `MaxImageBytes` (`E005`), checked
  against the inner manifest's first cover (`E004`). `translations` and
  `tags` may be outline properties.
- The manifest and moca-enc schemas are copied from moca-spec; the
  conformance adapter reports the three fields.

### Added (encrypted packages §2–§6)

- Envelopes: recognised by content in a Zip path or bytes (a `.moca.enc`
  path is read as a Zip by its signature); the entry and marker checks; the
  opening order of encrypted packages §5.2 through
  `ReadOptions.EnvelopeDecryptor` and `ReadOptions.KeyProvider`; the outline
  check; `PackageResult.Envelope`. `E001`–`E006` in `DiagnosticCodes`, and
  the `E` family makes a package invalid.
- `OpenMoca.Reader.Encryption`: `IEnvelopeDecryptor` and its types. The
  core has no implementation; `OpenMoca.Reader.Age` adds
  `AgeEnvelopeDecryptor`.
- `ReaderLimits.MaxScryptWorkFactor` (default 20).
- `SidecarBinder.BindEnvelopeAsync`: `index.age` through the same key
  provider; a failure makes the sidecar unusable and never changes validity.
- The moca-enc schema is embedded.

### Added (encrypted packages §4)

- `OpenMoca.Reader.Age`, a new package: decrypt-only age v1 on
  BouncyCastle (already a dependency), not the platform-dependent BCL
  `ChaCha20Poly1305`. X25519 and scrypt recipients, no plugins, no armor.
  Strict header parsing (64 KiB cap, canonical base64, scrypt alone,
  work-factor limit checked before scrypt runs, low-order shares refused),
  constant-time MAC compare, and no plaintext before the whole payload
  authenticates. `OpenMoca.Reader` never references it. Tested against
  every applicable C2SP age test vector and against the Go age CLI on
  moca-spec's envelope fixtures.

### Added (package spec §4.1)

- `PackageResult.Distribution`: the manifest's `distribution`, or `open`
  when it has none; null when the manifest fails its schema. A label only.

## [0.6.0] - 2026-11-11

### Fixed (Agent Framework binding, audience)

- `MocaTools` (`moca_get_node` and the structure tool's node lists) applied an
  older audience rule: unlabelled content always passed, and a malformed
  label was not checked. It now uses the core rule (Reader contract §8,
  moca-spec ADR-0016), honouring the search's `AllowUnlabelled`. Found while
  checking try.openmoca.org against 0.6.

### Added (OpenMoca.Reader.Sigstore)

- A new package, `OpenMoca.Reader.Sigstore`, verifies Sigstore bundles offline
  against the host's `sigstore.trustedRoot`: the transparency-log entry (signed
  timestamp, or checkpoint and inclusion proof) and its body, the certificate
  chain at the logged time, the embedded certificate-transparency timestamp,
  and the DSSE signature. It uses only .NET. The core Reader gains
  `ISigstoreVerifier` and `ReadOptions.SigstoreVerifier`; without one, Sigstore
  attestations stay A003. The corpus's `sigstore-*` cases pass.

### Fixed (attestations §5)

- Strict Ed25519 checks before verifying: A and R are canonical points of
  prime order, S < L. Before, BouncyCastle's cofactored check accepted
  signatures that the other Readers refused.

### Fixed (frontmatter line ends)

- Only LF or CRLF ends a line in the frontmatter block, in every Reader. The
  reference Reader also ended lines at a lone CR, U+2028 and U+2029.

### Fixed (edge cases)

- Regular expressions, trimming and name comparisons follow package spec §2:
  ASCII digits, ECMAScript white space, an absolute end of string, and ASCII
  case-insensitivity for reserved names and `.md`. Before, a dotless ı made a
  file `index.md`, and a trailing newline passed several checks.
- M007 knows every IANA formal URN namespace and informal ones.
- A leading byte order mark is not part of an evidence source.

### Changed (conformance adapter, bindings tests)

- The adapter passes the corpus option `strict`. A bindings test checks that
  package text written to close the JSON envelope stays inside it (Reader
  contract §12).

### Added (versioning, package spec §14)

- `M009_MOCA_VERSION_NEWER` (warning) for a package written for a newer
  `mocaVersion` than `MocaReader.MocaVersion` (`0.6`). Versions compare numerically, without
  overflow. The `mocaVersion` check takes ASCII digits only and never throws on an over-long number. The vendored schemas have portable patterns.

### Added (composition limits, package spec §7.1.1)

- Member resolution is bounded: `ReaderLimits.MaxMemberDepth` and `ReaderLimits.MaxMembers` (defaults 8 and 256, across every
  depth of one read). A member past either limit is not resolved, and
  `R001_MEMBER_UNRESOLVED` says why.

### Removed (structure.json, moca-spec ADR-0019)

- The Reader no longer reads or checks `structure.json`, and
  `O004_STRUCTURE_VIEW_MISMATCH` is retired. A package that ships the file
  stays valid and gets `structured` from `structure.ttl` alone. `StructureGraph.View()` stays; `PackageStructure.ViewFile` is removed.

### Changed (sidecars and search)

- A sidecar item's `text` must equal the package's bytes at its span, which
  must lie in the body (`S004`); lexical search reads the package's bytes, never
  the sidecar's text, so a sidecar cannot stuff keywords into a node.
- A prefix declared by only the index or only the embedder is `S006`.
- The gate keeps one result per node representation and span: the English and
  French pages of one node are no longer merged.

### Changed (determinism)

- `moca.json` and attestation statements that repeat a member name are `M001`
  and `A001` (they were read with the last value).
- Locale fallback: exact, then the longest tag the request starts with, then the
  lowest tag starting with the request, all case-insensitive.
- Evidence: Markdown and HTML quotes are normalised like their sources, and a
  `TextPositionSelector` on its own is no longer checked (`matched` absent).

### Changed (limits and archives, review B9 and M6)

- Evidence work is bounded per package (`maxEvidenceChecksPerPackage`, 10,000;
  `maxEvidenceBytesPerPackage`, 256 MiB scanned), each source is normalised once,
  and a quote is matched in one pass. A package of 200 nodes with 256 quotes each
  against an 8 MiB source now reads in well under a second.
- Directories have the entry and byte limits, and a file replaced by a link
  after listing is unreadable.
- Archives: names must be UTF-8 (flag set) with no NUL, empty or `.` segments;
  duplicate entries are `P003`; only stored and deflated entries are read
  (others `P002`), and an unreadable entry stops the read.

### Changed (identity and tampering, review M2 and M3)

- A library refuses a second package with the same `id@version` and a different
  digest, and reports `L001_IDENTITY_CONFLICT` (it was dropped silently).
- A package attestation naming this `id@version` with another digest is
  `A008_PACKAGE_DIGEST_MISMATCH`, not `A002`.
- A resolved member whose `id` or `version` differs from its pin is `R002`.
- `ReadOptions.RequireSignature` makes a package without a valid package attestation invalid
  (`A009_SIGNATURE_REQUIRED`).

### Changed (store search, review B2)

- A store search takes the loaded packages' states (`PackageState.From(result)`; `new MocaSearch(states, clock, …)`) instead of bare
  digests, and recomputes `superseded`, `contested`, `signed`, `signers`,
  `attestedReviews` and `reviewStatus` at query time (contract §9.4). A package
  superseded after ingest, or a key removed from the trust root, is reflected
  at once.

### Changed (reviews, moca-spec ADR-0017)

- `reviewed` needs a signed package with every reviewed file approved.
  Citation records gain `trust.reviewStatus` (`approved`, `disputed`,
  `unverified`, `none`) and each review's `packageVerified`. The latest review
  from each signer counts; a review dated after the clock is ignored
  (`A007_REVIEW_DATE_FUTURE`); a trust-root entry's `reviewers` list limits whom
  its key may sign as.

### Changed (audience, moca-spec ADR-0016)

- Under a host audience set, content with no audience label is left out unless
  the host allows it (`new MocaSearch(...) { AllowUnlabelled = true }`, off by default), and a malformed label never
  matches; citation records mark it `audienceInvalid`. A translation with no
  label has its default representation's. The gate, the backends and the store
  backend apply the same rule.

### Changed (relations, moca-spec ADR-0015)

- `supersedes` and `conflictsWith` take effect only when the declaring package
  has a valid package attestation from a key or Sigstore identity whose new
  trust-root `ids` cover the target's id (package spec §7.2). Otherwise the
  Reader reports `R004_RELATION_UNAUTHORISED` (info) and the relation has no
  effect. `LibraryOptions.AllowUnsignedRelations` (off by default) applies unsigned relations, for a library
  of the host's own packages.

### Changed

Dates follow package spec §4.4:

- A full date means 00:00Z; a date-time needs an offset, and one without is
  ignored (it already has a diagnostic) rather than read as UTC.
- A node's validity window is the intersection with its package's: a node can
  no longer stay in force after the package's `validUntil`. An inverted window
  is ignored.
- A trust-root key is not trusted from the instant of `expires`, checked
  against the host clock, which `ReadOptions.Clock` now takes (already present; now used for key expiry at the instant). A key whose `expires`
  does not parse is not trusted.

Attestation verification follows the numbered algorithm now in attestations §7
(0.6.0):

- DSSE payloads and signatures are decoded strictly: base64 or base64url, with
  or without padding. Anything else makes the payload malformed (`A001`) or the
  signature not verify. .NET used to refuse base64url, which DSSE allows.
- A trust root is refused if it repeats a `keyid` or has a key or identity
  without `roles`. An empty `roles` list trusts the entry for nothing.

### Fixed

Fixes from the 10 November 2026 specification review, each with a new case
in the moca-spec 0.6.0 conformance corpus:

- A malformed percent-encoding in a source `resource` or a link is reported as
  `C008_UNSAFE_PATH` instead of being left undecoded.
- An absolute reference (a leading `/` or `\`, or a drive prefix such as `C:/`)
  is `C008_UNSAFE_PATH`, as the Reader contract says. It was resolved under
  `content/`.
- Structure traversal is iterative: a 30,000-concept `requires` chain no longer
  risks the call stack.
- A validity bound or `stale_after` that does not parse as a date is treated as
  absent; it no longer takes a node out of force.
- A `generated` or `verified` entry with no `by` is dropped; it was reported
  with the actor `"undefined"`.
- `log.md` and `index.md` frontmatter gets the frontmatter size limit, and a
  level-two heading (`##`) inside a fenced code block in `log.md` is no longer a C004 error.
- `moca.json` that is not valid UTF-8 is `M001`; it was read with replacement
  characters.
- Only entries under `attestations/` are left out of the digest; a top-level
  file named `attestations` is digested.
- Member cycles are detected by `id@version`, so a package can compose another
  version of itself.
- A host source's byte limit is also enforced on the bytes it returns, and a
  limit broken while reading is one `T003`, not a `P002` per file. An existing
  file that is not a directory or a `.moca`/`.zip` archive is `T002`.

## [0.5.0] - Not published

### Added
- **`OpenMoca.Reader.Onnx`**, a binding for MOCA's standard embedding models (`bge-small-en-v1.5`,
  `nomic-embed-text-v1.5`, `all-MiniLM-L6-v2`, `multilingual-e5-small`): `StandardModel` (pinned by Hugging Face commit
  and SHA-256, with each model's prefixes, pooling and length), `ModelStore` (checks files before loading; `AddAsync`
  downloads only when the host asks) and `OnnxEmbedder` (ONNX Runtime; WordPiece written to match Hugging Face; one
  text per run). Its vectors match `moca-index --model` and the MCP server's exactly.

### Performance
- **Lexical search keeps each document's term counts** after its first search, per package digest, and drops them
  when the package leaves the library. Later searches only score: at 5,000 pages, about 9 ms a search instead of
  about 400 ms, for about 30 KB of memory a page instead of 17 KB. Scores and order are unchanged (the corpus and
  the parity check with the reference Reader pass).

### Changed (breaking, before the first publish)
- **Package IDs and namespaces are `OpenMoca.Reader.*`**: `OpenMoca.Reader`, `OpenMoca.Reader.Extensions.AI`,
  `OpenMoca.Reader.AgentFramework` and `OpenMoca.Reader.VectorData`. The `Moca.` NuGet prefix is taken (mocanet), so
  the `OpenMoca.` prefix is reserved for the openmoca organisation instead. Nothing was published under the old names.
- **DSSE attestations verify with Ed25519 keys only**, as attestations §5 now requires. A trusted ECDSA or RSA key
  verifies nothing, so its attestation is invalid (`A002`); before, this Reader accepted them and the Python Reader did
  not.

## [0.4.0-alpha.2] - 2026-10-30

The first release. It implements moca-spec 0.4 and passes all 92 cases of the
0.4.0-alpha.2 conformance corpus, both as xUnit theories and through the
runner protocol.

### Added

- `Moca.Reader.Extensions.AI`: `EmbeddingGeneratorEmbedder` uses any
  Microsoft.Extensions.AI embedding generator as a Reader embedder. It puts
  the declared query or document prefix before each text, by role, and takes
  the model name and dimensions from the generator when it reports them
  (ADR-0004).
- `Moca.Reader.AgentFramework`: `MocaContextProvider`, a Microsoft Agent
  Framework context provider over a `MocaSearch`. Each turn it adds the
  results as data in one user-role message, with the whole citation records
  in its additional properties (Reader contract §12). `MocaTools` offers
  `moca_search`, `moca_get_node` and `moca_structure`, as moca-mcp names and
  shapes them, under the host's audience filter. `MocaSearch` now exposes its
  `Library` and `Audiences`.
- `Moca.Reader.VectorData`: `VectorStoreBackend<TKey>`, a store backend over
  any Microsoft.Extensions.VectorData collection of `MocaStoreRecord<TKey>`
  (`MocaStore.Definition` creates it). It embeds through the host's embedder,
  never the store's; scores each hit as `MemoryStoreBackend` does; pushes the
  digest, exclusion, audience and concept filters down; and orders equal
  scores by node ref, locale and span. `RemoveAsync(digest)` drops a package.
- `StoreUnit.From(library)`: the units a store backend records at ingest,
  shared by `MemoryStoreBackend` and the VectorData backend.
- `HybridBackend`: a lexical and a dense backend merged by Reciprocal Rank
  Fusion (moca-spec#15). k (default 60) and per-backend weights (default 1)
  are the host's. Packages the dense side cannot search (`S006`, `S007`)
  come back through the lexical side.
- Embedding roles (moca-spec#12): `IEmbedder.EmbedAsync` takes an
  `EmbeddingRole`, `Query` for a search and `Document` when the in-memory
  store ingests (a breaking change for implementers). An embedder may declare
  `QueryPrefix` and `DocumentPrefix`; they are compared with an index's like
  `version` (`S006` on a difference), never applied.
- Dense search scores by the index's `model.distance`: cosine (the default),
  `dot` or `euclidean` (negative distance, so higher is better); it was
  always cosine (moca-spec#10). `MemoryStoreBackend` takes a `distance`
  parameter; `DenseBackend.Similarity` is public.
- `Diversify.Hooks(perNode)`: an opt-in egress strategy that brings each
  node's best results forward, so one node's chunks don't fill the list. With
  any egress hook, `MocaSearch` now fetches more than the limit from every
  backend, so the hook has candidates to choose from.
- `S007_NO_VECTORS`: `DenseBackend` reports each loaded package it has no
  vectors for, which it does not search. Such packages were skipped silently
  (Reader contract §9.3).

### Fixed

- Path collisions (`P003`) lower-case paths with the simple mapping per code
  point, as package spec §6.3 now says: `İ.md` and `i.md` collide. Invariant
  lower-casing left İ as it was.
- A package path holding U+FFFE no longer makes `ReadPackageAsync` throw.
  `string.Normalize` refuses U+FFFE and lone surrogates; the Reader now
  normalises around them and keeps them, as the reference Reader does, and
  computes the same digest (Reader contract §5: never throw for a package).
- Lexical search tokenises text in any script: NFKC, combining marks kept
  inside words, and Chinese, Japanese, Thai, Lao, Khmer and Myanmar split into
  characters and character pairs. Without a locale it searches every language
  of a node, not only the default. The rules are in the README; the
  `token-vectors.json` test strings are shared with the other Reader.
- Sidecar items without `text` (it is optional) are searched by the package
  text their offsets point at (`SidecarBinder.ChunkText`). The lexical and
  in-memory store backends used an empty string, so only node titles matched
  (Reader contract §9.1).

### Security

- References AngleSharp directly (1.8.6), so hosts no longer restore the
  1.4.0 that dotNetRdf.Core depends on (GHSA-pgww-w46g-26qg). The Reader
  never parses HTML. The reference goes when dotNetRdf.Core 4.0, which drops
  AngleSharp, is released. See [ADR-0003](docs/adr/0003-keep-dotnetrdf-and-take-its-fix-upstream.md).
