# Changelog

All notable changes to OpenMoca.Reader and its bindings. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).


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
