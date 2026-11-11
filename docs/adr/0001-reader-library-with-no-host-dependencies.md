# 0001 — A Reader library with no host dependencies, named as the spec names things, tested against the whole corpus

- **Status:** Accepted
- **Date:** 2026-10-28
- **Deciders:** MOCA maintainer

## Context

moca-spec defines a language-neutral Reader interface and one Reader
repository per language
([moca-spec ADR-0006](https://github.com/openmoca/moca-spec/blob/main/docs/adr/0006-reader-interface-and-per-language-readers.md)).
This Reader was first built inside a .NET host application, so interface
problems showed up early. To be useful to other hosts it has to load
in-process anywhere: a web API, a desktop or mobile app, a command-line tool
or an agent framework.

## Options

1. A Reader that also carries host concerns, such as web endpoints, an MCP
   server or file listing for a user interface.
2. A plain library with no host dependencies. Hosts add their own concerns on
   top.

## Decision

Option 2.

- **No host dependencies.** `Moca.Reader` references no ASP.NET, hosting or
  app packages: only YAML, JSON Schema, RDF and Ed25519 libraries
  (`Directory.Packages.props`). Host features, such as a package file browser
  or an MCP server, live in the host.
- **The spec's names.** The public API follows the
  [Reader interface](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-interface.md):
  `ReadPackageAsync`, `Library`, `Get`, `SearchAsync`, the structure
  operations, citation records and diagnostics. The namespace and package id
  are `Moca.Reader`.
- **The whole corpus, two ways.** `tests/Moca.Reader.Tests` runs moca-spec's
  `conformance/cases.json` as xUnit theories, and
  `tests/Moca.Reader.ConformanceAdapter` lets moca-spec's own runner drive the
  Reader through the runner protocol. A case this Reader does not pass is
  listed, with a reason, in `Conformance/expected-failures.json`. The list is
  empty.

## Consequences

- Any .NET host can read, verify and search packages without taking a web
  framework with it.
- Framework bindings (a Microsoft Agent Framework context provider, a
  Microsoft.Extensions.VectorData store backend) are separate packages in this
  repository that depend on `Moca.Reader`, never the reverse.
- The tests need a moca-spec checkout beside this repository, or
  `MOCA_SPEC_DIR`.

## What would change this decision

A host that needs a Reader feature the interface does not define. That
becomes a Spec Change Proposal in moca-spec, not a host dependency here.
