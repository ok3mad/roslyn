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
   flag on never changes what valid C# does, with one kind of exception: a new keyword may be accepted as a break when
   the old meaning is rare and a sharpener can always rewrite it. So far that is `val` in patterns and in
   `val (a, b) = e`, which code that uses `val` as an identifier there must spell `@val`. Each accepted break is listed
   in the [implementation plan](planning/implementation-plan.md#follow-ups); the sharpener and the `.cx` extension will
   make it safe to adopt. The sequence type adds a second, smaller one: `F<a[..]>(x)` is a generic call, where C#
   reads two comparisons that only compile with user-defined operators. Function types add the same one for
   `F<(p)->b>(x)`. Both also cover the argument-list form, such as `M(F < a[..], b > (c))`.
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

The IDE workspace does the same for documents whose path ends in `.cx`, in
`CSharpSyntaxTreeFactoryService.GetParseOptionsForFile` (`src/Workspaces/CSharp/Portable`). The document's
`ParseOptions` stay the project's; only its syntax tree gets the Sharper feature. Renaming a document between `.cs`
and `.cx` reparses it.

In an SDK-style project, the compiler toolset package (`Microsoft.Net.Compilers.Toolset`, which bootstrap builds use)
adds `**/*.cx` to the `Compile` items of C# projects, under the same conditions and excludes as the SDK's `**/*.cs`
glob. `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>` and `<Compile Remove="..." />` work as they do
for `.cs` files. Do not also include `*.cx` in the project, or the SDK reports duplicate `Compile` items.

The language server treats `.cx` files as C# (`LanguageInfoProvider`), and reloads a project when a `.cx` file is
added to or removed from its directory. [Sharper in VS Code](vscode.md) shows how to use it, with a release workflow
and a devcontainer.

Not done yet:

- `dotnet run app.cx`: the SDK's file-based apps only run `.cs` files, so the language server never treats a loose
  `.cx` file as a file-based app.

- No other .NET tool gives `.cx` a meaning. Outside .NET it is used by Cytoscape network files, CADMATIC exchange
  files and a few small hobby languages.
- GitHub Linguist does not map `.cx` to any language, so GitHub shows such files as plain text. Until Sharper is
  common enough to get its own Linguist entry, a repository can have them highlighted and counted as C# with this
  line in `.gitattributes`:

  ```
  *.cx linguist-language=C#
  ```

## Features

| Feature | Summary | Status | Issue |
| --- | --- | --- | --- |
| [Spread in `yield return`](features/yield-return-spread.md) | `yield return ..xs;` yields every element of `xs`. | Prototype, merged into `main` | [#13](https://github.com/ok3mad/roslyn/issues/13) |
| [Function types](features/function-types.md) | `(A, B) -> R` is `Func<A, B, R>`, and `(A) -> void` is `Action<A>`. | Prototype, merged into `main` | [#21](https://github.com/ok3mad/roslyn/issues/21) |
| [`val` locals](features/val-locals.md) | `val x = e;` declares an implicitly typed local that cannot be reassigned, also in deconstructions, `foreach`, `out`, patterns and lambda parameters (`(val x) => ...`). | Prototype, merged into `main` | [#14](https://github.com/ok3mad/roslyn/issues/14), [#15](https://github.com/ok3mad/roslyn/issues/15), [#18](https://github.com/ok3mad/roslyn/issues/18), [#17](https://github.com/ok3mad/roslyn/issues/17) |
| [`readonly` locals](features/readonly-locals.md) | `readonly int x = e;` and `readonly var x = e;` declare locals that cannot be reassigned; `readonly var` is `val`. | Prototype, merged into `main` | [#16](https://github.com/ok3mad/roslyn/issues/16) |
| [`readonly` parameters](features/readonly-parameters.md) | `void F(readonly int x)` declares a parameter that cannot be reassigned; it is not part of the signature. | Prototype | [#23](https://github.com/ok3mad/roslyn/issues/23), [#97](https://github.com/ok3mad/roslyn/issues/97) |
| [Sequence type](features/sequence-type.md) | `T[..]` is `IEnumerable<T>` in every type position. | Prototype, merged into `main` | [#19](https://github.com/ok3mad/roslyn/issues/19) |
| [Task type](features/task-type.md) | `T~` is `Task<T>`, and `void~` is `Task`. | Prototype, merged into `main` | [#20](https://github.com/ok3mad/roslyn/issues/20) |
| [Inferred return type](features/inferred-return-type.md) | `private static infer Greeting(int id) => $"Hello, {id}!";` returns `string`. Step R1: expression bodies of private methods and local functions. | Prototype of step R1, on `sharper/infer-return-r1` | [#67](https://github.com/ok3mad/roslyn/issues/67), under [#22](https://github.com/ok3mad/roslyn/issues/22) |
| [Implicit `it`](features/implicit-it.md) | `xs.Select(it.Name)` is `xs.Select(x => x.Name)`: an argument that uses `it` is a lambda, if the innermost parameter around it takes a one-parameter function. | Prototype, on `sharper/implicit-it` | [#24](https://github.com/ok3mad/roslyn/issues/24) |

Every planned feature has an issue, one per row of the
[syntax roadmap](planning/implementation-plan.md#syntax-roadmap), collected under the tracking issue
[#12](https://github.com/ok3mad/roslyn/issues/12). Labels give each issue's phase, size and design certainty.

"Prototype" means that the feature works end to end (binding, flow analysis, nullable analysis, lowering, tests) but
has open questions. Those are tracked in the code as `// PROTOTYPE(sharper): ...` comments:

```bash
git grep -n "PROTOTYPE(sharper)"
```

## Branches

- `main` is the integration branch. It holds upstream Roslyn plus the finished Sharper prototypes.
- Upstream `dotnet/roslyn` is merged into `main` with the [`sync-upstream`](../../.github/skills/sync-upstream/SKILL.md)
  skill. The GitHub workflows inherited from upstream run only when started by hand; the fork's own are named
  `sharper-*.yml`.
- `sharper/<feature>` branches hold features in progress; `sharper/gating`, `sharper/val`,
  `sharper/val-designations`, `sharper/val-patterns`, `sharper/function-types`, `sharper/task-type`, `sharper/val-lambda-parameters` and
  `sharper/readonly-locals` are merged. `sharper/readonly-parameters` and `sharper/implicit-it` are in progress.

## Diagnostics

Sharper diagnostics are ordinary C# compiler diagnostics, numbered from `SharperErrorCodes.Base` (20000) in
`ErrorCode.cs`: CS20001 and up. Upstream C# is at about CS9400 and adds roughly a hundred codes a year, so the gap
leaves room for decades of upstream codes. New Sharper codes go at the end of the Sharper block. Five-digit IDs work
in `#pragma warning`, `/nowarn` and `.editorconfig`.

| Code | Severity | Message |
| --- | --- | --- |
| CS20001 | Error | A spread 'yield return ..' cannot enumerate the asynchronous sequence '{0}', because that would need an 'await' that is not written. Use 'await foreach' with 'yield return' instead. |
| CS20002 | Warning | '..{0}' is a range here, not a spread, because '{0}' is dynamic. |
| CS20003 | Warning | Call to non-readonly member '{0}' on val variable '{1}' runs on a copy, so '{1}' is not changed. |
| CS20004 | Error | The syntax 'val' for a pattern is not permitted to refer to a type, but '{0}' is in scope here. Use '@val' for the type. |
| CS20005 | Error | Deconstruction 'val (...)' form disallows a specific type for 'val'. Use '@val' for the type. |
| CS20006 | Error | Cannot ref-assign '{0}' because it is a ref val: it always refers to the same variable. Writes through '{0}' are allowed. |
| CS20007 | Error | A function type cannot name its parameters. Remove the name '{0}'. |
| CS20008 | Error | A function type parameter cannot be '{0}', because a function type is a 'System.Func' or 'System.Action' delegate. |
| CS20009 | Error | A function type cannot have more than {0} parameters, because no 'System.Func' or 'System.Action' delegate has more. |
| CS20010 | Warning | Call to non-readonly member '{0}' on readonly local '{1}' runs on a copy, so '{1}' is not changed. |
| CS20011 | Error | A readonly local must be initialized in its declaration, because it cannot be assigned later. |
| CS20012 | Error | A spread 'yield return ..' cannot enumerate '{0}' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead. |
| CS20013 | Warning | Call to non-readonly member '{0}' on readonly parameter '{1}' runs on a copy, so '{1}' is not changed. |
| CS20014 | Error | Cannot infer the return type of '{0}', because its body ({1}) has no type. Write the return type. |
| CS20015 | Error | An inferred return type ('infer') is not supported {0} yet. Write the return type. |
| CS20016 | Error | '{0}' has no body to infer its return type from. Write the return type. |
| CS20017 | Error | Cannot infer the return type of '{0}', because its body uses '{1}', whose return type is inferred too. Write the return type of one of them. |
| CS20018 | Error | Cannot infer the return type of '{0}', because its body uses '{0}' itself. Write the return type. |
| CS20019 | Error | 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'. |
| CS20020 | Error | 'it' has two meanings here: the parameter of an outer lambda and of this inner one. Name one of them, as in 'x => ...'. |
| CS20021 | Error | 'it' is ambiguous here: '{0}' takes a value or a function at this argument, and the outer 'it' fits both. Write the lambda, as in 'x => ...', or name the outer value. |
| CS20022 | Error | Cannot declare '{0}' in an implicit 'it' lambda. Write the lambda, as in 'x => ...'. |
| CS20023 | Error | Cannot use 'await' in an implicit 'it' lambda. Write the lambda, as in 'async x => ...'. |

## Where the code is

| What | Where |
| --- | --- |
| Feature flag | `src/Compilers/CSharp/Portable/Errors/MessageID.cs` (`IDS_FeatureSharper`), `src/Compilers/Core/Portable/CommandLine/Feature.cs` (`Feature.Sharper`) |
| Parser | `src/Compilers/CSharp/Portable/Parser/LanguageParser.cs` (search for `IDS_FeatureSharper`) |
| Binding | `src/Compilers/CSharp/Portable/Binder/` (search for `IDS_FeatureSharper`); short type forms such as `T[..]` in `Binder_SharperTypes.cs`; inferred return types in `Binder_SharperInferredReturnType.cs` and `Symbols/Source/SharperInferredReturnType.cs`; implicit `it` in `Binder_SharperImplicitIt.cs` |
| Syntax nodes | `src/Compilers/CSharp/Portable/Syntax/Syntax.xml` (`FunctionTypeSyntax`, `TaskTypeSyntax`, `ParenthesizedTypeSyntax`); Sharper kinds in `SyntaxKind.cs` count up from 9900 |
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

After a parser change, run the upstream parser tests with `ROSLYN_TEST_SHARPER=1`, which turns Sharper on for every
parse in the tests (`TestOptions.SharperForAllParses`), to check design rule 2:

```bash
ROSLYN_TEST_SHARPER=1 dotnet test src/Compilers/CSharp/Test/Syntax/Microsoft.CodeAnalysis.CSharp.Syntax.UnitTests.csproj -f net10.0
```

The switch reaches the parse options of `ParsingTests`, `TestOptions` and `CSharpTestSource.Parse`, but not tests that
call `SyntaxFactory` without options. It also turns Sharper on for the compilations of the other C# test projects; for
a change that also affects binding, run the Semantic project the same way with
`--filter "FullyQualifiedName!~SharperTests"`, since the Sharper tests check the flag-off behavior too. Some
failures are expected: tests of invalid C# that Sharper gives a meaning (such as `readonly` on a local), tests that
count the parse options' features, and the accepted breaks. The
[implementation plan](planning/implementation-plan.md#follow-ups) has the last run's list; any other failure is a bug
unless it is one of those.

## Not covered yet

Sharper currently changes only the C# compiler. The IDE (completion, classification, refactorings), the language
server, Visual Basic, Razor and the expression evaluator know nothing about Sharper syntax yet, except that Edit and
Continue tells a spread `yield return ..xs` from a plain yield return.
