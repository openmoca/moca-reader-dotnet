# moca-reader-dotnet

The .NET MOCA Reader, `OpenMoca.Reader` ([ADR-0001](docs/adr/0001-reader-library-with-no-host-dependencies.md)).

- **No references** to ASP.NET, hosting or anything app-specific. Only the packages listed under "The Reader" in `Directory.Packages.props`.
- **Bindings** (`src/OpenMoca.Reader.*`, [ADR-0004](docs/adr/0004-framework-bindings-as-thin-adapters.md)) are thin adapters: each takes `OpenMoca.Reader` and one framework's abstractions package. `OpenMoca.Reader` never references a binding or a framework.
- **The spec is the contract.** Normative sources in `../moca-spec/spec/`: `moca-package-spec.md` (format, digest, locales), `moca-reader-contract.md` (what a Reader must do, diagnostic codes, limits), `moca-reader-interface.md` (operation names), `moca-sidecar-index-spec.md`, `moca-attestations.md`. The reference Reader (TypeScript) is `../moca-reader-typescript/lib/`. When they disagree, the spec wins; raise the difference as an issue in moca-spec.
- **Never throw for a malformed package.** Report diagnostics (`code`, `severity`, `message`, `file`, `line`). Throw only for programmer errors (null arguments) and cancellation.
- **Never reach the network or the host filesystem outside the package** because a package names something.
- Name things as the spec does; comment each rule with its section (`// Contract §3.2`).
- Public API gets `///` doc comments (the project builds its XML docs, and warnings are errors).
