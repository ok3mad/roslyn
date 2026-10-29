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
| `dynamic` | In an iterator of `object`, `..d` is valid C# (d converts to `Index` at runtime), so it stays a range and throws when d holds a sequence. In an iterator of `int` it spreads. | Warning CS20002. Deciding at runtime is the correct long-term semantics, but changes what valid C# does today. |
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
| `dynamic` | Keep the range meaning, warn (CS20002). | A runtime decision changes the behavior of valid C#. |
| val in patterns | A keyword in patterns, parsed like var (option 1). | Keeps val and var consistent. It breaks type patterns on a type named val, so the sharpener escapes those as @val. The alternative, reinterpreting a type pattern in the binder, kept C# meaning but diverged from var. |
| Sequence type stacking | Left to right, innermost first: each `[..]` wraps everything to its left (`int[..][]` is `IEnumerable<int>[]`); plain array ranks between two `[..]` keep the C# order. | The brainstorm's rule for all type suffixes (`int[..]~`, `int~[..]`), and the reading order of `T?[]`. The C# array order would make `int[..][]` a sequence of arrays. |
| Sequence type in a cast | `(A[..])` is a cast only before an identifier or a literal; `(int[..])` is always a cast. | `(a[..])(b)`, `(a[..])!`, `(a?[..]).b` and `(a[..]) with { }` are valid C# element accesses, which the usual "ambiguous type before `(`" rule would turn into casts. |
| `(val x) => ...` | An implicitly typed lambda parameter that cannot be reassigned, decided in the binder. | `val x` already parses as a parameter of type `val`; binding it like `var` (a type named `val` wins) keeps the C# meaning without a parser change. |
| Non-readonly members on a val struct | Run on a copy, with warning CS20003 (option C). | The foreach behavior let a method change the val, which breaks immutability. A silent copy (the readonly field rule) makes mutations disappear without notice. The warning makes the copy visible; member mutation inference will later silence it for members that do not mutate. |
| `->` as a lambda arrow | No: `->` is only the arrow of function types; lambdas keep `=>`. | `x -> F(x)` is already a pointer member access, parsed the same in and out of unsafe code (`ParsePostFixExpression`); a lambda meaning could only win after binding fails. |
| What a function type lowers to | Always `Func` or `Action`; `ref`/`out`/`in` parameters and more than 16 parameters are errors for now (#56). | The lambda's natural type synthesizes an anonymous delegate there, but that type is internal to one compilation, so it cannot appear in public signatures, where function types mostly go. |
| Implicit `it` scope | The innermost argument around `it` whose parameter, in the chosen overload, is a one-parameter delegate; a pipe stage is the outermost slot. Decided in the binder: the argument is bound as an implicit lambda first, and as a value only when no candidate takes a one-parameter delegate there. | Greeter's `() => profiles.Load(it.Id)` needs a type-directed rule: Scala's syntactic `_` rule would make `Load(x => x.Id)`. Converting a lambda to a parameter that is not a delegate fails without binding its body, so the fallback costs one more overload resolution, not a second binding. |
| Nested `it` | An error: one meaning of `it` per implicit lambda or pipe stage. An outer `it` plus a value and a function overload at the same argument is an error too. | Telling two `it`s apart needs every signature in between, and adding a function in between, or an overload in a library, would move the boundary silently. Allowing it later breaks nothing; banning it later would. |
| Where `ValKeyword` is a keyword | Only on tokens the parser makes, with Sharper on, for a val pattern. `GetContextualKeywordKind("val")` is `None`, and `GetContextualKeywordKinds()` does not list `ValKeyword`; `IsContextualKeyword(ValKeyword)` (and so `IsKeywordKind`) stays true. | Callers of the text-based and list members (Razor's tokenizer and escaping, and through `GetKeywordKinds()` the IDE's doc-comment `langword` completion) apply them to all code, so they must answer as upstream ([#109](https://github.com/ok3mad/roslyn/issues/109), design rule 2). The kind-based check only sees tokens that already are keywords: with Sharper off none exists, and the incremental parser (`Blender.Reader.IsFabricatedToken`) needs it so it re-parses a `ValKeyword` token instead of reusing it where `val` is an identifier. The upstream test of the list's range (`SyntaxTests.TestRangeBasedGetKindsMethodsReturnExpectedResults`) stops below `ValKeyword`. |
| New syntax nodes | Allowed when no existing node can hold the tokens; numbered from `SyntaxKind` 9900 up. | `(A) -> R` fits no existing node. A high range keeps upstream merges from reusing the values, as CS20000 does for diagnostics. |

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

The brainstorm proposes 26 pieces of syntax (#28, discard arguments, is still to be discussed). Rows 14 and 26, the parameter switch body and its lambda form, were rejected (see [Dropped](brainstorm.md#dropped)), and the other rows keep their numbers. Each row has a GitHub issue, collected under the tracking issue [#12](https://github.com/ok3mad/roslyn/issues/12). Nine are done: `yield return ..xs`, `val` in locals, deconstruction, patterns and lambda parameters, `readonly` locals and parameters, the sequence type `T[..]` and function types `(A, B) -> R`. Everything that depends on unions or purity waits for those designs.

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
| 9 | Function types | `(A, B) -> R`, `() -> T` | `Func<...>`, `Action<...>` | yes: new syntax nodes, `FunctionTypeSyntax` and `ParenthesizedTypeSyntax` (`((F))?`, done in [#58](https://github.com/ok3mad/roslyn/issues/58)), a one-pass tuple-or-function lookahead, and function types in patterns (done in [#59](https://github.com/ok3mad/roslyn/issues/59)) | M | Prototype | Decided: `->` only in types, lambdas keep `=>`. Follow-ups: `ref` parameters [#56](https://github.com/ok3mad/roslyn/issues/56), names [#57](https://github.com/ok3mad/roslyn/issues/57) | [#21](https://github.com/ok3mad/roslyn/issues/21) |
| 10 | Inferred return type | `static infer Hello(int id) => ...` | method with the inferred return type | none (`infer` parses as a type name) | L, in steps R1 to R5 | Med; R1 prototype | Ordering and cycles. Allowed on public members too, with no warning. Replaces the expression-bodied `fun`. R1 (expression bodies of private methods and local functions) is a prototype; see [Feature 5](#feature-6-inferred-return-type-step-r1) and the [feature page](../features/inferred-return-type.md) | [#22](https://github.com/ok3mad/roslyn/issues/22) |
| 11 | `readonly` parameters | `void F(readonly int x)` | plain parameter plus the `val` no-reassignment check | none (`readonly` already parses as a parameter modifier; C#'s error without `ref`, CS9190, comes from the binder) | S | Prototype | Completes the mutability grid with #4 and #5. Not part of the signature; `readonly ref` forbids ref reassignment, as for locals. Replaces the `mutable` parameter in `fun`: immutable-by-default parameters and a `mutable` marker are a strictness ladder setting now, not syntax. See [Feature 5](#feature-5-readonly-parameters) | [#23](https://github.com/ok3mad/roslyn/issues/23), [#97](https://github.com/ok3mad/roslyn/issues/97) |
| 15 | Inferred parameter types | `public infer Load(infer userId) => ...`, `infer timeout = TimeSpan.FromSeconds(5)` | method with the inferred types | none (`infer x` parses as a typed parameter) | S to XL, in steps P1 to P8 | Med | Copied from an overridden member, a default value, a function type or a call that pins one type, never from shape. Generic by default is open. Replaces untyped `fun` parameters inferred from arms | [#27](https://github.com/ok3mad/roslyn/issues/27) |

### Phase 2: functional core

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 12 | Implicit `it` | `xs.Select(Process(it, a))`, `when: it is X` | lambda `x => ...` | none | L | Prototype | Decided: the innermost argument whose parameter is a one-parameter delegate becomes the lambda; one meaning of `it` per lambda or stage; an outer `it` plus value and function overloads is an error; an existing `it` keeps its C# meaning (see [Decided](brainstorm.md#decided)). Calls only so far; `LookupSymbols` and Edit and Continue do not see the lambda yet. See the [feature page](../features/implicit-it.md) | [#24](https://github.com/ok3mad/roslyn/issues/24) |
| 13 | Pipe, without lifting | `x \|> F`, `x \|> F(it, y)` | nested call | yes (new `\|>` token) | M | Med–High | A bare stage versus a stage that uses `it` | [#25](https://github.com/ok3mad/roslyn/issues/25) |
| 16 | `try` expression | `try await F(url) catch { HttpRequestException { ... } => ..., }` | try/catch with the arms in a catch filter, with spilling | yes (`try` in expression position) | L (low end) | Med; slice 1 designed | Slice 1: target-typed to a declared C# 15 union, or the best common type of the body and arms; anonymous union results wait for #19. Unmatched exceptions propagate untouched through the filter; no `finally`; an awaitable body must be awaited. See [Feature 8](#feature-8-try-expression-slice-1-design) | [#28](https://github.com/ok3mad/roslyn/issues/28) |
| 17 | Lambda to single-method interface | `Render(user, id => new Profile())` | generated sealed class | none | M | Med | Only where C# reports an error today; the class needs a readable name for stack traces | [#29](https://github.com/ok3mad/roslyn/issues/29) |
| 18 | Interface initializer | `new IProfiles { Load = id => ... }`, `_ = Unexpected` | generated class | none (binder only) | M | Med | Builds on #17 | [#30](https://github.com/ok3mad/roslyn/issues/30) |

### Phase 3 and later: type system and effects

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 19 | Inferred or structural unions | `(Profile \| Unavailable)~` | not decided | yes | XL | Low | Spelling never discussed; CLR representation open (nominal, tagged or erased to `object`) | [#31](https://github.com/ok3mad/roslyn/issues/31) |
| 20 | Lifting in pipes and arguments | `users.Find(id) \|> Render(it, p)` | await, `Select` or union passthrough | none | XL | Low | Implicit versus an explicit marker; breaks down at generic and `object` parameters | [#32](https://github.com/ok3mad/roslyn/issues/32) |
| 21 | Refined types and members | `decimal Amount is >0`, `int is >=0` | plain type plus boundary checks | yes (pattern in a type position) | XL | Low–Med | Proof obligations at call sites; runtime guards for C# callers | [#33](https://github.com/ok3mad/roslyn/issues/33) |
| 22 | Opaque patterns | `is > 0 and <= max` | pattern plus a `when` guard | none (relational patterns already parse any expression) | M, plus purity | Med | Sound only with purity (#24) | [#34](https://github.com/ok3mad/roslyn/issues/34) |
| 23 | Extractor patterns | `s is Parse(val n and >0)` | call plus a sub-pattern | none | M–L | Low | Clashes with a positional pattern on a type named `Parse` | [#35](https://github.com/ok3mad/roslyn/issues/35) |
| 24 | `pure` and `total` modifiers | `total int Fib(...)`, `pure(provider)` | erased, plus attributes | small | XL (syntax alone S) | Low | Needs the whole effect checker and bindings for existing .NET code | [#36](https://github.com/ok3mad/roslyn/issues/36) |
| 25 | Member declared on another type | `static infer Currency.Parse(string s)` | C# 14 extension block | yes | M | Low | Appears only in an example; without `fun` it is spelled like an explicit interface implementation | [#37](https://github.com/ok3mad/roslyn/issues/37) |
| 27 | `assume` escape hatch | `assume x is >0` | runtime check | yes | S–M | Low | Only an open question so far | [#39](https://github.com/ok3mad/roslyn/issues/39) |

### Proposed, to be discussed

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 28 | Discard arguments | `store.LoadAsync(_)` | the only fitting local or parameter in scope, such as the one `CancellationToken` | none (`_` parses as an identifier) | L | Low | Not yet discussed whether it is sound: implicit wiring, which candidates count, and interaction with overload resolution. See [Discard arguments](brainstorm.md#discard-arguments-proposed-not-yet-discussed) | [#40](https://github.com/ok3mad/roslyn/issues/40) |

Not listed: `union Name(A, B)` comes from C# 15, and `collect`, `Retry` and `Unexpected` are library code.

### Suggested order

1. `val` (#2, #3, #6), in three steps: `val x = e` locals (binder only), then deconstruction and designations (parser), then patterns (keyword). It exercises the gated parser, the first deliberate break with its `@val` escape, and new semantics on an existing symbol, none of which the first PoC touched. The no-reassignment check reuses the read-only local check behind CS1656.
2. `T[..]` (#7), done. Its binder rewrite onto a BCL type is the pattern for the other short type forms (see [Feature 3](#feature-3-sequence-type-t)).
3. `T~` (#8) and function types (#9), done.
4. `readonly` parameters (#11), done.
5. Type inference (#10 and #15), the first real inference work, in the steps of the
   [work breakdown](#type-inference-work-breakdown).
6. Rows 19 onward wait for the union representation and the purity design.

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
| R3 | Methods with inferred return types calling each other: binding order across members, cycle and recursion errors | L | Whether a recursive method with a non-recursive `return` is allowed later |
| R4 | Every member kind and accessibility: public and protected members, `virtual`, `partial`, operators, metadata emission, Edit and Continue (a body edit that changes the type is a signature change) | M | None for public members: they infer like any other (decided) |
| R5 | IDE: Quick Info and inlay hints show the inferred type, a fixer writes it out | M | |

Parameter types (#15):

| Step | Scope | Size | Open point |
| --- | --- | --- | --- |
| P1 | `infer` as a parameter type, with source 2: the default value's type. `null`, `default` and target-typed values give an error | S | Prototype ([#72](https://github.com/ok3mad/roslyn/issues/72)). Decided: every member kind and lambdas; a value that is not a constant gives its type, then CS1736 as in C#; cycles through signatures are CS20028 |
| P2 | Source 1: the overridden or implemented member gives the parameter and return types. Members are matched by name and parameter count before their types are known; more than one candidate is an error | M | Explicit interface implementations; a partial `infer` signature where some types are written |
| P3 | `readonly infer x` | S | Whether `val x` is its shorthand on method parameters |
| P4 | Source 4, first cut: the parameter passed directly as an argument to a call with exactly one candidate (method, constructor, delegate). Needs binding the body with a placeholder type | L | How the placeholder interacts with lambdas and local functions in the body |
| P5 | Source 4 with overloads: every applicable candidate takes the same type at that position, including generic methods whose type arguments come from other arguments | L | |
| P6 | Source 4, other pinning uses: assignment to a typed local, field or property, `return x` from a method with a written return type, `ref` and `out` arguments. Several uses must agree, and the error names each one | M | |
| P7 | Source 3: a function type the method is converted to, local functions first, then private methods | L | Conversions that appear after the declaration in the same method |
| P8 | Source 5: decide generic by default or an error, then build it | M if generic | Undecided; see the brainstorm |

Suggested order: R1, P1, P2, P3, R2, R3, P4, P5, P6, P7, R4, R5, and P8 once it is decided. P4 to P6 are the
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
| `val x => x` (no parentheses) | A syntax error, with or without the flag, as C# requires parentheses for parameter modifiers. A clearer message is [#98](https://github.com/ok3mad/roslyn/issues/98). |
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
| Using itself | CS20018, permanent | The brainstorm's F# rule. A recursive method with a non-recursive `return` stays the R3 open point |
| Using another inferred method | CS20017 in R1, reported on the user | Ordering is R3; reporting on the user keeps the other method valid, and the error type is never cached, so the result does not depend on the order |
| Unsupported positions | CS20015 with the position, CS20016 for no body; properties, indexers and operators included | A clear "not yet" instead of CS0246 for a type named `infer` |
| Where the diagnostics are | On `infer` for CS20015 to CS20018, on the body for CS20014 | The use of an inferred method is found through the symbol, which has no syntax |

### Plan for the later steps

- **R2, block bodies.** Done; see [step R2](#feature-6-inferred-return-type-step-r2). The plan held, except that the
  binder reads the method's own return type in more places than the return statement.
- **R3, inferred methods using each other.** Turn the thread-static frame into a stack. A read of another inferred
  method whose type is not published infers it in a nested frame on the same thread (no lock is held, so nothing can
  deadlock); a method already on the stack is a cycle, reported on every method of the cycle. Two threads may infer
  the same method at once and compute the same type. A published type is read directly, also during inference,
  which is safe once nesting is allowed. Partial declarations and other types need nothing more, since inference is
  per symbol.
- **R4, every member.** Lift the accessibility row. Overrides and explicit implementations copy the type of the
  member they implement (P2), so `MethodChecks` must find that member before the custom-modifier copy that now
  assumes a return type. Properties need the same lazy type on `SourcePropertySymbolBase`; operators on
  `SourceUserDefinedOperatorSymbolBase`. Edit and Continue treats a changed inferred type as a signature change.

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
| Inferred local functions declared in an inferred body | CS20017 even when unused, until R3 | Binding the declaration reads the local function's return type; R3's nested frames remove the error |

Tests: 8 `Infer_Block_*` tests in `SharperTests.cs` next to the C# twins (types, runtime output, errors, nullable
annotations, the semantic model and IOperation), and the R1 tests updated for `async` and async iterators. All 261
Sharper test cases pass, also with `ROSLYN_TEST_IOPERATION=1`. Regression run (net10.0, Debug): Semantic 20,083
passed, Emit 7,312, Emit2 2,191 and Emit3 27,951, none failed; the Syntax project with `ROSLYN_TEST_SHARPER=1` has the
37 expected failures listed under Follow-ups and no other.

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

## Feature 8: `try` expression, slice 1 (design)

`RateResult r = try await http.GetFromJsonAsync<RateTable>(url) catch { HttpRequestException { StatusCode: 404 } =>
new NoRate(from, to), TimeoutException => new FxUnavailable(0) };` (issue [#28](https://github.com/ok3mad/roslyn/issues/28)).
Exceptions become values through switch-style arms; unmatched exceptions propagate, and there is no `finally`. Slice 1
needs no anonymous unions: the result is target-typed to a declared C# 15 union, or has a natural type when the body
and the arms agree. Union results without a target wait for slice 2 of [#31](https://github.com/ok3mad/roslyn/issues/31).
This section is the design; nothing is implemented yet.

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

Tests: 17 `InferParameter_*` tests in `SharperTests.cs`: runtime output for every member kind and lambdas next to C#
twins, the inferred types against written ones (also the default values), the errors, cycles in every order and on
two threads, caller information attributes, signature matching, nullable annotations, the semantic model, IOperation,
metadata seen from a C# compilation, and concurrent inference. `readonly infer x = 1u` already works (P3 needs only
its tests and the `val` decision).

## Follow-ups

Sharper diagnostics were renumbered from `SharperErrorCodes.Base` (20000): CS9402, CS9403 and CS9404 became CS20001, CS20002 and CS20003, and this document uses the new numbers throughout.

The open items are marked in code as `// PROTOTYPE(sharper):` (`git grep "PROTOTYPE(sharper)"`).

- [x] Turn Sharper on for `.cx` files in the command-line compiler.
- [x] Turn Sharper on for `.cx` documents in the IDE workspace.
- [x] Treat `.cx` files as C# in the language server.
- [ ] Support `dotnet run app.cx`. File-based apps live in the SDK and only accept `.cs` files, so this needs an SDK change; until then the language server treats a loose `.cx` file as a miscellaneous file, never a file-based app.
- [x] Add `*.cx` to the default `Compile` items (the `Microsoft.Net.Compilers.Toolset` props).
- [ ] Decide whether `dynamic` should choose range or spread at runtime, under the sharpening rules. [#42](https://github.com/ok3mad/roslyn/issues/42)
- [x] Decide whether async iterators should ever support spreads: never for an asynchronous source. [#43](https://github.com/ok3mad/roslyn/issues/43)
- [x] Support spreading a synchronous source in an async iterator. [#49](https://github.com/ok3mad/roslyn/issues/49)
- [x] Give ref-struct sources a spread-specific error instead of CS4007. [#44](https://github.com/ok3mad/roslyn/issues/44)
- [x] Verify the spread with Edit and Continue: the generated try/finally and the yield share one syntax offset. Fixed with a second state ordinal, and the IDE no longer matches a spread with a plain yield return. [#45](https://github.com/ok3mad/roslyn/issues/45)
- [ ] Design a real IOperation shape once the syntax settles. [#46](https://github.com/ok3mad/roslyn/issues/46)
- [ ] Avoid the O(depth) cost of recursive spreads with a delegating state machine. [#47](https://github.com/ok3mad/roslyn/issues/47)
- [x] Number Sharper diagnostics from their own base, `SharperErrorCodes.Base` = 20000 (CS20001 and up), far from upstream.
- [x] Full C# compiler regression run (net10.0, Debug): 88,327 tests passed and 0 failed across 10 test projects. WinRT was not run; it targets .NET Framework only.
- [ ] Infer whether a struct member mutates `this`, and silence CS20003 for members that do not. Until then, every call to a non-`readonly` member of a `val` warns, including interface members on a generic `val` (`g.Get()`). [#84](https://github.com/ok3mad/roslyn/issues/84); its default severity until then is [#85](https://github.com/ok3mad/roslyn/issues/85).
- [x] Copy and warn for the pattern-based members that bypassed the shared check: `await`/`GetAwaiter`, implicit Index and Range indexers, list patterns and property patterns. Collection spreads and `using` already copy their operand.
- [ ] Build the sharpening infrastructure and move Sharper to the `.cx` extension. The accepted breaks need it: `val` as a keyword in patterns, and `val (a, b) = e` as a declaration. The sharpener rewrites those to `@val`, and the extension keeps plain `.cs` files on C# semantics. The `.cx` part is done (see above); escaping `val` is [#86](https://github.com/ok3mad/roslyn/issues/86).
- [x] Give `val` its own messages: CS20004 and CS20005 (the `val` forms of CS8508 and CS8136), CS20006 for ref reassignment, and the 'val variable' cause also for `foreach` and `using` variables.
- [x] Script fields: `val` is a read-only inferred field, like `var` plus read-only.
- [x] Give `val` its own token kind: `SyntaxKind.ValKeyword` (8466, the top free slot below `ElifKeyword`, guarded by a test), used by val patterns. Unlike `var`, the lexer and `SyntaxFacts` do not report it for the text `val` ([#109](https://github.com/ok3mad/roslyn/issues/109)).
- [ ] `val`: IDE support (classification, Quick Info, completion, hints, and refactorings that write `var`). [#87](https://github.com/ok3mad/roslyn/issues/87)
- [ ] `val`: decide whether to expose val-ness in the public API (`ILocalSymbol` has no read-only flag). [#88](https://github.com/ok3mad/roslyn/issues/88)
- [ ] `val`: analyzer and fixer "local is never reassigned, use `val`". [#89](https://github.com/ok3mad/roslyn/issues/89)
- [ ] `val`: verify Edit and Continue with `val` locals and their struct copies. [#90](https://github.com/ok3mad/roslyn/issues/90)
- [ ] Implicit `it` in object creation, indexers and constructor initializers; today only calls. [#122](https://github.com/ok3mad/roslyn/issues/122)
- [ ] Implicit `it`: `LookupSymbols` and speculative binding inside an implicit lambda, which the semantic model finds by lambda syntax. [#123](https://github.com/ok3mad/roslyn/issues/123)
- [ ] Implicit `it`: Edit and Continue, whose lambda matching also works on syntax. [#124](https://github.com/ok3mad/roslyn/issues/124)
- [ ] Implicit `it`: variables declared next to or inside an implicit lambda, such as `F(Make(it), out var v)`. [#125](https://github.com/ok3mad/roslyn/issues/125)
- [ ] Implicit `it`: an error that names the inner call when it claims `it` (today CS0411 at the outer call), and a cause string for writes to `it`. [#126](https://github.com/ok3mad/roslyn/issues/126)
- [ ] Implicit `it`: the cost of the two trials and of the per-call syntax scan. [#127](https://github.com/ok3mad/roslyn/issues/127)
- [ ] Implicit `it`: IDE support (Quick Info, hints) and the "name the parameter" fixer. [#128](https://github.com/ok3mad/roslyn/issues/128)
- [ ] `try` expression: test a target-typed switch and `?:` into a C# 15 union before slice 1's binding. [#137](https://github.com/ok3mad/roslyn/issues/137)
- [ ] `try` expression: a warning on a catch-all arm. [#135](https://github.com/ok3mad/roslyn/issues/135)
- [ ] `try` expression: lift a task body instead of warning, after lifting (#32). [#136](https://github.com/ok3mad/roslyn/issues/136)
- [ ] Test `/nowarn` and `.editorconfig` with five-digit Sharper IDs. [#91](https://github.com/ok3mad/roslyn/issues/91)
- [ ] Decide immutability depth: binding, value or deep (a type-level immutability marker). [#92](https://github.com/ok3mad/roslyn/issues/92)
- [ ] Sequence type: decide whether array creation should accept sequence elements (`new int[3][..]`, today CS0178, or `new int[..][3]`). [#64](https://github.com/ok3mad/roslyn/issues/64)
- [x] Function types: parentheses apply a suffix to the whole function type, `((int) -> int)?`, `((int) -> int)[]` and `((int) -> int)~`, with the new node `ParenthesizedTypeSyntax`. `((p)->F)[i]`, `((p)->F)?.X`, `((p)->F) ? a : b` and `(((p)->F))(x)` keep their C# meaning. [#58](https://github.com/ok3mad/roslyn/issues/58)
- [x] Function types in patterns: declaration, type, recursive, list and property patterns, `switch` statements and expressions (`x is (int) -> int f`, `case () -> void a:`). In C#, `e is (p)->F` is a syntax error (the pattern parser stops at `->`), so there was no C# meaning to keep; `e is p->F` keeps its meaning. [#59](https://github.com/ok3mad/roslyn/issues/59)
- [x] Report constraint errors of the short type forms where their C# twins do: on a function type's parameter or return type ([#62](https://github.com/ok3mad/roslyn/issues/62)), and on the element of `T[..]` ([#65](https://github.com/ok3mad/roslyn/issues/65)) and `T~`.
- [ ] Sharpener: rewrite `F<a[..]>(x)` (two comparisons in C#) as `F < (a[..]) > (x)`, the sequence type's accepted break. [#66](https://github.com/ok3mad/roslyn/issues/66)
- [ ] Sharpener: rewrite `F<(p)->b>(x)` (two comparisons in C#) as `F < ((p)->b) > (x)`, the function type's accepted break, which was documented only in the feature page. Like `F<a[..]>(x)`, it also covers the argument-list form `M(F < (p)->b, c > (x))`. [#114](https://github.com/ok3mad/roslyn/issues/114)
- [ ] `readonly` locals: decide `readonly` in `for` initializers, `foreach`, deconstruction and `out` (no modifier slot; a public syntax change). [#95](https://github.com/ok3mad/roslyn/issues/95)
- [ ] `readonly` locals: IDE support (completion, refactorings that drop or break `readonly`). [#96](https://github.com/ok3mad/roslyn/issues/96)
- [x] `readonly` parameters, the grid's remaining explicitly typed corner. [#97](https://github.com/ok3mad/roslyn/issues/97)
- [ ] Inferred parameter type: steps P2 to P8 ([#73](https://github.com/ok3mad/roslyn/issues/73), [#74](https://github.com/ok3mad/roslyn/issues/74) and the rest of [#27](https://github.com/ok3mad/roslyn/issues/27)); the default value is bound twice, once to infer and once against the type.
- [ ] Inferred return type: steps R3 to R5 ([#69](https://github.com/ok3mad/roslyn/issues/69) to [#71](https://github.com/ok3mad/roslyn/issues/71)); a local function's nullable inference sees captured variables at their declared state.
- [ ] Inferred return type: async iterators (`IAsyncEnumerable<T>`), and an inferred union when the returned values have no best common type, which waits for [#31](https://github.com/ok3mad/roslyn/issues/31).
- [ ] `readonly` locals share the `val` follow-ups: silence CS20010 with CS20003 (#84), the "never reassigned" fixer can offer `readonly int` (#89), and the public API (#88) and Edit and Continue (#90) items cover them too.
- [x] Run the upstream parser tests with Sharper on ([#63](https://github.com/ok3mad/roslyn/issues/63)): `ROSLYN_TEST_SHARPER=1` adds the feature to the test parse options (`TestOptions`, `CSharpTestSource.Parse`, the `ParsingTests` helpers). Tests that call `SyntaxFactory` without options are not covered. Last run (net10.0, Debug, `main` with `val`, `readonly` locals and parameters, `T[..]`, function types, also parenthesized and in patterns, and `T~`), with no valid C# parsing differently:
  - Syntax project: 37 of 10,834 fail, all expected. 32 parse invalid C# with `readonly` on a local or a parameter, which Sharper accepts, so CS0106, CS9229 or CS8328 is gone (`DeclarationScopeParsingTests` 22, `RefReadonlyTests` 6, including `InverseReadOnlyRefShouldBeIllegal` and `RefReadonlyWithScoped_03` for `readonly ref int p`, `StatementParsingTests.TestUsingVar*` 2, `TopLevelStatementsParsingTests.UsingLocalDeclaration_06`, `DeclarationParsingTests.DefiniteStatementAfterGenericType_ReadOnlyKeyword`). 3 parse a contextual modifier before a destructor at C# 15, `partial ~C()` and `safe ~C()`, where `partial~` is now a task type (`SafeModifierParsingTests.Destructor_Before`, `Destructor_ExternSafe_Before`, `ModifierParserRecoveryTests.PartialBeforeDestructor`); both are errors either way. 2 assert that the options have no features (`LocalFunctionParsingTests.DiagnosticsWithoutExperimental`, `NodesWithExperimental`).
  - Semantic project: 29 of 20,085 fail. 24 are `SharperTests` that check the flag-off behavior or both modes; run the project with `--filter "FullyQualifiedName!~SharperTests"`. 5 are the same `readonly` locals in invalid C# (`UsingDeclarationTests.UsingDeclarationsWithInvalidModifiers`, `RefFieldTests.TypeScopeModifier_02_CSharp10` and `_CSharp11`, `ReadOnlyStructsTests.ReadOnlyLambda`, `TopLevelStatementsTests.LocalDeclarationStatement_02`). All pattern and deconstruction binding tests pass.
  - The run found one scanner difference, fixed: `ScanTupleOrSharperFunctionType` took a `ref` tuple element as a parameter modifier and refused the tuple, so `(ref readonly int, int)? t = null;` lost C#'s recovery (`RefReadonlyTests.RefReadonlyReturn_UnexpectedBindTime`; `SharperTests.FunctionType_Parsing_RefTupleElement`).
