# Sharper

Sharper is an experimental dialect of C#. This repository is a fork of [dotnet/roslyn](https://github.com/dotnet/roslyn)
that is being turned into the Sharper compiler. This folder is the home of Sharper's own documentation. The rest of
[`docs/`](../README.md) is inherited from upstream Roslyn.

## Goals

- Make common C# patterns shorter and stricter, without changing what existing C# code means.
- Stay a small, mergeable layer on top of Roslyn, so that upstream compiler and IDE improvements keep flowing in.
- Produce ordinary .NET assemblies. Sharper code needs no new runtime, library or metadata to run.

Sharper is not a fork of the language specification, a replacement for C#, or a supported product.

## Planning

- [Brainstorm](planning/brainstorm.md): the language design draft. It covers the motivation (a stricter C# for coding
  agents), the proposed syntax, immutability, effects and purity, bindings for existing .NET code, and sharpening
  (converting C# to Sharper).
- [Implementation plan](planning/implementation-plan.md): how features get built inside Roslyn. It has the map of
  compiler areas touched, known traps, decisions, the syntax roadmap with its suggested order, and follow-ups.

## Design rules

Every Sharper feature follows these rules. The [`sharper-feature` skill](../../.github/skills/sharper-feature/SKILL.md)
has the full implementation checklist.

1. **Gated on one flag.** All features sit behind `MessageID.IDS_FeatureSharper`, which is enabled with
   `/features:sharper` (`<Features>sharper</Features>` in a project) for every file, or per file by the `.cx`
   extension (see [File extension](#file-extension)). With the flag off, the compiler behaves like upstream, and
   Sharper syntax reports the usual C# errors.
2. **No semantic breaking changes.** If Sharper syntax already means something in C#, the C# meaning wins. The
   compiler first binds the code as C#, and uses the Sharper meaning only where that reports an error. Turning the
   flag on never changes what valid C# does.
3. **Reuse the C# syntax.** Features reinterpret existing syntax in the binder where possible, instead of adding
   syntax nodes and public API.
4. **Lower to C#.** Each feature lowers to constructs the compiler already emits, so debugging, PDBs and the runtime
   need no changes.
5. **Tested against C#.** Each Sharper test sits next to the plain C# it stands for, so the diagnostics and
   behavior can be compared (`src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests.cs`).

## File extension

Sharper source files use the **`.cx`** extension. The command-line compiler parses `.cx` files (in any letter case)
with the Sharper feature turned on, the way `.csx` scripts get their own parse options, and parses `.cs` files as
plain C#. A compilation can mix the two, and each file says which language it is. `/features:sharper` still turns
Sharper on for every file, `.cs` included. The code is `IsSharperFile` in
`src/Compilers/CSharp/Portable/CommandLine/CSharpCompiler.cs`.

Not done yet:

- The IDE workspace and the language server parse `.cx` documents with the project's options, so Sharper syntax in
  them shows the C# errors unless the project sets `<Features>sharper</Features>`.
- The SDK does not add `*.cx` to the `Compile` items. Add them in the project:
  `<Compile Include="**/*.cx" />`.

- No other .NET tool gives `.cx` a meaning. Outside .NET it is used by Cytoscape network files, CADMATIC exchange
  files and a few small hobby languages.
- GitHub Linguist does not map `.cx` to any language, so GitHub shows such files as plain text. Until Sharper is
  common enough to get its own Linguist entry, a repository can have them highlighted and counted as C# with this
  line in `.gitattributes`:

  ```
  *.cx linguist-language=C#
  ```

## Features

| Feature | Summary | Status |
| --- | --- | --- |
| [Spread in `yield return`](features/yield-return-spread.md) | `yield return ..xs;` yields every element of `xs`. | Prototype, merged into `main` |
| [`val` locals](features/val-locals.md) | `val x = e;` declares an implicitly typed local that cannot be reassigned. | Prototype (local declarations), merged into `main` |

"Prototype" means that the feature works end to end (binding, flow analysis, nullable analysis, lowering, tests) but
has open questions. Those are tracked in the code as `// PROTOTYPE(sharper): ...` comments:

```bash
git grep -n "PROTOTYPE(sharper)"
```

## Branches

- `main` is the integration branch. It holds upstream Roslyn plus the finished Sharper prototypes.
- `sharper/<feature>` branches hold features in progress; `sharper/gating` and `sharper/val` are merged.

## Diagnostics

Sharper diagnostics are ordinary C# compiler diagnostics (`CSxxxx`). They currently take the next free numbers in
`ErrorCode.cs`, which may clash with codes upstream adds later.

| Code | Severity | Message |
| --- | --- | --- |
| CS9402 | Error | A spread 'yield return ..' cannot be used in an async iterator. |
| CS9403 | Warning | '..{0}' is a range here, not a spread, because '{0}' is dynamic. |
| CS9404 | Warning | Call to non-readonly member '{0}' on val variable '{1}' runs on a copy, so '{1}' is not changed. |

## Where the code is

| What | Where |
| --- | --- |
| Feature flag | `src/Compilers/CSharp/Portable/Errors/MessageID.cs` (`IDS_FeatureSharper`), `src/Compilers/Core/Portable/CommandLine/Feature.cs` (`Feature.Sharper`) |
| Binding | `src/Compilers/CSharp/Portable/Binder/` (search for `IDS_FeatureSharper`) |
| Bound nodes | `src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml` |
| Lowering | `src/Compilers/CSharp/Portable/Lowering/LocalRewriter/` |
| Tests | `src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests.cs` |

## Building and testing

Sharper builds and tests like Roslyn. See
[Building, Debugging, and Testing on Unix](../contributing/Building%2C%20Debugging%2C%20and%20Testing%20on%20Unix.md)
or [on Windows](../contributing/Building%2C%20Debugging%2C%20and%20Testing%20on%20Windows.md). To run only the Sharper
tests:

```bash
dotnet test src/Compilers/CSharp/Test/Semantic/Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests.csproj -f net10.0 --filter "FullyQualifiedName~SharperTests"
```

Also run them with `ROSLYN_TEST_IOPERATION=1`, which checks IOperation, the semantic model and the control flow graph
for every compilation.

## Not covered yet

Sharper currently changes only the C# compiler. The IDE (completion, classification, refactorings), the language
server, Visual Basic, Razor and the expression evaluator know nothing about Sharper syntax yet.
