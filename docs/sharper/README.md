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
- [Upgrade analyzers and fixers](planning/upgrade-analyzers.md): a proposal for IDE analyzers and code fixes that find
  C# code with a better Sharper equivalent (`val`, `T[..]`, `T~`, function types, `yield return ..xs`, `it`, `try`
  expressions, `infer`, `readonly` parameters) and rewrite it, in the order to build them.

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
| [Inferred return type](features/inferred-return-type.md) | `public static infer Greeting(int id) => $"Hello, {id}!";` returns `string`. Steps R1 to R3: expression and block bodies of private methods and local functions, iterators and `async`, inferred methods that use each other, and cycles. Step R4: every accessibility, `virtual`, partial methods, extension block members, operators and conversion operators, and Edit and Continue. Step R5: the IDE (Quick Info, inlay hints, classification, completion, writing the type out and back, the fix for CS20027). | Prototype of steps R1 to R5, merged into `main` | [#67](https://github.com/ok3mad/roslyn/issues/67), [#68](https://github.com/ok3mad/roslyn/issues/68), [#69](https://github.com/ok3mad/roslyn/issues/69), [#70](https://github.com/ok3mad/roslyn/issues/70), [#71](https://github.com/ok3mad/roslyn/issues/71), under [#22](https://github.com/ok3mad/roslyn/issues/22) |
| [Inferred parameter type](features/inferred-parameter-type.md) | `void Fetch(string url, infer retries = 3)` gives `retries` the type of its default value, `int`. Step P1: default values, on every member kind and lambdas. Step P2: `public infer Load(infer userId)` implementing `IProfiles.Load` copies `Task<ProfileResult> Load(int userId)` from the member it overrides or implements; properties, indexers, operators and events copy too (`public override infer Name => "c";`). Step P3: `readonly infer x` cannot be reassigned. Step P4: `public infer Hello(infer id) => profiles.Load(id);` gives `id` the type of the one `Load`'s parameter, from a use in the body. Step P5: overloaded and generic calls pin it when every applicable candidate agrees (`Format(n, "N2")`, `dict.GetValueOrDefault(key)`). Step P6: assignments to typed locals, fields and properties, `return x` with a written return type, and `ref`, `out` and `in` arguments pin it too (`void Rename(infer name) { this.Name = name; }`; on `sharper/infer-parameter-p6`). | Prototype of steps P1 to P5, merged into `main`; P6 in progress | [#72](https://github.com/ok3mad/roslyn/issues/72), [#73](https://github.com/ok3mad/roslyn/issues/73), [#74](https://github.com/ok3mad/roslyn/issues/74), [#75](https://github.com/ok3mad/roslyn/issues/75), [#76](https://github.com/ok3mad/roslyn/issues/76), [#77](https://github.com/ok3mad/roslyn/issues/77), under [#27](https://github.com/ok3mad/roslyn/issues/27) |
| [Immutable types](features/immutable-types.md) | `[Immutable] sealed record Money(decimal Amount, string Currency);` is checked to be deeply immutable; `[ImmutableHierarchy]` extends the claim to every subclass. Slice 1: the check, the attributes and their diagnostics. | Prototype of slice 1, on `sharper/immutable-types-slice1` | [#139](https://github.com/ok3mad/roslyn/issues/139), designed in [#92](https://github.com/ok3mad/roslyn/issues/92) |
| [Purity](features/purity.md) | `[Pure] static int Fib(int i) => ...;` is checked to have no effect outside its own frame; its result depends only on the contents of its arguments. Slice 1, first part: declared `[Pure]` on non-virtual members, the attested BCL members, and the `out` warning. | Prototype of the first part of slice 1, on `sharper/purity-slice1` | [#173](https://github.com/ok3mad/roslyn/issues/173), [#177](https://github.com/ok3mad/roslyn/issues/177), under [#36](https://github.com/ok3mad/roslyn/issues/36) |
| [Refined types](features/refined-types.md) | `static int Page(int i is >=0)` refines a parameter with a closed pattern over constants. Slice 1, steps 1 and 2: the syntax, the binding and its checks (closed patterns, supported types, the default value, the members that cannot have one yet), and implicit `readonly`. No guard or call-site check yet, so runtime behavior is unchanged. | Prototype of slice 1 steps 1 and 2, on `sharper/refined-parameters-syntax` | [#33](https://github.com/ok3mad/roslyn/issues/33) |
| [Implicit `it`](features/implicit-it.md) | `xs.Select(it.Name)` is `xs.Select(x => x.Name)`: an argument that uses `it` is a lambda, if the innermost parameter around it takes a one-parameter function. | Prototype, merged into `main` | [#24](https://github.com/ok3mad/roslyn/issues/24) |
| [Pipe `\|>`](features/pipe.md) | `raw \|> ParseOrder \|> Resolve(it, accounts)` is `Resolve(ParseOrder(raw), accounts)`: a stage that uses `it` is an expression, any other stage is a function applied to the value. No lifting yet. | Prototype, on `sharper/pipe` | [#25](https://github.com/ok3mad/roslyn/issues/25) |
| [`try` expression](features/try-expression.md) | `try int.Parse(s) catch { FormatException => -1 }` turns exceptions into values with switch arms; an unmatched exception propagates untouched. Slice 1: a declared union or the best common type as the result. | Prototype of slice 1, on `sharper/try-expression-slice1` | [#28](https://github.com/ok3mad/roslyn/issues/28) |

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
  `sharper/readonly-locals`, `sharper/infer-return-r1`, `sharper/infer-return-r2`, `sharper/infer-return-r3`,
  `sharper/infer-parameter-p1`, `sharper/try-expression-slice1`, `sharper/implicit-it`, `sharper/readonly-parameters`,
  `sharper/immutable-types-slice1`, `sharper/infer-from-overridden-p2`, `sharper/infer-return-r4`, `sharper/pipe` and
  `sharper/readonly-infer-p3`, `sharper/purity-slice1`, `sharper/infer-p2-member-kinds` and `sharper/infer-ide-r5` are
  merged. `sharper/infer-parameter-p4` and `sharper/refined-parameters-syntax` are in progress.

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
| CS20014 | Error | Cannot infer the return type of '{0}', because the value it returns ({1}) has no type. Write the return type. |
| CS20015 | Error | An inferred return type ('infer') is not supported {0} yet. Write the return type. |
| CS20016 | Error | '{0}' has no body to infer its return type from. Write the return type. |
| CS20017 | | Retired in step R3, which infers the other method first. Was: "Cannot infer the return type of '{0}', because its body uses '{1}', whose return type is inferred too." |
| CS20018 | Error | Cannot infer the return type of '{0}', because its body uses '{0}' itself. Write the return type. |
| CS20019 | Error | 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'. |
| CS20020 | Error | 'it' has two meanings here: the parameter of an outer lambda and of this inner one. Name one of them, as in 'x => ...'. |
| CS20021 | Error | 'it' is ambiguous here: '{0}' takes a value or a function at this argument, and the outer 'it' fits both. Write the lambda, as in 'x => ...', or name the outer value. |
| CS20022 | Error | Cannot declare '{0}' in an implicit 'it' lambda. Write the lambda, as in 'x => ...'. |
| CS20023 | Error | Cannot use 'await' in an implicit 'it' lambda. Write the lambda, as in 'async x => ...'. |
| CS20024 | Error | Cannot infer the return type of '{0}', because the values it returns ({1}) have no best common type. Write the return type. |
| CS20025 | Error | Cannot infer the return type of the iterator '{0}', because it has no 'yield return' to infer the element type from. Write the return type. |
| CS20026 | Error | Cannot infer the type of parameter '{0}', because its default value ({1}) has no type. Write the parameter type. |
| CS20027 | Error | Cannot infer the type of parameter '{0}', because it has no default value, and {1}. Write the parameter type. ({1} says why nothing pinned it: "the body does not use it", "a member access pins nothing", "a call to 'F', which has more than one candidate, pins nothing yet", ...; "nothing else gives its type" for members whose body is no source) |
| CS20028 | Error | Cannot infer the type of parameter '{0}', because its default value depends on the type of '{0}' itself. Write the parameter type. |
| CS20029 | Error | The try expression has no type, because its body and arms ({0}) have no best common type. Give it a target type, such as a declared union of these types. |
| CS20030 | Warning | The body of this try expression is a '{0}', so the arms catch only the exceptions thrown before the task is returned. Write 'try await ...' to catch the exceptions of the operation. |
| CS20031 | Error | An expression tree cannot contain a try expression. |
| CS20032 | Error | A try expression cannot be used in a 'when' clause, because a catch filter cannot contain a try block. Move it out of the filter. |
| CS20033 | Error | Cannot infer the return type of '{0}', because it depends on itself: {1}. Write the return type of one of these methods. |
| CS20034 | Error | A 'val' lambda parameter needs parentheses. Write '(val {0}) => ...'. |
| CS20035 | Error | '{0}' is marked [Immutable], but it cannot be proven immutable: {1}. |
| CS20036 | Error | '{0}' is marked [Immutable], but it is mutable: {1}. |
| CS20037 | Error | '{0}' is marked [Immutable], but its base type '{1}' is not immutable. |
| CS20038 | Error | '{0}' must be immutable, because '{1}' is marked [ImmutableHierarchy], but {2}. |
| CS20039 | Warning | '{0}' must be immutable, because '{1}' is marked [ImmutableHierarchy], but {2}. |
| CS20040 | Error | '{0}' is marked [ImmutableHierarchy], so it must also be marked [Immutable]. |
| CS20041 | Error | [UnobservableWrite] is only valid on a field that is not readonly, of a class marked [Immutable]. |
| CS20042 | Error | Type parameter '{0}' is marked [Phantom], but '{1}' uses it in the state of the type. |
| CS20043 | Error | Cannot infer the types of '{0}' from the member it overrides or implements, because {1}. Write the types. ({1} names the members the written types ruled out, and what ruled each out) |
| CS20044 | Error | Cannot infer the types of '{0}', because it could override or implement more than one member: {1}. Write the types of the one it overrides or implements. |
| CS20045 | Error | Cannot infer the types of '{0}' from the member it overrides or implements, because the signature of that member depends on '{0}' itself. Write the types. |
| CS20046 | Error | The left side of '\|>' must be a value with a type, but it is {0}. |
| CS20047 | Error | This stage does not use the value piped into it: its 'it' belongs to an inner lambda, the argument of '{0}'. Name that lambda's parameter, as in 'x => ...', or use the piped 'it' outside it. |
| CS20048 | Error | The stage after '\|>' must be a function, such as a method group or a delegate, or an expression that uses 'it', but it is {0}. |
| CS20049 | Error | An expression tree cannot contain a pipe '\|>'. |
| CS20050 | Error | '{0}' is marked [Pure], but it is not pure: {1}. |
| CS20051 | Error | '{0}' is marked [Pure], so it must be readonly: declare it 'readonly', or make the struct 'readonly'. |
| CS20052 | Error | '{0}' cannot be marked [Pure] yet: [Pure] is not supported on virtual, abstract and override members. |
| CS20053 | Warning | The out argument '{1}' of the [Pure] member '{0}' is not a fresh local, so '{0}' may observe its own write through another argument. |
| CS20054 | Error | 'it' here belongs to '{0}', so this argument is a value, not a lambda. Name the parameter, as in 'x => ...'. |
| CS20055 | Error | Cannot infer the type of event '{0}', because it overrides or implements no event. Write the type. |
| CS20056 | Error | Cannot infer the type of parameter '{0}' from its uses, because binding the body of '{1}' needs the signature of '{1}', directly or through other members whose types are inferred. Write the parameter type. |
| CS20057 | Error | Cannot infer the type of parameter '{0}', because its uses need different types: '{1}' for '{2}' and '{3}' for '{4}'. Write the parameter type. |
| CS20058 | Error | A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '{0}' is not. |
| CS20059 | Error | Cannot refine parameter '{0}' of type '{1}'. A refinement needs a type whose constants can be compared: an integer, floating-point, 'decimal', 'char', 'bool', 'string' or enum type. |
| CS20060 | Error | The default value of parameter '{0}' ({1}) does not satisfy its refinement '{2}'. |
| CS20061 | Error | A refinement is not supported {0} yet. ({0} is "on a virtual, abstract or override member", "on an interface member", "on a partial member", "on a lambda or anonymous method parameter", ...) |

## Where the code is

| What | Where |
| --- | --- |
| Feature flag | `src/Compilers/CSharp/Portable/Errors/MessageID.cs` (`IDS_FeatureSharper`), `src/Compilers/Core/Portable/CommandLine/Feature.cs` (`Feature.Sharper`) |
| Parser | `src/Compilers/CSharp/Portable/Parser/LanguageParser.cs` (search for `IDS_FeatureSharper`) |
| Binding | `src/Compilers/CSharp/Portable/Binder/` (search for `IDS_FeatureSharper`); short type forms such as `T[..]` in `Binder_SharperTypes.cs`; inferred return types in `Binder_SharperInferredReturnType.cs` and `Symbols/Source/SharperInferredReturnType.cs`; inferred parameter types in `Binder_SharperInferredParameterType.cs` and `Symbols/Source/SharperInferredParameterType.cs`, the types copied from an overridden or implemented member in `Symbols/Source/SharperInferredParameterType_OverriddenMember.cs`, and the types pinned by uses in the body in `Symbols/Source/SharperInferredParameterType_Body.cs` and `Symbols/Source/SharperPlaceholderMethodSymbol.cs`; implicit `it` in `Binder_SharperImplicitIt.cs`; the pipe in `Binder_SharperPipe.cs`; refinements in `Symbols/Source/SharperRefinement.cs`; immutable types in `Symbols/SharperImmutability.cs`, and purity in `Symbols/SharperImmutability_Pure.cs`; the `try` expression in `Binder_SharperTryExpression.cs` |
| Syntax nodes | `src/Compilers/CSharp/Portable/Syntax/Syntax.xml` (`FunctionTypeSyntax`, `TaskTypeSyntax`, `ParenthesizedTypeSyntax`, `TryExpressionSyntax`, `PipeExpressionSyntax`, `RefinementClauseSyntax`, the new `Refinement` field of `ParameterSyntax`); Sharper kinds in `SyntaxKind.cs` count up from 9900, and the pipe token `BarGreaterThanToken` takes 8231, the top of the free punctuation gap |
| Bound nodes | `src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml` |
| Lowering | `src/Compilers/CSharp/Portable/Lowering/LocalRewriter/` |
| Tests | `src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests.cs` and the other files of the partial class `SharperTests`, such as `SharperTests_TryExpression.cs`, `SharperTests_ImmutableTypes.cs`, `SharperTests_Purity.cs`, `SharperTests_InferFromOverridden.cs`, `SharperTests_Pipe.cs` and `SharperTests_Refinements.cs` |

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

To check that Sharper leaves existing C# alone, build the whole repository with its own compiler and
`/features:sharper`, then run the unit tests on the result. The
[`sharper-self-build`](../../.github/skills/sharper-self-build/SKILL.md) skill has the commands and shows how to tell
failures Sharper causes from the rest.

## Not covered yet

Sharper mostly changes the C# compiler. The IDE (completion, classification, refactorings), the language server,
Visual Basic, Razor and the expression evaluator know nothing about Sharper syntax yet, with two exceptions:

- Edit and Continue tells a spread `yield return ..xs` from a plain yield return, and treats an edit that changes an
  inferred return type as a rude edit.
- The inferred types of `infer` (step R5): Quick Info and inlay hints show them, `infer` is classified as a keyword
  and offered by completion, a refactoring writes the type out and its reverse replaces a written type with `infer`,
  and a fix for CS20027 and CS20057 writes the parameter type. The language server gets these through the same
  services. See [IDE](features/inferred-return-type.md#ide-step-r5).

Every Sharper IDE feature works in Sharper documents only (a `.cx` document, or a project with
`<Features>sharper</Features>`), through `SharperFacts.IsSharperEnabled` in `src/Workspaces/CSharp/Portable/Sharper/`;
plain C# documents do not change. The other Sharper syntax (`val`, `T[..]`, `T~`, function types, `it`, `|>`, `try`
expressions) has no IDE support yet, and the upgrade analyzers and fixers of
[the proposal](planning/upgrade-analyzers.md) are not built yet.
