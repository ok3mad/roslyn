---
name: sharper-self-build
description: "Build the whole repository with its own compiler in Sharper mode (/features:sharper on every project), run the unit tests on those binaries, sort every failure into Sharper's fault or not, and file each new problem as a `self-build` issue without duplicating known ones. Use when: asked to 'compile everything in Sharper mode', 'dogfood Sharper', 'build Roslyn with Sharper', look for unintended breaking changes, regression-test a Sharper feature against real code, or check main after a merge."
---

# Building and Testing the Repository in Sharper Mode

Roslyn is several million lines of real C#, and Sharper's design rule 2 (`docs/sharper/README.md`) says turning the
flag on never changes what valid C# means. This skill tests that rule on the repository itself:

1. Build the compiler from source (the bootstrap compiler), then build every project with it and
   `/features:sharper`.
2. Run the unit tests on the binaries this produces.
3. Match every warning, error and failing test against the known failures in GitHub issues. For each one that is
   new, decide whether Sharper caused it, by comparing with a build without the flag. Then file it.

It finds three kinds of problems:

- **Compile errors or new warnings.** Sharper binds valid C# differently.
- **Test failures that only happen with the Sharper-built binaries.** Sharper miscompiled code, or changed its meaning
  without an error.
- **Breaks in the source changes themselves**, whatever the flag. Examples are a dropped public API, or metadata that
  plain C# callers now see (`SyntaxFacts`). These fail the same way without the flag, but they are still Sharper's.

## Known failures live in issues

The expected result of a run is not "all green". It is: every failure belongs to an open issue labeled `self-build`.
Each such issue lists what it covers in fenced blocks in its body, so a run can match failures without reading the
prose:

````markdown
```self-build
Microsoft.CodeAnalysis.VisualBasic.UnitTests.Semantics.BinaryOperators.Test1_Date
Microsoft.CodeAnalysis.LanguageServer.ProcessHost.UnitTests.Workspaces.AutoLoadProjectsTests.*
```

```self-build-diagnostics
src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests.cs CS8602
```
````

- **`self-build`:** one fully qualified test name per line, as the test results print it (parameters included). A
  line ending in `*` is a prefix. Use a prefix only when all its tests fail for the same cause.
- **`self-build-diagnostics`:** one compiler diagnostic per line, as the file path from `src/` and the code. Line
  numbers are left out because they move.

These issues are the only list of known failures. Don't copy them into this file or anywhere else: a fix closes its
issue, and the next run expects the tests to pass.

## Prerequisites

- Linux (or macOS) with `./build.sh`. Only the .NET Core test builds run there; the net472 builds need Windows.
- About 15 GB of free disk. In a cloud sandbox, `df` shows the session allowance (see the environment
  documentation). The build needs `ROSLYNUSEHARDLINKS=true`: without it, test projects copy their dependencies and
  the build runs out of disk two-thirds of the way through.
- Time. On 4 cores, the bootstrap and full rebuild take about 15 minutes, and the full .NET Core test run about
  50 minutes.
- A clean working tree, on the commit to test.
- Read access to the repository's issues: `gh` (logged in), or the GitHub MCP tools.

Run long steps in the background, and check results from the files they write rather than from console output.
A container restart kills running work but keeps `artifacts/`. After one, rerun only what did not finish (see
step 4).

## Steps

### 0. Fetch the known failures

Fetch all `self-build` issues, open and closed (a closed one that fails again is a regression), into a JSON file:

```bash
gh issue list --repo ok3mad/roslyn --label self-build --state all --limit 200 \
  --json number,title,state,body > artifacts/self-build-issues.json
```

Without `gh`, call the GitHub MCP `list_issues` tool with `labels: ["self-build"]` and no `state` (both states). Ask
for the fields `number`, `title`, `state` and `body`, and save the result to the same file.
`summarize-results.py` reads either format.

### 1. Build in Sharper mode

