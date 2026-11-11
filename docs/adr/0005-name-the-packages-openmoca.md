# 0005 — Name the packages and namespaces OpenMoca.Reader

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** MOCA maintainer

## Context

The packages were `Moca.Reader`, `Moca.Reader.Extensions.AI`, `Moca.Reader.AgentFramework` and `Moca.Reader.VectorData`.
NuGet lets an organisation reserve an ID prefix, so nobody else can publish a package that looks like ours. Another
publisher, mocanet, already uses IDs that start with `Moca.`, so that prefix cannot be reserved for us. Nothing has been
published yet: a rename costs nothing now, and is painful after the first release.

## Options

1. Keep `Moca.Reader.*`, without a reserved prefix.
2. Rename the package IDs only, to `OpenMoca.Reader.*`, and keep the `Moca.Reader` namespaces.
3. Rename the package IDs, the namespaces and the projects to `OpenMoca.Reader.*`.

## Decision

Option 3. The packages, namespaces, assemblies and project folders are `OpenMoca.Reader.*`, and the openmoca NuGet
organisation reserves the `OpenMoca.` prefix. The name matches the project's brand and its npm scope (`@openmoca`).
A user writes `dotnet add package OpenMoca.Reader` and `using OpenMoca.Reader;`: one name, not two.

## Consequences

- No other publisher can ship a package that starts with `OpenMoca.`, once the prefix is reserved.
- Every doc and example that named `Moca.Reader` changes with this record: moca-examples, openmoca.org and moca-spec.
- ADRs 0001–0004 keep the old names. They record what was true when they were written.

## What would change this decision

Nothing, once a version is published. Before that: a clash on the `OpenMoca.` prefix as well.
