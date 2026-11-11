# Getting started with the .NET Reader

Install the .NET MOCA Reader, open an example package, search it and cite what
you find, then run the conformance corpus. You need the .NET 10 SDK, and
Node.js 22 or later for the corpus runner.

## 1. Install the Reader

```sh
dotnet new console -o moca-start
cd moca-start
dotnet add package OpenMoca.Reader
```

## 2. Get an example package

`support-kb` is a small customer-support knowledge base: a refund policy in
English and French, a shipping guide, and an internal account-deletion
procedure. It is signed, one node has a signed review, and the refund policy
quotes the customer terms it came from, which travel inside the package.

```sh
curl -fsSLO https://github.com/openmoca/moca-spec/releases/download/v0.6.0/support-kb.moca
curl -fsSLO https://github.com/openmoca/moca-spec/releases/download/v0.6.0/example-trust-root.json
```

The trust root says whose signatures you accept, and for what. This one trusts
the example keys, which are published on purpose: use it for the examples
only. To make your own, see
[your trust root](https://github.com/openmoca/moca-spec/blob/main/docs/guides/consuming.md#your-trust-root).

## 3. Open it

Replace `Program.cs`:

```csharp title="Program.cs"
using OpenMoca.Reader;
using OpenMoca.Reader.Attestations;

var pkg = await MocaReader.ReadPackageAsync("support-kb.moca", new ReadOptions
{
    TrustRoot = TrustRoot.LoadFile("example-trust-root.json"),
});
if (!pkg.Valid)
{
    foreach (var d in pkg.Diagnostics) Console.Error.WriteLine($"{d.Code} {d.Message}");
    return 1;
}

Console.WriteLine(pkg.Digest);
Console.WriteLine(string.Join(", ", pkg.Capabilities));
return 0;
```

```sh
dotnet run
```

```text
sha256:4992176b90ea0992037617ee56d5dcafecf60aeb400cfa9369c9164453df96da
core, localized, located-evidence, reviewed, self-contained-evidence, signed
```

The digest covers every file in the package: change one byte and it changes.
`signed` and `reviewed` appear because the signatures verify against your trust
root; without one, they are reported as unverifiable. `ReadPackageAsync` never
throws for a bad package: problems come back as diagnostics with stable codes.

## 4. Search and cite

Replace `Program.cs` again:

```csharp title="Program.cs"
using OpenMoca.Reader;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Search;

var pkg = await MocaReader.ReadPackageAsync("support-kb.moca", new ReadOptions
{
    TrustRoot = TrustRoot.LoadFile("example-trust-root.json"),
});
var library = new Library().Add(pkg);
var search = new MocaSearch(library, new LexicalBackend(library), audiences: ["public"]);

var hits = await search.SearchAsync("How long is the refund window?", new SearchOptions { Limit = 1 });
if (hits.Count == 0) throw new InvalidOperationException("no result");
var hit = hits[0];

Console.WriteLine(hit.Text?.Trim());
Console.WriteLine();
Console.WriteLine($"Source:   {hit.Node.Title} ({hit.Node.Ref})");
Console.WriteLine($"Package:  {hit.Package.Id} {hit.Package.Version}, signed: {hit.Package.Signed}");
Console.WriteLine($"Digest:   {hit.Package.Digest}");
var reviewers = string.Join(", ", hit.Trust.AttestedReviews.Select(r => r.Reviewer));
Console.WriteLine($"Reviewed: {(reviewers.Length > 0 ? reviewers : "no")}");
Console.WriteLine($"Evidence: {(hit.Evidence.All(e => e.Matched == true) ? "every quote found in its source" : "not all checked")}");
```

```sh
dotnet run
```

Each result is a **citation record**: the text, plus the package, version and
digest it came from, a versioned reference to the node, its trust signals, and
its evidence, marked `Matched` when the quote was found in the original. Show
your users where an answer came from, and log the digest with the answer so you
can prove later what was read.

The search left out the internal account-deletion procedure: you asked for the
`public` audience only, and nothing a caller does can widen that. It also
leaves out content that is superseded, out of force or deprecated, unless you
set `IncludeAll`.

When you hand results to a model, pass the text as quoted reference material,
never as instructions
([Reader contract §12](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#12-handing-content-to-a-model)).
`OpenMoca.Reader.AgentFramework` does this for Microsoft Agent Framework agents.

## 5. Run the conformance corpus

The corpus is how every MOCA Reader, in any language, shows it reads packages
the same way. moca-spec's runner drives this Reader through its conformance
adapter:

```sh
cd ..
git clone --depth 1 --branch v0.6.0 https://github.com/openmoca/moca-spec.git
git clone --depth 1 --branch v0.6.0 https://github.com/openmoca/moca-reader-dotnet.git
dotnet build moca-reader-dotnet/tests/OpenMoca.Reader.ConformanceAdapter -c Release
cd moca-spec
npm ci
node scripts/run-conformance.mjs --reader "dotnet run --no-build -c Release --project ../moca-reader-dotnet/tests/OpenMoca.Reader.ConformanceAdapter"
```

```text
All 196 conformance case(s) pass.
```

## Next

- [Framework bindings](../README.md#framework-bindings): Microsoft.Extensions.AI
  embedders, a Microsoft Agent Framework context provider, and a
  Microsoft.Extensions.VectorData store backend.
- [Consuming packages](https://github.com/openmoca/moca-spec/blob/main/docs/guides/consuming.md):
  your own retrieval stack, structure, dense search and MCP.
- [moca-examples](https://github.com/openmoca/moca-examples#readme): a command
  line and a chat over a package.
