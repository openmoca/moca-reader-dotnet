# 0003 — Keep dotNetRdf; drop the AngleSharp pin when dotNetRdf 4.0 ships

- **Status:** Accepted
- **Date:** 2026-10-29
- **Deciders:** MOCA maintainer
- **Supersedes:** [0002](0002-pin-anglesharp-then-replace-dotnetrdf.md)

## Context

[ADR-0002](0002-pin-anglesharp-then-replace-dotnetrdf.md) pinned AngleSharp
through a direct reference, and planned to replace dotNetRdf with a Turtle
parser of our own before the first NuGet release. The reason was
[GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg) in
the AngleSharp 1.4.0 that dotNetRdf.Core 3.5.2 depends on, and the size of
dotNetRdf's dependency tree.

Building and maintaining a Turtle parser is not what this project is for. A
parser is security-sensitive code that reads untrusted input, and dotNetRdf is
a maintained library with its own tests and releases.

Upstream has already fixed the advisory:

- [dotnetrdf#888](https://github.com/dotnetrdf/dotnetrdf/issues/888) reported
  it, and [dotnetrdf#892](https://github.com/dotnetrdf/dotnetrdf/pull/892)
  (merged 2026-08-16) removed dotNetRdf.Core's dependency on AngleSharp.
- The fix is on dotNetRdf's main branch for 4.0.0, which is not released yet.
  3.5.2 (2026-07-03) is the latest release.

4.0.0 is a major version. Its breaking changes are in JSON-LD and in APIs
already marked obsolete; the Reader uses only `TurtleParser` and a
`BaseRdfHandler` subclass, in `src/Moca.Reader/Structure/StructureParser.cs`.
[dotnetrdf#628](https://github.com/dotnetrdf/dotnetrdf/issues/628) proposes
changing `HandleQuad(Triple, IRefNode)`, which that handler overrides; if it
lands in 4.0, the change is small.

## Options

1. Replace dotNetRdf with our own Turtle parser (ADR-0002).
2. Keep dotNetRdf. Keep the direct AngleSharp reference until a dotNetRdf
   release without AngleSharp ships, then upgrade and remove it.
3. Keep dotNetRdf and switch to another RDF library with a smaller
   dependency tree.

## Decision

Option 2.

- **Now.** `Moca.Reader` keeps its direct reference to AngleSharp, at the
  version pinned in `Directory.Packages.props`, so every host gets the patched
  version. NuGet audit and Dependabot keep watching, as in ADR-0002.
- **When dotNetRdf 4.0 ships.** Upgrade dotNetRdf.Core, remove the AngleSharp
  reference and its `PackageVersion`, and adapt `StructureParser` to any API
  change. The whole conformance corpus and the structure tests must pass
  unchanged.
- **The first NuGet release no longer waits** for this. It can ship on 3.5.2
  with the pin.

## Consequences

- No parser to own. Turtle parsing stays with a maintained library.
- Until 4.0, hosts restore a package the Reader does not call (AngleSharp, at
  a patched version). The comment in `Moca.Reader.csproj` points here.
- dotNetRdf.Core's other transitive packages stay. 4.0 drops Newtonsoft.Json
  for System.Text.Json; the rest are accepted for now.
- The timing depends on dotNetRdf's release, which has no date.

## What would change this decision

- A new advisory in dotNetRdf.Core or a dependency that upstream does not fix.
- dotNetRdf no longer maintained.
- A host for whom dotNetRdf's dependency tree is a real blocker (for example
  on mobile or edge devices). Option 3 comes first then, before a parser of
  our own.
