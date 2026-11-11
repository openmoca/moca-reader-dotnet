# 0004 — Framework bindings as thin adapters in their own packages

- **Status:** Accepted
- **Date:** 2026-10-30
- **Deciders:** MOCA maintainer

## Context

[ADR-0001](0001-reader-library-with-no-host-dependencies.md) keeps
`Moca.Reader` free of host dependencies and says framework bindings are
separate packages in this repository. The
[Reader interface §7](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-interface.md#7-bindings)
lists the .NET ones: a Microsoft Agent Framework context provider, and a
Microsoft.Extensions.VectorData store backend. A binding must carry the whole
citation record, and hand text to a model as
[Reader contract §12](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#12-handing-content-to-a-model)
says.

The Reader's extension points are interfaces (`ISearchBackend`, `IEmbedder`)
and delegates (`SearchHooks`). The frameworks' are mostly abstract classes:
MAF's `AIContextProvider` and VectorData's `VectorStoreCollection<TKey, TRecord>`.
Microsoft.Extensions.AI's `IEmbeddingGenerator` is an interface, with no idea
of a query or document role (Reader interface §5.3).

VectorData connectors can embed records themselves, and their scores differ
by connector: some return a similarity, some a distance. The connectors are
published by the .NET Community Toolkit (`CommunityToolkit.VectorData.*`);
Semantic Kernel's older copies lag the abstractions.

## Options

1. One `Moca.Reader.Extensions` package with every binding.
2. One package per framework, each a thin adapter over the Reader's own types.
3. Bindings in the frameworks' repositories, or in our products.

## Decision

Option 2. Three packages, each depending on `Moca.Reader` and one framework's
abstractions package, never the reverse:

| Package | Provides | Wraps |
| --- | --- | --- |
| `Moca.Reader.Extensions.AI` | `EmbeddingGeneratorEmbedder : IEmbedder` | any `IEmbeddingGenerator<string, Embedding<float>>` |
| `Moca.Reader.AgentFramework` | `MocaContextProvider : AIContextProvider`, and `search`, `get_node` and `structure` tools | a `MocaSearch`, and a `Library` for the tools |
| `Moca.Reader.VectorData` | `VectorStoreBackend : ISearchBackend` | any `VectorStoreCollection` of `MocaStoreRecord` |

Each adapter inherits the framework's base type or implements its interface,
and holds the Reader's interfaces inside. Nothing from a framework reaches
`Moca.Reader`. `MocaSearch` stays one sealed class: it runs the gate, and a
host changes its behaviour through backends and hooks, not by replacing it.

Rules the bindings keep:

- **The embedder applies its own role convention.** The M.E.AI adapter is
  told the model's name, version, dimensions and prefixes, and puts the prefix
  for each role before the text. The Reader still never adds text.
- **The store never embeds.** The store backend embeds through the host's
  `IEmbedder` and passes vectors to the collection, so the role is always
  right and the model is the one the host named.
- **Scores are the Reader's.** The store backend reads each hit's vector
  back and scores it by the index's distance, as `DenseBackend` does, so every
  connector gives `MemoryStoreBackend`'s scores. Equal scores are ordered by
  node ref, locale and span, the same for every connector. An approximate
  (ANN) index can still miss a hit that an exact search finds.
- **Dense only.** Connectors' own keyword-hybrid search does not score as
  [Reader contract §9.6](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#96-hybrid-backends)
  says. A host that wants hybrid search puts the store backend in a
  `HybridBackend`.
- **Content is data.** The context provider gives the model citation records
  as a quoted, delimited user-role message, with a note that they are
  reference material, never as instructions (contract §12).

## Consequences

- A host takes only the framework it uses.
- Any vector store with a VectorData connector works, with the same scores.
  Keys are strings or GUIDs made from each unit, so ingesting again replaces;
  other key types take a key function.
- Reading vectors back costs some bandwidth on each search; at most 200 hits
  are fetched.
- The framework packages move fast. Their versions are pinned in
  `Directory.Packages.props`, and Dependabot raises updates.

## What would change this decision

A framework that adds query and document roles, or a stable score contract,
to its abstractions. Then the matching rule above goes. A third framework
binding for .NET, such as Semantic Kernel, follows the same pattern.