```bash
rm -f artifacts/log/Debug/vbcscompiler.log
./build.sh --restore --rebuild --bootstrap --configuration Debug --binaryLog \
  '/p:Features=strict%3Bsharper' /p:ROSLYNUSEHARDLINKS=true 2>&1 | tee artifacts/self-build.log
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
- The build ends with `N Warning(s)` and `N Error(s)`.

### 2. Check that every compile got the flag

`--binaryLog` makes `build.sh` log every compiler request to `artifacts/log/Debug/vbcscompiler.log`:

```bash
grep -c "/features:sharper" artifacts/log/Debug/vbcscompiler.log   # one per compile
grep -c "/features:strict" artifacts/log/Debug/vbcscompiler.log    # must be the same number
pgrep -af VBCSCompiler                                               # while building: runs from artifacts/Bootstrap
```

### 3. Match the compile diagnostics

```bash
python3 .github/skills/sharper-self-build/summarize-results.py \
  --issues artifacts/self-build-issues.json --build-log artifacts/self-build.log
```

Each distinct diagnostic is printed with its issue (`#113`), as `REGRESSION #n`, or as `NEW`. For each new one,
replay that project's compile without the flag, using the SDK's stock compiler:

```bash
python3 .github/skills/sharper-self-build/replay-compile.py <project name> [net10.0]
```

- **Same diagnostic without the flag:** the code has the problem, not Sharper.
- **Only with the flag:** Sharper changed how valid C# binds. That breaks design rule 2. Reduce it to a test in
  `SharperTests.cs`.

Also check what the build regenerated, with `git status`. The build rewrites
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
python3 .github/skills/sharper-self-build/summarize-results.py \
  --issues artifacts/self-build-issues.json artifacts/TestResults/sharper
```

- With no `--include`, RunTests runs every `*UnitTests*` assembly, about 70 work items covering the compiler and
  the IDE. `--testSet compiler` runs only the compiler's 15 assemblies, which are the most likely to show a miscompile.
- The script labels every failure `#n` (known), `REGRESSION #n` (matches a closed issue) or `NEW`. It also lists open
  issues whose tests all passed, and work items whose result file is empty because they never finished. Rerun those
  with `--include '^<project folder name>$'` and a new `--out`, then pass both directories to the script. It exits 0
  only when every failure is known and every work item finished.
- **Disk:** the analyzer-testing library caches packages in `/tmp/test-packages` (about 10 GB), and every test host
  hang writes dumps of about 1 GB. Delete the dumps whenever they appear. Delete the cache after the run.

### 5. Triage the new failures

Only `NEW` and `REGRESSION` failures need work. For each failing assembly, rebuild its test project, and everything
it depends on, without the flag and with the SDK's stock compiler. Then rerun only those tests:

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

### 6. Report without duplicates

Every failure must end up in exactly one open `self-build` issue.

- **Known (`#n`):** do nothing. Don't comment on the issue just to say it still fails.
- **New:** search the issues first, open and closed, including unlabeled ones, by test name, diagnostic code and
  cause. A run before this skill existed, or a person, may have filed it without the label.
  - **Same cause already filed:** add the label and the blocks to that issue, or add the new tests to its existing
    block. Don't open another issue.
  - **Otherwise:** open one issue per cause, not per test, labeled `self-build`. Give it the cause, the evidence (with
    and without the flag, the commit tested), a proposed fix, and the blocks listing every test or diagnostic it
    covers.
- **Regression (`REGRESSION #n`):** reopen the closed issue with a comment naming the commit, rather than filing a new
  one. If the cause is clearly different, file a new issue and link the old one.
- **Open issue with nothing failing:** check that its tests actually ran (not skipped, not in an incomplete work
  item). If they did, comment once with the commit where they pass, and suggest closing. Close it only when its fix
  merged, or when asked to.
- **A known issue with more or fewer failing tests:** update its block, so the next run matches exactly.

Then report the numbers: compiles with the flag, warnings and errors, tests run, failed and skipped, and how many
failures were known, new and regressions, with the issues filed or updated. Remove `/tmp/test-packages` and the
dumps when done.
