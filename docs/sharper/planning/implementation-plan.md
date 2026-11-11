# Sharper: Roslyn Implementation Plan

> [!NOTE]
> This plan is maintained here; it started as the "Sharper: Roslyn Implementation Plan" planning doc. For a quick
> view of feature status, see the [Sharper overview](../README.md#features).


This is the companion to the [language design draft](brainstorm.md), which stays for design discussions. This doc tracks how Sharper features get built inside the C# compiler.

## Goal of the first PoC

The aim is to prove that a Sharper feature can be built inside Roslyn itself, and to map which parts of the compiler have to change. Neither the transformation nor the syntax needs to survive. Traps that force a redesign are useful findings. An external CLI that rewrites C# is out of scope.

## Result

The PoC works: behind `/features:sharper`, `yield return ..xs` compiles, runs and is analyzed like hand-written C#, on branch `sharper/gating` of ok3mad/roslyn. No parser change was needed, and the iterator state machine was not touched.

- 27 Sharper tests cover runtime output, diagnostics, nullable warnings, IOperation, the control flow graph and region analysis. They also pass with the IOperation test hook (`ROSLYN_TEST_IOPERATION=1`).
- The feature reached every layer of the pipeline, but most of the work was in places no grep for the feature name finds (see the map and the lessons below).
- Async iterators are unsupported for now (a clear error), and `dynamic` gets a warning instead of a runtime decision.

## Probe feature: `yield return ..xs`

The probe yields every element of a sequence from an iterator. It is small, it is not a breaking change (`..xs` is treated as a spread only where C# would report an error), so sharpening is skipped, and it touches almost every layer of the compiler.

## Map of Roslyn areas touched

In pipeline order. Paths are under `src/Compilers/CSharp/Portable` unless noted.

| Area | Files | What we did and what it taught us |
| --- | --- | --- |
| Gating | `Errors/MessageID.cs`, `Core/Portable/CommandLine/Feature.cs` | `IDS_FeatureSharper` mapped to the `sharper` flag. Flag names must also be registered in `Feature.cs`. Check through the syntax node, never `Compilation.IsFeatureEnabled`. |
| Parser | none | `..xs` already parses as a range with no left operand. It inherits range precedence: `..a + b` is `(..a) + b`. |
| Binder | `Binder/Binder_Statements.cs` | Bind as C# into a trial diagnostic bag; spread only if that reports errors. Enumerator lookup reused from foreach (`GetEnumeratorInfoAndInferCollectionElementType`, `isSpread: true`). Element conversion is implicit, unlike a foreach variable's cast. |
| Bound node | `BoundTree/BoundNodes.xml` + regenerated code | `BoundYieldReturnSpreadStatement`, shaped like `BoundForEachStatement`. `SkipInVisitor` on the placeholder fields, `DoesNotSurvive="LocalRewriting"`. |
| Flow analysis | `FlowAnalysis/AbstractFlowPass.cs`, `ControlFlowPass.cs`, `ExitPointsWalker.cs` | One read of xs plus one pending branch. Three places switch on `BoundKind.YieldReturnStatement` and needed the new kind. |
| Nullable | `FlowAnalysis/NullableWalker.cs` | Collection analyzed like foreach (CS8602), elements like a yield return (CS8603). Placeholder-based fields are checked, never visited. |
| IOperation | `Operations/CSharpOperationFactory.cs`, `BoundTree/Statement.cs` | `OperationKind.None` with xs as its child, the documented feature-branch approach. A real shape is an API decision. |
| Lowering | `Lowering/LocalRewriter/LocalRewriter_Yield.cs`, `_ForStatement.cs`, `_WhileStatement.cs` | Expanded with the shared foreach helpers into a loop of plain yield returns. The loop helpers have node-kind allowlists. |
| Diagnostics | `Errors/ErrorCode.cs`, `ErrorFacts.cs`, `CSharpResources.resx` + `.xlf` | CS20001 (asynchronous source) and CS20002 (dynamic). A checklist at the end of `ErrorCode.cs` lists every place to update. |
| Edit and Continue | `Lowering/IteratorRewriter/IteratorMethodToStateMachineRewriter.cs` (`PushFrame`), `Syntax/SyntaxBindingUtilities.cs`, `Core/Portable/Emit/EditAndContinue/DefinitionMap.cs`, `src/Features/CSharp/Portable/EditAndContinue/SyntaxComparer.cs` | States are mapped by syntax offset and relative ordinal (`AwaitDebugId`); the spread's finalize state takes ordinal 1 of the yield's syntax. In an async iterator the `try` has no state, and `BindsToTryStatement` accepts the spread's syntax. The IDE matcher gives the spread its own label, so it never maps to a plain yield return. |
| Tests | `Test/Semantic/Semantics/SharperTests.cs` | Diagnostics, runtime output, nullable, IOperation, flow graph, region analysis. |

## Known traps

Each trap was probed before deciding how to handle it.

| Trap | What the probe showed | How the PoC handles it |
| --- | --- | --- |
| Range ambiguity | `..i` is valid C# in iterators of `Range` and of `object` (boxed). | C# is tried first and wins; covered by tests for both element types. |
| Async iterator, sync source | Compiler crash: the async rewriter only accepts `try` blocks with certain syntax, for Edit and Continue. The crash was only that Debug assertion (`SyntaxBindingUtilities.BindsToTryStatement` in `AsyncExceptionHandlerRewriter`): a `try` whose `finally` does not await gets no state and no syntax-keyed locals in an async iterator. | Supported: `BindsToTryStatement` accepts the spread's yield syntax ([#49](https://github.com/ok3mad/roslyn/issues/49); see Decisions). |
| Async iterator, async source | Misleading "foreach statement cannot operate…" error; would need an implicit `await foreach`. | CS20001, which names the source and says why (an `await` that is not written). Never supported ([#43](https://github.com/ok3mad/roslyn/issues/43)). |
| Ref-struct sources (spans, inline arrays) | Rejected with CS4007 about `Span<int>.Enumerator`, which the user never wrote. Only the enumerator matters: a ref struct source with a class enumerator works. | Error CS20012 in the binder when the enumerator is a ref struct (or the source is an inline array), naming the source type. |
| `dynamic` | In an iterator of `object`, `..d` is valid C# (d converts to `Index` at runtime), so it stays a range and throws when d holds a sequence. In an iterator of `int` it spreads. | Warning CS20002. `(..d)` keeps the range without the warning; a cast to a sequence type spreads. |
| Both bindings fail (`..5`) | The spread error (CS9212) explains it better than the range error. | Spread error wins. |
| Edit and Continue | The generated `try`/`finally` and the yield return both had the yield's syntax and ordinal 0, so the next generation mapped the yield to the finalize state and crashed. Adding a spread (or a plain `foreach`) to an iterator without a finalize state also crashed: the PDB path started finalize states at 0. | The finalize state takes ordinal 1; `DefinitionMap` starts decreasing states as the in-memory path does. |

## Decisions

| Decision | Outcome | Why |
| --- | --- | --- |
| How Sharper is gated | Feature flag now; the `.cx` file extension later. | Flags live on each syntax tree's parse options, so binder checks stay the same. The extension switch is a per-file parse-options variant, like `.csx` scripts in `CSharpCompiler.cs`, plus per-document options in the IDE workspace. `.cx` is one letter from `.cs`, is used by no other .NET tool, and is unknown to GitHub Linguist (see [File extension](../README.md#file-extension)). |
| Range node or dedicated spread node | Reuse the range node. | No parser, `Syntax.xml` or public API change; C# precedence comes for free. |
| Spread versus range | Spread only where binding as C# reports an error (trial binding). | Matches the no-breaking-changes rule by construction; a hand-written rule missed the `object` case. |
| Element conversion | Implicit, like `yield return x`. | A foreach-style explicit cast would silently accept `..objects` in an `IEnumerable<int>` iterator. |
| Nullable warnings | CS8602 for a maybe-null source, CS8603 for maybe-null elements. | Same warnings as the C# the spread stands for; CS8619 was too generic. |
| IOperation | `OperationKind.None` for now. | A real shape is public API; wait until the syntax settles. |
| Async iterators | A synchronous source is supported ([#49](https://github.com/ok3mad/roslyn/issues/49)); an asynchronous source never is, and stays CS20001 ([#43](https://github.com/ok3mad/roslyn/issues/43)). A source that is both is enumerated synchronously. | A synchronous source is a legitimate spread and only needs lowering work. Spreading an `IAsyncEnumerable<T>` would insert an `await` the user never wrote, so there is no marker form either. The spread is a `foreach` without `await`, so "both" picks the synchronous enumerator, as `foreach` does; only when that lookup fails and the asynchronous one succeeds is it CS20001. |
| Spread lowering in async iterators | The same `foreach` lowering as in synchronous iterators; `SyntaxBindingUtilities.BindsToTryStatement` also accepts a `yield return ..xs` statement. | The async rewriter only asserts that a `try`'s syntax is one Edit and Continue was tested with. In an async iterator, a `try` whose `finally` does not await (a synchronous `Dispose` never does) becomes disposal-mode jumps: no finalize state and no locals keyed to the `try` syntax, so the yield keeps the only state on the node (ordinal 0) and the hoisted enumerator keeps its slot. The emit tests show both survive edits. Rejected: synthesizing a `ForEachStatementSyntax` (not in the tree, so no syntax offset for Edit and Continue), giving the `try` another node's syntax, such as the operand (the two iterator kinds would then differ, and the synchronous iterator's finalize-state mapping from [#45](https://github.com/ok3mad/roslyn/issues/45) relies on the yield's syntax), and dropping the `try`/`finally` (the enumerator would not be disposed). |
| `dynamic` | Keep the range meaning, warn (CS20002); `(..d)` is the silent range ([#42](https://github.com/ok3mad/roslyn/issues/42)). | A runtime decision (an inline `(Index)d` that falls back to a spread on `RuntimeBinderException`) changes the behavior of valid C#, and an error would be a break of a kind design rule 2 does not accept (it only accepts new keywords). The warning already names the mistake, and both fixes are one cast or one pair of parentheses. |
| val in patterns | A keyword in patterns, parsed like var (option 1). | Keeps val and var consistent. It breaks type patterns on a type named val, so the sharpener escapes those as @val. The alternative, reinterpreting a type pattern in the binder, kept C# meaning but diverged from var. |
| Sequence type stacking | Left to right, innermost first: each `[..]` wraps everything to its left (`int[..][]` is `IEnumerable<int>[]`); plain array ranks between two `[..]` keep the C# order. | The brainstorm's rule for all type suffixes (`int[..]~`, `int~[..]`), and the reading order of `T?[]`. The C# array order would make `int[..][]` a sequence of arrays. |
| Sequence type in a cast | `(A[..])` is a cast only before an identifier or a literal; `(int[..])` is always a cast. | `(a[..])(b)`, `(a[..])!`, `(a?[..]).b` and `(a[..]) with { }` are valid C# element accesses, which the usual "ambiguous type before `(`" rule would turn into casts. |
| `(val x) => ...` | An implicitly typed lambda parameter that cannot be reassigned, decided in the binder. | `val x` already parses as a parameter of type `val`; binding it like `var` (a type named `val` wins) keeps the C# meaning without a parser change. |
| Non-readonly members on a val struct | Run on a copy, with warning CS20003 (option C). | The foreach behavior let a method change the val, which breaks immutability. A silent copy (the readonly field rule) makes mutations disappear without notice. The warning makes the copy visible; member mutation inference will later silence it for members that do not mutate. |
| `->` as a lambda arrow | No: `->` is only the arrow of function types; lambdas keep `=>`. | `x -> F(x)` is already a pointer member access, parsed the same in and out of unsafe code (`ParsePostFixExpression`); a lambda meaning could only win after binding fails. |
| What a function type lowers to | `Func` or `Action` when they can express it. Otherwise (`ref`, `out`, `in`, `ref readonly` and `scoped` parameters, ref returns, more than 16 parameters) the anonymous delegate of a lambda's natural type, allowed wherever an internal type is; a member visible outside the assembly reports a Sharper error that says to declare a named delegate ([#56](https://github.com/ok3mad/roslyn/issues/56), decided, not built: errors for now). | The anonymous delegate is internal to one compilation, and nothing else gives one type across assemblies: a public copy per assembly makes distinct CLR types, reusing a referenced copy turns private uses into public API, NoPIA type equivalence needs a canonical assembly and excludes generics, and a delegate library needs 5ⁿ·4 types because ref kinds cannot be type arguments. The internal boundary is the one `infer` already has for anonymous types (CS0050), so locals only would leave a type that a private `infer` method can return but nobody can write. See the [feature page](../features/function-types.md#planned-ref-parameters). |
| Function type parameter names | Supported the way tuples carry their element names, in four steps under [#57](https://github.com/ok3mad/roslyn/issues/57): N1, names in the syntax (a Sharper `FunctionTypeParameterSyntax`), ignored by the binder except for duplicates (CS0100), done ([#212](https://github.com/ok3mad/roslyn/issues/212)); N2, names on the constructed `Func`/`Action` symbol within a compilation ([#213](https://github.com/ok3mad/roslyn/issues/213)); N3, signature help, Quick Info and inline hints ([#214](https://github.com/ok3mad/roslyn/issues/214)); N4, names in metadata through a synthesized, embedded attribute ([#215](https://github.com/ok3mad/roslyn/issues/215)). Names never change conversions or overload resolution, and until N4 they change nothing at all. Decided for N2 to N4: the names live on the `Func`/`Action` symbol as an analog of `TupleExtraData`, reusing the `IgnoreTupleNames` comparison bit; a lambda's natural type carries its names (Sharper only); overrides follow the tuple rules (CS8139, CS8141); no name-mismatch warning like CS8123; metadata uses an embedded `Sharper.FunctionParameterNamesAttribute`, not the runtime library, whose reader drops the names on a count mismatch; named arguments on a delegate call are allowed after N4, only with a function type's names. See the [feature page](../features/function-types.md#planned-parameter-names). | Names document what each argument means, as tuple names do. Documentation only (dropping them after parsing) gives the IDE nothing to show beyond the declaration, and loses them through `var`, generic arguments and references. `TupleElementNamesAttribute` cannot be reused: `TupleTypeDecoder` reports a type with the wrong number of names as unsupported, so plain C# consumers would break. Names stored only in the compilation would differ between the IDE (project references are source symbols) and the command line (metadata), which is why they stay meaningless until N4. |
| Implicit `it` scope | The innermost argument around `it` whose parameter, in the chosen overload, is a one-parameter delegate; a pipe stage is the outermost slot. Decided in the binder: the argument is bound as an implicit lambda first, and as a value only when no candidate takes a one-parameter delegate there. | Greeter's `() => profiles.Load(it.Id)` needs a type-directed rule: Scala's syntactic `_` rule would make `Load(x => x.Id)`. Converting a lambda to a parameter that is not a delegate fails without binding its body, so the fallback costs one more overload resolution, not a second binding. |
| Nested `it` | An error: one meaning of `it` per implicit lambda or pipe stage. An outer `it` plus a value and a function overload at the same argument is an error too. An inner pipe stage is not a second meaning: it owns every `it` in it, which the syntax shows (Feature 12). | Telling two `it`s apart needs every signature in between, and adding a function in between, or an overload in a library, would move the boundary silently. Allowing it later breaks nothing; banning it later would. |
| Where `ValKeyword` is a keyword | Only on tokens the parser makes, with Sharper on, for a val pattern. `GetContextualKeywordKind("val")` is `None`, and `GetContextualKeywordKinds()` does not list `ValKeyword`; `IsContextualKeyword(ValKeyword)` (and so `IsKeywordKind`) stays true. | Callers of the text-based and list members (Razor's tokenizer and escaping, and through `GetKeywordKinds()` the IDE's doc-comment `langword` completion) apply them to all code, so they must answer as upstream ([#109](https://github.com/ok3mad/roslyn/issues/109), design rule 2). The kind-based check only sees tokens that already are keywords: with Sharper off none exists, and the incremental parser (`Blender.Reader.IsFabricatedToken`) needs it so it re-parses a `ValKeyword` token instead of reusing it where `val` is an identifier. The upstream test of the list's range (`SyntaxTests.TestRangeBasedGetKindsMethodsReturnExpectedResults`) stops below `ValKeyword`. |
| What `val` is | Sugar: `readonly` plus the type inference of its position. `val x = e` is `readonly var x = e`, and `(val x) => ...` is `(readonly x) => ...`. Whether `val x` on a method parameter is `readonly infer x` is [#178](https://github.com/ok3mad/roslyn/issues/178). The messages keep naming what the user wrote ('val variable', 'readonly local'); whether the struct-copy warnings CS20003, CS20010 and CS20013 become one code is [#228](https://github.com/ok3mad/roslyn/issues/228). Decided in [#88](https://github.com/ok3mad/roslyn/issues/88). | The only fact is "cannot be reassigned"; how it was spelled is a question about syntax. One meaning keeps the tooling, Quick Info and the docs to one rule. |
| Sharper facts in the public API | Facts that are not a contract for other assemblies (`val`, `readonly` locals and parameters, inferred types) get no public symbol API: the IDE and analyzers read them through `SharperFacts` (`src/Workspaces/CSharp/Portable/Sharper/`), from syntax and the semantic model. Contracts for other assemblies (purity, immutability, refinements) are embedded attributes, which every consumer can read from metadata. A public symbol member is added only when a consumer outside this repository needs a fact that neither gives. Decided in [#88](https://github.com/ok3mad/roslyn/issues/88). | A member on `ILocalSymbol` or `IParameterSymbol` would be the fork's first change to Core's public interfaces, needs a VB implementation, and conflicts on every upstream merge, while every consumer in the repository can answer from syntax plus the semantic model (`GetSymbolInfo` on the `val` of `(val x)` tells the keyword from a type named `val`). The IDE layers have no `InternalsVisibleTo` from the compiler, and upstream keeps it that way. `readonly` and `infer` are rules about the body and are not emitted, so metadata consumers have nothing to read. |
| New syntax nodes | Allowed when no existing node can hold the tokens; numbered from `SyntaxKind` 9900 up. | `(A) -> R` fits no existing node. A high range keeps upstream merges from reusing the values, as CS20000 does for diagnostics. |
| Runtime library | Allowed, but only for a type that two assemblies must agree on and the BCL does not have. `Fail<T>` is the first. Behavior is still lowered or synthesized into each assembly, and facts for other assemblies are still embedded attributes. Not built yet (see [Follow-ups](#follow-ups) and the [overview](../README.md#runtime-library)). | A type the compiler copies into each assembly is a different type in each one, so its values cannot cross a public boundary; a sound design then needs one shared assembly. This replaces the earlier goal that Sharper code needs no library. Lowering, synthesis and embedding stay the default, because they add no dependency. |
| Anonymous unions in signatures | Private and internal members only. A member visible outside the assembly writes a named `union`, also where its type is inferred. | As for anonymous types in C#: a compiler-made type in a public signature would hinder C# callers. The spelling and the representation are still a parked proposal ([#31](https://github.com/ok3mad/roslyn/issues/31)). |
| Failures in lifted pipelines | Tagged: a result is `(Ok \| Fail<TFail>)`, not `(Ok \| Fail1 \| Fail2)`. Only the `Fail<...>` case passes through a stage. A stage that can fail adds its error to `TFail`, which is one flat set of errors, not one level per stage: `Find` then `Render` gives `(string \| Fail<(NotFound \| Unavailable)>)`. A `Fail<...>` is never a success value. | With bare cases, a generic `T` that is itself an error type cannot be told from a failure, and lifting has to guess which cases pass through. One level per stage, `((string \| Unavailable) \| NotFound)`, does not fix that, because a C# 15 union keeps one `object?` value and no discriminator. It also ties the type to the pipeline: splitting or reordering stages changes it, and two errors of one type swap their meaning silently. Considered: marking error types instead of values, which needs no library, but then an ordinary type cannot be a failure and a generic `T` cannot be instantiated with an error type. `Fail<T>` must be one type for every assembly, hence the runtime library. |

## Lessons for the next feature

A new construct's real cost is in the places that never mention it. Model it on the closest existing construct, then follow that construct everywhere it goes.

1. **Find the precedent's every mention, not just its visitors.** Grep the precedent's `Visit...` overrides and also its `BoundKind.X`: flow analysis, lowering helpers and `ExitPointsWalker` all had kind lists or switches that throw.
2. **A base-class default switches off a safety net.** Adding the override to `AbstractFlowPass` made `NullableWalker` silently inherit it instead of asserting. Only a nullable test caught the gap.
3. **Placeholder fields are checked, never visited.** `SkipInVisitor` fields are templates; `NullableWalker.DebugVerifier` asserts if their nullability is recorded.
4. **If you report an error, set `hasErrors`.** Later passes rely on the flag; a reported but unflagged conversion error crashed nullable analysis.
5. **Use the debug safety nets on purpose.** `DoesNotSurvive`, `AbstractFlowPass.DefaultVisit`, `PipelinePhaseValidator`, `DebugVerifier` and pool-leak tracking each caught a mistake. Run tests in Debug.
6. **Write tests before deciding.** Probe first, read the actual diagnostics, then paste them as expectations. Add a plain-C# twin next to each Sharper line to compare against.
7. **Mind the test environment.** Tests default to an old framework without `System.Range`; use `TargetFramework.Net90` and `ExecutionConditionUtil.IsCoreClr` for runtime output.
8. **Turn on `ROSLYN_TEST_IOPERATION=1`.** It makes every test also validate IOperation, the semantic model and the control flow graph.
9. **Follow-ups are `// PROTOTYPE(sharper):` comments.** Plain TODOs are banned; `eng/todo-check.ps1` blocks merging PROTOTYPE comments into `main`.

## Syntax roadmap

The brainstorm proposes 26 pieces of syntax, and row 28, discard arguments, was proposed later. Rows 14 and 26, the parameter switch body and its lambda form, and row 28 were rejected (see [Dropped](brainstorm.md#dropped)), and the other rows keep their numbers. Each row has a GitHub issue, collected under the tracking issue [#12](https://github.com/ok3mad/roslyn/issues/12). Nine are done: `yield return ..xs`, `val` in locals, deconstruction, patterns and lambda parameters, `readonly` locals and parameters, the sequence type `T[..]` and function types `(A, B) -> R`. Everything that depends on unions or purity waits for those designs.

- **Size**, relative to the `yield return ..xs` PoC. **S**: about that size (no parser change, one bound node, reused lowering). **M**: a gated parser change, or a new lowering with its own rules. **L**: new inference or binding rules that interact with overload resolution or signature binding. **XL**: a new checker layer (refinements, unions, effects).
- **Certainty**. **High**: decided in the brainstorm, spelling stable. **Med**: the leading direction, details or spelling open. **Low**: only sketched in examples, contradicted elsewhere, or blocked on an open question.

### Phase 1: inference, immutability and short type forms

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Spread in iterators | `yield return ..xs` | `foreach` of yield returns | none | S | Done | Follow-ups below: async iterators, `dynamic`, Edit and Continue | [#13](https://github.com/ok3mad/roslyn/issues/13) |
| 2 | `val` immutable local | `val total = 0;` | `var` plus a no-reassignment check | none (`val` parses as a type name) | S | Done | The only breaking keyword; trial binding plus a sharpener `@val` escape | [#14](https://github.com/ok3mad/roslyn/issues/14) |
| 3 | `val` deconstruction | `val (small, large) = ...;` | deconstruction plus the same check | small: accept `val` wherever the parser checks for `var` (`IsPossibleDeconstructionLeft`, statement disambiguation, `IsVarType`) | S | Done | Comes with #2. Without the parser change, `val (a, b) = e` parses as an invocation; the same checks cover `foreach (val (a, b) in ...)` and `out val (a, b)` | [#15](https://github.com/ok3mad/roslyn/issues/15) |
| 4 | `readonly` locals | `readonly int x`, `readonly var x` | local plus the same check | small: the modifier already parses, with CS0106; Sharper drops that error for variable declarations | S | Done | Completes the mutability grid. `for`, `foreach`, deconstruction and `out` left out (no modifier slot; `val` covers them) | [#16](https://github.com/ok3mad/roslyn/issues/16) |
| 5 | `val` lambda parameter | `(val x) => ...` | lambda plus the same check | none (`val x` already parses as a typed parameter) | S | Done | A type named `val` keeps the C# meaning; anonymous methods and local functions are out of scope (see [step 4](#feature-2-step-4-val-lambda-parameters)) | [#17](https://github.com/ok3mad/roslyn/issues/17) |
| 6 | `val` in patterns | `is { Length: val n }` | `var n` pattern plus the same check | yes: in a pattern the parser turns `val` into a keyword, as it does `var` (`LanguageParser_Patterns.cs`) | S | Done | Decided: `val` is always a keyword in patterns, like `var`. Breaks a type pattern on a type named `val`, so the sharpener escapes it as `@val`. | [#18](https://github.com/ok3mad/roslyn/issues/18) |
| 7 | Sequence type | `int[..]` | `IEnumerable<int>` | small: `ScanType` accepts `[..]`; `ScanCast` keeps `(a[..])` an element access | S | Done | `new T[..]` keeps its C# meaning ([#64](https://github.com/ok3mad/roslyn/issues/64)); accepted break: `F<a[..]>(x)` is a generic call ([#66](https://github.com/ok3mad/roslyn/issues/66)). See [Feature 3](#feature-3-sequence-type-t) | [#19](https://github.com/ok3mad/roslyn/issues/19) |
| 8 | Task tilde | `T~`, `void~`, `int[..]~` | `Task<T>`, `Task` | yes (a postfix `~` after a type, next to `?` and `*`; `~` has no binary meaning, so no disambiguation) | S–M | Prototype | Spelling `~` chosen over the hat `^` (XOR is valid in tuples, `is` and `as`). Confirmed on six applications; `ValueTask` stays unsugared (0.4% of their async declarations). A new syntax node, `TaskTypeSyntax`; see the [feature page](../features/task-type.md) | [#20](https://github.com/ok3mad/roslyn/issues/20) |
| 9 | Function types | `(A, B) -> R`, `() -> T` | `Func<...>`, `Action<...>` | yes: new syntax nodes, `FunctionTypeSyntax` and `ParenthesizedTypeSyntax` (`((F))?`, done in [#58](https://github.com/ok3mad/roslyn/issues/58)), a one-pass tuple-or-function lookahead, and function types in patterns (done in [#59](https://github.com/ok3mad/roslyn/issues/59)) | M | Prototype | Decided: `->` only in types, lambdas keep `=>`. Follow-ups: `ref` parameters as the internal anonymous delegate [#56](https://github.com/ok3mad/roslyn/issues/56) (decided), parameter names [#57](https://github.com/ok3mad/roslyn/issues/57) in steps N1 (names in the syntax, done) to N4 (see Decisions) | [#21](https://github.com/ok3mad/roslyn/issues/21) |
| 10 | Inferred return type | `static infer Hello(int id) => ...` | method with the inferred return type | none (`infer` parses as a type name) | L, in steps R1 to R5 | Med; R1 prototype | Ordering and cycles. Allowed on public members too, with no warning. Replaces the expression-bodied `fun`. R1 (expression bodies of private methods and local functions) is a prototype; see [Feature 5](#feature-6-inferred-return-type-step-r1) and the [feature page](../features/inferred-return-type.md) | [#22](https://github.com/ok3mad/roslyn/issues/22) |
| 11 | `readonly` parameters | `void F(readonly int x)` | plain parameter plus the `val` no-reassignment check | none (`readonly` already parses as a parameter modifier; C#'s error without `ref`, CS9190, comes from the binder) | S | Prototype | Completes the mutability grid with #4 and #5. Not part of the signature; `readonly ref` forbids ref reassignment, as for locals. Replaces the `mutable` parameter in `fun`: immutable-by-default parameters and a `mutable` marker are a strictness ladder setting now, not syntax. See [Feature 5](#feature-5-readonly-parameters) | [#23](https://github.com/ok3mad/roslyn/issues/23), [#97](https://github.com/ok3mad/roslyn/issues/97) |
| 15 | Inferred parameter types | `public infer Load(infer userId) => ...`, `infer timeout = TimeSpan.FromSeconds(5)` | method with the inferred types | none (`infer x` parses as a typed parameter) | S to XL, in steps P1 to P8 | Med | Copied from an overridden member, a default value, a function type or a call that pins one type, never from shape. Nothing pinning it is an error, not a generic method (#79). Replaces untyped `fun` parameters inferred from arms | [#27](https://github.com/ok3mad/roslyn/issues/27) |
| 29 | Immutable types | `[Immutable] sealed record Money(decimal Amount, string Currency);` | the type plus embedded attributes | none (attributes first, a keyword later) | M | Med; slice 1 prototype | Deep and observational; inferred only from representational immutability plus pure observers. `[Immutable]` describes exact instances, `[ImmutableHierarchy]` opts in for subclasses, and type arguments are judged statically. Not a brainstorm syntax row: it answers the open question on immutability depth ([#92](https://github.com/ok3mad/roslyn/issues/92)). See [Feature 10](#feature-10-immutable-types-slice-1-design) | [#139](https://github.com/ok3mad/roslyn/issues/139) |

### Phase 2: functional core

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 12 | Implicit `it` | `xs.Select(Process(it, a))`, `when: it is X` | lambda `x => ...` | none | L | Prototype | Decided: the innermost argument whose parameter is a one-parameter delegate becomes the lambda; one meaning of `it` per lambda or stage; an outer `it` plus value and function overloads is an error; an existing `it` keeps its C# meaning (see [Decided](brainstorm.md#decided)). Calls only so far; `LookupSymbols` and Edit and Continue do not see the lambda yet. See the [feature page](../features/implicit-it.md) | [#24](https://github.com/ok3mad/roslyn/issues/24) |
| 13 | Pipe, without lifting | `x \|> F`, `x \|> F(it, y)` | nested call (a spill sequence with one local) | yes (`\|>` merged from `\|` and `>`, a new token kind and node) | M | Prototype | Decided: a stage with a free `it` is an expression, any other stage a function applied to the value; left-associative between `??` and `?:`; the left side first; a stage is its own scope. See [Feature 12](#feature-12-pipe-without-lifting) and the [feature page](../features/pipe.md) | [#25](https://github.com/ok3mad/roslyn/issues/25) |
| 16 | `try` expression | `try await F(url) catch { HttpRequestException { ... } => ..., }` | try/catch with the arms in a catch filter, with spilling | yes (`try` in expression position) | L (low end) | Med; slice 1 prototype | Slice 1: target-typed to a declared C# 15 union, or the best common type of the body and arms; anonymous union results wait for #19. Unmatched exceptions propagate untouched through the filter; no `finally`; an awaitable body must be awaited. See [Feature 8](#feature-8-try-expression-slice-1-design) | [#28](https://github.com/ok3mad/roslyn/issues/28) |
| 17 | Lambda to single-method interface | `Render(user, id => new Profile())` | generated sealed class | none | M | Med | Only where C# reports an error today; the class needs a readable name for stack traces | [#29](https://github.com/ok3mad/roslyn/issues/29) |
| 18 | Interface initializer | `new IProfiles { Load = id => ... }`, `_ = Unexpected` | generated class | none (binder only) | M | Med | Builds on #17 | [#30](https://github.com/ok3mad/roslyn/issues/30) |

### Phase 3 and later: type system and effects

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 19 | Inferred or structural unions | `(Profile \| Fail<Unavailable>)~` | not decided | yes | XL | Low | Decided: private and internal members only, with a named `union` on visible members, and failures tagged with `Fail<T>` (see [Decisions](#decisions)). The spelling and the CLR representation are a parked proposal in the issue | [#31](https://github.com/ok3mad/roslyn/issues/31) |
| 20 | Lifting in pipes and arguments | `users.Find(id) \|> Render(it, p)` | await, `Select`, or passing a `Fail<...>` case through | none | XL | Low | Decided: failures are tagged with `Fail<T>` from the runtime library, and only that case passes through (see [Decisions](#decisions)). Open: implicit versus an explicit marker; breaks down at generic and `object` parameters | [#32](https://github.com/ok3mad/roslyn/issues/32) |
| 21 | Refined types and members | `decimal Amount is >0`, `int is >=0` | plain type plus boundary checks | yes (pattern in a type position) | XL | Low–Med | Proof obligations at call sites; runtime guards for C# callers | [#33](https://github.com/ok3mad/roslyn/issues/33) |
| 22 | Opaque patterns | `is > 0 and <= max` | an opaque test in the decision DAG | none (relational patterns already parse any expression) | M, plus purity | Med; slice 1 prototyped on `sharper/opaque-patterns` | Decided: any pure non-constant operand of a relational or constant pattern, in every pattern context except refinements; pure is enough, as for `Deconstruct` in C#. See [Feature 13](#feature-13-patterns-on-pure-functions-design) | [#34](https://github.com/ok3mad/roslyn/issues/34) |
| 23 | Extractor patterns | `s is Parse(val n and >0)` | call plus a sub-pattern, an evaluation in the decision DAG | none | M–L | Low; designed | Decided: a method only where type lookup fails (a type named `Parse` wins); the predicate form first, then `bool TryX(T, out U)`, then `U F(T)`, unions last. See [Feature 13](#feature-13-patterns-on-pure-functions-design) | [#35](https://github.com/ok3mad/roslyn/issues/35) |
| 24 | `pure` and `total` modifiers | `total int Fib(...)`, `pure(provider)` | erased, plus attributes | small | XL (syntax alone S) | Low | Needs the whole effect checker and bindings for existing .NET code | [#36](https://github.com/ok3mad/roslyn/issues/36) |
| 25 | Member declared on another type | `static infer Currency.Parse(string s)` | C# 14 extension block | yes | M | Deferred | C# 14 `extension(Currency) { ... }` blocks already cover it, with `infer` and `[Pure]` on their members, so the shorthand only saves a wrapper class. If revived: file level only, where the dotted form is never valid C# (CS0116 or CS9348, plus CS0538); inside a type it stays an explicit interface implementation. See [Parked](brainstorm.md#parked) | [#37](https://github.com/ok3mad/roslyn/issues/37) |
| 27 | `assume` escape hatch | `assume x is >0` | runtime check | yes | S–M | Low | Only an open question so far | [#39](https://github.com/ok3mad/roslyn/issues/39) |

Not listed: `union Name(A, B)` comes from C# 15, and `collect`, `Retry` and `Unexpected` are library code. `Fail<T>`, which rows 19 and 20 rely on, is in the [runtime library](../README.md#runtime-library); whether those three go there too is open (see [Follow-ups](#follow-ups)).

### Suggested order

1. `val` (#2, #3, #6), in three steps: `val x = e` locals (binder only), then deconstruction and designations (parser), then patterns (keyword). It exercises the gated parser, the first deliberate break with its `@val` escape, and new semantics on an existing symbol, none of which the first PoC touched. The no-reassignment check reuses the read-only local check behind CS1656.
2. `T[..]` (#7), done. Its binder rewrite onto a BCL type is the pattern for the other short type forms (see [Feature 3](#feature-3-sequence-type-t)).
3. `T~` (#8) and function types (#9), done.
4. `readonly` parameters (#11), done.
5. Type inference (#10 and #15), the first real inference work, in the steps of the
   [work breakdown](#type-inference-work-breakdown).
6. Immutable types (#29), slice 1 (prototype, [#139](https://github.com/ok3mad/roslyn/issues/139)), which purity (#24) and the patterns that need it (#22, #23) build on. Of those patterns, opaque patterns ship first, then the predicate form, then extractors ([Feature 13](#feature-13-patterns-on-pure-functions-design)).
7. Rows 19 onward wait for the union representation and the purity design.

Decided before the type-syntax rows: `->` is not a lambda arrow, only the arrow of function types (see Decisions).

Rows 10, 11 and 15 used to be parts of one `fun` declaration, and rows 24 and 25 used its keyword. They are separate features on ordinary methods now, and each can be built alone; see [Functions as separate elements](brainstorm.md#functions-as-separate-elements). `fun` itself is parked for a later "functional all in" mode, whose meaning is decided once these pieces have settled.

### Type inference work breakdown

Rows 10 and 15 are split into steps that each fit one branch and one PR, like the three `val` steps. The design is in
the brainstorm: [`infer`](brainstorm.md#infer-the-type-is-inferred),
[Inferred return type](brainstorm.md#inferred-return-type) and
[Inferred parameter types](brainstorm.md#inferred-parameter-types), whose sources 1 to 5 the parameter steps refer to.
Every step covers binding, the semantic model (the declared symbol shows the inferred type), IOperation, nullable
analysis of the inferred type, and tests next to the C# with the type written out.

Return type (#10):

| Step | Scope | Size | Open point |
| --- | --- | --- | --- |
| R1 | `infer` as a return type: gating, a type named `infer` wins, CS0246 with the flag off. Expression bodies of private methods and local functions that call only methods with written return types | S–M | Prototype ([#67](https://github.com/ok3mad/roslyn/issues/67)). Decided: nothing to infer from is CS20014, where `var` reports CS0815, CS8917 or CS8716 |
| R2 | Block bodies: several `return` statements with the best common type, no return value as `void`, iterators, `async` | M | Prototype ([#68](https://github.com/ok3mad/roslyn/issues/68)). Decided: one maybe-null `return` makes the type annotated, as for a lambda. Async iterators wait |
| R3 | Methods with inferred return types calling each other: binding order across members, cycle and recursion errors | L | Prototype ([#69](https://github.com/ok3mad/roslyn/issues/69)). Decided: a recursive method with a non-recursive `return` stays CS20018; a cycle is CS20033 on every member, naming it |
| R4 | Every member kind and accessibility: public and protected members, `virtual`, `partial`, operators, metadata emission, Edit and Continue (a body edit that changes the type is a signature change) | M | Prototype ([#70](https://github.com/ok3mad/roslyn/issues/70)). Decided: public members infer like any other; overrides and implementations copy (P2) and never infer from their body; properties and indexers wait for a step with [#166](https://github.com/ok3mad/roslyn/issues/166) |
| R5 | IDE: Quick Info and inlay hints show the inferred type, a fixer writes it out | M | Prototype ([#71](https://github.com/ok3mad/roslyn/issues/71)). Decided: no compiler API; the IDE reads the types from the semantic model, and the fix for CS20027 rebuilds the error's list of types from its additional locations. "Use 'infer'" is a refactoring on every accessibility, checked by binding the document with `infer` in place. See [IDE](../features/inferred-return-type.md#ide-step-r5) |

Parameter types (#15):

| Step | Scope | Size | Open point |
| --- | --- | --- | --- |
| P1 | `infer` as a parameter type, with source 2: the default value's type. `null`, `default` and target-typed values give an error | S | Prototype ([#72](https://github.com/ok3mad/roslyn/issues/72)). Decided: every member kind and lambdas; a value that is not a constant gives its type, then CS1736 as in C#; cycles through signatures are CS20028 |
| P2 | Source 1: the overridden or implemented member gives the parameter and return types. Members are matched by name and parameter count before their types are known; more than one candidate is an error | M | Prototype ([#73](https://github.com/ok3mad/roslyn/issues/73)). Decided: explicit implementations copy from the interface they name; several interfaces with different signatures are CS20044; written types narrow the candidates |
| P3 | `readonly infer x` | S | Prototype ([#74](https://github.com/ok3mad/roslyn/issues/74)). Decided: `readonly` combines with P1 and P2 with no new code; whether `val x` is its shorthand on method parameters is [#178](https://github.com/ok3mad/roslyn/issues/178), not built |
| P4 | Source 4, first cut: the parameter passed directly as an argument to a call with exactly one candidate (method, constructor, delegate). Needs binding the body with a placeholder type | L | Prototype ([#75](https://github.com/ok3mad/roslyn/issues/75)). Decided: uses in lambdas and local functions in the body count, read from the final binding only; recursion binds to the placeholder and is no cycle; disagreeing uses are CS20057; CS20027 says why nothing pinned |
| P5 | Source 4 with overloads: every applicable candidate takes the same type at that position, including generic methods whose type arguments come from other arguments | L | Prototype ([#76](https://github.com/ok3mad/roslyn/issues/76)). Decided: applicable means applicable with the parameter in place, the other arguments bound and converted first; the two forms of `params` are two candidates; a group with one member keeps P4's rule; disagreeing candidates are listed in CS20027 |
| P6 | Source 4, other pinning uses: assignment to a typed local, field or property, `return x` from a method with a written return type, `ref` and `out` arguments. Several uses must agree, and the error names each one | M | Prototype ([#77](https://github.com/ok3mad/roslyn/issues/77)). Decided: `ref`, `out` and `in` arguments pin the type of a parameter that takes their ref kind; a return pins only a written return type (a lambda's delegate type can come from error recovery); higher sources still win, and a use that disagrees with one reports the C# error of the twin, which names both types; CS20057 keeps its shape and names assignment targets and returns |
| P7 | Source 3: a function type the method is converted to, local functions first, then private methods | L | Prototype for local functions ([#78](https://github.com/ok3mad/roslyn/issues/78)). Decided: every conversion in the declaring body counts, also after the declaration; a conversion inside a generic call counts once the delegate's input types are fixed (`xs.Select(Format)`); conversions that disagree, also the overloads of one call, are CS20057. Prototype for private methods ([#207](https://github.com/ok3mad/roslyn/issues/207), step P7b): every body of the type that names the method without invoking it counts; a stand-in replaces every such private method in those bindings; a cycle through another member's inferred type is CS20063 |
| P8 | Source 5: nothing pins the parameter. Decided an error, not a generic method: CS20027 says why nothing pinned (for example "member access pins nothing") and asks for the type. The rewording is part of P4; the IDE fix that writes a type or makes the method generic is part of R5 | S | Decided ([#79](https://github.com/ok3mad/roslyn/issues/79)): a silent generic would change the arity when a pin appears or disappears, a source and binary break, and arity is fixed before any binding. Generic for local functions only may be reconsidered after P6 |

Suggested order: R1, P1, P2, P3, R2, R3, P4, P5, P6, P7, R4, R5. P8 needs no step of its own: P4 rewords CS20027 and R5 adds the fix. P4 to P6 are the
expensive core; everything before them needs no change to how bodies are bound.

## Feature 2, step 1: `val` locals

Step 1 makes `val x = e;` a `var` local that cannot be reassigned, on branch `sharper/val` rebased on `main`, which now includes `sharper/gating` through PR #1. No parser change: `val` already parses as a type name, and one binder chokepoint decides whether `var` is a type or the keyword.

### Scope and semantics

In scope: `val` in local declaration statements, including top-level statements, `using val x = ...` and a `for` initializer. Out of scope until steps 2 and 3: deconstruction, `out val`, `foreach (val ...)` and patterns. Out of scope for now: fields, `readonly` locals and `(val x) =>` lambda parameters.

| Question | Proposal | Status |
| --- | --- | --- |
| Meaning | `var` inference, plus the local is not a writable variable (`IsWritableVariable` false, as for foreach and using locals) | Decided |
| Writes | Reuse CS1656 and CS1657 with a new cause: "Cannot assign to 'x' because it is a 'val variable'". `=`, `+=`, `++`, `ref`/`out` arguments and writes from lambdas all go through the same check | Decided |
| Struct values | A struct's fields are part of the variable, so assigning one is an error (CS1654). A non-readonly member (method, getter, indexer, `GetEnumerator`, `Deconstruct`, event accessor) runs on a copy and warns (CS20003), also when the val is captured or hoisted. Readonly members, readonly structs and `ref val` are unaffected | Decided |
| A type named `val` in scope | The type wins, exactly as for `var`; no warning yet | Decided |
| Initializer, several declarators, `null`, `const` | Same errors as `var` (CS0818, CS0819, CS0815, CS0822) | Decided |
| `ref val x = ref y` | Allowed, with no extra code: writes through the ref compile, and `r = ref j` is CS1656 "because it is a 'val variable'" | Decided |
| `scoped val` | Allowed: `scoped val s = span;` is a scoped local that cannot be reassigned | Decided |
| Feature flag off | Unchanged C#: CS0246 for an unknown type `val` | Decided |
| Public API and IDE | No new API (`ILocalSymbol` has no `IsReadOnly`); keyword classification and Quick Info become `PROTOTYPE(sharper)` comments | Deferred |

### Work breakdown

One commit per row, tests first. Paths are under `src/Compilers/CSharp/Portable`.

| # | Work | Where | Done when |
| --- | --- | --- | --- |
| 1 | Probe tests with empty expectations: runtime output, every kind of write, struct fields and methods, a type named `val`, flag off, `ref val`, `scoped val`, `using`, `for`, top-level statements, each next to its `var` twin | `Test/Semantic/Semantics/SharperTests.cs` | Tests compile and the actual diagnostics are recorded |
| 2 | Recognize `val` as the keyword: a gated `IsVal` check next to `IsVar` in `BindTypeOrAliasOrVarKeyword`, so lookup still prefers a type named `val` | `Binder/Binder_Symbols.cs`, `Syntax/` helper | `val x = 1` infers `int`; a type named `val` still wins |
| 3 | Make the local read-only: `IsVar` and a new `IsVal` on the local symbol, `IsWritableVariable` false for `val` | `Symbols/Source/SourceLocalSymbol.cs` | Writes are errors, reads compile and run |
| 4 | The diagnostic cause "val variable" for CS1656, CS1657 and CS1654 | `Binder/Binder.ValueChecks.cs`, `Errors/MessageID.cs`, `CSharpResources.resx` + `.xlf` | Messages name the val variable, not the generic "left-hand side" error |
| 5 | Semantic model, IOperation, nullable: tests only, no change expected. `GetSymbolInfo` and `GetTypeInfo` on `val`, the declarator operation, nullable inference as for `var` | `SharperTests.cs` | Tests also pass with `ROSLYN_TEST_IOPERATION=1` |
| 6 | Decide `ref val` and `scoped val` from the probe; allow them or report an error | Binder | Covered by tests |
| 7 | Regression run of the C# compiler tests, then update this doc and the sharper-feature skill with what step 1 taught | `.github/skills/sharper-feature/SKILL.md` | No failures; lessons recorded |

Progress: rows 1 to 6 are done and pushed to `sharper/val` (commits `eae7a3b0` and `5a0bdb9d`). The compiler change is about a dozen lines plus one message string: `val` joins `var` at the binder chokepoint (`BindTypeOrAliasOrVarKeyword`), and `SourceLocalSymbol.IsWritableVariable` is false for it. All 40 Sharper tests pass, also with `ROSLYN_TEST_IOPERATION=1`. Struct members on a copy with CS20003: commit 7d38072c, regression run in progress. Row 7 is done: the C# compiler regression run (net10.0, Debug) passed 88,340 tests with 0 failures across 10 projects, and the skill records the lessons below.

### What step 1 taught

1. **A contextual keyword gets "C# wins" for free.** Routed through the same type-or-keyword lookup as `var`, a type named `val` still binds as a type, with no trial binding.
2. **The keyword's spelling is checked far from its binder.** `var` is tested by spelling in the parser, `SourceLocalSymbol`, the foreach binder, the semantic model and script fields. Steps 2 and 3 are mostly about those places.
3. **Look for an existing flag before writing an analysis.** Read-only locals already existed for foreach, using and fixed variables, so one writability flag and one message string covered every kind of write.
4. **Probe after the smallest binding.** Before any binding, every probe just reports "type not found".

## Feature 2, step 2: `val` in deconstruction, foreach and `out`

Merged as #4. The first Sharper parser change: one helper (`IsIdentifierVarOrSharperVal`) accepts `val` in the three places the parser checked the spelling `var` (deconstruction declarations, statement disambiguation, tuple designations); `@val` stays an identifier. `val (a, b) = e`, `(val a, var b) = e`, `foreach (val x in xs)`, `foreach (val (k, v) in pairs)` and `out val n` infer like `var` and cannot be reassigned. A type named `val` keeps its C# meaning, with the error `var` gets in the same position (CS8136). Accepted break: `val (a, b) = e` is a declaration even where a ref-returning method named `val` makes it an assignment in C#. Regression run: 88,356 passed, 0 failed.

## Feature 2, step 3: `val` in patterns

`val` followed by a designation in a pattern (`is val x`, `case val x:`, `val x =>`, `val (a, b)`, `(val a, var b)`, `{ P: val p }`) is a var pattern whose keyword token is `SyntaxKind.ValKeyword` (first a `VarKeyword` token with the text `val`; replaced so code that rebuilds a token from its kind keeps `val`). `VarPatternSyntax` is reused, so no syntax node is added. Its variables cannot be reassigned. As for `var`, `val` is a keyword in patterns even where a type named `val` exists; that surfaces as the existing error CS8508 rather than a change of meaning. `is val` without a designation and `is @val x` keep their C# meaning.

`val` is not a contextual keyword of the lexer or of `SyntaxFacts` ([#109](https://github.com/ok3mad/roslyn/issues/109)). It first was, with or without Sharper: `GetContextualKeywordKind("val")` returned `ValKeyword`, `GetContextualKeywordKinds()` listed it, and every identifier spelled `val` had it as its contextual kind. Razor treats any identifier with a contextual kind as a keyword, so `@val!.Name` and `@val?.Name` ended the implicit expression at `val` in ordinary pages (69 Razor tests). Now the lexer leaves `val` an identifier, as upstream does; the parser checks the spelling (`IsSharperValIdentifier`, behind the flag in `IsIdentifierVarOrSharperVal`) and makes the pattern's `ValKeyword` token itself (`ConvertToSharperValKeyword`, which keeps the trivia and diagnostics). `val (a, b) = e` and `val x = e` keep an identifier `val` as their type syntax, so they need no keyword token.

## Feature 2, step 4: `val` lambda parameters

`(val x) => ...` declares an implicitly typed lambda parameter that cannot be reassigned (issue #17). No parser change:
with the flag on or off, `val x` parses as a parameter whose type is the identifier `val`, in every lambda form. The
binder (`GetSharperValLambdaParameters` in `Binder_Lambda.cs`) looks `val` up as `var` locals do: if no type named `val`
binds, the parameter counts as implicitly typed, so it is inferred from the target delegate like `(x) => ...`, and
`LambdaParameterSymbol.IsSharperVal` makes it read-only. Writes go through the shared value check
(`CheckParameterValueKind`), and struct copies through `IsSharperValValue`, so a val parameter reuses every rule of a val
local.

| Question | Decision |
| --- | --- |
| A type named `val` in scope | C# wins: `(val x)` is an explicitly typed parameter of that type, as without the flag. |
| Writes | CS1656 and CS1657 with the 'val variable' cause, CS1654 for struct fields, also from nested lambdas. |
| Struct members | A non-readonly member runs on a copy and warns (CS20003), also when captured, after `await` and in patterns. |
| `(val a, b)` | Allowed: both are implicitly typed. |
| `(val a, int b)`, `(int a, val b)` | CS0748, as for `(a, int b)`: C# does not mix implicit and explicit parameters. |
| `async`, `static`, an explicit return type (`long (val x) => ...`), expression trees | Allowed, as for `(x) => ...`. |
| `var f = (val x) => x` | CS8917, as for `(x) => x`: a natural type needs explicit parameter types. |
| `(ref val x)`, `(out val x)` | Allowed: writes through the ref work. `x = ref y` on a `ref val` parameter is CS20006, as for a `ref val` local. |
| `(in val x)` | Allowed; writes are CS8331, as for `(in x)`. |
| `(scoped val x)` | Allowed: a scoped parameter that cannot be reassigned. |
| `(params val x)` | CS9272, as for `(params x)`. |
| `(val x = 1)` | CS9098, as for `(x = 1)`: implicitly typed parameters cannot have default values. |
| `val x => x` (no parentheses) | An error, as C# requires parentheses for parameter modifiers. With the flag, the parser reports CS20034, which suggests `(val x) => x`, and recovers as that form with missing parentheses (`IsSharperValSimpleLambdaStart` in `LanguageParser.cs`), so the body binds with no further errors ([#98](https://github.com/ok3mad/roslyn/issues/98)). Without the flag, the C# errors stay. |
| `delegate (val x) { }`, `void Local(val x)` | Out of scope: their parameters are always explicitly typed, so `val` is a type name there (CS0246), as in C#. |
| Semantic model and IOperation | The parameter is an ordinary `IParameterSymbol`; `GetTypeInfo` and `GetSymbolInfo` on `val` give the inferred type, as for `var`. IOperation is unchanged. |

Implicitly typed lambda parameters were detected by their missing type syntax (`p.Type == null`) in three places, all
of which now also count a val parameter: the explicit-or-implicit check (`CheckParenthesizedLambdaParameters`), the
explicitly typed parameter list in `AnalyzeAnonymousFunction`, and the `params` check in
`ParameterHelpers.CheckParameterModifiers`, which `Binder_Lambda.cs` repeats for val.

Tests: 14 new Sharper tests (91 in all) pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug) of
the C# Semantic, Symbol, IOperation, Syntax and Emit test projects: 54,449 passed, 0 failed.

## Feature 3: sequence type `T[..]`

`T[..]` is an alias for `IEnumerable<T>` in every type position, on branch `sharper/sequence-type`
([feature page](../features/sequence-type.md)). It adds no syntax node, bound node or public API, and needs no
lowering: the binder resolves the syntax to the constructed BCL type, so everything after binding sees
`IEnumerable<T>`.

### Rewriting a type form onto a BCL type

This is the pattern for Sharper's short type forms. Function types (#21) were built in parallel and follow it with a new syntax node: `BindFunctionType` constructs `Func` or `Action` with the same `BindSharperTypeAlias` call. The task type (#20) follows it too, with the syntax node `TaskTypeSyntax` and `BindSharperTaskType`.

1. **Syntax.** Reuse a C# node that already parses the form, and give the Sharper reading one gated predicate on
   it. `T[..]` is an `ArrayTypeSyntax` whose rank holds a range with no operands; `ArrayRankSpecifierSyntax.IsSharperSequence`
   decides, and every other place (binder, semantic model, local declarations) asks it. A form with no C# parse (the task
   suffix, `->` in a type) needs a new node instead, and with it public API (see `FunctionTypeSyntax`).
2. **Parser.** `ParseType` usually parses the form already, but statements, casts, generic arguments in
   expressions and tuple elements first scan with `ScanType`, which must accept the form too. Then check every
   caller that turns a scanned type into a decision (`ScanCast`, `ScanPossibleTypeArgumentList`) for valid C#
   expressions that would change meaning, and do not return `ScanTypeFlags.MustBeType` for a form that is also an
   expression.
3. **Binder.** Bind the type arguments as usual, get the generic definition with `GetSpecialType` or
   `GetWellKnownType` (they report a missing type), then call `BindSharperTypeAlias` (`Binder_SharperTypes.cs`). It
   constructs the type and checks constraints now, or leaves them to the declaring symbol where the binder suppresses
   checks (signatures), exactly as for the type written out. Pass it one location per type argument, the part of the
   form that stands for it (a function type's parameter and return types, the element of `T[..]` or `T~`), so that a
   constraint error lands where the C# form reports it. Set the nullable annotation from the form's last token, as
   the C# form would.
4. **Places that read the syntax directly.** Local declarations bind array sizes as "invalid dimensions" for the
   semantic model (`Binder_Statements.cs`), and the semantic model would bind the `..` as `Range.All`
   (`CSharpSemanticModel.CanGetSemanticInfo`). Both skip a sequence rank.
5. **Tests.** Compare each Sharper form with its C# twin: member signatures and local symbols across all type
   positions, diagnostics for invalid type arguments, nullable warnings, metadata seen from a C# compilation, and C#
   expressions that the scan change must leave alone.

### What it taught

1. **The lexer has no `..` token.** It produces two `.` tokens, which the parser merges (`IsAtDotDotToken`,
   `EatDotDotToken`). A check for `SyntaxKind.DotDotToken` on the current token never matches.
2. **Declarations are decided by scanning, not parsing.** `int[..] x` already parsed in members and parameters, and
   even as a local, but `Func<int[..], int> f` and `(int, int[..]) t` did not, because the statement scanner
   rejected them. Test the form inside a generic argument and a tuple, not only at the top.
3. **"Type or expression" ambiguities move.** Accepting a form in `ScanType` hands it to the cast heuristic, which
   treats an ambiguous type before `(`, `!` or an identifier as a cast. Every expression form the type form overlaps
   with (`a[..]`, `a?[..]`) needs a test with each token that can follow a parenthesized expression.
4. **A small accepted break can come from the scanner.** `F<a[..]>(x)` follows the C# rule for `F<a>(x)` and becomes
   a generic call. It only differs from C# with user-defined comparison operators, so it is accepted and listed
   for the sharpener.

## Feature 4: `readonly` locals

`readonly int x = e;` and `readonly var x = e;` declare locals that cannot be reassigned, merged from
`sharper/readonly-locals` ([#16](https://github.com/ok3mad/roslyn/issues/16), feature page
[readonly-locals.md](../features/readonly-locals.md)). `readonly var x` means exactly `val x`.

**Probe.** The parser already accepts `readonly` as a local declaration modifier (`ParseLocalDeclarationStatementModifiers`)
and attaches CS0106 to the token; after `using` it attaches CS9229. A `for` initializer, `foreach`, a deconstruction and
an `out` argument have no modifier list, so `readonly` there is a cascade of syntax errors. In a script, a top-level
`readonly int t = 1;` is a C# readonly field.

**Change.** With the feature on, the parser skips the error on `readonly` and adds it back where the same modifier list
ends up on a local function (`TryParseLocalFunctionStatementBody`) or next to `const`. `SourceLocalSymbol.IsSharperReadOnly`
reads the modifier from the declaration statement and makes `IsWritableVariable` false, which reuses the whole `val`
machinery: the write checks, `ReportReadonlyLocalError` (cause 'readonly local') and the struct copy
(`Binder.IsSharperValValue`). With the flag off, the parser is unchanged and the C# errors stay.

| Question | Decision | Why |
| --- | --- | --- |
| Meaning | Not a writable variable, like `val`; `readonly var x` is `val x` | The brainstorm's mutability grid |
| Messages | CS1656, CS1657 and CS1654 with the cause 'readonly local'; CS20010 (the readonly-local form of CS20003) for a member call on a copy | The user wrote `readonly`, not `val`. A separate warning code keeps CS20003's text unchanged for `val` |
| No initializer | Error CS20011 for an explicitly typed `readonly` local; `readonly var x;` keeps CS0818 | Nothing can assign the local later, so the declaration is the place to say so |
| `using readonly var` | Allowed, cause 'readonly local' | Covered by the issue; CS9229 still applies to every other modifier |
| `for` initializer | Not supported; stays a syntax error | `ForStatementSyntax` has no modifier slot, so it needs a public syntax API change, for a loop variable the loop usually increments. `for (val i = ...)` works |
| `foreach`, deconstruction, `out` | Not supported; stay syntax errors | No modifier slot; `val` covers each of them |
| `const readonly` | CS0106 on `readonly`, as without Sharper | A constant is already read-only |
| `readonly ref int r` | Allowed: no ref reassignment (CS1656), writes through the ref allowed | Matches C#'s `readonly ref` fields and the brainstorm's "one meaning of readonly"; same as `ref val` |
| `ref readonly int r` | Unchanged C# | Already valid C#, so the C# meaning wins |
| `readonly ref readonly int r` | Both: no writes through the ref (CS0131, as for `ref readonly`) and no ref reassignment | Matches C#'s `readonly ref readonly` fields |
| `readonly scoped` | Allowed | `scoped readonly` stays a syntax error, as for any modifier after `scoped` |
| `readonly val x` | Allowed, means `val x` | Redundant but harmless |
| Local functions | CS0106 kept | `readonly` does not apply to them |
| Script top-level fields | Unchanged C# readonly fields | Already C#; the C# meaning wins |

Tests: 17 `ReadonlyLocal_*` tests (19 cases) in `SharperTests.cs`; all 95 Sharper tests pass, also with `ROSLYN_TEST_IOPERATION=1`.
Regression run of the C# compiler tests (net10.0, Debug, 10 projects; WinRT not run): 88,398 passed, 0 failed.

## Feature 5: `readonly` parameters

`void F(readonly int x)` declares a parameter that cannot be reassigned, on branch `sharper/readonly-parameters`
([#23](https://github.com/ok3mad/roslyn/issues/23), [#97](https://github.com/ok3mad/roslyn/issues/97), feature page
[readonly-parameters.md](../features/readonly-parameters.md)).

**Probe.** The parser already accepts `readonly` as a parameter modifier everywhere, for C#'s `ref readonly`
parameters, and `GetModifiers` reads it only right after `ref`. C#'s error for any other `readonly`, CS9190 ("must be
specified after 'ref'"), comes from the binder (`ParameterHelpers.CheckParameterModifiers`), unlike `readonly` locals,
whose CS0106 the parser attaches. Implicitly typed lambda parameters with modifiers (C# 14) already parse, so
`(readonly x) => ...` needed nothing of its own.

**Change.** `ParameterHelpers.IsSharperReadOnly` (a leading `readonly`, the feature on, not an extension block
receiver) lets `CheckParameterModifiers` skip CS9190. `SourceComplexParameterSymbolBase.IsSharperReadOnly` carries it to
the symbol; `SourceParameterSymbol.Create` never makes such a parameter a `SourceSimpleParameterSymbol`, which keeps no
syntax. The rest reuses the `val` lambda parameter machinery: `CheckParameterValueKind` reports the 'readonly
parameter' cause, `Binder.IsSharperValValue` makes struct copies, and `ReportSharperValReceiverCopy` picks CS20013.

| Question | Decision | Why |
| --- | --- | --- |
| Positions | Methods, local functions, lambdas, anonymous methods, constructors, primary constructors (records too), indexers, operators, delegates; `params` and extension method `this` parameters | Every parameter list that belongs to a body, plus bodiless declarations where it is harmless |
| Spelling next to `ref readonly` | `readonly` first means "cannot be reassigned"; `readonly ref` forbids ref reassignment and allows writes through the ref; `ref readonly` keeps its C# meaning; `readonly ref readonly` is both | The same reading as for locals and C#'s `readonly ref` fields |
| Order | `readonly` must be the first modifier (`readonly scoped`, `readonly this`, `readonly params`); elsewhere CS9190 stays | One position, as for locals, and no clash with `ref readonly`. `scoped readonly` was already an error |
| Part of the signature | No: nothing in metadata, no effect on overriding, implementation, partial methods, delegate conversions or overloads (`H(int)` and `H(readonly int)` clash, CS0111) | It is a rule about the body. C#'s `scoped` takes part in those checks because it changes what callers may pass; `readonly` changes nothing a caller can see |
| Partial methods | The implementing declaration decides | It holds the body |
| Bodiless members (abstract, interface, delegate) | Allowed, no effect | Follows from "not part of the signature"; an error would need a new code for a harmless modifier |
| Struct parameters | Copy and warn, CS20013 (the parameter form of CS20010), also captured, hoisted and in patterns | Same as `val` and `readonly` locals; a separate code keeps CS20010's text, which names a local |
| `out` | Allowed: must be assigned as usual, no ref reassignment | The `readonly ref` reading, and the same as `(out val x)` |
| `in` | Allowed: no ref reassignment; writes are CS8331, as for `in` | Same |
| `(readonly x) => ...` | Allowed: implicitly typed, like `(val x) => ...` | C# 14 already parses modifiers on implicitly typed lambda parameters |
| Primary constructor parameters | Writes are errors in initializers and members; a captured parameter's field is `readonly` | The field then matches the rule, as in a `readonly struct` |
| Extension block receivers, function pointers | CS9190 kept | A receiver is shared by the block's members; a function pointer has no body |
| Flag off | CS9190, exactly as in C# | No change without Sharper |
| `fun` parameters (#97) | Not redundant | `fun` was dropped from the roadmap; immutable-by-default parameters are a strictness ladder setting, where `readonly` stays the opt-in form |

Diagnostics: one new warning, CS20013, the parameter form of CS20010.

Tests: 15 `ReadonlyParameter_*` tests in `SharperTests.cs`; all 172 Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`.
Regression run (net10.0, Debug) of the C# Syntax, Symbol and Semantic test projects: 44,720 passed, 0 failed.

## Feature 6: inferred return type, step R1

`private static infer Greeting(int id) => $"Hello, {id}!";` returns `string`, on branch `sharper/infer-return-r1`
([#67](https://github.com/ok3mad/roslyn/issues/67), feature page [inferred-return-type.md](../features/inferred-return-type.md)).
It adds no syntax, bound node or public API, and needs no lowering: once the type is known, the method is an ordinary
method with that return type.

**Probe.** `infer` already parses as a type name in every return type position, and binds to CS0246. The return type of
a source method is bound in `MethodChecks`, under a per-method lock (`LazyMethodChecks`), and is read during the
checks themselves (`CheckModifiers`, override and explicit implementation matching). Binding a body there would
read other methods' signatures under that lock: two methods that use each other would deadlock on two threads, and
a method that uses itself would see its return type half set. Local functions compute their return type without a
lock. C#'s nearest precedents are `var` locals (`SourceLocalSymbol` binds its initializer on demand and detects cycles
with a thread-static set) and lambda return types (`UnboundLambda.InferReturnType` and its nullable re-inference).

### The mechanism

| Piece | What it does | Extended by |
| --- | --- | --- |
| `Binder.BindTypeOrSharperInferKeyword` | Binds the return type syntax; `infer` is a keyword only when no type named `infer` is in scope (`BindTypeOrAliasOrKeyword`, as for `var`) | P1 (parameters) |
| `SharperInferredReturnType` | Holds the inferred type of one method, computed on first use outside of any lock and published once (diagnostics before the type). `MethodChecks` stores it instead of a return type and skips the checks that need the type; they run after inference | R2 (more body shapes), R3 |
| Thread-static inference frame | While a body is bound for inference, a read of any inferred return type is recorded and gets an uncached error type: the method itself is CS20018, another inferred method CS20017 | R3 replaces the frame with a stack |
| `Binder.BindSharperInferredReturnType` | Binds the expression body with the body's own binder, takes the natural type as `var` does (`BindToNaturalType`, `BindToInferredDelegateType`) | R2 collects `return` statements and uses the best common type, as lambdas do |
| `NullableWalker.AnalyzeSharperInferredReturnType` | Analyzes the body with `returnTypesOpt`, as for a lambda, and takes the top-level annotation from the returned state | R2 joins several returns (`BestTypeForLambdaReturns`) |
| `CSharpSemanticModel.GetSharperInferredReturnTypeMethod` | `GetTypeInfo` and `GetSymbolInfo` on `infer` return the inferred type | R5 |
| `CheckSharperInferredReturnTypeSupported` | The R1 limits: CS20015 for what a later step adds, CS20016 for members with no body | R2 and R4 remove rows |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| What is inferred | The type `var r = e;` gives `r`, or `void` for a call that returns nothing | `var` already decides natural types (lambdas, method groups, tuples); one rule for locals and return types |
| Nothing to infer from | CS20014, on the expression, where `var` reports CS0815, CS8917 or CS8716 | One message for every expression without a type, naming the method |
| Body with errors | Error type, no extra diagnostic | The body binding reports the errors when the method is compiled |
| Nullable annotation | From the flow state of the body; oblivious where annotations are disabled at `infer` | Matches lambda return types; a maybe-null body is the R2 open point for several returns |
| Using itself | CS20018, permanent | The brainstorm's F# rule. A recursive method with a non-recursive `return` was the R3 open point; R3 keeps the error |
| Using another inferred method | CS20017 in R1, reported on the user; R3 infers the other method first and retires CS20017 | Ordering is R3; reporting on the user keeps the other method valid, and the error type is never cached, so the result does not depend on the order |
| Unsupported positions | CS20015 with the position, CS20016 for no body; properties, indexers and operators included | A clear "not yet" instead of CS0246 for a type named `infer` |
| Where the diagnostics are | On `infer` for CS20015 to CS20018, on the body for CS20014 | The use of an inferred method is found through the symbol, which has no syntax |

### Plan for the later steps

- **R2, block bodies.** Done; see [step R2](#feature-6-inferred-return-type-step-r2). The plan held, except that the
  binder reads the method's own return type in more places than the return statement.
- **R3, inferred methods using each other.** Done; see [step R3](#feature-6-inferred-return-type-step-r3). The plan
  held, except that a cycle is found as a strongly connected component, so that its members and messages do not
  depend on where the inference started. The original plan: turn the thread-static frame into a stack. A read of another inferred
  method whose type is not published infers it in a nested frame on the same thread (no lock is held, so nothing can
  deadlock); a method already on the stack is a cycle, reported on every method of the cycle. Two threads may infer
  the same method at once and compute the same type. A published type is read directly, also during inference,
  which is safe once nesting is allowed. Partial declarations and other types need nothing more, since inference is
  per symbol.
- **R4, every member.** Done; see [step R4](#feature-6-inferred-return-type-step-r4). The plan held for methods and
  operators; properties were left to a later step with [#166](https://github.com/ok3mad/roslyn/issues/166).

Tests: 16 `Infer_*` tests in `SharperTests.cs`: runtime output, every inferred type next to a C# twin, the errors,
nullable annotations, the semantic model, IOperation, metadata seen from a C# compilation, the order of inference
and concurrent inference.

## Feature 6: inferred return type, step R2

Block bodies, iterators and `async` ([#68](https://github.com/ok3mad/roslyn/issues/68), on branch
`sharper/infer-return-r2`): `private static infer Classify(int n) { if (n < 0) return "negative"; return "other"; }`
returns `string`. The step builds on R1's mechanism (`SharperInferredReturnType`, the thread-static frame, the second
binding against the inferred type) and adds no syntax, bound node or public API.

**Probe.** Binding a block body while its type is inferred read the method's own return type, which the frame
counts as a use of the method by itself (CS20018) on every `return`. The reads are the binder's, not the user's:
`GetCurrentReturnType` for `return`, `InMethodBinder.GetIteratorElementType` for `yield return` (which also caches the
element type on the symbol, so an error type read there would stick), and `IsEffectivelyTaskReturningAsyncMethod` and
its siblings for `async`. The nullable walker reads it for `yield return` and spreads.

| Piece | What it does |
| --- | --- |
| `Binder.IsSharperReturnTypeBeingInferred` (`SharperInferredReturnType.IsBeingInferred`) | True while the frame on this thread infers this method. The binder's reads of the containing method's return type check it first and then behave as for a lambda whose return type is being inferred: no target type for `return`, no element type for `yield return`, no task type for `async` |
| `Binder.BindSharperInferredReturnType` | Binds a block body with the body's binder into a throwaway `BoundBlock`, collects the `return` values (or the `yield return` values and spread elements of an iterator) with `SharperReturnsCollector`, and infers with `BoundLambda.InferSharperReturnType`: the lambda's `InferReturnTypeImpl` with no delegate type, which picks the best common type and wraps `async` results in `Task` or `Task<T>`. An iterator wraps the element type in `IEnumerable<T>` |
| `BindSharperInferredYieldReturnSpread` | `yield return ..xs` while the element type is inferred: a spread with the element type of `xs` when `xs` is a sequence, otherwise the C# range |
| `NullableWalker` in inference mode | `yield return` and spread elements are collected like returned values (`returnTypesOpt`), and the collected states go through the lambda's inference again with nullability (`BestTypeForLambdaReturns`) |
| `SharperInferredReturnType.HasFailedInference` | No CS0161 ("not all code paths return a value") for a method whose inference failed, which has no type to miss |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Nullable annotation when one `return` is maybe-null (the R2 open point) | Annotated: `return null;` next to `return s;` gives `string?`; the element of an iterator and the result of a task follow the same rule, and the sequence or task itself is not annotated | The lambda rule (`BestTypeForLambdaReturns`), so a body means the same as a method and as a lambda. The other choice, not annotated plus a warning on the `null`, would infer a type the body contradicts |
| Several values, no best common type | CS20024, on `infer`, listing the types. It asks for the type and does not mention unions | Unions are parked ([#31](https://github.com/ok3mad/roslyn/issues/31)); the message stays true whatever they become |
| One value with no type (`return null;`) | CS20014, on the value, as for an expression body | Same case as R1; the message now says "the value it returns" so it fits both |
| No `return` value | `void`; `async` gives `Task` | As lambdas |
| An iterator with only `yield break` | CS20025 | No element type to infer, and an `IEnumerable<object>` guess would hide the mistake |
| `async` expression bodies | `Task<T>` of the body's type, `Task` for `void` | R1 rejected `async`; with lambdas' rule for blocks, the expression body follows the same rule |
| Async iterators | CS20015, not supported yet | Lambdas cannot be iterators, so there is no lambda rule to copy. `IAsyncEnumerable<T>` is the natural answer for a later step |
| Errors in a block body | An error in a returned value (or a `ref` return) gives an error type silently; other errors do not stop the inference | The second binding reports both. Unrelated errors should not cascade into every caller |
| Spread in an inferred iterator | A spread when the operand is a sequence, otherwise the C# range | There is no written element type to try the C# meaning against. The second binding, against the inferred type, applies the usual rule and agrees |
| Inferred local functions declared in an inferred body | CS20017 even when unused, until R3 | Binding the declaration reads the local function's return type; R3's nested frames remove the error (done) |

Tests: 8 `Infer_Block_*` tests in `SharperTests.cs` next to the C# twins (types, runtime output, errors, nullable
annotations, the semantic model and IOperation), and the R1 tests updated for `async` and async iterators. All 261
Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug): Semantic 20,083
passed, Emit 7,312, Emit2 2,191 and Emit3 27,951, none failed; the Syntax project with `ROSLYN_TEST_SHARPER=1` has the
37 expected failures listed under Follow-ups and no other.

## Feature 6: inferred return type, step R3

Inferred methods that use each other ([#69](https://github.com/ok3mad/roslyn/issues/69), on branch
`sharper/infer-return-r3`): `private static infer Hello(User u) => $"Hello, {Name(u)}!";` infers `Name` first. The
step changes only `SharperInferredReturnType` and adds no syntax, bound node or public API.

**Probe.** R1's frame already sat outside every lock, so a nested inference on the same thread cannot deadlock. A
plain stack with "a method on the stack is a cycle" (R1's plan, and P1's mechanism) is not deterministic for return
types, though: a method `Z` in a cycle with `A` and `B` reads `B` after `B` finished and published its error, and then
sees no cycle, so whether `Z` reports an error depended on where the inference started. With several threads the
same happens across threads. The C# precedents do not help here: `var` locals and script `var` fields report a cycle
on whichever member closes it (`SourceLocalSymbol.s_LocalTypeInferenceInProgress`,
`SourceMemberFieldSymbol` with its `fieldsBeingBound` list), and lambdas never refer to each other by name.

| Piece | What it does |
| --- | --- |
| Frame stack (`SharperInferredReturnType.Context`) | Per thread: the frames whose bodies are being bound, innermost last. A read of an unpublished inferred return type infers it in a nested frame; `IsBeingInferred` asks the innermost frame |
| Tarjan's algorithm on the frames | Each frame is a node with an index and a low link, and records the inferred methods its body reads. A read of a method on Tarjan's stack lowers the reader's low link and gets an uncached error type. A frame whose body is done waits on the stack until its component is complete |
| `DecideAndPublish` | For a complete component: one method with no use of itself keeps its type; a method that uses itself gets CS20018; every other member of a larger component gets CS20033. All results are decided (compare-exchange) before any is published, with its diagnostics |
| `FormatCycle` | The cycle named in CS20033: the shortest path from the method back to itself through the component, breadth first, taking each body's uses in order |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| A recursive method with a non-recursive `return` (the R3 open point) | Stays CS20018 | Inferring from the base case and checking the rest is a fixed point that can widen the type (`return 1;` then `return F() + 1L;`), and the type would then depend on the order of the `return` statements. F# asks for the type too; writing it is short |
| Methods that need each other | CS20033 on every member of the cycle, naming the cycle from that member | Each member is equally the place to write a type; naming the path tells which methods to look at. Reporting only one member would depend on the order |
| Which methods are in a cycle | The strongly connected components of "reads the inferred return type of", found by Tarjan's algorithm as the frames nest | A plain stack finds a cycle only from some starting points (see the probe). Components are a property of the uses, so the result is the same from any start |
| Another thread's results | All results of a component are decided before any is published; a thread that finds a result decided takes it | A thread that reads a published member of a cycle cannot see the whole cycle, and would compute a smaller one. A member is published only after every member is decided, so that thread finds its own results decided |
| A method that uses a cycle without being in it | Error type, no diagnostic | The same as a use of any method with errors; the cycle reports once, on its members |
| Overload resolution | Reads the return types it reads for written types | A call reads none, so overloads never depend on each other's bodies; a method group conversion or type inference from a method group reads the candidates it checks. Reading fewer would change which overload wins compared with the written type |
| CS20017 | Retired; CS20033 is new | Using another inferred method is no longer an error, and the cycle message has a different argument |
| A local function in the body that calls the method | A cycle (CS20018 on the method) | Inferring the method binds the local function's body. Skipping it during inference would break the body's nullable analysis; the case is rare |

Tests: 7 new `Infer_UsesInferredMethod*` and `Infer_Cycle*` tests in `SharperTests.cs`: chains, method groups,
iterators and local functions next to C# twins with runtime output, every order of inference, partial declarations,
nested types, overloads and method group conversions, cycles of two and three methods and of local functions, and
cycles and chains on many threads at once. R1's and R2's tests of CS20017 now infer. All 283 Sharper test cases pass,
also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug): Semantic 20,104 passed, Symbol 13,923, Emit
7,312, Emit2 2,191 and Emit3 27,951, none failed; the Syntax project with `ROSLYN_TEST_SHARPER=1` has the 37 expected
failures listed under Follow-ups and no other.

## Feature 7: implicit `it`

`xs.Select(Process(it, a))` is `xs.Select(x => Process(x, a))` (issue #24). The rules are in the brainstorm's
[Decided](brainstorm.md#decided) list and the [feature page](../features/implicit-it.md); this section keeps what the
implementation taught.

- **The precedent is the query clause, not the lambda.** A query clause is a lambda whose syntax is a plain expression
  (`QueryUnboundLambdaState`), and an implicit `it` lambda is the same: an `UnboundLambda` over the argument
  expression, whose body binds in an `ExecutableCodeBinder` rooted there. Type inference, overload resolution,
  nullable analysis, closures and expression trees then need no change. Only assertions that list lambda syntax kinds
  did (`BoundLambda`, `ClosureConversion.GetLambdaId`, `SynthesizedClosureEnvironment`).
- **A type-directed scope needs two trials, and a filter in front of them.** Whether an argument is the lambda depends
  on the parameter it goes to, which is known only after overload resolution, so each call binds its arguments as
  lambdas, then as values. A failed call binds its lambda arguments again for error recovery, so without a filter the
  work multiplied with the nesting depth (CS9236, "binding the lambda at least 100 times", on a two-level example).
  `SharperCandidateTakesFunction` checks the candidates' parameter types first, extension methods included; a lambda
  trial then runs only where some candidate takes a one-parameter delegate.
- **Variables declared in the arguments break trial binding.** An `out var` gets its type while the call binds, and a
  second trial sets it again (an assertion in `SourceLocalSymbol.SetTypeWithAnnotations`). An argument list that
  declares a variable binds as values only, which is decision 5 (no declarations in an implicit lambda).
- **Trial bags need diagnostics.** The semantic model binds with a bag that collects none, and a trial copied from it
  could not tell success from failure (`HasAnyErrors` asserts). Trials use their own bags with diagnostics.
- **"Does it use `it`?" is a bound question.** `xs.Select(Make(ys.First(it.X)))` binds as a lambda whose `it` is
  unused, because the inner call claims it. The lambda trial counts only when each lambda reads its parameter.
- **Name the inner call only when nothing else went wrong.** CS20054 is added when the value trial fails and the lambda
  trial had no errors apart from a lambda without its own `it`. The value trial's C# errors stay: they can be
  independent mistakes (a wrong argument count), and telling them from consequences would mean guessing. An inner
  call that already reported makes the outer lambda trial fail, so the error appears once, at the innermost level.
- **One pair of trials for every argument list.** Object creation, indexers and constructor initializers (#122) share
  `BindSharperImplicitItArguments` with calls: each site passes its candidates (the instance constructors, the
  receiver's indexers, the base or own constructors) for the filter and a callback that binds the arguments and the
  expression. The upstream methods were split where the arguments are bound (`BindClassCreationExpressionWithArguments`,
  `BindElementAccessWithArguments`, a `BindConstructorInitializerCore` overload), and the Sharper calls live in separate
  methods, so the C# path allocates no closures.
- **Target-typed `new(...)` decides at its conversion.** The constructors are known only when the expression gets its
  type, so its free-`it` arguments bind first as unbound implicit lambdas, which carry no errors, and
  `ConvertObjectCreationExpression` runs the trials, rebinding only those arguments. Binding them first as values with
  discarded diagnostics did not work: the errors flagged the expression, and `GenerateConversionForAssignment` then
  discarded the conversion's diagnostics too, so `Value v = new(it + 1)` reported nothing.

## Feature 8: `try` expression, slice 1 (design)

`RateResult r = try await http.GetFromJsonAsync<RateTable>(url) catch { HttpRequestException { StatusCode: 404 } =>
new NoRate(from, to), TimeoutException => new FxUnavailable(0) };` (issue [#28](https://github.com/ok3mad/roslyn/issues/28)).
Exceptions become values through switch-style arms; unmatched exceptions propagate, and there is no `finally`. Slice 1
needs no anonymous unions: the result is target-typed to a declared C# 15 union, or has a natural type when the body
and the arms agree. Union results without a target wait for slice 2 of [#31](https://github.com/ok3mad/roslyn/issues/31).
Slice 1 is implemented as designed below, on `sharper/try-expression-slice1`; [Implementation (slice 1)](#implementation-slice-1)
lists what the code added to the design. Feature page: [`try` expression](../features/try-expression.md).

**Probe.** What the compiler does today, and what the design reuses:

| Area | What the code shows |
| --- | --- |
| Flag off | In any expression position (initializer, argument, `return`, `=>` body) `try` is rejected by `IsInvalidSubExpression` (`LanguageParser.cs`) with CS1525 "Invalid expression term 'try'", so no valid C# changes meaning. At the start of a statement `try` is always the try statement (`ParseStatementCore`, `IsDefiniteStatement`) |
| Arms | `ParseSwitchExpression`'s arm loop (`LanguageParser_Patterns.cs`) produces `SwitchExpressionArmSyntax`, and `SwitchExpressionArmBinder.BindSwitchExpressionArm(node, switchGoverningType, ...)` takes the governing type as a parameter, so `System.Exception` gives patterns, designations and `when` clauses unchanged |
| Result type | `SwitchExpressionBinder.InferResultType` picks the best common type of the arms (`BestTypeInferrer.GetBestType`); `BoundUnconvertedSwitchExpression` and `ConversionKind.SwitchExpression` give target typing (about 9 sites use the kind, about 37 handle the unconverted node) |
| Union conversion | C# 15 converts a case value into a union through its factory methods (`AnalyzeImplicitUnionConversions`), tried after user-defined conversions (`GetImplicitUserDefinedOrUnionConversion`). It is not a standard conversion, so it does not chain (`UnionsTests.UnionConversion_09` and `_11`): the body's own type must be a case. No test converts a target-typed switch or `?:` into a union yet ([#137](https://github.com/ok3mad/roslyn/issues/137)) |
| Spilling | A switch expression lowers to a `BoundSpillSequence` and sets `_needsSpilling` (`LocalRewriter_SwitchExpression.cs`), as `stackalloc` does to empty the evaluation stack (`LocalRewriter_StackAlloc.cs`). A try/catch inside a spill sequence gets the same treatment: earlier operands, as `a` in `F(a, try ...)`, move to temps, ref temps for refs (`SpillSequenceSpiller.Spill`) |
| Filters | `BoundCatchBlock.ExceptionFilterPrologueOpt` holds statements that run before the filter condition. The spiller, `AsyncExceptionHandlerRewriter` and `EmitStatement` support it, so a decision DAG can run inside a filter, as in the valid C# `catch (Exception e) when (e switch { ... })` |
| Restrictions | `stackalloc` in a catch block is CS0255 through `BinderFlags.InCatchBlock` (`Binder_Expressions.cs`); `await` in a filter is CS7094 through `InCatchFilter` (`Binder_Await.cs`). `yield` cannot appear inside an expression, so iterators are not affected |
| State machines | `AsyncExceptionHandlerRewriter` and `MethodToStateMachineRewriter` assert that a try's syntax is one `SyntaxBindingUtilities.BindsToTryStatement` accepts, which keys Edit and Continue's locals and states. The spread's `yield return ..xs` was added there for the same reason |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Syntax | `try <expr> catch { <arms> }`: a new `TryExpressionSyntax` (try keyword, body, catch keyword, braces, `SwitchExpressionArmSyntax` list). The body runs up to `catch`. The whole expression has the lowest precedence, like a lambda, so `(try ... catch { ... }).X` needs parentheses | A new node is unavoidable (the task and function types are the precedent), and sharing the arm syntax keeps one parser and one binder for arms. Lowest precedence avoids puzzles such as `try a catch { ... } + b` |
| Statement position | A `try` at the start of a statement stays the C# statement | C# wins; an expression statement made of a `try` expression would be CS0201 anyway |
| Result type | Target-typed first: the body and each arm convert to the target, as for a switch expression, which reaches the C# 15 union conversion. Without a target, the best common type of the body and arms. Neither: a Sharper error that names the types and asks for a declared union or a target type | The issue's `RateResult r = try ...` needs target typing; the natural type comes free from the switch code. The error stays true whatever anonymous unions become |
| Arms | Governing type `System.Exception`; patterns, designations and `when` as in a switch. No exhaustiveness check, because unmatched exceptions propagate; the subsumption error (an arm that can never match) stays. A catch-all arm (`_`, `Exception`) is allowed; a warning for it is [#135](https://github.com/ok3mad/roslyn/issues/135) | Same arms as a switch expression, minus the rule that has no meaning here |
| Lowering | Through a catch filter, not catch plus `throw;`: the body's value goes to a result temp in the try block; `catch (Exception e) when (...)` runs the decision DAG in the filter prologue, setting an arm index and the pattern variables, and is false when no arm matches; the catch block assigns the chosen arm's value to the result temp | Unmatched exceptions are never caught, so their stack trace and first-pass semantics (inner `finally` blocks run after the filter decides) are untouched, and no rethrow is needed. Pattern variables set in a filter and read in the catch body are what C# already does for `catch (E e) when (e is X x)` |
| Rethrow | Not expressible: an arm is an expression and a throw expression needs an operand. `=> throw new Wrapped(e)` is an ordinary throw expression | Unmatched exceptions already propagate untouched, so a rethrow arm adds nothing |
| `finally` | None | Decided in the brainstorm; low-level code lives in C# |
| An awaitable body | Slice 1 requires `try await F()`; a Sharper warning reports a body whose type is awaitable when it is not awaited. Lifting it is [#136](https://github.com/ok3mad/roslyn/issues/136), after [#32](https://github.com/ok3mad/roslyn/issues/32) | `try http.GetFromJsonAsync(url) catch { ... }` catches only synchronous throws: the 404 surfaces on `await`, outside the `try` |
| `await` | Allowed in the body and in arm values (catch blocks allow it since C# 6), an error in `when` clauses (CS7094) | The arm binder gets `InCatchBlock` for values and `InCatchFilter` for patterns and `when` |
| `stackalloc` | Allowed in the body, CS0255 in an arm value | IL forbids `localloc` in a handler; the flags give the C# error |
| `ref` and ref structs | The result is an rvalue; `ref` results are not supported. Spilled operands follow the spiller's rules: temps and ref temps in synchronous methods, and in `async` methods the usual rules for values kept across an `await` | Nothing new to check beyond what a switch expression with an `await` meets |
| Expression trees | An error, as for a switch expression (CS8514) | Expression trees have no try/catch with filters |

### Plan

1. **Syntax.** `TryExpressionSyntax` in `Syntax.xml` with its public API; a gated parse in place of the CS1525 path, sharing the arm loop with `ParseSwitchExpression`; parser tests with the flag on and off.
2. **Binding.** `BoundUnconvertedTryExpression` (natural type, like the switch) and `BoundTryExpression` (converted), a new conversion kind for target typing, the arm binder with the catch and filter flags, and the decision DAG over `Exception` without the exhaustiveness check. The awaitable-body warning.
3. **Analysis.** Definite assignment and nullable, modelled on the try statement (state at the start of a catch is the join of the states in the body) and on switch arms; IOperation.
4. **Lowering.** The filter lowering above as a spill sequence; `TryExpressionSyntax` in `BindsToTryStatement`; Edit and Continue syntax maps.
5. **Tests** in `SharperTests.cs`, next to the C# they stand for: union and best-common-type results, `when`, propagation of unmatched exceptions with their stack trace, `await` in the body and arms, spilled arguments (`F(a, try ...)`), `stackalloc`, iterators, expression trees, and the flag off.

**Size:** L at the low end, about the size of implicit `it`. The parser and the lowering are M; most of the work is the
target-typed conversion and flow and nullable analysis.

### Implementation (slice 1)

| Question | What the code does | Why |
| --- | --- | --- |
| Parse position | Where C# reports CS1525 for `try` (`ParseSubExpressionCore`), unless `{` follows; at a higher precedence than a lambda (`1 + try ...`) it parses and reports CS1525, as a throw expression does | `try {` in an expression is a statement in the wrong place, and no expression starts with `{`, so its recovery stays C#'s |
| Arm binders | `SwitchExpressionArmBinder` with no switch binder (input `System.Exception`); `LocalBinderFactory` and `ExpressionVariableFinder` get `VisitTryExpression`, so the arms' variables get their own scopes and not the enclosing statement's | One arm binder for both; the flags go on the pattern and `when` clause (`InCatchFilter`) and the value (`InCatchBlock`) as they bind |
| Bound nodes | `BoundTryExpression` (abstract), `BoundUnconvertedTryExpression` and `BoundConvertedTryExpression`, with the arms, the decision DAG and a placeholder for the exception (the DAG's input) | The switch expression's shape; the conversion is `ConversionKind.SharperTryExpression`, whose underlying conversions are the body's, then each arm's |
| Diagnostics | CS20029 (no type), CS20030 (task-like body, level 1), CS20031 (expression tree), CS20032 (in a `when` clause: IL has no try block in a filter, and codegen asserts) | The awaitable check covers task-like types (`IsGenericTaskType`, `IsNonGenericTaskType`), not every type with `GetAwaiter` |
| Nullable | The DAG's input is a `BoundExpressionWithNullability` over the placeholder, not the placeholder itself | `NullableWalker` asserts on unregistered value placeholders (`AssertPlaceholderAllowedWithoutRegistration`) |
| Lowering | As designed; the arm index dispatches with a `BoundSwitchDispatch` in the catch block. The rewriter records an `await` in an arm as an await in a handler (`_sawAwaitInExceptionHandler`), and `AsyncExceptionHandlerRewriter` takes the try expression's syntax for a catch block's frame | Without the first, the await stayed in the handler and codegen crashed; the second cast the catch's parent to `TryStatementSyntax` |
| IOperation | `OperationKind.None` with the body as its only child ([#144](https://github.com/ok3mad/roslyn/issues/144)) | The control flow graph has no region for the arms' pattern variables and cannot visit an arm outside a switch |
| Edit and Continue | `BindsToTryStatement` accepts `TryExpressionSyntax`; no instrumentation and no emit-difference tests yet ([#145](https://github.com/ok3mad/roslyn/issues/145)) | Slice 1 keeps the debugging work for later |

Tests: 22 in `SharperTests_TryExpression.cs` (runtime results next to the C# twins, the issue's union example with
`await`, stack trace and first-pass order of an unmatched exception, spilling with `await`, `ref` and `stackalloc`,
iterators, lambdas, errors, definite assignment and nullable, IOperation, flow graph, semantic model, region analysis,
parsing, the flag off), and five union conversion tests in `UnionsTests` (#137). They found a C# 15 gap: a possibly
null value converted into a union case in a target-typed arm does not warn ([#146](https://github.com/ok3mad/roslyn/issues/146), since fixed).

## Feature 6: inferred return type, step R4

Every member kind and accessibility ([#70](https://github.com/ok3mad/roslyn/issues/70), on branch
`sharper/infer-return-r4`): `public static infer Hello(int id) => $"Hello, {id}!";` returns `string`, and so do
`virtual` methods, partial methods, extension block members, operators and conversion operators. The step builds on
R1 to R3's `SharperInferredReturnType` and on P2's copy from the overridden member, and adds no syntax, bound node or
public API.

**Probe.** Lifting the accessibility row was enough for methods: the inferred type is published before anything reads
it, and the checks of a written type (`CheckEffectiveAccessibility`, override and implementation matching, nullable
attributes) run on it as on a written one. Three member kinds needed more. Operators read their return type in the
method checks, under the lock (`CheckOperatorSignatures`). A partial definition has no body, and its parts are linked
after its method checks, which the partial method comparer runs while merging the parts. Edit and Continue saw a body
edit that changes the type as an ordinary signature change, which the runtime allows, so callers compiled against the
old type would break.

| Piece | What it does |
| --- | --- |
| `SourceOrdinaryMethodSymbol.CheckSharperInferredReturnTypeSupported` | Only async iterators, `ref` returns and members without a body remain; accessibility, partial methods and extension block members are lifted |
| `ReturnTypeFromPartialImplementation` | The `infer` type of a partial definition: reads its implementation's return type, inferred or written, renaming the type parameters. A read during an inference counts as a use of the implementation, so a body that calls its own partial method is CS20018. A definition without an implementation is CS20016, from the partial method checks |
| `SourceUserDefinedOperatorSymbolBase` | Creates an `InferredReturnType` for operators and conversion operators (the base class `SourceOrdinaryMethodOrUserDefinedOperatorSymbol` already held one), and moves `CheckOperatorSignatures` to `AfterAddingTypeMembersChecks` for an inferred type |
| `CSharpSemanticModel.GetSharperInferredReturnTypeMethod` | Also answers for the `infer` of an operator and of a conversion operator |
| `AbstractEditAndContinueAnalyzer.GetSignatureChangeRudeEdit` | A changed return type of a method whose old and new declarations both have `infer` is `RudeEditKind.ChangeSharperInferredReturnType` (ENC1000), as a change of the implicit `Main`'s return type is. `CSharpEditAndContinueAnalyzer.IsSharperReturnTypeInferred` reads the declaration syntax, since the IDE cannot see the compiler's internal flag |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Public members | Infer like private ones, with no warning (decided in the issue) | The author chooses; the public API analyzers still catch an accidental change |
| A body-inferred type on an override (the issue's check) | Never happens: an override, an explicit implementation and a public method that implements an interface member copy their type (P2), and their body converts to it as to a written type (`override infer V() => "d"` over `int V()` is CS0029). When P2 finds no member, its error stands (CS20043) | Inferring from the body and then comparing with the base would make the body choose between an override and an error; the base already says the type. A method that infers because its own type has no interface member of that name, and that a derived type uses as an implementation, is checked as a written one (CS0738) |
| Partial methods | The implementation infers from its body; a definition with `infer` takes the implementation's type; written on one part and inferred on the other is compared as usual (CS8817) | The definition has nothing else to infer from, and callers bind to it |
| Operators and conversion operators | Infer from their body; the signature checks (CS0215, CS0216, CS0448, ...) apply to the inferred type after inference | As for methods. A conversion operator is read by every lookup of its type's user-defined conversions, so one whose body converts from its own type uses itself (CS20018) |
| Explicit implementations of operators | CS20015 until [#166](https://github.com/ok3mad/roslyn/issues/166), which copies their types (done) | Their method checks match the implemented operator by return type, under the lock; copying, as P2 does for methods, is #166's |
| Extension block members | Infer, operators included | The implementation methods read the member's return type lazily (`RewrittenMethodSymbol`), so nothing else was needed |
| Members without a body | CS20016, permanent | Nothing to infer from |
| `ref` returns | CS20015, later | The lambda rule the inference follows infers no `ref` returns |
| Anonymous types on visible members | CS0050, as for any internal type | An anonymous type is internal. The same rule for an anonymous union `(A \| B)` on a visible member, an error (see [Decisions](#decisions)), waits for unions ([#31](https://github.com/ok3mad/roslyn/issues/31)); a `PROTOTYPE(sharper)` comment marks the place |
| Ref safety | Nothing to add: the second binding, against the inferred type, runs the escape analysis of a written `Span<T>` (CS8352) | The inference binds without a target type, as a lambda does |
| Properties and indexers (open point) | Not in R4: CS20015 stays, and a later step does them together with [#166](https://github.com/ok3mad/roslyn/issues/166) | A property's type has two sources, the getter and the overridden property, and is read by its accessors, backing field and object initializers before a lazy type exists on `SourcePropertySymbolBase`. One step decides both sources together |
| Edit and Continue | A body edit that changes the inferred type is a rude edit; one that keeps it is a body update | The runtime would accept the signature change, but callers are not recompiled, so they would call a deleted method. The implicit `Main` is the precedent |

Tests: 15 `InferMember_*` tests in `SharperTests_InferEveryMember.cs`: every accessibility next to C# twins with runtime
output, `virtual` and overrides, interface default and static virtual members, the errors of overrides and
implementations against body-inferred bases, partial methods (written and inferred parts, generic, errors), operators
and conversion operators next to C# twins and their signature errors, extension block members, the accessibility of
the inferred type, ref safety, metadata seen from a C# compilation (image and compilation references), the semantic
model, IOperation, the order of inference across operators, conversions and partial methods, and a conversion operator
in a cycle. `Infer_NotSupportedYet` and two P2 tests lost their rows for public members, partial methods, operators and
extension members. `SharperTopLevelEditingTests.InferredReturnType_BodyEditChangesType` in the C# Features tests covers
Edit and Continue.

## Feature 9: inferred parameter type, step P1

`void Fetch(string url, infer retries = 3)` gives `retries` the type `int`, on branch `sharper/infer-parameter-p1`
([#72](https://github.com/ok3mad/roslyn/issues/72), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md)). Like R1, it adds no syntax, bound node or public
API, and needs no lowering.

**Probe.** Every member kind builds its parameters in `ParameterHelpers.MakeParameter`, and lambdas in
`Binder.AnalyzeAnonymousFunction`; the type is bound there and stored in the parameter symbol, which every signature
comparison then reads (overrides, implementations, duplicate members, partial methods). The default value is bound
later and lazily, against that type (`SourceComplexParameterSymbol.MakeDefaultExpression`). So the type must be known
when the symbol is created, unlike R1's return type, which could stay lazy because `MethodChecks` skips the checks that
need it. Methods and constructors create their parameters inside `MethodChecks`, under the per-member lock, and
indexers inside `EnsureSignature`, under another.

### The mechanism

| Piece | What it does | Extended by |
| --- | --- | --- |
| `infer` branch of `ParameterHelpers.MakeParameter`, `Binder.BindLambdaParameterType` | Recognize `infer` with R1's `BindTypeOrSharperInferKeyword` and create the parameter with the inferred type | P2 (the overridden member when there is no default value), P4 to P7 |
| `Binder.BindSharperInferredParameterType` | Binds the default value with the binder it is bound with anyway, takes the natural type as `var` does, and the annotation from whether the value is a null constant | |
| `SharperInferredParameterType` | Keeps the results per parameter syntax in the compilation, published once. A thread-static stack of the parameters being inferred finds cycles, and every parameter of a cycle gets CS20028 as soon as the cycle is found | P2 and P4 add sources; a body used as a source (P4) can cycle through the signature the same way |
| `InferBeforeSignature` in `LazyMethodChecks` and `EnsureSignature` | Infers before the signature lock is taken, so binding a default value never holds a member's lock while it waits for another's | |
| `CSharpSemanticModel.GetSharperInferredParameter` | `GetTypeInfo` and `GetSymbolInfo` on `infer` return the parameter type | R5 |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| What is inferred | The type `var x = e;` gives `x`; a lambda or method group gets its natural function type | One rule with locals and R1 |
| A default value that is not a constant | Its type, then CS1736 from the default value check, as for the written type | The issue's `TimeSpan.FromSeconds(5)` names a type, and C# reports the same error for its twin; non-constant defaults would be a feature of their own |
| Nothing to infer from | CS20026 on the value; no CS1736 or conversion error after it | One error per parameter, which asks for the type |
| No default value | CS20027 on `infer` | P2 adds the overridden member as a source before this error |
| Nullable annotation | Annotated for a null constant (`default(string)`), not annotated otherwise, oblivious where annotations are disabled | A default value is a constant, so it needs no flow analysis; `infer s = "a"` is a `string` that callers cannot pass null to |
| Where it runs | Before the signature lock, cached per parameter syntax | The lock-order trap R1 found (trap 23 of the skill); a cache by syntax serves members without a lock and lambdas, which are bound several times |
| Cycles | CS20028 on every parameter of the cycle, published when the cycle is found | Publishing at the end was not enough: a member whose signature completes inside the cycle let another thread infer the rest of the cycle without the error (found by `InferParameter_Cycle_Concurrent`) |
| `ref`, `out`, `params`, `this` | No special rule: without a default value CS20027, with one the C# error for the default value | The C# twin reports the same, and the type is still known |

Tests: 18 `InferParameter_*` tests in `SharperTests.cs`: runtime output for every member kind and lambdas next to C#
twins, the inferred types against written ones (also the default values), the errors, cycles in every order and on
two threads, caller information attributes, signature matching, nullable annotations, the semantic model, IOperation,
metadata seen from a C# compilation, and concurrent inference. `readonly infer x = 1u` already works (P3 needs only
its tests and the `val` decision).

## Feature 9: inferred parameter type, step P2

An override or an interface implementation copies its `infer` parameter and return types from the member it overrides or
implements: `public infer Load(infer userId) => result;` in a class implementing `IProfiles` is
`Task<ProfileResult> Load(int userId)`, merged into `main` from `sharper/infer-from-overridden-p2` in
[#161](https://github.com/ok3mad/roslyn/pull/161) ([#73](https://github.com/ok3mad/roslyn/issues/73), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md#from-the-overridden-or-implemented-member-step-p2)).
No syntax, bound node, public API or lowering.

**Probe.** C# finds the overridden member (`OverriddenOrHiddenMembers`) and the implemented one
(`FindImplementationForInterfaceMember`, `FindExplicitlyImplementedMember`) by comparing complete signatures, after
`MethodChecks` has created the parameters. An `infer` parameter without a default value cannot be created without its
type, so the member must be found earlier, from syntax and the symbols that exist before the signature. Reading a
candidate's parameters completes the candidate's signature, which can bind its default values and so read the copying
method again: the same lock-order and cycle problem as P1's default values.

### The mechanism

| Piece | What it does |
| --- | --- |
| `SharperInferredParameterType.GetOverriddenSignature` | For a method with an `infer` this source can fill (`override`, explicit implementation, or public in a type with interfaces), binds the written types, takes the P1 types of `infer` parameters with defaults, and matches candidates by name, arity, parameter count and `static`, narrowed by those types and the written ref kinds. Kept per method syntax by the compilation, published once |
| The P1 stack, now of syntax nodes | A search and a default value in progress share the thread-static stack, so a cycle through both is found from either end; each member of the cycle gets its error (CS20045 for a search, CS20028 for a default value) when it is found |
| `InferBeforeSignature` | Runs the search before the signature lock, after the default values |
| `ParameterHelpers.MakeParameter`, `SourceParameterSymbol.Create` | Take the copied type, the ref kind (when none is written) and `params` |
| `SourceOrdinaryMethodSymbol.MakeParametersAndBindReturnType` | Takes the copied return type instead of R1's inference, and reports the search's errors once |
| `CSharpSemanticModel.GetSharperInferredReturnTypeMethod` | Also answers for a copied return type (`IsSharperReturnTypeFromOverriddenMember`) |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Which methods copy | `override` from base classes; explicit implementations from the interface they name; public methods from the interfaces of their type, when one has a member with the same name and parameter count | These are the methods C# matches against another member's signature. A public method with no such interface member has no source, so its `infer` keeps P1's and R1's rules (CS20027, CS20015) |
| Explicit implementations (open point) | Supported: the named interface's members are the candidates | The interface is named, so the candidate set is as small as it gets |
| Several interfaces (open point) | Members with the same signature (`I1.N(int)` and `I2.N(int)`) count once and one method implements both; different signatures are CS20044, which lists them | The issue's proposal; the method could only implement one of them, and guessing which would hide the other |
| Written types | Narrow the candidates (types compared ignoring nullability, tuple names and `dynamic`, ref kinds exactly); `infer` with a default value narrows with its P1 type | A partial signature chooses an overload, which was the open point of the work breakdown. A written type with its own errors narrows nothing |
| A virtual member and its overrides | One candidate, the most derived | They are one slot; counting each would make every override chain ambiguous |
| What is copied | Types with nullability and tuple names, generic substitutions, method type parameters renamed; ref kinds and `params` for parameters that write none. Not custom modifiers (the method checks copy them for overrides, as for written types) and not the return's `ref` | The issue's list. A written `ref` on the return is part of the declaration's shape, as for R1 |
| Default value and overridden member both present | The default value gives the type (P1), and narrows the candidates | No disagreement can arise from narrowing; a mismatch then is ordinary C# (CS0115) |
| Return type limits | None of R1's limits (CS20015 for public members, CS20016 for no body) and none of R4's restrictions on public members | Nothing is inferred from a body: the type is copied from a signature that is already written somewhere |
| Errors | CS20043 no candidate, CS20044 several, CS20045 cycle, on the method name, once; the `infer`s get error types. CS20043 names each candidate that the written types ruled out and the written part that ruled it out, which is also an additional location ([#167](https://github.com/ok3mad/roslyn/issues/167)) | One error per method, which asks for the types; error types keep C#'s override and implementation errors quiet |
| Properties, indexers, operators, events | Done in [#166](https://github.com/ok3mad/roslyn/issues/166), below | They have their own signature code; the search runs on their symbols, before their own locks |
| Records | Overrides of synthesized members of a base record and of `object` copy like any other; a record's own declaration of a member it would synthesize overrides nothing and needs its types | That declaration is matched against the synthesized member by signature, which needs the types |

Tests: 15 `InferOverridden_*` tests in `SharperTests_InferFromOverridden.cs`: runtime output next to C# twins (the
issue's example, overrides, explicit implementations), the copied ref kinds, `params`, nullability, tuple names and
generics, narrowing by written and P1 types, the errors, binding order from every member, cycles from every member
and on several threads, records, nullable warnings, the semantic model, IOperation, metadata seen from a C#
compilation, and concurrent searches. `Infer_NotSupportedYet` lost its override and implementation rows.

Follow-ups:

- Properties, indexers, operators and events of overrides and implementations: [#166](https://github.com/ok3mad/roslyn/issues/166)
  (done, below).
- Clearer errors when no member matches, and no CS1503 after a cycle: [#167](https://github.com/ok3mad/roslyn/issues/167)
  (done: `InferOverridden_Errors_RuledOut`, `InferParameter_Cycle_ArgumentConversions`).
- A deterministic test for the cycle publication race: [#168](https://github.com/ok3mad/roslyn/issues/168).
- Edit and Continue when the overridden or implemented member's signature changes: [#169](https://github.com/ok3mad/roslyn/issues/169).

### Every member kind ([#166](https://github.com/ok3mad/roslyn/issues/166))

Properties, indexers, operators, conversion operators and events copy their `infer` types as methods do, on branch
`sharper/infer-p2-member-kinds`: `public override infer Name => "c";` is a `string` property, `public override infer
this[infer key] => 0;` an `int this[string key]`, `public static infer operator +(infer a, infer b)` implementing
`IAdd<S>`'s static abstract operator an `S operator +(S, S)`, and `public override event infer Changed;` an
`EventHandler` event. No syntax, bound node, public API or lowering.

**Probe.** The parameters of every member kind already go through `ParameterHelpers.MakeParameter` with the member as
owner, so once the search accepted any member, indexer and operator parameters were copied with no change there. Each
kind binds its type in its own place: properties and indexers in `ComputeType` under `EnsureSignature`'s lock, which
already ran `InferBeforeSignature` for indexers' default values; operators in their method checks, like methods; and
events in their constructor, before they are members of their type.

| Piece | What it does |
| --- | --- |
| `MemberDeclaration` (in `SharperInferredParameterType_OverriddenMember.cs`) | One description per member kind: the cache key (the declaration, or a field-like event's declarator), the candidates' name (`this[]` for indexers, `op_Addition` for operators), kind check, parameters, type syntax, explicit interface, and where errors go. The search, the written signature, the copied signature and the errors (with #183's ruled-out explanation) use only it |
| `GetOverriddenSignature` | Accepts any member; the cache, cycle stack and decided cycle results are keyed by `CSharpSyntaxNode` |
| `InferBeforeSignature` | Runs the search for properties too, which have no parameter list |
| `SourcePropertySymbol.ComputeType` | Takes the copied type and reports the search's errors; otherwise R4's CS20015 |
| `SourceUserDefinedOperatorSymbolBase.MakeParametersAndBindReturnType` | Takes the copied return type before R4's inference; explicit implementations no longer report CS20015 |
| `SourceEventSymbol.BindEventType` | Searches from the constructor: base types' and interfaces' members are complete there, and the written types bind with the containing type's binder, so the event is never looked up |
| `CSharpSemanticModel.GetSharperInferredPropertyOrEventType` | `GetTypeInfo` and `GetSymbolInfo` on a property's, indexer's or event's `infer` |
| `SharperInferredParameterType.AddDefaultValueDiagnostics` | Also drops CS1503 for an indexer of a cycle, as #183 does for methods |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Matching | By kind (an ordinary method, an operator, a conversion, a property, an indexer, an event), name and parameter count, narrowed by the written types, as for methods | The same rule as methods; an operator's name is its kind, so `+` matches `+` and `checked -` matches `checked -` |
| Public operators | Copy from an interface operator of their type with the same kind and parameter count, implicitly as for public methods; otherwise R4's inference from the body | Operators are always public, so this is the same rule as for public methods |
| Accessors | Follow the copied type: `get`, `set`, `init`, `add`, `remove`, auto-property and field-like event backing fields | They read the member's type, which is set before anything reads it |
| A property or indexer with nothing to copy | R4's CS20015 stays | Inferring from the getter is R4's open point ([#70](https://github.com/ok3mad/roslyn/issues/70)); a property's type is read by its accessors and backing field before a lazy type exists |
| An event with nothing to copy | CS20055, new | An event has no body to infer from, so CS20015's "not yet" would be wrong |
| Events searched in their constructor | Accepted | Events bind their type there in C#, and already find the overridden event there; the search reads only other types |
| Messages | P2's messages name the member as written: `this` for an indexer, `operator +`, `implicit operator`. A ruled-out property or indexer says "it returns" its type | One message set for every kind |

Tests: 12 `InferMemberKinds_*` tests in `SharperTests_InferMemberKinds.cs`: the issue's examples next to C# twins with
runtime output, accessors of every kind with expression and block bodies, explicit implementations, operators
(binary, unary, checked, equality, explicit conversion, overloads narrowed by written types), narrowing indexer
overloads by written types, P1 types and ref kinds, the errors (CS20043 with ruled-out members, CS20044), members
with nothing to copy (CS20015, CS20055, and CS0246 with the flag off), generics, nullability and tuple names, the
semantic model, metadata seen from a C# compilation, an indexer cycle from either end, and concurrent reads.
`InferMember_Operators_Errors` lost its CS20015 row for an explicit operator implementation, which now copies.

## Feature 9: inferred parameter type, step P3

`readonly infer x` is a parameter whose type is inferred (P1 or P2) and that cannot be reassigned:
`void Fetch(readonly infer timeout = TimeSpan.FromSeconds(5)) { timeout = default; }` reports CS1656 (and CS1736 for
the default value, as its C# twin does), on branch `sharper/readonly-infer-p3`
([#74](https://github.com/ok3mad/roslyn/issues/74), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md#readonly-infer-step-p3)). No compiler change.

**Probe.** The two mechanisms do not meet: `readonly` is read from the parameter's modifiers
(`ParameterHelpers.IsSharperReadOnly`, carried by `SourceComplexParameterSymbolBase.IsSharperReadOnly` and checked in
`CheckParameterValueKind` and `IsSharperValValue`), and the type comes from the `infer` branch of `MakeParameter`. P2's
search reads ref kinds with `GetModifiers`, which ignores a leading `readonly`, so `readonly infer r` copies `ref` from
`M(ref int r)` and is then a `readonly ref` parameter. Every probe gave the diagnostics of the twin with the type written
out, so the step is tests and docs.

| Question | Decision | Why |
| --- | --- | --- |
| Sources | Both on `main`: default values (P1) and the overridden or implemented member (P2); the body sources (P4 to P6) will combine the same way | `readonly` is a rule about the body and plays no part in the signature, so it cannot affect a source |
| Copied ref kinds | `readonly infer r` over `ref int r` is `readonly ref int r`: writes through the ref allowed, ref reassignment CS1656 | The same reading as a written `readonly ref` |
| `val x` on method parameters (open point) | Not built; [#178](https://github.com/ok3mad/roslyn/issues/178) (`follow-up`, `needs-decision`) | It fits the grid, but on a method parameter it works only where a source exists; a decision, not glue code |

Diagnostics: none new. Tests: 7 `ReadonlyInfer_*` tests in `SharperTests_ReadonlyInfer.cs`: runtime output next to the
written twins (methods, local functions, constructors, primary constructors, lambdas, overrides and implementations),
writes in every member kind, the copied and written ref kinds, struct copies (CS20013) from both sources, and the
flag off.

## Feature 9: inferred parameter type, step P4

`public infer Hello(infer id) => profiles.Load(id);` gives `id` the type `int` when `IProfiles.Load(int)` is the only
`Load`, on branch `sharper/infer-parameter-p4` ([#75](https://github.com/ok3mad/roslyn/issues/75), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md#from-uses-in-the-body-step-p4)). Source 4 of the
brainstorm, first cut: the parameter, passed directly as an argument to a method, constructor or delegate invocation
with exactly one candidate, gets that candidate's parameter type. No syntax, bound node, public API or lowering.

**Probe.** Roslyn binds a body only after the signature exists: `InMethodBinder` looks every name up in
`method.Parameters`, which completes the signature, which needs the type being inferred. Binding the body with the
method itself would make every name lookup a cycle, and the binder reads the containing method's return type, ref
kind and async state in more places than the user wrote (trap 27). The precedent is the lambda: `UnboundLambda`
binds the same body once per candidate delegate type, each time with a fresh `LambdaSymbol` whose parameters have
that delegate's types, and keeps or drops each binding.

### The mechanism

The body is bound once with a stand-in for the method, the **placeholder method**, whose `infer` parameters have a
placeholder type; the pinning uses are read from the bound tree; then the signature is completed and the body is
bound normally, against the real types, by the method compiler, which reports its errors. P5 and P6 add sources by
reading more of the same bound tree, not by changing the binding.

| Piece | What it does | Extended by |
| --- | --- | --- |
| `SharperPlaceholderMethodSymbol` | A `SourceMemberMethodSymbol` over the method's syntax, modifiers, flags and type parameters, outside its type's members, reporting no diagnostics. Its parameters are synthesized: written types, P1 types, and the placeholder type (an error type) for the parameters being inferred. Its return type is the written one (`void` for a constructor), or, for `infer`, "being inferred" (`LambdaSymbol.ReturnTypeIsBeingInferred`, and `Binder.IsSharperReturnTypeBeingInferred` is true for it), so `return`, `yield` and `async` never read the method's signature. Its body binder is the method's own chain with the `InMethodBinder` replaced | P6 reads its written return type for `return x` |
| `SharperPlaceholderMethodSymbol.ReplaceOriginal` | A call in the body whose candidates include the method itself (recursion, or an overload of it) binds to the placeholder instead: in `OverloadResolution.MethodInvocationOverloadResolution` and `Binder.GetAccessibleConstructorsForOverloadResolution`, when the binder's containing member is a placeholder. The placeholder's parameters being inferred pin nothing; its written ones pin | |
| `SharperInferredParameterType.GetBodyInference` | Binds the body (`Binder.BindMethodBody`, constructor initializers included) with the placeholder's body binder. Runs from `InferBeforeSignature`, after P1 and P2, outside the signature lock, only when an `infer` parameter has no default value and P2 has no member to copy from; the result is kept by the compilation per parameter list (`SharperBodyInferences`) and published once, like P1's and P2's. `GetType` reads it for such a parameter | |
| `UsesCollector` | Walks the bound body, lambdas and local functions included, and collects every *use* of a placeholder parameter that pins a type: the parameter itself as a by-value argument of a call whose candidate set has one member (`BoundCall.Method` and `OriginalMethodsOpt`, `BoundObjectCreationExpression`, `BoundBadExpression.Symbols` for a constructor that failed), mapped to a parameter by position or name, that is neither `params` nor `ref`/`out`, and whose type involves no type parameter out of the signature's scope. It also records each parameter's first use that pins nothing, and why | P5 widens "one candidate" (overloads, generic inference from the other arguments, `params`) with the candidates overload resolution records (step P5, below); P6 adds assignments, returns, `ref` and `out` arguments |
| `DecideFromUses` | One type per parameter from its uses: none is CS20027 with the reason of the first use that pins nothing (an additional location) or "the body does not use it"; uses that agree (the same type ignoring nullability and tuple names, merged as inputs: not annotated wins) give that type; uses that disagree are CS20057, which names two uses, their types and candidates | P5 and P6 add uses, not rules |
| The P1 stack | The body inference is on the thread-static stack of P1 and P2, keyed by the parameter list, so a body that needs the method's signature through other members whose types are inferred is a cycle found from either end: CS20056 on every parameter inferred from that body, decided and published with the rest of the cycle (`PublishCycle`) | |
| `SharperInferredReturnType.SuspendInference` | Hides R3's frames while the placeholder body is bound: that body is not an R3 frame, so a read of an inferred return type there starts its own frames instead of counting as a use by whichever body R3 is binding below it. That keeps cycles through P4 and R3 independent of the starting member | |

The placeholder type is an error type, so a call that it pins fails overload resolution in that binding, and the
binder's error recovery keeps the single candidate and the call's return type, so the rest of the body binds as it
will with the real type: `profiles.Load(id).Name` still finds `Name`. Every other use of the placeholder (member
access, operators, `is`, interpolation) binds to an error silently. All diagnostics of the placeholder binding are
dropped. Results computed inside it that depend on the placeholder are not cached: the P1 type of a lambda or local
function parameter declared in the body is computed, not published, while that body is bound with placeholders.
Everything the body reads of other members (signatures, inferred return types, default values) does not depend on the
placeholder and is inferred and published as usual.

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Which members | Methods of any accessibility (also extension methods), constructors, and user-defined operators and conversions, when an `infer` parameter has no default value and P2 has no member to copy from. Local functions, lambdas, indexers, primary constructors and delegates keep CS20027 | They are the members with a body and a signature lock (`InferBeforeSignature`). A local function's body binder is built by the enclosing body's binder map, so it needs a placeholder of its own; P7 starts with local functions and can add it |
| Precedence of sources | P2, then P1, then the body: the body is bound with placeholders only when neither gives the type. A use that does not accept the type of a higher source reports the C# error of the written twin (CS1503 names both types) | P2's precedent: a default value narrows the candidates, and a mismatch is ordinary C#. Binding every overriding member's body to find a disagreement would add a body binding, and its cycles, to members that need none |
| Several uses | Must agree; disagreeing uses are CS20057, naming two uses with their types and candidates, pointing at both. Uses that pin nothing do not count | P6's rule, already needed with one-candidate calls (`Take(x); TakeLong(x);`) |
| Lambdas and local functions in the body (the open point) | Their uses count like any other: the parameter has one type in the whole body. Only the final binding of a lambda is read (the bound tree), never the trial bindings overload resolution makes for other delegate types. A lambda or local function parameter with the same name hides the parameter and is not a use | A trial binding with a rejected delegate type can find other candidates; the bound tree holds only the chosen one |
| What a candidate means | The members of the method group (or the constructors of the type, or the delegate's `Invoke`) that overload resolution considered. One is P4; more is P5 and pins nothing yet | A count that does not depend on applicability keeps P4 independent of the other arguments, which P5 must look at |
| `params`, `ref`, `out`, `in` | A `params` parameter pins nothing (normal and expanded form are two candidates: P5); `ref` and `out` arguments are P6; a by-value argument to an `in` or `ref readonly` parameter pins its type | The exact-type rule of `ref` belongs with P6's other uses |
| Generic candidates | A parameter type that involves the candidate's own type parameters pins nothing unless the type arguments are written (`Generic<long>(x)`); other parameters of a generic method pin. Types of the candidate's containing type are substituted (`IRepo<User>.Put(T)` pins `User`); the method's own type parameters are in scope, a local function's are not | Inferring type arguments from the other arguments is P5 |
| Member access on the parameter | Pins nothing, also as the receiver of an extension method (`x.Twice()`), although the binder finds `Twice(this int)` for an error-typed receiver | Shape never pins (the brainstorm's rule) |
| Recursion | A call to the method itself or to one of its overloads binds to the placeholder, so it reads no signature that is being inferred and is no cycle. The parameters being inferred pin nothing there | Recursion is common, and the call needs only the placeholder's parameters. R3 keeps CS20018 for a return type that needs itself; a parameter type does not |
| Cycles through other members | CS20056 on every parameter inferred from a body of the cycle, whichever member is used first. Overload resolution reads every candidate's signature, so two constructors with `infer` parameters whose initializers call each other's overloads are a cycle | The P1 stack's rule; replacing only the method itself keeps every other read real |
| CS20027 (P8's rewording) | Says why nothing pinned the parameter, from its first use that pins nothing ("a member access pins nothing", "a call to 'Over', which has more than one candidate, pins nothing yet", ...), or "the body does not use it"; "nothing else gives its type" where the body is no source | Decided in #79: the error asks for the type and says what would have pinned it |
| Nullability | The type is copied as the candidate declares it (`Load(string?)` pins `string?`), as P2 copies | The candidate's declaration is the source |
| Thread safety | The result is published once per parameter list, after the cycle results decided for it (`Publish`); racing threads compute the same result | P1's and P2's rule |
| IDE cost | One extra binding of the body, without flow analysis, per member that has an `infer` parameter with neither a default value nor an overridden member, once per compilation (cached by syntax). Other members are not rebound; an edit makes a new compilation, which infers again, as for return types | |

Diagnostics: CS20056 (cycle) and CS20057 (uses disagree) are new; CS20027 has a second argument, the reason. Tests: 12
`InferFromBody_*` tests in `SharperTests_InferFromBody.cs`: runtime output next to C# twins for every kind of call
(the issue's example, constructors and initializers, delegates, named arguments, extension methods, lambdas and local
functions, `async`, iterators, operators and conversions), what pins nothing and why, agreeing and disagreeing uses
with nullability and tuple names, precedence over P1 and P2, generics, recursion, cycles from every starting member,
concurrent inference, the semantic model and IOperation, metadata seen from a C# compilation, default values of
lambdas and local functions in the body, and the flag off. The CS20027 expectations of P1 and P2 tests now carry
their reason. All 434 Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug):
Semantic 20,256 passed, Symbol 13,923 and Emit 7,312, none failed; the Syntax project with `ROSLYN_TEST_SHARPER=1` has
the 37 expected failures listed under Follow-ups and no other; the analyzer build reports no error.

Follow-ups:

- A call elsewhere to a member of a body cycle (CS20056) reports CS1503 for the argument whose parameter is an error
  type.
- Local functions, lambdas and indexers as members whose body is a source (with P7 for local functions).
- Edit and Continue: a body edit that changes a pinning use changes the signature, as for an inferred return type
  (R4's rude edit) and the copied member ([#169](https://github.com/ok3mad/roslyn/issues/169)).

## Feature 9: inferred parameter type, step P5

`infer Show(infer n) => Format(n, "N2");` gives `n` the type `int` when every `Format` that accepts a string second takes
an `int` first, and `infer Pick(infer key) => dict.GetValueOrDefault(key);` gives `key` the dictionary's `TKey`, on
branch `sharper/infer-parameter-p5` ([#76](https://github.com/ok3mad/roslyn/issues/76), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md#from-overloaded-and-generic-calls-step-p5)). P4's
mechanism, extended: the body is still bound once with the placeholder, and the call still fails there; what is new is
which candidates count, read from overload resolution. No syntax, bound node, public API or lowering.

**Probe.** With the placeholder type an error type, `MethodTypeInferrer` takes no bound from it (`IsReallyAType`), so a
type parameter that other arguments fix is fixed (`GetValueOrDefault<string, int>`), and the call then fails on the
placeholder argument alone: `BadArgumentConversion`, with the bad arguments listed, since a failed call is resolved
again with complete results. Overload resolution keeps one form per `params` candidate (the normal form, unless the
expanded form is preferred), and drops the results when the call ends; the bound tree keeps only the candidates
(`OriginalMethodsOpt`). Bound nodes are no key for what overload resolution saw: the binder clones parameters
(`WithWasConverted` in Debug, `WithHasErrors`, `WithSuppression`), and a lambda's body is bound once per delegate type.

### What a candidate means

P4's table, row "What a candidate means", is extended; the rows of P4 stay as they were.

| Call | Candidates that count | Pins |
| --- | --- | --- |
| A group with one member, non-generic at the position and not `params` | That member, applicable or not (P4) | Its parameter type |
| A group with one member whose parameter type involves its own type parameters | The member if it is applicable with the placeholder in place, constructed with the type arguments inferred from the other arguments and the receiver | The substituted type; a type parameter that nothing fixes pins nothing |
| A `params` parameter at the position | The normal and the expanded form, each if applicable with the placeholder in place | The type both forms take; `Sum(x)` (`int[]` and `int`) pins nothing, `Sum(1, x)` (expanded form only) pins `int` |
| A group with several members (methods, extension methods, constructors, constructor initializers) | Every member and form applicable with the placeholder in place, from the first overload resolution of the call that has any: instance methods, then the extension method scopes from the innermost, as C# stops there | The type every one takes, ignoring nullability and tuple names (merged as P4 merges uses); otherwise nothing, and CS20027 lists them |
| A delegate invocation | The delegate's `Invoke` (P4) | Its parameter type |

**Applicable with the placeholder in place.** The other arguments are bound and converted as in C#, and a candidate is
applicable when the placeholder arguments are its only bad arguments, each passed with a ref kind its parameter accepts
(the ref kind itself, or by value, `in` or `ref` to an `in` or `ref readonly` parameter). Argument count, names and
optional parameters decide as in C#. A candidate whose type inference failed counts only when a placeholder goes to a
parameter whose type involves its type parameters, since the placeholder's type could have fixed them; it then takes a
type parameter there, which pins nothing. Better and worse candidates are not compared: the placeholder has no type to
compare by, so a candidate that would lose to another for one type of the parameter still counts. When several
placeholders are arguments of one call, each is assumed to fit, independently. A placeholder that makes a candidate
applicable is never the question: the placeholder converts to nothing, so it only ever makes candidates inapplicable,
and the definition puts it back in place.

### The mechanism

| Piece | What it does |
| --- | --- |
| `OverloadResolution.RecordSharperPlaceholderCandidates` (`OverloadResolution_Sharper.cs`) | Runs at the end of `MethodInvocationOverloadResolution` (not for method group conversions, function pointers or dynamic calls) and `ObjectCreationOverloadResolution`, when the binder's containing member is a placeholder and an argument is a parameter being inferred (`SharperPlaceholderMethodSymbol.IsBeingInferred`). Skips a call that succeeded. Reads the complete results; for a `params` candidate, evaluates both forms again (`IsMemberApplicableInNormalForm` and `...InExpandedForm`, or the constructor twins). Per placeholder argument, the applicable candidates with the parameter and the type each takes (the element type in the expanded part) |
| `SharperPlaceholderMethodSymbol.RecordCandidates` and `GetCandidates` | Keep the records per argument syntax and per owner of the binding (`Binder.ContainingMemberOrLambda`: the placeholder, a lambda symbol of one delegate type, or a local function), in the order of the resolutions. `GetCandidates` returns those of the first resolution that has any, empty if none has, default if the call recorded nothing. Freed after the collector ran |
| `UsesCollector.AddUseFromCandidates` | Tracks the owner while it walks (`VisitLambda`, `VisitLocalFunctionStatement`), so it reads the records of the binding the bound tree holds. A call with several candidates, or a single candidate whose parameter is `params` or involves its type parameters, asks for the records: candidates that agree pin, a type parameter or the forms of one method keep P4's reasons, and candidates that disagree are listed |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Which other arguments are bound first | All of them, as C# binds them: their conversions, named arguments, optional parameters and type inference decide applicability; the placeholder arguments are assumed to fit | The issue's definition, and C# already does the work: the complete results list every bad argument |
| A group with one member | Keeps P4's count rule: it pins whether or not the other arguments fit, except at a type parameter or `params` | Every P4 result stays as it was; the final binding reports the C# error for the other argument |
| Betterness | Not applied | Without a type for the placeholder, betterness could rule out a candidate that would win for another type; the rule asks that every applicable candidate agree |
| Extension methods | The first scope with an applicable candidate, after instance methods | C#'s order; the receiver is an argument there and still pins nothing (P4) |
| `params` | Two candidates, one per form | Both are C# candidates; the issue lists `params` at the position |
| Candidates that disagree | Pin nothing. CS20027's reason lists every applicable candidate and the type it takes ("'C.Over(int)' takes 'int'", "... in its expanded form"); none applicable is "no candidate of a call to 'Over' accepts the other arguments". The forms of one method keep P4's reasons (type parameter, `params`) | The issue asks for the list; an existing message is extended with a reason, no new code |
| Several uses | A use in an overloaded call is one use, with the first applicable candidate as its candidate in CS20057 | P4's rule |
| Recursion | An overload of the method itself counts like any candidate (`Overload(x, 0)` pins `string` from `Overload(string, int)`); the placeholder's parameters being inferred pin nothing | P4's placeholder replacement; this changed one P4 test expectation, which had no candidate rule for overloads yet |
| P4's reasons | Unchanged wording, also where P5 now pins in other cases ("no use passes it directly to a call with one candidate", "... which pins nothing yet") | P4's diagnostics stay as they were; the only P4 expectation whose text changed is the overloaded call `Over(x)`, which now lists its candidates |
| What is recorded | Per argument syntax and per binding owner, not per bound node | Bound nodes are cloned, and a lambda is bound once per delegate type; the collector reads only the binding the bound tree holds, as P4 does |
| IDE cost | One more evaluation of the expanded form of each `params` candidate of a failed call with a placeholder argument, in the placeholder binding only; nothing in compilations without Sharper beyond a containing-member check per constructor resolution | The records reuse the complete results that C# computes for a failed call anyway |

Does not block P6 or P7: P6 adds uses (assignments, returns, `ref` and `out`) to the same collector, and the records'
ref kind check already rules out `ref` and `out` parameters for by-value placeholders; P7's conversions of a method
group are not recorded (`IsMethodGroupConversion` is skipped).

Diagnostics: no new code; CS20027 has three new reasons (`IDS_SharperInferCandidatesDisagree`,
`IDS_SharperInferCandidateTakes` and `...Expanded`, `IDS_SharperInferNoApplicableCandidate`). Tests: 7
`InferFromOverloads_*` tests in `SharperTests_InferFromOverloads.cs`: the issue's examples with runtime output next to C#
twins (overloads, `GetValueOrDefault`, generic inference from other arguments, named and optional arguments, the expanded
form of `params`, constructors and constructor initializers, extension methods, lambdas), applicability (argument count,
other arguments, names, optional parameters, ref kinds, `in`, the expanded form), generics fixed by arguments, the receiver
and the containing type, what pins nothing and the listed candidates, lambdas bound for several delegate types and local
functions, and uses that agree or disagree. Two P4 expectations changed: `Over(x)` lists its candidates, and
`Overload(x, 0)` now pins.
All 448 Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug): Semantic
20,270 passed, Symbol 13,923 and Emit 7,312, none failed; the Semantic test project builds with `-warnaserror`; the
analyzer build reports no error (its three RS0062 warnings are in P2's `SharperInferredParameterType_OverriddenMember.cs`,
as on `main`).

## Feature 9: inferred parameter type, step P6

`void Rename(infer name) { this.Name = name; }` gives `name` the type of the property, and `int Clamp(infer x) { return x; }`
the written return type, on branch `sharper/infer-parameter-p6` ([#77](https://github.com/ok3mad/roslyn/issues/77), feature
page [inferred-parameter-type.md](../features/inferred-parameter-type.md#from-assignments-returns-and-ref-arguments-step-p6)).
P4's mechanism, extended as P4 and P5 planned it: new kinds of use in `UsesCollector`, read from the same placeholder
binding. No syntax, bound node, public API, lowering or new diagnostic code.

**Probe.** In the placeholder binding, `ref`, `out` and `in` arguments already reach the collector like by-value ones: a
call with one candidate fails and keeps it, and P5's records already list the candidates whose parameter takes the
argument's ref kind. An assignment, a local's initializer and a returned value are converted to the target type; the
conversion from the placeholder type is an error, a `BoundConversion` over the parameter with the parameter's syntax and
no cast in code, but not marked compiler-generated, so P4's argument check (which strips only compiler-generated
conversions) does not see through it. A `var` local initialized with the parameter gets the placeholder type. An
expression body is a `return`.

### What pins

| Use | Pins | Notes |
| --- | --- | --- |
| `y = x`, `T y = x;`, `r = ref x`, `ref T r = ref x;` | The type of the local or parameter | A `var` local and a parameter being inferred have the placeholder type and pin nothing |
| `F = x`, `P = x`, `this[i] = x`, `new C { P = x, F = x }` | The type of the field, property or indexer, as declared there, with the containing type's type arguments substituted | |
| `return x;`, `=> x` | The written return type of the method, local function or lambda (`int () => x`) it returns from; `T` for `async Task<T>`, the referenced type of a `ref` return | Not an `infer` return type, a lambda without a written return type, `void`, `async Task` or an iterator. A `yield return` pins nothing |
| `M(ref x)`, `M(out x)`, `M(in x)` | The candidate's parameter type, exactly | The parameter must take the ref kind, as overload resolution accepts it (`ref` and `in` to an `in` or `ref readonly` parameter). Overloads and generic candidates follow P5's records |

A type that involves a type parameter out of the signature's scope (a local function's own) pins nothing, as for calls.
The value must be the parameter itself, through the target's implicit conversion; a cast or any other expression around it
pins nothing.

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Which assignments | Locals and parameters with a known type, fields, properties and indexers, also in object initializers; not array elements, events, pointers or deconstructions | The issue's list; an indexer is a property; an object initializer member is an assignment to one |
| Returns from lambdas | Pin only with a written return type (`int () => x`); `Func<int> f = () => x` pins nothing | A lambda's delegate type can come from overload resolution's error recovery in the placeholder binding (`Run(() => x)` with two `Run` overloads), and the issue asks for a written return type. A local function's return type is written unless it is `infer` |
| `ref`, `out` and `in` arguments | Pin the parameter type of a candidate that takes the ref kind; one by value to a `ref` or `out` parameter, or by reference to a by-value one, pins nothing ("an argument whose ref kind its parameter does not take pins nothing", the reworded P4 reason) | "Exact type": no conversion applies, so the type is the parameter's. The agreement rule is the same as for other uses |
| Several uses | Must agree, as in P4: CS20057 keeps its arguments (two uses, each with its type and what it is) and names an assignment target (`'C.Name'`, `'y'`) or a return (`'the return of C.Clamp'`, `'the return of L'`, `'the return of a lambda expression'`) where a call names its candidate | Extends the existing message; every P4 and P5 expectation stays as it was. Naming two uses that differ is enough to fix either |
| Precedence across sources | Unchanged: P2, then P1, then P7 (a slot left for #78 in `InferBeforeSignature`), then the body. The body is bound only when no higher source gives the type, and a use that disagrees with that type reports the C# error of the twin, which names both types (CS0029 `int` to `string`, CS1503 `ref int` to `ref long`); P1 against P2 is CS20043, which names the member and the type it takes | P4's decision: an error of its own would need the body bound for every member with a default value or an overridden member. The twin's error already names both types |
| Lambdas and local functions | Uses in them count like any other (P4), and a return pins the return type of the lambda or local function it returns from | P4's rule |
| Nullability | Merged as for calls: not annotated wins (`string? a = x; string b = x;` gives `string`), also for `ref` uses | One rule for every use; nullable analysis then warns as for the twin |

Diagnostics: no new code. CS20027's reason "a ref or out argument pins nothing yet" is now "an argument whose ref kind its
parameter does not take pins nothing" (`IDS_SharperInferRefArgumentPinsNothing`); CS20057's use slots take two new
fragments (`IDS_SharperInferReturnOf`, `IDS_SharperInferReturnOfLambda`). Tests: 5 `InferFromUses_*` tests in
`SharperTests_InferFromUses.cs`: every kind of use with runtime output next to C# twins (the issue's examples, fields,
locals, parameters, `out`, `ref`, `in`, `ref` returns, object initializers, indexers, `async`, lambdas and local functions
with written return types, the method's type parameters), the semantic model, uses that disagree with where each error
points, what pins nothing, precedence over P1 and P2, and nullability. Two expectations of earlier steps changed, both
for `ref` arguments: `Ref(ref x)` and `Out(out x)` in `InferFromBody_PinsNothing` now pin, and `Over(ref x)` in
`InferFromOverloads_PinsNothing` has the reworded reason. All 461 Sharper test cases pass, also with
`ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug): Semantic 20,283 passed, Symbol 13,923 and Emit 7,312, none
failed; the Semantic test project builds with `-warnaserror`; the analyzer build reports no error (its warnings are the
three RS0062 in P2's `SharperInferredParameterType_OverriddenMember.cs`, as on `main`).

Follow-ups:

- A return from a lambda without a written return type, `yield return`, array elements and deconstruction pin nothing.
- CS20057 names the first two uses that differ, not every use.

## Feature 9: inferred parameter type, step P7

`xs.Select(Format)` gives `infer Format(infer x)` the type `int` from `Func<int, TResult>`, on branch
`sharper/infer-parameter-p7` ([#78](https://github.com/ok3mad/roslyn/issues/78), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md#from-conversions-to-a-function-type-step-p7)). Source 3
of the brainstorm, for local functions. No syntax, bound node, public API, lowering or new diagnostic code.

**Probe.** Every method group conversion to a delegate type goes through `OverloadResolution.MethodInvocationOverloadResolution`
with `Options.IsMethodGroupConversion` and the delegate's parameters as the arguments: the conversion itself
(`Conversions.MethodGroupConversion`), its diagnostics, and method type inference's output type inference from a method
group (`MethodTypeInferrer.MethodGroupReturnType`), which runs in the second phase once the delegate's input types are
fixed. So one hook sees `Func<int, string> f = Format;`, `Run(Format)` and `xs.Select(Format)`. A local function holds no
signature lock and computes its parameters on demand, while the body that declares it is bound; that body can be bound
again with a body binder of its own (`TryGetBodyBinder`, a new `CreateBodyBinder` for top-level statements), whose
local functions are new symbols. A lambda whose body has errors stays unbound in the tree (`BoundUnboundLambda`).

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Which members | Local functions. Private methods are [#207](https://github.com/ok3mad/roslyn/issues/207) | A local function's conversions are all in one body. A private method's are in every member body of its type, which would have to be bound with a stand-in for the method in every lookup, with the cycles of P4 and R3 across those bodies |
| Mechanism | Bind the declaring body once more with an error type for the local function's parameters; overload resolution of a method group conversion records the delegate's parameter types for the local function (`RecordMethodGroupConversion`); only method groups that the final bound tree holds count | P4's placeholder binding, applied to the declaring body. Recording in overload resolution sees the delegate types that method type inference fixes, which the bound tree of a failed call does not hold |
| Conversions after the declaration (open point) | Count: every conversion in the declaring body, before or after the declaration, also in lambdas and other local functions there | A local function is in scope in its whole block, and the body is bound as a whole anyway |
| Generic calls that also infer from the method group (open point) | Count once the delegate's input types are fixed: `xs.Select(Format)` records `int` from `Func<int, TResult>`, and `TResult` then comes from `Format`'s inferred return type. A delegate whose parameter types involve a type parameter that is not fixed records nothing (`Apply<T>(Func<T, string>)`, CS0411 as in C#) | Method type inference resolves the method group only after fixing its input types, so the hook gets them for free. No error of its own is needed |
| Fit | The parameter count, the written ref kinds and the written parameter types (identity or implicit reference conversion from the delegate's) must fit; otherwise the conversion records nothing, and C# reports CS0123 | As a lambda: an explicitly typed part must match, and the rest is copied |
| Several conversions | Must agree, as in P4; CS20057 names two delegate types with a new fragment, "the conversion to System.Func<int, string>" (`IDS_SharperInferConversionTo`). The overloads of one call that take different delegate types are CS20057 too | Extends the existing message. Choosing among overloads would need the type being inferred; an error that asks for it is the small first slice |
| Nullability | Merged with annotated winning (`Func<string?, int>` and `Func<string, int>` give `string?`) | The delegate passes its arguments to the parameter, the reverse of P4 to P6's uses, so the parameter must accept every one |
| Precedence | P1 (a default value) first; local functions have no P2 and no body source. A conversion that disagrees with a default value reports the twin's CS0123 | P6's rule: the twin's error names the types |
| Nothing pins | CS20027 keeps "nothing else gives its type", as before P7: calls to the local function, `var f = F;`, `Delegate d = F;` and conversions that do not fit | Keeps every earlier expectation; inference from callers is dropped |
| Bodies bound with placeholders | A local function in a body that P4 binds with placeholders for its member's parameters is not inferred there (its result would depend on them, and that binding reports nothing); the other local functions of a body being bound for P7 are placeholders too, unless already inferred | Keeps P4 to P6 results unchanged, and binds the body once per local function instead of once per combination |

Diagnostics: no new code; CS20057 takes the new fragment. Tests: 6 `InferFromConversions_*` tests in
`SharperTests_InferFromConversions.cs`: every kind of conversion with runtime output next to C# twins (the issue's
example, a conversion before the declaration, a cast, a generic call, a custom delegate, a function type, a written
parameter, `ref`, local functions in a lambda, in a local function and in an accessor), top-level statements, the
semantic model, conversions that disagree, what pins nothing, precedence and P4 together, and nullability. No earlier
expectation changed. All 467 Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0,
Debug): Semantic 20,289 passed, Symbol 13,923 and Emit 7,312, none failed; the Semantic test project builds with
`-warnaserror`; the analyzer build reports no error (its warnings are the three RS0062 in P2's
`SharperInferredParameterType_OverriddenMember.cs`, as on `main`). After merging `main` (R5's IDE), the Sharper tests
pass again, also with `ROSLYN_TEST_IOPERATION=1`, and both analyzer builds (compiler, C# Features) report no error. R5's
fix for CS20057 offers, at a conversion, the delegate's parameter type instead of the candidates of the call around it
(`SharperWriteInferredTypeCodeFixProvider.TryAddTypeFromConversion`, test
`CS20057_ConversionsDisagree_OffersEachParameterType`); the 76 Sharper tests of the C# Features project pass.

Follow-ups:

- Private members ([#207](https://github.com/ok3mad/roslyn/issues/207)).
- The overloads of one call that take different delegate types are CS20057; the conversions of a lambda's trial binding
  for another delegate type count when they are at a method group of the final binding.
- After CS20057 or CS20027, C# reports its own errors for the conversions to the error type (CS0123, CS1503).
- The declaring body is bound once more per local function with such a parameter, once per compilation.

## Feature 9: inferred parameter type, step P7b

`static infer Format(infer x)` gets `x: int` from `xs.Select(Format)` in any member body of its type, on branch
`sharper/infer-parameter-p7b` ([#207](https://github.com/ok3mad/roslyn/issues/207), feature page
[inferred-parameter-type.md](../features/inferred-parameter-type.md#private-methods-step-p7b)). Source 3 of the
brainstorm, for private methods; it extends P7's mechanism (the frame, `RecordMethodGroupConversion`, the sites walker,
`DecideFromUses`) rather than adding a second one. No syntax, bound node, public API or lowering.

### Design

| Question | Decision | Why |
| --- | --- | --- |
| Which members | Private ordinary methods (`DeclaredAccessibility` private, not partial, not an explicit implementation), for their `infer` parameters without a default value. Precedence: P2, P1, P7b, then the body (P4 to P6). Every conversion must agree (CS20057 with P7's fragment); a parameter that no conversion gives falls through to the body | As #78 says; other members would be inference from callers, which is dropped |
| Which bodies are bound | The private method's accessibility domain: every declaration of its containing type, other partial declarations and nested types at any depth included. In it, only the bodies that name the method without invoking it (an identifier or generic name with its name that is not the expression of an invocation, `SyntaxFacts.IsInvoked`): only those can hold a method group conversion of it. Bodies are those of methods, constructors (with their initializer), destructors, operators, conversions and accessors, and field and property initializers. A per-type index from name to bodies is built once per type by one syntax walk | A body that only calls the method converts nothing, so binding it would find nothing. This keeps every program without such a method group exactly as before |
| Per member or one pre-pass | Per member: one inference per private method, binding the bodies that name it. A body is bound once per inferred method it names as a method group, and never if it names none, so no body of a large type is bound per inferred member unless it names each of them. Not a single pre-pass over the type | The cost is bounded by the number of method group mentions, not by members times bodies. A pre-pass would bind the same bodies once but would make all the private methods of a type one inference unit: one member's cycle would fail all of them, and the keys, the stack and the publication of P4 and P7 are per member |
| The stand-in | One placeholder for the method (`SharperPlaceholderMethodSymbol`, as P4's), whose parameters inferred from conversions have an error type, the others their written or P1 type. In the bindings the inference makes (and only there: the binder chain of the binding must contain the root binder the inference made for that body), overload resolution replaces the method by the placeholder among its candidates (calls, method group conversions, `&M`), and so does the natural type of a method group (`var f = Format;`). Every other private method that may infer from conversions (judged from its declaration) is replaced too, by a stand-in that records nothing. The method's own body is bound with that placeholder as its member | A binding that reads the method's signature would be a cycle; a recursive call already binds to the placeholder in P4. Replacing only the method being inferred was tried first: a call to another such method in the same body (`Square(1)` next to `pad = Pad`) started that method's inference, which bound the same body and found the first in progress, a cycle that depended on the order. Checking the root binder rather than the member keeps nested bindings out: an inferred return type (R3) or a body inference (P4) that another member starts during the binding has its own binder chain and sees the real methods |
| Members of the other bodies | A method, constructor, operator or conversion body is bound with a placeholder for its own member too, built as P4 builds one: written and P1 parameter types, an error type for its own `infer` parameters without a default, the written return type or `infer` as a lambda's being inferred. Accessors, destructors and static constructors use their own symbol (their signature has no body source); an initializer is bound as the field's | Binding a body with its real member would read that member's signature and its inferred return type for every `return`, starting the member's own inferences (P4, P7b, R3) inside this one, which then needs this method's signature: a cycle in ordinary code (`infer Run(int[] xs) => xs.Select(Format)`) |
| Cycles with P4 to P6 and R3 | R3's frames are suspended during the binding, as in P4 and P7. The inference is on the same stack as P1, P2 and P4 (keyed by the method's return type syntax, the one node of the declaration that no other source uses as a key). A nested inference that needs the method's real signature (a member called in such a body whose inferred return type calls the method) finds it on the stack: a cycle, CS20063 for each parameter being inferred from conversions, and the other sources' cycle errors for the rest of the cycle, decided before any is published (`PublishCycle`) | The dependency is real: the delegate type depends on that member's return type, which depends on the method's signature. P4 reports the same shape through one body with CS20056 |
| Publication | `CSharpCompilation.SharperConversionInferences`, per parameter list, published once through `Publish` (which prefers a result decided for a cycle); the placeholder and the recorded conversions are per thread (P7's thread-static frames), so racing threads bind the same bodies and compute the same result | P2's and P4's cycle publication: a result is decided for the whole cycle before any is published, so a thread that finds one published adopts the decided results |
| P4 after P7b | The body inference reads P7b's result first: a parameter that a conversion gives has that type in P4's placeholder and is not a target of P4 | The body sees the final type; uses there that disagree are C#'s errors, as for a written type |
| Lambdas and local functions in these bodies | Their default values are not published (the bodies are pushed as placeholder bodies, as in P4), and a local function's own P7 does not run there (its member is a placeholder) | Their results may depend on the placeholders |
| Not covered | Primary constructor base arguments, top-level statements (the method is not in a type), partial methods, extension method groups of private extension methods (the receiver form) | Follow-ups; a method group conversion there pins nothing, as before |

**Cost.** Per private method with such a parameter that is named without being invoked: each body naming it is bound
once more, once per compilation; plus one syntax walk per type for the index. A method that is only called costs one
lookup in the index.

Diagnostics: CS20063, "Cannot infer the type of parameter '{0}' from the conversions of '{1}', because binding the
members that convert '{1}' needs the signature of '{1}', directly or through other members whose types are inferred.
Write the parameter type." CS20056 names one body and cannot say this without changing its arguments, which would change
every P4 cycle expectation.

Tests: 8 `InferFromMemberConversions_*` tests in `SharperTests_InferFromMemberConversions.cs`: every place and kind of
conversion with runtime output next to C# twins (the issue's example, field and property initializers, a cast, a
constructor, a getter with a function type, a written parameter, `ref`, a lambda, a nested type), partial declarations
in two files with inferred return types around the conversion, conversions that disagree, what pins nothing (other
accessibility, calls, natural types, a body source behind them), precedence, nullability, the cycle from every starting
member, and threads. One earlier expectation changed on purpose: P7's `InferFromConversions_PinsNothing` had a private
`Member` whose conversion pinned nothing, which is what #207 changes; it is now public, so the test keeps checking that
other members pin nothing. All 508 Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0,
Debug): Semantic 20,330 passed, Symbol 13,923 and Emit 7,312, none failed; the Semantic test project builds with
`-warnaserror`; the analyzer build reports no error (its warnings are the three RS0062 in P2's
`SharperInferredParameterType_OverriddenMember.cs`, as on `main`).

Follow-ups: partial methods, primary constructor base arguments and top-level statements; a member called next to the
conversion whose inferred return type needs the method's signature is a cycle even where the conversion does not depend
on it; after CS20057, C# reports its own CS0123 for the conversions to the error type, as in P7.

## Feature 10: immutable types, slice 1 (design)

`[Immutable] sealed record Money(decimal Amount, string Currency);` (issue [#139](https://github.com/ok3mad/roslyn/issues/139),
designed in [#92](https://github.com/ok3mad/roslyn/issues/92)). `val` and `readonly` make a binding immutable; this
makes a value deeply immutable, so that "depends only on its arguments" means something for purity
([#36](https://github.com/ok3mad/roslyn/issues/36)), opaque and extractor patterns ([#34](https://github.com/ok3mad/roslyn/issues/34),
[#35](https://github.com/ok3mad/roslyn/issues/35)) and lazy `T[..]`. This section is the design; slice 1 is implemented
as a prototype (see [Implementation of slice 1](#implementation-of-slice-1) and the feature page,
[Immutable types](../features/immutable-types.md)).

**Probe.** What C#, the BCL and the compiler give today:

| Area | What the code shows |
| --- | --- |
| C# | `readonly` fields, `readonly struct`, `readonly` members, `init` and records are all shallow: a `readonly List<int>` field or `record R(List<int> Xs)` can still change through the list |
| BCL | `System.Collections.Immutable` is shallow in `T`, and `ImmutableArray<T>` wraps a `T[]` that `ImmutableCollectionsMarshal.AsArray` hands out. `[ImmutableObject]` is an unchecked designer hint. `ReadOnlyCollection<T>` and `IReadOnlyList<T>` are views over a list that can still change |
| Symbols | Every fact the field check needs is there for source and metadata alike: `FieldSymbol.IsReadOnly`, `TypeSymbol.IsReadOnly` (`readonly struct`), `MethodSymbol.IsInitOnly` and `IsEffectivelyReadOnly`, `IsRecord`. No IL bodies are needed for fields |
| Reference assemblies | They strip private class fields and method bodies, so BCL types compiled against a reference pack cannot be analyzed; `string` and `ImmutableArray<T>` would fail a field check anyway. They need attestations |
| Cycles | Struct layout cycles are already found per strongly connected component (`InfiniteFlatteningGraph`, `SourceMemberContainerSymbol.cs`) |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Depth | Deep, as a property of a type. `val` stays binding-only | Purity needs it; C# already has binding and shallow immutability |
| Definition | Observationally immutable. Hold everything outside the object fixed (statics, clock, randomness, I/O). Then every repeated access returns an equivalent result: structurally (the same exact type, every field equivalent, primitives compared by bits, `[UnobservableWrite]` fields skipped), and the same reference for a result whose type is not statically immutable. Identity is not observable to pure code. Reflection, `Unsafe`, debuggers and timing do not count | A lazy cache does not change what anyone can observe, and representational immutability alone would reject it. Requiring the same object rejected most real getters (`FullName => $"{First} {Last}"`, `Total => new Money(...)`); revised in [#153](https://github.com/ok3mad/roslyn/issues/153) |
| Equivalence | User-written `Equals` is not part of the definition; it is an ordinary observer, which must be pure. Optimizations use a comparer the compiler generates, and every `[Immutable]` type carries a synthesized equivalence member for other assemblies; types that cannot be analyzed get attested equivalences (`string` ordinal, collections element by element) ([#154](https://github.com/ok3mad/roslyn/issues/154)) | `Equals` is weaker than indistinguishable: `1.0m` equals `1.00m` but has another `Scale`, and `0.0` equals `-0.0` but `1 / x` differs. A user's `Equals` can also be wrong. Fields suffice: observers are pure functions of the fields, so equal fields give equal observations. The exact type matters because a subclass can add observers. A mutable result must stay the same reference, because two callers sharing one memoized mutable object would see each other's writes |
| Observers | Instance getters, indexers, `Equals`, `GetHashCode` and `IEquatable<T>.Equals` must be pure. Ordinary methods may be impure, and statics are not observers | `Now => DateTime.Now` has no fields yet changes on every read. A `Roll()` method reads the outside world, and declaring it as an extension method instead could not change the type's immutability; the purity checker sees the effect at the call. Property patterns, extractors and memoization keys rely on stable observers |
| Not implied | Pure methods (`ToString()` may read the culture) | Only observers are required to be pure |
| Identity | Not observable to pure code, so it is impure: `ReferenceEquals`, `==` and `!=` on references without a user-defined operator, `object.Equals` and `object.GetHashCode` where they are not overridden, `lock` and `ConditionalWeakTable`. Comparisons with `null` stay pure, and so does an identity fast path at the start of `Equals` (records generate one). The compiler may merge or split the results of pure code (memoization, sharing) and keeps the identity of `this`. A type without value equality can still be immutable; only calls to its identity-based `Equals` are impure ([#153](https://github.com/ok3mad/roslyn/issues/153)) | The price of equivalence: once identity is invisible, equivalent results are interchangeable. It also settles memoization keys: pure code cannot observe identity and its primitives compare by bits, so equivalent arguments give equivalent results |
| What the compiler proves | Proven: every field is of an immutable type and, in a class, `readonly`; the base is immutable; observers are pure (they read fields, constants and other pure observers, and may create immutable results). Disproven: state writable from outside after construction (a non-`readonly` class field, a non-`init` setter). Otherwise not proven | Inference is only ever from representational immutability; the observational definition is what claims may assert beyond it |
| Purity spiral | Immutability and purity depend on each other. Each step only widens what is accepted, and cycles assume immutable or pure until disproven (`sealed record Node(int V, Node? Next)` passes). Totality is the exception and assumes the worst | A monotone, optimistic fixpoint converges, and later steps break nothing |
| Inferred, declared, attested | Inferred is certain for the binary analyzed but says nothing about the next one. Declared (the owner) and attested (a third party) are promises over versions. Inside one assembly, inference is as good as a declaration and is never emitted as a public contract; across assemblies only claims count | A patch of .NET can change any inferred fact, while the code inside one assembly is the code that runs |
| Conditional and hard claims | `[Immutable]` is conditional: the build fails unless inference proves it, also when the type cannot be analyzed. A hard claim is accepted unless disproven, and names what it vouches for: `[UnobservableWrite]` on a cache field (its reads and writes do not count against an observer's purity), `[AssumePure]` on an observer. A whole-type hard claim exists only in attestations about types that cannot be analyzed | A named obstacle catches the honest mistake of a hard claim going stale (a new mutable field fails the build) without hashing everything the type reaches |
| Trust | Only the project that writes a claim checks it; consumers trust claims and never re-check them, as with nullable annotations. Metadata carries one plain `[Immutable]` | The checks catch honest mistakes; Reflection can break any guarantee anyway |
| Inheritance | `[Immutable]` describes exact instances (`Inherited = false`) and puts no requirement on subclasses: a mutable subclass simply is not immutable. An immutable class needs an immutable base. `[Immutable]` on an abstract class means "my own state is immutable" | Authored in Sharper, `System.Object` would be immutable, and every .NET type cannot be required to be. Records are unsealed by default and .NET has almost no closed hierarchies, so sealing cannot be required either |
| `[ImmutableHierarchy]` | A separate opt-in that every subclass, at any depth, or every implementation of an interface, is immutable. On a class it requires `[Immutable]`. A violating Sharper subclass is an error, a C# one in the same compilation a warning; one elsewhere, or a mock or proxy, is trusted to honour the contract. Adding or removing it breaks compatibility | Library authors who cannot seal (open public hierarchies, interfaces) still need "every value of this type is immutable" |
| Static immutability | Every value of static type `T` is immutable when `T` is a value type with immutable state, sealed and `[Immutable]`, `[Immutable]` with `[ImmutableHierarchy]`, an `[ImmutableHierarchy]` interface, or `[Immutable]` with every subclass visible and immutable (internal types); or when flow analysis knows the exact type | This is what generic conditions and `where T : immutable` use |
| Generics | A generic type is immutable for an instantiation when the type arguments it depends on are statically immutable. A marked type depends on all its type parameters unless one opts out with `[Phantom]`, which the compiler checks. Conditions are computed per definition, so polymorphic recursion terminates. An instantiation such as `Box<List<int>>` is not a requirement, just not immutable | The condition is a public contract. A run-time check cannot afford to walk a collection's elements, so `ImmutableList<Person>` with an unsealed `Person` is not immutable |
| Structs | A struct is immutable when no mutable object is reachable from it; its own fields need not be `readonly`. `(int, string)`, `KeyValuePair<K, V>` and `Nullable<T>` are immutable when their components are | A struct value has no aliases: in a `readonly` location every write goes to a copy, and the rest is binding mutability |
| Construction | Ends after the object initializer, so `init` setters and `with` are part of it | The clone is changed before it is published |
| Run-time guard | Later, with optimizations ([#140](https://github.com/ok3mad/roslyn/issues/140)): where memoization, hoisting or laziness relies on immutability that is not static, check that `x.GetType()` carries `[Immutable]`, cached per `Type` in an emitted helper, else take the unoptimized path | A mutable subclass then costs a missed optimization, never a wrong result |
| Views | Read-only views protect the producer ("you cannot change this"), immutability protects the consumer ("nobody can change this"). Views are never immutable and belong with effects | An `IReadOnlyList<T>` that is a `List<T>` can be cast back |
| Lazy `T[..]` | A collection expression may be made lazy only if every spread source is total and immutable, or created inside the expression | `List<T>` converts to `T[..]` silently |
| One notion of purity (revised in #173) | A pure member's result depends only on the contents of what it reads through its arguments, `this` included, judged by equivalence, not identity. There is no weak/strong split on the member: a call whose arguments are immutable has inputs that cannot change between calls, which is what memoizing, hoisting or making a call lazy needs, and that is checked at the call. Purity holds within one process: `string.GetHashCode` is randomized per process, which is part of the outside held fixed. Compile-time evaluation needs results that are the same across runs, which is settled with `total` | Pure code observes neither identity nor aliasing, so contents are all it can depend on; purity needs no immutable types ([purity](../features/purity.md)) |
| Spelling | Attributes first, embedded like `IsReadOnlyAttribute`; a keyword later. All names are placeholders | No parser change, no break. `readonly class` would clash with the shallow `readonly struct` |

### Plan

1. **The check.** `IsImmutable` for exact and static types over source and metadata symbols: the field and struct
   rules, observers that only read fields, cycles per strongly connected component, generic conditions with
   `[Phantom]`, cached per type.
2. **Attestations.** Hard-coded hard claims for primitives, `string`, the immutable and frozen collections (when `T` is
   statically immutable) and common BCL value types, until the attestation format exists
   ([#142](https://github.com/ok3mad/roslyn/issues/142)).
3. **Attributes and diagnostics.** `[Immutable]`, `[ImmutableHierarchy]`, `[UnobservableWrite]`, `[AssumePure]` and
   `[Phantom]`, embedded. About six diagnostics:
   - not immutable, with the reason;
   - disproven;
   - base not immutable;
   - a subclass or implementation breaks `[ImmutableHierarchy]`;
   - a misplaced `[UnobservableWrite]`;
   - a `[Phantom]` parameter used in state.
4. **Tests** in `SharperTests.cs`, next to the C# they stand for: records, structs and tuples, lazy caches, open and
   hierarchy-marked bases, generics including recursion, metadata types, and the flag off.

Not in slice 1: a keyword, `where T : immutable` and the run-time guard ([#140](https://github.com/ok3mad/roslyn/issues/140)),
the checked lazy-field construct ([#141](https://github.com/ok3mad/roslyn/issues/141)), the attestation format
([#142](https://github.com/ok3mad/roslyn/issues/142)), the Edit and Continue rude edit for an edit that makes a type
mutable, and a fixer that offers `sealed`. As a side effect, a struct proven immutable needs no CS20003 copy warning
([#84](https://github.com/ok3mad/roslyn/issues/84)).

**Size:** M, about 1.5k lines with tests. No parser change; the work is the symbol query, its cycles and generic
conditions, and the diagnostics.

### Implementation of slice 1

Done as planned, with no parser change: `Symbols/SharperImmutability.cs`, about 1.1k lines, and 19 tests in
`SharperTests_ImmutableTypes.cs`. The diagnostics are CS20035 to CS20042. The details the design left open:

| Question | Decision | Why |
| --- | --- | --- |
| How a Sharper file writes the attributes | When lookup finds no type of that name, in a Sharper file, `Immutable` (or `ImmutableAttribute`) binds to `Sharper.ImmutableAttribute`: the one this compilation declares, or else one the compiler synthesizes and embeds (internal, `[Embedded]`, with its `AttributeUsage`), like `IsReadOnlyAttribute`. Other assemblies recognize the five attributes by namespace and name. A type of that name in scope wins | Users write these attributes, unlike `IsReadOnlyAttribute`, so binding must find them; falling back only when lookup fails keeps the C# meaning (design rule 2). One namespace, `Sharper`, keeps them apart from BCL names |
| Inference across assemblies | None: a metadata type is immutable only with `[Immutable]` (trusted) or an attestation. Inside the compilation, unmarked types are inferred, also as static types (a sealed class need not be marked) | "Across assemblies only claims count", and reference assemblies strip private fields, so inference over metadata would be wrong exactly where it matters |
| Generic conditions | Declared, never inferred: every generic type, marked or not, depends on all its type parameters, including those of containing types, except `[Phantom]` ones. A `[Phantom]` parameter in a field type or a non-phantom base type argument is CS20042 | No fixpoint over conditions, so polymorphic recursion terminates by construction. An unmarked generic type that ignores a parameter needs `[Phantom]` too, which is rare |
| Cycles | Tarjan's algorithm on a thread-static stack; a component is cached only when its root finishes. A failure under the optimistic assumption is real; the other members are analyzed again knowing it | The optimistic, monotone fixpoint of the design, without caching results that depend on an assumption |
| Reasons in a cycle | The reason does not depend on where the analysis started: first a reason of the type's own (analyzed with the other source types assumed immutable), else the first check that fails with every status known | Which member is reached first decides where a cycle is found, and so which check fails first; diagnostics must not depend on threads or on which type asked first. Members of a cycle then point to the one that breaks it |
| What an observer may do | Read fields (static ones only when `readonly` or `const`), constants, parameters and locals; write locals and `[UnobservableWrite]` fields; call observers on `this` or `base`, on a receiver whose static type is immutable, or non-virtual ones of an immutable type; use built-in operators, patterns (not deconstruction), `is`, `as`, `?:`, `??`, `??=`, switch expressions, tuples, `typeof` and `throw`. When its return type is statically immutable ([#153](https://github.com/ok3mad/roslyn/issues/153)), also: `new` of a statically immutable type with a pure constructor (one that writes only its own `this`'s fields and auto-properties and otherwise follows these rules, with the initializers it runs and the constructor it calls), string concatenation and interpolation with string or `char` parts, and `System.HashCode`; an object initializer on such a `new`, and `with`, when every setter they run is pure by the constructor rules and a record's clone is synthesized, not virtually dispatched, and runs a pure copy constructor ([#163](https://github.com/ok3mad/roslyn/issues/163)). Not: other calls, other allocations, boxing, lambdas, nested and collection initializers, `await`, and identity: `ReferenceEquals`, reference `==` and `!=` without a user-defined operator (null comparisons are fine), and `Equals(object)` or `GetHashCode()` when the receiver's type does not override them. A hand-written `Equals` may start with `if (ReferenceEquals(this, other)) return true;` | Equivalent results suffice when the result is immutable; a mutable result shared by two callers of a memoized observer would alias, so then nothing may be allocated. Holes other than strings and chars format with the current culture. A call binds to the least derived declaration, so whether `Equals` is identity is decided by the receiver's type. A whitelist of bound node kinds, so new kinds are impure until reviewed |
| Operators | Pure when declared by an attested type and returning a value type (`decimal +`, `string ==`); otherwise `[AssumePure]` | They are not observers, but `Equals` and getters over `decimal` and `string` are common and the BCL operators are known |
| `[AssumePure]` | On a property, an indexer or any method, including operators; never misplaced | A call to it counts as pure anywhere, which is all a hard claim on purity can mean in slice 1 |
| `[UnobservableWrite]` | Only on a non-`readonly` instance field of a class marked `[Immutable]` (CS20041). Its type must still be immutable | Elsewhere it vouches for nothing: a struct's fields need not be `readonly`, and a `readonly` field cannot be written |
| Events and setters | An instance event is mutable in classes and structs; a non-`init` setter only in classes | Subscribing writes a delegate field; a struct setter writes a copy, which the struct rule allows |
| Where claims are checked | In `AfterMembersChecks` of a type declared in a Sharper file; a hierarchy violation by a C# type is a warning only when the hierarchy is declared in a Sharper file of the same compilation | Compilations without Sharper never pay for the check or see a new diagnostic (design rule 1) |
| Exact versus static in the API | `IsImmutable(type, exact)`. A type parameter is not immutable outside its own type's analysis | `where T : immutable` is [#140](https://github.com/ok3mad/roslyn/issues/140) |

Not done in slice 1, beyond the list above: an internal `[Immutable]` class whose subclasses are all visible and
immutable is not yet closed (a `PROTOTYPE(sharper)` comment), and the CS20003 copy warning is unchanged: a struct
proven immutable may still have members that change it, so [#84](https://github.com/ok3mad/roslyn/issues/84) still
needs member mutation inference.

## Feature 11: refined types, slice 1 (design)

`static int Page(int i is >=0) => ...;` and, later, `record Money(decimal Amount is >0, Currency Currency);` (issue
[#33](https://github.com/ok3mad/roslyn/issues/33)). A refinement is a closed pattern over constants attached to a
declaration; a value reaches it only through a door that proves the pattern, and a runtime guard covers every door the
compiler cannot see. This section is the design; steps 1 and 2 are a prototype (see [Progress](#progress-of-slice-1)),
and feature page [refined-types.md](../features/refined-types.md).

**Probe.** What the compiler does today, and what the design reuses:

| Area | What the code shows |
| --- | --- |
| Flag off | `ParseParameter` (`LanguageParser.cs`) reads attributes, modifiers, a type, a name and an optional `= default`; an `is` after the name is a syntax error from the parameter list's recovery. Record parameters take the same path (`ParseParenthesizedParameterList`), and `decimal Amount is >0 { get; }` is a syntax error too, so no valid C# changes meaning |
| Default values | `int i = 0 is >=0` is valid syntax today: a default value of `(0 is >=0)`. The refinement must come before `=` |
| Type positions | In expressions a refinement on a type would clash with valid C#: `e is int is >0` is `(e is int) is >0`. Refinements therefore stay on declarations |
| Pattern parser | `ParsePattern(Precedence...)` (`LanguageParser_Patterns.cs`) parses relational, `and`, `or` and `not` patterns. A relational operand stops at `=`, `,` and `)`, so it ends cleanly inside a parameter list |
| Proofs | `ValueSetFactory.ForType` (`Utilities/ValueSetFactory.cs`) gives `IConstantValueSet`s with `Related`, `Intersect`, `Complement` and `IsEmpty` for the integer types, `char`, `bool`, `float`, `double`, `decimal`, `string`, `nint`, `nuint` and enums. "A implies B" is `A ∩ ¬B` empty; no decision DAG is needed |
| Exhaustiveness | The switch DAG starts from `ImmutableDictionary<BoundDagTemp, IValueSet>.Empty` (`DecisionDagBuilder.cs`, the initial `uniquifyState`). Seeding the root input with the refinement's set makes `i switch { 0 or 1 => 1, >=2 => ... }` exhaustive for `int i is >=0`, and `PatternExplainer` then names only missing values inside the domain. The DAG also holds the narrowed set at each arm's leaf, which slice 2 uses |
| Flow state | Flow analysis tracks only definite assignment and null state. Narrowing after `if (x > 0)` and `assume` ([#39](https://github.com/ok3mad/roslyn/issues/39)) need a new per-slot value-set state, split on true and false like nullable and joined by union |
| Subsumed arms | An arm that can never match is an error (`ERR_SwitchArmSubsumed`, `SwitchExpressionBinder.cs`); a non-exhaustive switch expression is the warning CS8509 |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Spelling | `int i is >=0` after the parameter name, before any `= default`. No refined type form such as `int is >=0` in slice 1 | It reads as a predicate on the name, as in the brainstorm and the Fib example; a type form clashes with `e is int is >0` |
| Syntax | A new `RefinementClauseSyntax` (`is` keyword, pattern), kind 9900 or above, as a new optional last field of `ParameterSyntax` | The token order needs a slot on the parameter; a new type node (the precedent of `T~`) cannot hold a pattern written after the name. Upstream rarely changes `ParameterSyntax` |
| What a refinement may contain | Closed patterns over constants (relational, constant, `and`, `or`, `not`, parentheses) on a type `ValueSetFactory` supports. Opaque patterns ([#34](https://github.com/ok3mad/roslyn/issues/34)) are an error | Declared domains stay with constants, so subsumption is exact |
| Parameter refinements | Always written out; never inferred from a default value or from switch arms. `infer` may carry one: `infer retries is >=0 = 3` is `int is >=0` | A domain is a contract, and an inferred domain would swallow a forgotten arm |
| Return refinements | Inferred for `infer` returns, as the union of what every return can produce: `infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 }` is `int is -1 or 0 or 1`. `val y = e` keeps the refinement of `e` | Widening a return domain is meant to break callers at compile time, so they adapt |
| Where an inferred return refinement goes | With the inferred return type: where `infer` is allowed on a public member, its refinement is emitted too. Slice 1 follows R1 and R2, which cover private methods and local functions | Inferred types are allowed on public members, and whoever cares about a published API writes its types out. Forcing written refinements on public members would be the language taking a side; a fixer that offers to write the inferred refinement out, to stabilize an API, is the tool for it |
| Call sites | Proven is silent and disproven is an error (`Page(-1)`). An argument that cannot be proven is an error when its value carries an inferred refinement (a widened return breaks the caller), and a warning when its value has no refinement at all (a C# result, or a value flow analysis does not cover yet) | Slice 1 proves little; the warning tightens as slices 2 and 3 prove more, while inferred refinements already break callers as intended |
| Exhaustiveness | A switch on a refined value seeds the DAG with the refinement's set. When an inferred refinement widens and a switch is no longer exhaustive, that is an error, not CS8509 | Same reason: the widening must break the caller |
| Unreachable arms | An arm that is unreachable only because of a refinement is a warning, not `ERR_SwitchArmSubsumed` | Narrowing a domain, by a body change or a better prover, must never break a caller |
| Runtime guard | At entry, on every accessibility: `if (!(i is >=0)) throw new ArgumentOutOfRangeException(nameof(i), i, "...")`, lowered through the `is`-pattern lowering. In iterators and `async` methods it runs in the kickoff method, so it throws eagerly | Guards make every escape sound: unproven callers, C# callers, method groups converted to delegates, reflection. Eliding proven guards is an optimization for later |
| Metadata | An embedded `SharperRefinementAttribute(string pattern)` on the parameter (and on the return) holding the normalized pattern text. Sharper consumers re-parse it and trust it; C# callers see the plain type and meet the guard | Readable in decompilers and independent of the type; trust as for `[Immutable]` |
| Reassignment | A refined parameter is implicitly `readonly` ([#23](https://github.com/ok3mad/roslyn/issues/23)) | The fact then holds for the whole body, which seeding and later flow analysis rely on |
| Signatures | A refinement is not part of the signature. Slice 1 reports an error on virtual, abstract, override, interface, partial and extern members, on lambdas, and on recursive `infer` returns (as R1 does) | Overriding rules (identical, or wider parameters and narrower returns) can be added later without a break |
| Default values | The default must satisfy the refinement; a disproven default is an error | It is a call site the compiler always sees |

### Plan

1. **Syntax.** `RefinementClauseSyntax` and the new `ParameterSyntax` field in `Syntax.xml`, with its public API; the
   gated parse after the parameter name; parser tests with the flag on and off, including `= default` after the clause.
2. **Binding.** Bind the pattern against the parameter type, check that it is closed and constant, and compute its
   value set once per parameter symbol; the default-value check; implicit `readonly`.
3. **Return refinements.** A refinement slot on the return of `infer` methods and local functions and on `val` locals,
   inferred from constants, a refined parameter or `val` returned unchanged, and switch expressions whose arms all
   yield such values.
4. **Call sites and switches.** Proven, disproven and unknown arguments with the error-or-warning rule above; DAG
   seeding; the exhaustiveness error and the unreachable-arm warning.
5. **Guard and metadata.** The entry guard (kickoff method for state machines) and the embedded attribute, written
   and read.
6. **Tests** in `SharperTests.cs`, next to the C# they stand for: constants in and out of the domain, refined to
   refined calls, Fib's switch without `_`, a widened inferred return breaking a caller, a narrowed one only warning,
   guards for C# callers, iterators and `async`, metadata round trips, and the flag off.

Not in slice 1: records and properties, whose `with` and `init` accessors bypass the constructor and need guards
there, with immutable fields from [#139](https://github.com/ok3mad/roslyn/issues/139); written refinements on
returns; arm narrowing plus interval arithmetic, which makes `Fib(i - 1)` provable (slice 2); value flow after `if`
tests and `assume` (slice 3).

**Size:** L, about 3.5k lines with tests. The parser and the guard are S; most of the work is binding, the call-site
rules and return inference.

### Progress of slice 1

- [x] **Step 1, syntax** (`sharper/refined-parameters-syntax`). `RefinementClauseSyntax` (kind 9905) is the optional
  `Refinement` field of `ParameterSyntax`. The design said "last field", but a node's fields are its children in source
  order, so the field sits between `Identifier` and `Default`; the generated `Update` and `SyntaxFactory.Parameter` take
  one more argument, and the five-argument overloads are kept by hand (`Syntax/ParameterSyntax.cs`,
  `Syntax/SyntaxFactory.cs`), so the analyzer build reports no RS0017. `ParseParameter` reads `is` and a pattern after
  the name, behind the flag; `CanReuseParameter` does not reuse a refined parameter, which may end in a pattern. A
  lambda's parameter list is scanned before it is parsed, and the scan is unchanged, so a refinement on a lambda
  parameter stays a syntax error; anonymous methods parse it and report CS20061.
- [x] **Step 2, binding.** `SharperRefinement.Bind` (`Symbols/Source/SharperRefinement.cs`), once per parameter symbol
  (`SourceComplexParameterSymbolBase.SharperRefinement`, forced by `ForceComplete`). Patterns that are not closed are
  rejected on syntax before binding, because a designation outside a body asserts in the binder, and on the bound
  pattern after it (a constant pattern that binds to a type). The value set is built from `ValueSetFactory.ForType`
  with the relations the DAG uses; `MatchesNull` records whether `null` matches (`string s is not "x"`). The default
  value is checked by evaluating the pattern on the constant. The flag `SharperReadOnly` makes the parameter readonly,
  with the readonly parameter machinery of Feature 5. CS20058 to CS20061.
  Beyond the design: `ref`, `out` and `in` parameters (a write through the ref would break the fact), explicit interface
  implementations, delegates, extension block receivers and positional record parameters (the records exclusion) are
  CS20061 too.
  Left for step 3: the error on recursive `infer` returns, which only matters once returns have refinements. Open
  points, as `PROTOTYPE(sharper)` comments: implicit interface implementations, `[DefaultParameterValue]` defaults, the
  semantic model inside a refinement.
  Tests: 14 `Refinement_*` tests (`SharperTests_Refinements.cs`). The Syntax project with `ROSLYN_TEST_SHARPER=1` has
  the same 37 expected failures as the last run below, and none new.
- [ ] Step 3, return refinements.
- [ ] Step 4, call sites and switches.
- [x] **Step 5, guard and metadata** (`sharper/refined-parameters-guard`). `SharperRefinementGuard`
  (`Lowering/SharperRefinementGuard.cs`) builds `if (p is not (pattern)) throw new ArgumentOutOfRangeException(nameof(p),
  p, "...")` for each refined parameter, lowers it with `LocalRewriter.Rewrite` on its own, and `MethodCompiler` puts it
  first in the emitted body after state machine rewriting: in the kickoff method of iterators and `async` methods, and
  before the captured-parameter stores and field initializers of constructors (constructors and class and struct
  primary constructors allow refinements since step 2). The guard is built against the parameters of the method that is
  emitted, so local functions (their closure methods, compiled with the synthesized methods) and extension block members
  (their implementation methods) are covered; the pattern is rebuilt from its constants against those parameters'
  types (`SharperRefinement.MakePattern`). It starts with a hidden sequence point, since codegen requires one at offset 0
  once a method has any. The exception stays the BCL's: #217's runtime library is only for types two assemblies must
  agree on, and a guard is behavior. `ArgumentOutOfRangeException` is not a well-known type, so its constructor is looked
  up by name in the core library; a missing one is CS0656 at the refinement.
  The metadata is an embedded `Sharper.SharperRefinementAttribute(string pattern)`, a compiler-only entry in the table of
  `[Immutable]` and `[Pure]` (`SharperImmutabilityAttribute.Refinement`, whose name does not bind in source), synthesized
  with a string constructor when a refinement binds and written by `SourceParameterSymbolBase.AddSynthesizedAttributes`.
  The text is normalized from the bound constants (`SharperRefinement.Text`): literals of the parameter type, enum
  members by name or as a cast of the value, `double.NaN` and infinities by name, only the parentheses precedence needs.
  `PEParameterSymbol.SharperRefinement` parses it back without binding names (`SharperRefinement.FromMetadata`) and
  computes the value set with the same code as source; `ParameterSymbol.SharperRefinement` is virtual, and cloned
  (indexer accessor) and wrapped (substituted, retargeting, extension implementation) parameters forward it.
  Runtime async has no kickoff method, and the runtime puts any exception of a runtime-async method into its task (a
  probe showed `Load(0)` returning a faulted task), so `IsRuntimeAsyncEnabledIn` keeps the compiler's state machine for
  a method with a guard (`SharperRefinementGuard.HasGuard`); other methods of the same compilation still use runtime
  async. A wrapper that checks and calls a runtime-async body is left as a `PROTOTYPE(sharper)` comment
  ([#224](https://github.com/ok3mad/roslyn/issues/224)).
  Tests: 14 `RefinementGuard_*` and `RefinementMetadata_*` tests (`SharperTests_RefinementGuards.cs`).

## Feature 12: pipe, without lifting

`raw |> ParseOrder |> Resolve(it, accounts)` is `Resolve(ParseOrder(raw), accounts)` (issue
[#25](https://github.com/ok3mad/roslyn/issues/25)). The rules are on the [feature page](../features/pipe.md); this
section keeps the decisions and what the implementation taught. Lifting over `Task`, sequences and unions is row 20
([#32](https://github.com/ok3mad/roslyn/issues/32)); until then a type mismatch is an ordinary error.

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Token | `\|>`: a `\|` directly followed by `>`, merged by the parser behind the flag (`IsAtSharperPipeToken`), as `>>` is. A new kind, `BarGreaterThanToken` = 8231, the top of the free punctuation gap (upstream fills it from the bottom), so `SyntaxFacts.GetText` and `IsPunctuation` work | No C# expression starts with `>`, so `a \|> b` is CS1525 in C#: no valid code changes. A lexer token would apply with the flag off too (trap 13). `x \|> F<int> \|> G` stays generic, because `\|` is in the list of tokens after which `F<int>` is a type argument list; `a \| > b` with a space is not a pipe |
| Precedence | Left-associative, a level of its own between `?:` (looser) and `??` (tighter), so looser than every binary operator | F# puts `\|>` with the comparisons, above `&&` and `\|\|`; the TC39 (Hack) proposal puts it with `=>` and assignment, below `?:` and `??`. A Sharper stage is often an expression with `it`, so a loose pipe lets `x \|> it > 3 && it < 9` be one stage without parentheses, as in TC39. Below `??`: `s ?? "default" \|> Parse` reads left to right as data flow, and the other reading, `s ?? Parse("default")`, would type-check and silently differ, while `x \|> F ?? d` is only an error (`F ?? d` on a method group). Above `?:`: `x \|> IsValid ? a : b` tests the result, and the branches of `?:` take a pipe (`c ? a : b \|> F`). Lambdas and assignment take a whole pipe: `y = x \|> F`, `x => x \|> F` |
| Bare stage | A method group, a delegate value or an expression of delegate type, invoked with the value as its only argument through the ordinary invocation binder: overloads, type inference, extension methods, `dynamic`. Bound as a method group conversion would be (not "invoked"), because lookup's mode must match `SyntaxFacts.IsInvoked`, which primary constructor captures rely on. Anything else is CS20048 | The stage is a function; C# already knows how to call one. CS20048 catches the common slip `x \|> y.Length` for `x \|> it.Length` |
| Stage with `it` | A stage with a free `it` (the implicit lambda's test: C# finds no `it`, or only an outer implicit one) binds as an expression in a scope that declares `it` as a read-only local holding the value. Not a lambda | So `await` works in a stage, nothing is allocated, and `it` is an ordinary variable for closures, the debugger and flow analysis |
| `it` scoping | A stage is the outermost slot: an argument inside it that takes a one-parameter function claims an `it` first (innermost wins, Feature 7). A stage left without an `it` of its own is CS20047. A stage that uses its `it` and contains an implicit lambda is CS20020, and CS20021 takes the stage's `it` as the outer one. An inner pipe stage is not a second meaning: `xs.Select(it.Id \|> it * 10)` | The implicit lambda rules exist because only signatures tell lambdas apart; a stage's extent is syntax, so nesting pipes needs no signature to read |
| Evaluation order | The left side once, before anything in the stage, including a bare stage's receiver or delegate expression | Reading order. `x \|> o.M` as the plain call `o.M(x)` would evaluate `o` first |
| Scope of declarations | A stage is its own scope, like a lambda body: `out var` and pattern variables in it end with it | The stage binds when its `it` has a type, under its own binder map; the enclosing statement's scope cannot hold variables bound later. `x \|> (TryParse(it, out var n) ? n : 0)` needs the parentheses, since `?:` is looser |
| `await` | Allowed in a stage (`id \|> await Load(it)`, `x \|> F \|> await it`); `await x \|> F` awaits `x` | `await` is a unary operator, tighter than any binary one |
| Lowering | A spill sequence: `it = x;` before the enclosing statement and the stage in place, as a switch expression lowers. No new runtime type; in optimized builds a local read once stays on the stack, which leaves the nested call | `it` and the stage's variables are user variables (the debugger shows them, closures capture them), and codegen keeps those out of an expression's sequence: it records a sequence's locals as expression temps and frees them, which named locals cannot be. Closure conversion has no field name for a captured lowering temp |
| IOperation | `OperationKind.None` with the left side as its only child ([#155](https://github.com/ok3mad/roslyn/issues/155)) | The control flow graph has no region for the stage's `it` |
| Expression trees | CS20049 | The stage's `it` is a variable |
| Target typing ([#187](https://github.com/ok3mad/roslyn/issues/187), implemented) | A stage with `it` is target-typed, as a switch expression's arms are: `int? r = x \|> (it > 0 ? 1 : null)`, `Func<int, int> f = x \|> (y => y + it)`. An unconverted pipe and a pipe conversion with the stage's conversion inside, at the lowest priority, so a natural type that converts wins. Overload resolution classifies the conversion per candidate with no new betterness rule, and type inference does not look through the pipe | C#'s pattern for `?:`, switch and the Sharper try expression, which already has every site. C# does not infer through `?:` or a switch either; inference through a pipe can come later without breaking anything |
| Lambda stage ([#186](https://github.com/ok3mad/roslyn/issues/186), implemented) | A stage that is a lambda with one parameter and an expression body names the value: `x \|> (y => y + 1)` is an `it` stage whose variable is `y`, not a delegate. An explicit parameter type converts the left side implicitly. A block body, more parameters, `async`, `static` or attributes stay CS20048, with a hint to use `it`. A lambda that uses the stage's `it` is the stage's value (target typing); with a free `it` in the body, a trial binding with both names in scope decides, since an inner implicit lambda may claim it | Naming the value is the way out where `it` is taken: `x \|> (v => xs.Where(it > v))` (CS20047 with `it`), `xs \|> (ys => ys.Select(it * 2))` (CS20020). An `it` stage costs nothing, while a real lambda would allocate and follow lambda rules for `await`. `\|>` is never valid C#, so no C# code changes meaning |

### What it taught

- **A scope whose types come from binding needs its own binder map.** The stage's `it` has the type of the left side,
  so its binder is made when the pipe binds (`SharperPipeStageBinder`), and the member's map must leave the stage out:
  `LocalBinderFactory` and `ExpressionVariableFinder` both get `VisitPipeExpression` (trap 31). An
  `ExecutableCodeBinder` would build the map too, but it clears the catch, filter and finally flags; a
  `LocalScopeBinder` that builds the map keeps them. Each binding of a pipe makes its own binder and local, as each
  binding of a lambda does, so trial binding (implicit `it`) is safe.
- **Debug checks list the nodes that declare locals.** `BoundNode.LocalsScanner` (undeclared local) and
  `RefSafetyAnalysis.VisitLocal` (no escape scope) assert on a local that no known node declares; both need the new
  node, and ref safety needs `GetValEscape` and `CheckValEscape` cases for it.
- **A new syntax for a `BoundCall` meets the interceptor check.** `BoundExpression.InterceptableNameSyntax` asserts on
  call syntax it does not know; a bare stage's call is not interceptable for now ([#158](https://github.com/ok3mad/roslyn/issues/158)).
- **User variables cannot live in an expression's sequence.** See Lowering above; a spill sequence is the precedent.
- **A check on a target-typed expression waits for its conversion.** A lambda in a target-typed stage is unbound until
  the pipe converts, so the stage's `it` rules (CS20047, CS20020) run in `ConvertSharperPipe`, and the unconverted node
  needs the flow and nullable visitors too: a lambda's return type inference visits returns before they convert
  (`VisitUnconvertedSharperPipe`). With no natural type, the conversion error is the stage's (`GenerateImplicitConversionError`
  on the stage), and `Display` falls back to the stage's.
- **Spilled locals captured in a declaration's initializer trip a Debug check.** The spiller gives the block that holds
  a spill sequence's locals the syntax of the enclosing statement, a `VariableDeclarator` for a local declaration, and
  `SynthesizedClosureEnvironment.AssertIsClosureScopeSyntax` rejects it: `Func<int> f = 3 |> Make(() => it);` asserts
  in Debug builds, as C#'s `Func<int> g = 3 switch { var i => Make(() => i) };` does (upstream code, not changed here).
- **A lambda that is not a lambda needs the semantic model's help.** A lambda stage's parameter declares a local, so
  `GetDeclaredSymbol` on it must not look for a lambda symbol (`GetLambdaParameterSymbol` asserts there is one): the
  member model finds the bound pipe and returns its local, and the typed `GetDeclaredSymbol(ParameterSyntax)`, which
  returns `IParameterSymbol`, returns null. Whether a lambda with a free `it` is a lambda stage depends on which `it`
  its body uses, which only binding tells, so the binder tries the body once with both names in scope.

## Feature 13: patterns on pure functions (design)

`amount switch { > 0 and <= max => Accept(amount), _ => Reject(amount) }` with a non-constant `max` (opaque patterns,
[#34](https://github.com/ok3mad/roslyn/issues/34)), and `s is Parse(val n and > 0)` (extractor patterns,
[#35](https://github.com/ok3mad/roslyn/issues/35)). Both need purity, whose slice 1 ([#173](https://github.com/ok3mad/roslyn/issues/173))
checks declared `[Pure]` members only. This section is the design for both and the plan for the first slice.

**Probe.** What the compiler does today:

| Area | What the code shows |
| --- | --- |
| Flag off, opaque | A relational operand is any expression; a non-constant one fails in `BindExpressionForPatternContinued` (`Binder_Patterns.cs`) with CS9135 "A constant value of type 'int' is expected", or CS0150 when the input is `object` or a type parameter. A constant pattern `x is max` fails the same way. No valid C# changes meaning |
| Flag off, extractor | `s is Parse(...)` is a positional pattern: `BindRecursivePattern` → `BindTypeForPattern` looks up types only, so with no type named `Parse` it is CS0246, and with a method `Parse` in scope CS0118 (`LookupResult.NotTypeOrNamespace`, `Binder_Lookup.cs`). With a type named `Parse` it is valid C# (a `Deconstruct` call). `x is IsEven` is an error: a method group is neither a constant nor a type |
| Guards in the DAG | A `when` clause is a `BoundWhenDecisionDagNode` after all of an arm's tests (`DecisionDagBuilder.cs`); a guarded arm counts for neither exhaustiveness nor subsumption, so a switch expression without a catch-all gets CS8509 |
| Purity | `PurityWalker` in member mode (`SharperImmutability.cs`), which `CheckPureBody` uses, accepts locals and parameters, constants, fields reached through any object, `static readonly` fields of immutable types, and calls of `[Pure]`, attested and observer members. Its reason text comes from `GetPureReason` (`SharperImmutability_Pure.cs`) |
| Order of tests | C# leaves the order of pattern tests unspecified and lets the DAG skip or share `Deconstruct` calls and property reads, which C# assumes have no side effects |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| Lowering of an opaque test | A new DAG test, `BoundDagOpaqueTest`: a boolean evaluated on a DAG temp whose outcome the value sets know nothing about. Not a rewrite into a `when` guard | A trailing guard only covers `and` chains: `not (<= max)` and `<= max or 5` cannot become one guard. In the DAG the test sits where it is written, under `and`, `or`, `not`, property and list patterns, and the same machinery later evaluates an extractor's call |
| Pure or total | Pure is enough. The DAG may evaluate an opaque test in a different order than written, skip it when the outcome is already decided, and share two identical tests on the same input. It never hoists a test ahead of the arms | C# gives `Deconstruct` and property reads the same contract. Reordering a pure test can only change which deterministic exception surfaces, or skip it. Hoisting and speculative evaluation need `total` ([#36](https://github.com/ok3mad/roslyn/issues/36)) |
| Where opaque patterns are allowed | Every pattern context: `is`, switch statements and switch expressions, at any depth. Not in refinements (Feature 11) | The issue's "match arms only" is about declared domains, which must stay closed so that call sites can prove them |
| Which patterns can be opaque | Slice 1: a relational pattern (`<`, `<=`, `>`, `>=`) or a constant pattern (`== max`) whose operand is not a constant, in Sharper files, where C# reports CS9135 or CS0150 | Both are errors in C# today. The operand converts to the input type as a constant would, and the comparison is the one C# would use for `input op operand` with that type |
| Purity of an operand | Checked by the member-mode `PurityWalker` over the bound operand. An impure one is a new error (CS20062, the next free code when slice 1 was built), with `GetPureReason`'s reason | The same rules as a `[Pure]` body, so the two cannot drift. Reading a mutable instance field is fine: nothing in a pure match can write it while the match runs. Mutable statics stay rejected, as in `[Pure]` bodies |
| Exhaustiveness and subsumption | An opaque test never narrows a value set, so an arm with one counts as a guarded arm: no help for exhaustiveness (CS8509 without a catch-all), never subsumes a later arm. Two identical opaque tests on the same input are one test | Best effort, as in the brainstorm. Symbolic reasoning over the same `val` (such as `<= max` then `> max`) can come later without breaking anything |
| Extractor lookup | `Parse(...)` in a positional pattern is an extractor only when type lookup fails; a type named `Parse` always wins, silently. Simple names and `Type.Method` | Design rule 2: with a type in scope it is valid C#. With no type it is CS0246 or CS0118 today |
| Extractor shapes, in order | 1. The predicate form `x is IsEven` (a `bool M(T)`), which is an opaque test. 2. `bool TryX(T, out U…)`: matches when it returns `true`, sub-patterns on the `out` values, like `Deconstruct`. 3. `U F(T)`, the sub-pattern on the result as it is (`Parse({ } n)` for a nullable result; no special meaning for `null`). 4. C# 15 unions, after Sharper's union story ([#31](https://github.com/ok3mad/roslyn/issues/31)) | Each shape adds one rule; the predicate form needs nothing beyond slice 1, and the `Try` shape is what the BCL has. A `U?` that means "no match" would hide a legitimate `null` |
| `out` arguments in extractors | A pattern's temporaries are fresh, so a `[Pure]` `TryX` or `Deconstruct` called by a pattern never gets CS20053 ([#177](https://github.com/ok3mad/roslyn/issues/177)) | The aliasing that CS20053 guards against needs an `out` argument the caller can also reach |
| Slices | One DAG infrastructure, shipped in steps: opaque patterns (slice 1), the predicate form (slice 2), extractors (slice 3) | Opaque patterns need no lookup rule and no shape choices, and are the most certain |

### Plan for slice 1

1. **Binder.** In `BindExpressionForPatternContinued`, in Sharper files and when C# reports CS9135 or CS0150 for a
   relational or constant pattern, bind an opaque pattern instead (a new `BoundOpaquePattern` node: the operand, the
   operator and the comparison's operator kind), and check the operand's purity. With the flag off nothing changes.
2. **DAG.** `DecisionDagBuilder` makes a `BoundDagOpaqueTest` for it; only an identical test (same input, operator and
   operand) implies it, and it leaves the value sets alone.
3. **Lowering and the rest.** The decision DAG rewriter emits the comparison; flow, nullable and definite-assignment
   visits; IOperation as `IRelationalPatternOperation` (or `IConstantPatternOperation`) with a non-constant value.
4. **Tests** next to the C# with a `when` guard: runtime results, `and`, `or`, `not`, nesting in property and list
   patterns, CS8509 without a catch-all, no subsumption, a shared test, an impure call, a mutable static, a `[Pure]`
   call, refinements still rejecting it, and the flag off.

**Size:** M, about 800 to 1,200 lines with tests; most of the risk is in the DAG builder's state handling.

### Slice 1, as built

Prototype on `sharper/opaque-patterns` ([feature page](../features/opaque-patterns.md), tests in
`SharperTests_OpaquePatterns.cs`). What the plan left open, and how it was settled:

| Question | As built |
| --- | --- |
| Which operand types | The operand must convert to the input type (CS0266 otherwise), or, for an input of type `object`, `System.ValueType` or a type parameter, keep its own type (the boxing is dropped and the match tests for the type first, as for a constant). The comparison is the one constant tests use (`PatternLocalRewriter.MakeComparison`): numeric types, `char`, enums and native integers; `bool` and `string` for `==` only. Other types keep C#'s error. A NaN operand compares as `x == d` does, unlike the constant `double.NaN` |
| "Two identical tests are one test" against "never subsumes a later arm" | An opaque test decides nothing on its own, so it subsumes nothing and helps no exhaustiveness check. An identical test (`BoundDagOpaqueTest.IsSameComparison`: the same operator and the same operand by structure, on the same value) is decided by the first, so `> max => 1, not > max => 2` is exhaustive, and the second of two `> max` arms is unreachable (CS8510), which a `when` twin never is |
| Which warning without a catch-all | CS8509, as planned; the `when` twin gets CS8846, whose text names a `when` clause |
| When the operand is read | Before the match assigns the pattern's own variables: flow analysis visits the operands in the state before the match (`AbstractFlowPass.VisitSharperOpaquePatternOperands`), so `(var a, > a)` is CS0165. The nullable walker analyzes the operand where the DAG tests it, as it does a `when` clause |
| Union matching | Not covered: a union input keeps C#'s error, since `UnionMatchingRewriter` rewrites the pattern before the DAG is built |
| Refinements rejecting opaque patterns | A refinement binds its pattern with the same binder, so `int i is > s_max` makes an opaque pattern, which `SharperRefinement.FindOpenPattern` rejects as not closed (CS20058). The operand's purity is not checked inside a refinement clause, so an impure operand gets CS20058 alone (`Opaque_RefinementRejects`) |
| Pointers | `e is p->F` was CS0150 under Sharper and is now CS20062, since reading through a pointer is not pure (`FunctionType_Pattern_PointerMemberAccess`) |

## Feature 14: purity, slice 1, second part: virtual and interface members

`[Pure]` on virtual, abstract, override and interface members, with the rule that every override and implementation of
a `[Pure]` slot is `[Pure]` too (issue [#173](https://github.com/ok3mad/roslyn/issues/173), "Second PR"; the
[feature page](../features/purity.md#virtual-and-interface-members)). The first part checked declared `[Pure]` on
non-virtual members and rejected the rest with CS20052. Prototype on `sharper/purity-virtual-members`, tests
`PureSlot_*` in `SharperTests_Purity.cs`.

**Probe.** What the code showed:

| Area | What the code shows |
| --- | --- |
| Calls | A call binds to the most derived override visible on the receiver's static type (`d.A()` is `Derived.A`), and a call on an interface to the interface member. So "the slot is `[Pure]`" must look up the chain of overridden members, not only at the bound member |
| Interface map | `TypeSymbol.FindImplementationForInterfaceMember` answers for methods and accessors, implicit, explicit, inherited from a base class, and default implementations in derived interfaces; `MethodSymbol.ExplicitInterfaceImplementations` gives the explicit ones directly |
| Observers | The observer mode of `PurityWalker` accepted no `[Pure]` callee and no attested method; a `[Pure]` operator did not count in either mode |
| Precedent | `[ImmutableHierarchy]`: an error for a Sharper type, a warning for a C# type of the same compilation (CS20038, CS20039), from `SharperImmutability.CheckDeclaration` |

### Decisions

| Question | Decision | Why |
| --- | --- | --- |
| One attribute or two | One: `[Pure]` means "a call of this member is pure" everywhere. On a non-virtual member that is a claim about its body, on a virtual or interface member about every body a call can run. No separate "hierarchy" attribute, as `[Immutable]` has | Purity is part of the member's contract, like its return type and nullability, which overrides cannot widen either; D's `pure` and Java's checked exceptions work the same way. `[Immutable]` needed two because it describes values of an exact type, and a subclass is a new type; a virtual call has no exact member. A body-only claim would serve only `base.M()` and sealed or exact receivers, such as a `[Pure]` override that extends a pure default while other overrides stay impure. Its idiomatic workaround is a non-virtual `[Pure]` core method that the virtual one calls, and inference inside one assembly ("Later" in #173) would cover `base.M()` without an attribute. No realistic case needed more, so it stays out. It can be added later without breaking anything, while splitting one attribute after it ships would break |
| CS20052 | Retired, not narrowed: nothing of "virtual, abstract or override" is left unsupported | Every member kind that can be `[Pure]` can be a slot or fill one |
| What must be `[Pure]` | A member that overrides a `[Pure]` slot, at every level (an abstract or sealed override too), and a member that implements a `[Pure]` interface member. Error CS20064, on the member's name | The contract is on the slot, so each link must carry it; an unmarked link would also leave its body unchecked |
| An override that starts the contract | Allowed: `[Pure]` on an override of an unmarked member binds the overrides below it | It is a stronger promise on a subtype, and the call on the derived type binds to the override |
| When a call through a slot is pure | `IsPureSlot`: the bound member is declared `[Pure]`, implements a `[Pure]` interface member for its own type (through the interface map, cached per member), or overrides, directly or not, a member that does either | An override or implementation that is unmarked (a C# file, another assembly) is still under the contract, so `d.A()` and `((Base)d).A()` agree, and so do `c.M()` and `((I)c).M()`. Leaving the direct call impure would let a C# or metadata implementation be called as if it promised nothing, which breaks the guarantee from the other side |
| Explicit implementations | Must be marked; attributes are allowed there. Found through `ExplicitInterfaceImplementations` | The member is declared for the slot and nothing else |
| Implicit implementations | Found through the interface map of every class and struct, for every interface it has. The member in the type is reported on its name. A base class's member that implements the interface for a type that declares it (`class D : B, I`) is reported on `D`, naming `B.M`; a base class that declares the interface itself is checked, or trusted, on its own | The map is the only place that says which member fills the slot. `B.M` may be in another assembly, so the fix has to be possible in `D` (an explicit implementation) |
| Explicit implementations and the public member | An explicit implementation takes the slot, so a public member of the same name is free and may be impure. A slot stays a slot: a member that implements a `[Pure]` interface member binds its overrides, also in a derived type that implements the interface member explicitly. An implementation inherited from a base class (`class D : B, I`) is not `B.M`'s slot, so `d.M()` is pure only if `B.M` is | The explicit member is what fulfils the contract. A `C`-typed call can run an override of `C.M` whatever `D` does with `I`, so the override cannot drop the promise. `B` did not declare `I`, so it promised nothing for its own callers |
| Default interface members | A `[Pure]` one is checked like any body; a reimplementation in a derived interface (`int I.M() => ...`) is an explicit implementation and must be marked. `static abstract` members follow the same rule | An interface is an implementer too |
| Generic virtual methods | Nothing special: the claim is read from the original definition, and the chain of overridden members is walked on definitions | Purity does not depend on type arguments in slice 1 |
| Properties and indexers | `[Pure]` on the property means its getter (as in the first part), so only the getter of an override is held to the slot; `[Pure]` on the `get` accessor counts too. Diagnostics name the property | A setter writes, so it is never part of the contract |
| `[AssumePure]` on an override or implementation | Satisfies the rule. `[AssumePure]` on a base slot stays what it was: a hard claim about calls of that member, which puts nothing on overrides | A hard claim names what its author vouches for (Feature 10); a caching override needs the escape. Whoever writes it on a virtual member vouches for the overrides |
| C# files of a mixed compilation | Warning CS20065 on the same location as the error, also for a slot from metadata. Its text says that a C# file is not checked and that the member should move to a Sharper file. A compilation without Sharper files gets nothing | `[Pure]` does not bind in a C# file, so the member cannot be marked there; the warning says the contract is on trust. Unlike `[ImmutableHierarchy]`'s warning it is not limited to slots declared in the same compilation: the gate on Sharper files already keeps plain C# compilations as upstream |
| A slot that gains or loses `[Pure]` | Gaining it breaks overriders and implementers when they next compile (CS20064, CS20065); compiled assemblies keep running and are trusted. Losing it breaks the `[Pure]` callers of the slot (CS20050); overrides that keep `[Pure]` stay valid and start the contract themselves. Neither is a binary break | The issue: "adding it breaks overriders, removing it breaks callers". Only the attribute changes in metadata |
| Metadata | A `[Pure]` slot from another assembly counts as one from source, recognized by name. Overrides in other assemblies are trusted | "Consumers trust it and do not re-check" |
| One `IsPure(member)` | `SharperImmutability.IsPure`: a `[Pure]` slot, `[AssumePure]`, or an attested member. `PurityWalker` asks it in both modes (`IsPureByClaim`), for calls, getters and operators. In observer mode a callee that is not `[AssumePure]` must also return a statically immutable type, unless the observer may create its result | A pure member may create and return a mutable object; an observer that returned it would hand out a new mutable object on every read. This widens the observer rule (`[Pure]` and attested callees, `Math.Abs`) and the member rule (`[Pure]` operators), and narrows nothing |
| Compiler-written members | Not held to a slot (a record's `ToString`, `Equals`, `GetHashCode`): `PROTOTYPE(sharper)` in `CheckPureOverrides` | The user cannot mark them; whether a record's members are pure is a question of its own |
| The `out` warning | Unchanged: CS20053 still looks at the bound member only | The rules for `out` are [#177](https://github.com/ok3mad/roslyn/issues/177) |

### What it taught

- **A contract on a slot is checked from both ends.** The declaration side needs the chain of overridden members and
  the interface map; the call side needs the chain again, because the bound member of a call is the most derived
  override, not the slot.
- **The interface map reports where the fix is possible.** An inherited implementation belongs to a base class that
  may be in another assembly, so the diagnostic goes to the type that declares the interface.
- **Sharing a query between two checkers exposes where their rules differ.** The observer rule forbids creating a
  mutable result, which a pure member may do, so the shared answer needs the observer's creation rule on top.

## Follow-ups

Sharper diagnostics were renumbered from `SharperErrorCodes.Base` (20000): CS9402, CS9403 and CS9404 became CS20001, CS20002 and CS20003, and this document uses the new numbers throughout.

The open items are marked in code as `// PROTOTYPE(sharper):` (`git grep "PROTOTYPE(sharper)"`).

- [x] Turn Sharper on for `.cx` files in the command-line compiler.
- [x] Turn Sharper on for `.cx` documents in the IDE workspace.
- [x] Treat `.cx` files as C# in the language server.
- [ ] Support `dotnet run app.cx`. File-based apps live in the SDK and only accept `.cs` files, so this needs an SDK change; until then the language server treats a loose `.cx` file as a miscellaneous file, never a file-based app.
- [x] Add `*.cx` to the default `Compile` items (the `Microsoft.Net.Compilers.Toolset` props).
- [ ] A fixer that writes an inferred return refinement out explicitly, so a public API stops following its body ([#33](https://github.com/ok3mad/roslyn/issues/33)).
- [x] Decide whether `dynamic` should choose range or spread at runtime: no, it keeps the range and the warning. [#42](https://github.com/ok3mad/roslyn/issues/42)
- [x] Decide whether async iterators should ever support spreads: never for an asynchronous source. [#43](https://github.com/ok3mad/roslyn/issues/43)
- [x] Support spreading a synchronous source in an async iterator. [#49](https://github.com/ok3mad/roslyn/issues/49)
- [x] Give ref-struct sources a spread-specific error instead of CS4007. [#44](https://github.com/ok3mad/roslyn/issues/44)
- [x] Verify the spread with Edit and Continue: the generated try/finally and the yield share one syntax offset. Fixed with a second state ordinal, and the IDE no longer matches a spread with a plain yield return. [#45](https://github.com/ok3mad/roslyn/issues/45)
- [ ] Design a real IOperation shape once the syntax settles. [#46](https://github.com/ok3mad/roslyn/issues/46)
- [ ] Avoid the O(depth) cost of recursive spreads with a delegating state machine. [#47](https://github.com/ok3mad/roslyn/issues/47)
- [x] Number Sharper diagnostics from their own base, `SharperErrorCodes.Base` = 20000 (CS20001 and up), far from upstream.
- [x] Full C# compiler regression run (net10.0, Debug): 88,327 tests passed and 0 failed across 10 test projects. WinRT was not run; it targets .NET Framework only.
- [ ] Infer whether a struct member mutates `this`, and silence CS20003 for members that do not. Until then, every call to a non-`readonly` member of a `val` warns, including interface members on a generic `val` (`g.Get()`). [#84](https://github.com/ok3mad/roslyn/issues/84); until then it stays a default warning, decided in [#85](https://github.com/ok3mad/roslyn/issues/85), so that inference only removes warnings.
- [x] Copy and warn for the pattern-based members that bypassed the shared check: `await`/`GetAwaiter`, implicit Index and Range indexers, list patterns and property patterns. Collection spreads and `using` already copy their operand.
- [ ] Build the sharpening infrastructure and move Sharper to the `.cx` extension. The accepted breaks need it: `val` as a keyword in patterns, and `val (a, b) = e` as a declaration. The sharpener rewrites those to `@val`, and the extension keeps plain `.cs` files on C# semantics. The `.cx` part is done (see above); escaping `val` is [#86](https://github.com/ok3mad/roslyn/issues/86).
- [x] Give `val` its own messages: CS20004 and CS20005 (the `val` forms of CS8508 and CS8136), CS20006 for ref reassignment, and the 'val variable' cause also for `foreach` and `using` variables.
- [x] Script fields: `val` is a read-only inferred field, like `var` plus read-only.
- [x] Give `val` its own token kind: `SyntaxKind.ValKeyword` (8466, the top free slot below `ElifKeyword`, guarded by a test), used by val patterns. Unlike `var`, the lexer and `SyntaxFacts` do not report it for the text `val` ([#109](https://github.com/ok3mad/roslyn/issues/109)).
- [ ] `val`: IDE support (classification, Quick Info, completion, hints, and refactorings that write `var`). [#87](https://github.com/ok3mad/roslyn/issues/87) Its first step adds the `SharperFacts` helpers decided in #88: `IsReassignable(ISymbol)` (false also for C#'s `foreach`, `using` and `const` locals) and `IsDeclaredSharperReadOnly(ISymbol)` (`val`, `readonly`, implicit `it`, script `val` fields), plus an `IsValKeyword` check like `IsInferKeyword`. Quick Info shows `val x`, `readonly var x` and `readonly int x` alike, as `readonly int x`.
- [x] `val`: no public symbol API; tooling reads val-ness through `SharperFacts` (see [Decisions](#decisions)). [#88](https://github.com/ok3mad/roslyn/issues/88)
- [ ] `val` and `readonly`: one warning code for struct copies (CS20003, CS20010, CS20013)? [#228](https://github.com/ok3mad/roslyn/issues/228)
- [ ] `val`: analyzer and fixer "local is never reassigned, use `val`". [#89](https://github.com/ok3mad/roslyn/issues/89)
- [ ] `val`: verify Edit and Continue with `val` locals and their struct copies. [#90](https://github.com/ok3mad/roslyn/issues/90)
- [x] Implicit `it` in object creation (also target-typed `new(...)`), indexers, constructor initializers and primary constructor base arguments; attributes keep CS20019. [#122](https://github.com/ok3mad/roslyn/issues/122)
- [ ] Implicit `it`: `LookupSymbols` and speculative binding inside an implicit lambda, which the semantic model finds by lambda syntax. [#123](https://github.com/ok3mad/roslyn/issues/123)
- [ ] Implicit `it`: Edit and Continue, whose lambda matching also works on syntax. [#124](https://github.com/ok3mad/roslyn/issues/124)
- [ ] Implicit `it`: variables declared next to or inside an implicit lambda, such as `F(Make(it), out var v)`. [#125](https://github.com/ok3mad/roslyn/issues/125)
- [x] Implicit `it`: an error that names the inner call when it claims `it` (today CS0411 at the outer call), and a cause string for writes to `it`. [#126](https://github.com/ok3mad/roslyn/issues/126)
- [ ] Implicit `it`: the cost of the two trials and of the per-call syntax scan. [#127](https://github.com/ok3mad/roslyn/issues/127)
- [ ] Implicit `it`: IDE support (Quick Info, hints) and the "name the parameter" fixer. [#128](https://github.com/ok3mad/roslyn/issues/128)
- [ ] Pipe: an IOperation shape, and the stage in the control flow graph. [#155](https://github.com/ok3mad/roslyn/issues/155)
- [ ] Pipe: `LookupSymbols` and speculative binding inside a stage. [#156](https://github.com/ok3mad/roslyn/issues/156)
- [x] Pipe: decide target-typed stages and a bare lambda stage. [#157](https://github.com/ok3mad/roslyn/issues/157)
- [x] Pipe: a lambda stage names the value. [#186](https://github.com/ok3mad/roslyn/issues/186)
- [x] Pipe: target-typed stage. [#187](https://github.com/ok3mad/roslyn/issues/187)
- [ ] Pipe: sequence points, Edit and Continue and interceptors. [#158](https://github.com/ok3mad/roslyn/issues/158)
- [ ] Pipe: IDE support. [#159](https://github.com/ok3mad/roslyn/issues/159)
- [x] `try` expression: test a target-typed switch and `?:` into a C# 15 union before slice 1's binding. [#137](https://github.com/ok3mad/roslyn/issues/137)
- [ ] `try` expression: a warning on a catch-all arm. [#135](https://github.com/ok3mad/roslyn/issues/135)
- [ ] `try` expression: lift a task body instead of warning, after lifting (#32). [#136](https://github.com/ok3mad/roslyn/issues/136)
- [ ] `try` expression: an IOperation shape with the arms. [#144](https://github.com/ok3mad/roslyn/issues/144)
- [ ] `try` expression: sequence points, code coverage and Edit and Continue for the arms. [#145](https://github.com/ok3mad/roslyn/issues/145)
- [x] Nullable: a union conversion in a target-typed arm (switch, `?:`, `try`) does not check the case parameter for null. [#146](https://github.com/ok3mad/roslyn/issues/146): fixed in `NullableWalker.VisitUnionConversion`.
- [ ] Test `/nowarn` and `.editorconfig` with five-digit Sharper IDs. [#91](https://github.com/ok3mad/roslyn/issues/91)
- [ ] Purity: hold the members the compiler writes for a record to a `[Pure]` slot they override, and decide whether they are pure. `PROTOTYPE(sharper)` in `SharperImmutability.CheckPureOverrides`.
- [x] Decide immutability depth: binding, value or deep (a type-level immutability marker). [#92](https://github.com/ok3mad/roslyn/issues/92): deep, see [Feature 10](#feature-10-immutable-types-slice-1-design). Slice 1 is [#139](https://github.com/ok3mad/roslyn/issues/139), a prototype; follow-ups [#140](https://github.com/ok3mad/roslyn/issues/140), [#141](https://github.com/ok3mad/roslyn/issues/141), [#142](https://github.com/ok3mad/roslyn/issues/142).
- [ ] Immutable types: treat an internal `[Immutable]` class whose subclasses are all visible and immutable as closed (a static type), as the design allows. `PROTOTYPE(sharper)` in `SharperImmutability.IsClosed`.
- [x] Sequence type: decide whether array creation should accept sequence elements (`new int[3][..]`, today CS0178, or `new int[..][3]`). [#64](https://github.com/ok3mad/roslyn/issues/64): no, array creation keeps its C# meaning; write `new IEnumerable<int>[3]`. Not worth fixing for now; can be revisited.
- [x] Function types: parentheses apply a suffix to the whole function type, `((int) -> int)?`, `((int) -> int)[]` and `((int) -> int)~`, with the new node `ParenthesizedTypeSyntax`. `((p)->F)[i]`, `((p)->F)?.X`, `((p)->F) ? a : b` and `(((p)->F))(x)` keep their C# meaning. [#58](https://github.com/ok3mad/roslyn/issues/58)
- [x] Function types in patterns: declaration, type, recursive, list and property patterns, `switch` statements and expressions (`x is (int) -> int f`, `case () -> void a:`). In C#, `e is (p)->F` is a syntax error (the pattern parser stops at `->`), so there was no C# meaning to keep; `e is p->F` keeps its meaning. [#59](https://github.com/ok3mad/roslyn/issues/59)
- [x] Report constraint errors of the short type forms where their C# twins do: on a function type's parameter or return type ([#62](https://github.com/ok3mad/roslyn/issues/62)), and on the element of `T[..]` ([#65](https://github.com/ok3mad/roslyn/issues/65)) and `T~`.
- [ ] Function types: `ref`, `out`, `in`, `ref readonly` and `scoped` parameters, ref returns and more than 16 parameters as the anonymous delegate of a lambda's natural type, with a "declare a named delegate" error on members visible outside the assembly, for written and inferred types. `PROTOTYPE(sharper)` in `BindFunctionType`. [#56](https://github.com/ok3mad/roslyn/issues/56)
- [ ] Function types: parameter names, `(int width, int height) -> int` ([#57](https://github.com/ok3mad/roslyn/issues/57)): N1 names in the syntax ([#212](https://github.com/ok3mad/roslyn/issues/212), done: `FunctionTypeParameterSyntax`, CS20007 retired), N2 names on the type symbol ([#213](https://github.com/ok3mad/roslyn/issues/213)), N3 IDE ([#214](https://github.com/ok3mad/roslyn/issues/214)), N4 names in metadata and named arguments ([#215](https://github.com/ok3mad/roslyn/issues/215)). N2 to N4 are decided (see Decisions).
- [ ] Sharpener: rewrite `F<a[..]>(x)` (two comparisons in C#) as `F < (a[..]) > (x)`, the sequence type's accepted break. [#66](https://github.com/ok3mad/roslyn/issues/66)
- [ ] Sharpener: rewrite `F<(p)->b>(x)` (two comparisons in C#) as `F < ((p)->b) > (x)`, the function type's accepted break, which was documented only in the feature page. Like `F<a[..]>(x)`, it also covers the argument-list form `M(F < (p)->b, c > (x))`. [#114](https://github.com/ok3mad/roslyn/issues/114)
- [ ] `readonly` locals: decide `readonly` in `for` initializers, `foreach`, deconstruction and `out` (no modifier slot; a public syntax change). [#95](https://github.com/ok3mad/roslyn/issues/95)
- [ ] `readonly` locals: IDE support (completion, refactorings that drop or break `readonly`). [#96](https://github.com/ok3mad/roslyn/issues/96)
- [x] `readonly` parameters, the grid's remaining explicitly typed corner. [#97](https://github.com/ok3mad/roslyn/issues/97)
- [ ] Inferred parameter type: steps P5 to P7 (P4 is a prototype; P8 is decided: an error, built with P4 and R5) (the rest of [#27](https://github.com/ok3mad/roslyn/issues/27)); P3's open point, `val x` as the shorthand of `readonly infer x` on method parameters, is [#178](https://github.com/ok3mad/roslyn/issues/178); the default value is bound twice, once to infer and once against the type. P2 ([#73](https://github.com/ok3mad/roslyn/issues/73)): a deterministic test for the cycle publication race ([#168](https://github.com/ok3mad/roslyn/issues/168)); Edit and Continue when the copied member's signature changes ([#169](https://github.com/ok3mad/roslyn/issues/169)).
- [ ] Inferred return type: step R5's follow-ups ([#201](https://github.com/ok3mad/roslyn/issues/201)); a local function's nullable inference sees captured variables at their declared state; a local function that calls the inferred method around it makes a cycle even when the method never calls it.
- [ ] Inferred return type after R4 ([#70](https://github.com/ok3mad/roslyn/issues/70)): properties and indexers that override or implement nothing, from their getter (overrides and implementations copy since [#166](https://github.com/ok3mad/roslyn/issues/166)); `ref` returns; the anonymous union exception for visible members, with unions ([#31](https://github.com/ok3mad/roslyn/issues/31)).
- [ ] Inferred return type: `Infer_Cycle_Concurrent` fails about once in 2,000 iterations, on `main` too: under threads, the cycle that CS20033 names for one member of a three-member cycle can differ from the single-threaded one (seen while validating R4).
- [ ] Inferred return type: async iterators (`IAsyncEnumerable<T>`), and an inferred union when the returned values have no best common type, which waits for [#31](https://github.com/ok3mad/roslyn/issues/31).
- [ ] Runtime library (decided, see [Decisions](#decisions)): create it, with `Fail<T>` as its first type. Open: its name and namespace (the embedded attributes use `Sharper`), the package and its target frameworks, how a project gets the reference, and whether a C# project references it to write `Fail<T>` in its own signatures.
- [ ] Runtime library: decide each further candidate against the rule, a type that two assemblies must agree on. The candidates so far: `collect` and `Retry`; `NoValue` from the `Try*` bindings; a shared equivalence interface for memoization ([#154](https://github.com/ok3mad/roslyn/issues/154), [#140](https://github.com/ok3mad/roslyn/issues/140)); delegates for `ref`, `out` and `in` function types ([#56](https://github.com/ok3mad/roslyn/issues/56)); and the anonymous union templates, which only friend assemblies would need shared ([#131](https://github.com/ok3mad/roslyn/issues/131)).
- [ ] Tagged failures (decided, see [Decisions](#decisions)), to settle before lifting ([#32](https://github.com/ok3mad/roslyn/issues/32)): whether `Fail<T>` is a struct or a class; how a failure is written and matched (`Fail(e)`, a keyword, or a conversion from the error value); how a visible member spells several errors, since `Fail<(A | B)>` holds an anonymous union (one `Fail<E>` case per error, or a named union of the errors); and what `try` arms, `collect` and the `Try*` bindings produce.
- [ ] `readonly` locals share the `val` follow-ups: silence CS20010 with CS20003 (#84), the "never reassigned" fixer can offer `readonly int` (#89), and the `SharperFacts` helpers (#88, built with #87) and Edit and Continue (#90) items cover them too.
- [x] Run the upstream parser tests with Sharper on ([#63](https://github.com/ok3mad/roslyn/issues/63)): `ROSLYN_TEST_SHARPER=1` adds the feature to the test parse options (`TestOptions`, `CSharpTestSource.Parse`, the `ParsingTests` helpers). Tests that call `SyntaxFactory` without options are not covered. Last run (net10.0, Debug, `main` with `val`, `readonly` locals and parameters, `T[..]`, function types, also parenthesized and in patterns, and `T~`; the Syntax project again on `sharper/pipe` with `|>`, with the same 37 failures), with no valid C# parsing differently:
  - Syntax project: 37 of 10,844 fail, all expected. 32 parse invalid C# with `readonly` on a local or a parameter, which Sharper accepts, so CS0106, CS9229 or CS8328 is gone (`DeclarationScopeParsingTests` 22, `RefReadonlyTests` 6, including `InverseReadOnlyRefShouldBeIllegal` and `RefReadonlyWithScoped_03` for `readonly ref int p`, `StatementParsingTests.TestUsingVar*` 2, `TopLevelStatementsParsingTests.UsingLocalDeclaration_06`, `DeclarationParsingTests.DefiniteStatementAfterGenericType_ReadOnlyKeyword`). 3 parse a contextual modifier before a destructor at C# 15, `partial ~C()` and `safe ~C()`, where `partial~` is now a task type (`SafeModifierParsingTests.Destructor_Before`, `Destructor_ExternSafe_Before`, `ModifierParserRecoveryTests.PartialBeforeDestructor`); both are errors either way. 2 assert that the options have no features (`LocalFunctionParsingTests.DiagnosticsWithoutExperimental`, `NodesWithExperimental`).
  - Semantic project: 29 of 20,085 fail. 24 are `SharperTests` that check the flag-off behavior or both modes; run the project with `--filter "FullyQualifiedName!~SharperTests"`. 5 are the same `readonly` locals in invalid C# (`UsingDeclarationTests.UsingDeclarationsWithInvalidModifiers`, `RefFieldTests.TypeScopeModifier_02_CSharp10` and `_CSharp11`, `ReadOnlyStructsTests.ReadOnlyLambda`, `TopLevelStatementsTests.LocalDeclarationStatement_02`). All pattern and deconstruction binding tests pass.
  - The run found one scanner difference, fixed: `ScanTupleOrSharperFunctionType` took a `ref` tuple element as a parameter modifier and refused the tuple, so `(ref readonly int, int)? t = null;` lost C#'s recovery (`RefReadonlyTests.RefReadonlyReturn_UnexpectedBindTime`; `SharperTests.FunctionType_Parsing_RefTupleElement`).
