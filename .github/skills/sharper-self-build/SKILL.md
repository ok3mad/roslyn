---
name: sharper-self-build
description: "Build the whole repository with its own compiler in Sharper mode (/features:sharper on every project), run the unit tests on those binaries, and sort every failure into Sharper's fault or not. Use when: asked to 'compile everything in Sharper mode', 'dogfood Sharper', 'build Roslyn with Sharper', look for unintended breaking changes, regression-test a Sharper feature against real code, or check main after a merge."
---

# Building and Testing the Repository in Sharper Mode

Roslyn is several million lines of real C#, and Sharper's design rule 2 (`docs/sharper/README.md`) says turning the
flag on never changes what valid C# means. This skill tests that rule on the repository itself:

1. Build the compiler from source (the bootstrap compiler), then build every project with it and
   `/features:sharper`.
2. Run the unit tests on the binaries this produces.
3. For every warning, error or failing test, decide whether Sharper caused it, by comparing against a build
   without the flag.

It finds three kinds of problems:

- **Compile errors or new warnings.** Sharper binds valid C# differently.
- **Test failures that only happen with the Sharper-built binaries.** Sharper miscompiled code, or changed its meaning
  without an error.
- **Breaks in the source changes themselves**, whatever the flag. Examples are a dropped public API, or metadata that
  plain C# callers now see (`SyntaxFacts`). These fail the same way without the flag, but they are still Sharper's.

## Prerequisites

- Linux (or macOS) with `./build.sh`. Only the .NET Core test builds run there; the net472 builds need Windows.
- About 15 GB of free disk. In a cloud sandbox, `df` shows the session allowance (see the environment
  documentation). The build needs `ROSLYNUSEHARDLINKS=true`: without it, test projects copy their dependencies and
  the build runs out of disk two-thirds of the way through.
- Time. On 4 cores, the bootstrap and full rebuild take about 15 minutes, and the full .NET Core test run about
  50 minutes.
- A clean working tree, on the commit to test.

Run long steps in the background, and check results from the files they write rather than from console output.
A container restart kills running work but keeps `artifacts/`. After one, rerun only what did not finish (see
step 4).

## Steps

### 1. Build in Sharper mode

```bash
rm -f artifacts/log/Debug/vbcscompiler.log
./build.sh --restore --rebuild --bootstrap --configuration Debug --binaryLog \
  '/p:Features=strict%3Bsharper' /p:ROSLYNUSEHARDLINKS=true
```

