# 0002 — Pin AngleSharp now; replace dotNetRdf with a small Turtle parser before the first NuGet release

- **Status:** Superseded by [0003](0003-keep-dotnetrdf-and-take-its-fix-upstream.md)
- **Date:** 2026-10-28
- **Deciders:** MOCA maintainer

## Context

The Reader parses `structure.ttl` with dotNetRdf.Core. That is its only use of
dotNetRdf: one file, `src/Moca.Reader/Structure/StructureParser.cs`, which
reads Turtle into triples.

dotNetRdf.Core 3.5.2, the latest release, depends on AngleSharp 1.4.0.
AngleSharp below 1.5.0 has a moderate advisory,
[GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg)
(mXSS through an HTML parsing bypass). The Reader never parses HTML, so the
flaw is not reachable through it. But every host that takes the Reader also
takes the vulnerable package, and its own audit flags it.

dotNetRdf.Core also brings 17 other transitive packages, including
HtmlAgilityPack, Newtonsoft.Json and System.Configuration.ConfigurationManager.
The Reader is meant to load in-process anywhere, including mobile and edge
devices
([moca-spec ADR-0006](https://github.com/openmoca/moca-spec/blob/main/docs/adr/0006-reader-interface-and-per-language-readers.md)),
so that footprint has a cost of its own.

`structure.ttl` is a constrained file: absolute IRIs, no `owl:imports`,
nothing fetched, and a fixed set of SKOS, Dublin Core and OWL terms
([moca-spec ADR-0007](https://github.com/openmoca/moca-spec/blob/main/docs/adr/0007-structure-core.md)).

## Options

1. Pin AngleSharp only, inside this repository's build.
2. Reference AngleSharp directly from `Moca.Reader`, so the patched version
   reaches every host.
3. Replace dotNetRdf with a small Turtle parser in the Reader.

## Decision

Option 2 now, and option 3 before the first NuGet release.

- **Now.** `Moca.Reader` references AngleSharp directly, at the version
  pinned in `Directory.Packages.props` (1.8.1). A pin through central
  transitive pinning alone held only inside this repository's own build: a
  host using the Reader still restored AngleSharp 1.4.0.
- **Watching.** NuGet audit runs on every restore, and warnings are errors, so
  CI fails on any known vulnerability in the dependency tree. Dependabot
  raises a pull request for each new advisory.
- **Before the first NuGet release.** Replace dotNetRdf with a Turtle parser
  in the Reader, covering what `structure.ttl` can contain. The whole
  conformance corpus and the structure tests must pass unchanged. That removes
  AngleSharp and dotNetRdf's other dependencies from every host.

**Upstream.** If a dotNetRdf.Core release depends on AngleSharp 1.5.0 or later
before the parser is replaced, the direct reference can be removed.

## Consequences

- Hosts get the patched AngleSharp today, and lose it, with dotNetRdf's other
  dependencies, once the parser lands.
- The Reader will own a parser. The corpus and the structure tests are its
  safeguard, and the TypeScript reference Reader (which uses N3.js) gives a
  second implementation to compare against.
- The direct AngleSharp reference is a package the Reader does not call. Its
  comment in `Moca.Reader.csproj` points here.

## What would change this decision

- A Turtle feature that real packages need and a small parser cannot handle
  safely. The Reader would then keep a full RDF library, chosen for a small
  dependency tree.
- dotNetRdf splitting its Turtle parser into a package without the HTML
  dependencies.
