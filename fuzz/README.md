# Fuzzing

Packages are untrusted archives, so the Reader's open-and-search path is fuzzed with
[SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer, seeded from moca-spec's conformance corpus.

| Target (`MOCA_FUZZ_TARGET`) | Input | What fails it |
| --- | --- | --- |
| `open` (the default) | Archive bytes, read with `MocaReader.ReadPackageAsync` | Any exception from reading, or from searching a package that read as valid |
| `files` | One file of a valid package replaced (manifest, frontmatter, Turtle), read through a host source; the first byte chooses the file | The same |

`ReadPackageAsync` never throws for a malformed package: damaged input is a diagnostic. A crash, a timeout or running
out of memory is a bug: fix it, and add a test (as `tests/OpenMoca.Reader.Tests/ArchiveRobustnessTests.cs` does) or
a case to moca-spec's corpus.

The harness (`OpenMoca.Reader.Fuzz`) is not in the solution, so the usual build and tests don't build it.

## Run locally

libFuzzer's .NET driver runs on Linux x86_64 (and Windows); on another system run it in a container
(`mcr.microsoft.com/dotnet/sdk:10.0`). With moca-spec checked out beside this repository:

```sh
dotnet tool install --global SharpFuzz.CommandLine --version 2.3.0
# libfuzzer-dotnet: https://github.com/Metalnem/libfuzzer-dotnet/releases (check its SHA-256 against fuzz.yml)
cd fuzz
dotnet publish OpenMoca.Reader.Fuzz -c Release -o out
sharpfuzz out/OpenMoca.Reader.dll                        # instruments the Reader in place
./out/OpenMoca.Reader.Fuzz seed ../../moca-spec corpus   # builds corpus/ and corpus-files/
MOCA_SPEC_DIR=../../moca-spec MOCA_FUZZ_TARGET=open  libfuzzer-dotnet --target_path=out/OpenMoca.Reader.Fuzz kept-open corpus -max_total_time=60
MOCA_SPEC_DIR=../../moca-spec MOCA_FUZZ_TARGET=files libfuzzer-dotnet --target_path=out/OpenMoca.Reader.Fuzz kept-files corpus-files -max_total_time=60
```

Set `MOCA_FUZZ_BASE` to fuzz a different fixture's files (by default `structure-procedure`).

## In CI

`.github/workflows/fuzz.yml` runs each target for 60 seconds on pull requests that touch the Reader or `fuzz/`, and
for 30 minutes nightly, keeping the corpus between nightly runs. Crashing inputs are uploaded as an artifact. Seeds
come from moca-spec at the same ref as `test.yml`. SharpFuzz is versioned in `Directory.Packages.props`, so
Dependabot's NuGet updates cover it.