- `eng/targets/Settings.props` gives every project `<Features>strict</Features>`. A global property overrides it, so
  pass `strict` again. `%3B` is MSBuild's escaped `;`. The Csc task turns the value into `/features:strict
  /features:sharper`.
- `--bootstrap` packs `Microsoft.Net.Compilers.Toolset` from the current source into `artifacts/Bootstrap` and builds
  the repository with it. To build again with the same compiler (the compiler source is unchanged), skip
  `--bootstrap` and pass `/p:BootstrapBuildPath=$PWD/artifacts/Bootstrap`.
- Use `--rebuild`, not `--build`, unless `artifacts/` holds nothing but a previous Sharper-mode build of the same
  compiler. An incremental build only recompiles projects whose inputs changed. A changed compiler, or a project
  rebuilt without the flag (step 5), would leave stale binaries behind.
- The build ends with `N Warning(s)` and `N Error(s)`. A plain build of `main` has 0 of both.

### 2. Check that every compile got the flag

`--binaryLog` makes `build.sh` log every compiler request to `artifacts/log/Debug/vbcscompiler.log`:

```bash
grep -c "/features:sharper" artifacts/log/Debug/vbcscompiler.log   # one per compile
grep -c "/features:strict" artifacts/log/Debug/vbcscompiler.log    # must be the same number
pgrep -af VBCSCompiler                                               # while building: runs from artifacts/Bootstrap
```

### 3. Triage compile diagnostics

For each warning or error, replay that project's compile without the flag, using the SDK's stock compiler:

```bash
python3 .github/skills/sharper-self-build/replay-compile.py <project name> [net10.0]
```

- **Same diagnostic without the flag:** the code has the problem. File it against the code, not Sharper.
- **Only with the flag:** Sharper changed how valid C# binds. That breaks design rule 2. Reduce it to a test in
  `SharperTests.cs` and fix it, or file it.

Also check what the build regenerated: `git status`. The build rewrites
`src/Tools/SemanticSearch/ReferenceAssemblies/Apis/*.txt` from the compiled public API.

- **Added lines:** the checked-in copy is stale.
- **A removed line:** a shipped public API disappeared. That is a binary breaking change. RS0017 would report it,
  but analyzers don't run in a local build by default.

Restore these files (`git checkout -- <file>`) unless the task is to commit them.

### 4. Run the unit tests

```bash
dotnet=.dotnet/dotnet; runtests=artifacts/bin/RunTests/Debug/net10.0/RunTests.dll
$dotnet exec $runtests --testConfiguration Debug --testFramework core --timeout 300 --html- \
  --out $PWD/artifacts/TestResults/sharper
find artifacts/TestResults -name "*.dmp" -delete
python3 .github/skills/sharper-self-build/summarize-results.py artifacts/TestResults/sharper
```

- With no `--include`, RunTests runs every `*UnitTests*` assembly, about 70 work items covering the compiler and
  the IDE. `--testSet compiler` runs only the compiler's 15 assemblies, which are the most likely to show a miscompile.
- `summarize-results.py` lists the failures, and the work items whose result file is empty because they never
  finished. Rerun those with `--include '^<project folder name>$'` and a new `--out`, then pass both directories to
  the script.
- **Disk:** the analyzer-testing library caches packages in `/tmp/test-packages` (about 10 GB), and every test host
  hang writes dumps of about 1 GB. Delete the dumps whenever they appear. Delete the cache after the run.

### 5. Triage failing tests

For each failing assembly, rebuild its test project, and everything it depends on, without the flag and with the
SDK's stock compiler. Then rerun only the failing tests:

```bash
$dotnet build <test project>.csproj -t:Rebuild -c Debug -f net10.0 -p:ROSLYNUSEHARDLINKS=true
$dotnet exec $runtests --testConfiguration Debug --testFramework core --html- \
  --out $PWD/artifacts/TestResults/baseline --include '^<project folder name>$' \
  --testfilter 'FullyQualifiedName~<test>|FullyQualifiedName~<test>'
```

Check first that the plain build really drops the flag:
`$dotnet msbuild <project> -getProperty:Features -getProperty:BootstrapBuildPath` must print `strict` and an empty
path.

- **Passes without the flag:** compiling the code in Sharper mode broke it. This is the most serious result. Find the
  code the test exercises, and reduce it to a `SharperTests.cs` test.
- **Fails the same way:** the flag is not the cause. Then decide between:
  - **A Sharper source change:** for example, `SyntaxFacts` or other shared metadata reporting a Sharper keyword to
    plain C# callers. Compare with the upstream merge base when unsure.
  - **The environment:** for example ICU, the shell, the network, or a missing Windows feature.
  - **An upstream bug.**

The rebuild replaces those projects' binaries with non-Sharper ones. Before testing in Sharper mode again, run step 1
again (with `--rebuild`).

### 6. Report

Report the numbers: compiles with the flag, warnings and errors, tests run, failed and skipped. For each failure,
give its classification and the evidence for it. File new problems as issues, after checking the open ones. Remove
`/tmp/test-packages` and the dumps when done.

## Known failures

These fail with and without the flag. Check each issue: once it is closed, its failures should be gone.

| Failures | Cause | Issue |
|---|---|---|
| `Microsoft.AspNetCore.Razor.Language.UnitTests`: 69 tests about identifiers named `val` | `SyntaxFacts` reports `val` as a contextual keyword for all code | #109 |
| `DiagnosticAnalyzerAllInOne` (Emit3) | The all-in-one test source has no example of the Sharper syntax kinds | #101 |
| `LanguageServer.ProcessHost.UnitTests`: 4 tests, and 1 hang | The test project never loads in the sandbox | #110 |
| C# stdin `CommandLineTests` (3), VB `BinaryOperators.Test1_Date` and `Test5_DateConst` | Environment: `sh` quoting, ICU time format | #111 |
| 13 nullable warnings in `SharperTests.cs` | Missing `!` in tests | #113 |
