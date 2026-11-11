# Architecture decision records

Each record states one decision, the options considered, and what would make
us revisit it. Records are numbered in order and never renumbered. A decision
that changes is superseded by a new record, not edited.

Decisions about the format and the Reader interface are in
[moca-spec](https://github.com/openmoca/moca-spec/blob/main/docs/adr/README.md).
These are the decisions about this Reader.

| ADR | Decision |
| --- | --- |
| [0001](0001-reader-library-with-no-host-dependencies.md) | A Reader library with no host dependencies, named as the spec names things, tested against the whole corpus |
| [0002](0002-pin-anglesharp-then-replace-dotnetrdf.md) | Superseded by 0003. Pin AngleSharp now; replace dotNetRdf with a small Turtle parser before the first NuGet release |
| [0003](0003-keep-dotnetrdf-and-take-its-fix-upstream.md) | Keep dotNetRdf; drop the AngleSharp pin when dotNetRdf 4.0 ships |
| [0004](0004-framework-bindings-as-thin-adapters.md) | Framework bindings as thin adapters, one package per framework |
| [0005](0005-name-the-packages-openmoca.md) | Name the packages, namespaces and projects OpenMoca.Reader; reserve the `OpenMoca.` NuGet prefix |

New records use the template in [0000-template.md](0000-template.md).
