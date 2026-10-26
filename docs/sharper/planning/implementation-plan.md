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
- Async iterators are deliberately unsupported (a clear error), and `dynamic` gets a warning instead of a runtime decision.

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
| Diagnostics | `Errors/ErrorCode.cs`, `ErrorFacts.cs`, `CSharpResources.resx` + `.xlf` | CS20001 (async) and CS20002 (dynamic). A checklist at the end of `ErrorCode.cs` lists every place to update. |
| Tests | `Test/Semantic/Semantics/SharperTests.cs` | Diagnostics, runtime output, nullable, IOperation, flow graph, region analysis. |

## Known traps

Each trap was probed before deciding how to handle it.

| Trap | What the probe showed | How the PoC handles it |
| --- | --- | --- |
| Range ambiguity | `..i` is valid C# in iterators of `Range` and of `object` (boxed). | C# is tried first and wins; covered by tests for both element types. |
| Async iterator, sync source | Compiler crash: the async rewriter only accepts `try` blocks with certain syntax, for Edit and Continue. | Error CS20001. May never be supported. |
| Async iterator, async source | Misleading "foreach statement cannot operate…" error; would need an implicit `await foreach`. | Same CS20001. |
| Ref-struct sources (spans, inline arrays) | Rejected with CS4007 about `Span<int>.Enumerator`, which the user never wrote. | Kept as is; a clearer error is a follow-up. |
| `dynamic` | In an iterator of `object`, `..d` is valid C# (d converts to `Index` at runtime), so it stays a range and throws when d holds a sequence. In an iterator of `int` it spreads. | Warning CS20002. Deciding at runtime is the correct long-term semantics, but changes what valid C# does today. |
| Both bindings fail (`..5`) | The spread error (CS9212) explains it better than the range error. | Spread error wins. |

## Decisions

| Decision | Outcome | Why |
| --- | --- | --- |
| How Sharper is gated | Feature flag now; the `.cx` file extension later. | Flags live on each syntax tree's parse options, so binder checks stay the same. The extension switch is a per-file parse-options variant, like `.csx` scripts in `CSharpCompiler.cs`, plus per-document options in the IDE workspace. `.cx` is one letter from `.cs`, is used by no other .NET tool, and is unknown to GitHub Linguist (see [File extension](../README.md#file-extension)). |
| Range node or dedicated spread node | Reuse the range node. | No parser, `Syntax.xml` or public API change; C# precedence comes for free. |
| Spread versus range | Spread only where binding as C# reports an error (trial binding). | Matches the no-breaking-changes rule by construction; a hand-written rule missed the `object` case. |
| Element conversion | Implicit, like `yield return x`. | A foreach-style explicit cast would silently accept `..objects` in an `IEnumerable<int>` iterator. |
| Nullable warnings | CS8602 for a maybe-null source, CS8603 for maybe-null elements. | Same warnings as the C# the spread stands for; CS8619 was too generic. |
| IOperation | `OperationKind.None` for now. | A real shape is public API; wait until the syntax settles. |
| Async iterators | Not supported (CS20001). | Needs Edit and Continue work and a hidden `await`; may never be wanted. |
| `dynamic` | Keep the range meaning, warn (CS20002). | A runtime decision changes the behavior of valid C#. |
| val in patterns | A keyword in patterns, parsed like var (option 1). | Keeps val and var consistent. It breaks type patterns on a type named val, so the sharpener escapes those as @val. The alternative, reinterpreting a type pattern in the binder, kept C# meaning but diverged from var. |
| Sequence type stacking | Left to right, innermost first: each `[..]` wraps everything to its left (`int[..][]` is `IEnumerable<int>[]`); plain array ranks between two `[..]` keep the C# order. | The brainstorm's rule for all type suffixes (`int[..]~`, `int~[..]`), and the reading order of `T?[]`. The C# array order would make `int[..][]` a sequence of arrays. |
| Sequence type in a cast | `(A[..])` is a cast only before an identifier or a literal; `(int[..])` is always a cast. | `(a[..])(b)`, `(a[..])!`, `(a?[..]).b` and `(a[..]) with { }` are valid C# element accesses, which the usual "ambiguous type before `(`" rule would turn into casts. |
| Non-readonly members on a val struct | Run on a copy, with warning CS20003 (option C). | The foreach behavior let a method change the val, which breaks immutability. A silent copy (the readonly field rule) makes mutations disappear without notice. The warning makes the copy visible; member mutation inference will later silence it for members that do not mutate. |
| `->` as a lambda arrow | No: `->` is only the arrow of function types; lambdas keep `=>`. | `x -> F(x)` is already a pointer member access, parsed the same in and out of unsafe code (`ParsePostFixExpression`); a lambda meaning could only win after binding fails. |
| What a function type lowers to | Always `Func` or `Action`; `ref`/`out`/`in` parameters and more than 16 parameters are errors for now (#56). | The lambda's natural type synthesizes an anonymous delegate there, but that type is internal to one compilation, so it cannot appear in public signatures, where function types mostly go. |
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

The brainstorm proposes 28 pieces of syntax (#28, discard arguments, is still to be discussed). Each row has a GitHub issue, collected under the tracking issue [#12](https://github.com/ok3mad/roslyn/issues/12). Six are done: `yield return ..xs`, `val` in locals, deconstruction and patterns, the sequence type `T[..]` and function types `(A, B) -> R`. Everything that depends on unions or purity waits for those designs.

- **Size**, relative to the `yield return ..xs` PoC. **S**: about that size (no parser change, one bound node, reused lowering). **M**: a gated parser change, or a new lowering with its own rules. **L**: new inference or binding rules that interact with overload resolution or signature binding. **XL**: a new checker layer (refinements, unions, effects).
- **Certainty**. **High**: decided in the brainstorm, spelling stable. **Med**: the leading direction, details or spelling open. **Low**: only sketched in examples, contradicted elsewhere, or blocked on an open question.

### Phase 1: inference, immutability and short type forms

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Spread in iterators | `yield return ..xs` | `foreach` of yield returns | none | S | Done | Follow-ups below: async iterators, `dynamic`, Edit and Continue | [#13](https://github.com/ok3mad/roslyn/issues/13) |
| 2 | `val` immutable local | `val total = 0;` | `var` plus a no-reassignment check | none (`val` parses as a type name) | S | Done | The only breaking keyword; trial binding plus a sharpener `@val` escape | [#14](https://github.com/ok3mad/roslyn/issues/14) |
| 3 | `val` deconstruction | `val (small, large) = ...;` | deconstruction plus the same check | small: accept `val` wherever the parser checks for `var` (`IsPossibleDeconstructionLeft`, statement disambiguation, `IsVarType`) | S | Done | Comes with #2. Without the parser change, `val (a, b) = e` parses as an invocation; the same checks cover `foreach (val (a, b) in ...)` and `out val (a, b)` | [#15](https://github.com/ok3mad/roslyn/issues/15) |
| 4 | `readonly` locals | `readonly int x`, `readonly var x` | local plus the same check | likely none (modifier probably parses, with an error) | S | Med | Rarely written; completes the mutability grid | [#16](https://github.com/ok3mad/roslyn/issues/16) |
| 5 | `val` lambda parameter | `(val x) => ...` | lambda plus the same check | small | S | Med |  | [#17](https://github.com/ok3mad/roslyn/issues/17) |
| 6 | `val` in patterns | `is { Length: val n }` | `var n` pattern plus the same check | yes: in a pattern the parser turns `val` into a keyword, as it does `var` (`LanguageParser_Patterns.cs`) | S | Done | Decided: `val` is always a keyword in patterns, like `var`. Breaks a type pattern on a type named `val`, so the sharpener escapes it as `@val`. `val i` in an arm is a binding pattern, since arms hold only patterns (#14) | [#18](https://github.com/ok3mad/roslyn/issues/18) |
| 7 | Sequence type | `int[..]` | `IEnumerable<int>` | small: `ScanType` accepts `[..]`; `ScanCast` keeps `(a[..])` an element access | S | Done | `new T[..]` keeps its C# meaning ([#64](https://github.com/ok3mad/roslyn/issues/64)); accepted break: `F<a[..]>(x)` is a generic call ([#66](https://github.com/ok3mad/roslyn/issues/66)). See [Feature 3](#feature-3-sequence-type-t) | [#19](https://github.com/ok3mad/roslyn/issues/19) |
| 8 | Task tilde | `T~`, `void~`, `int[..]~` | `Task<T>`, `Task` | yes (a postfix `~` after a type, next to `?` and `*`; `~` has no binary meaning, so no disambiguation) | S–M | High | Spelling `~` chosen over the hat `^` (XOR is valid in tuples, `is` and `as`). Confirmed on six applications; `ValueTask` stays unsugared (0.4% of their async declarations). Needs a new syntax node | [#20](https://github.com/ok3mad/roslyn/issues/20) |
| 9 | Function types | `(A, B) -> R`, `() -> T` | `Func<...>`, `Action<...>` | yes: a new syntax node, `FunctionTypeSyntax`, and a one-pass tuple-or-function lookahead | M | Prototype | Decided: `->` only in types, lambdas keep `=>`. Follow-ups: `ref` parameters [#56](https://github.com/ok3mad/roslyn/issues/56), names [#57](https://github.com/ok3mad/roslyn/issues/57), `((F))?` [#58](https://github.com/ok3mad/roslyn/issues/58), patterns [#59](https://github.com/ok3mad/roslyn/issues/59) | [#21](https://github.com/ok3mad/roslyn/issues/21) |
| 10 | Inferred return type | `static infer Hello(int id) => ...` | method with the inferred return type | none (`infer` parses as a type name) | L, in steps R1 to R5 | Med | Ordering and cycles. Allowed on public members too, with no warning. Replaces the expression-bodied `fun` | [#22](https://github.com/ok3mad/roslyn/issues/22) |
| 11 | `readonly` parameters | `void F(readonly int x)` | plain parameter plus the `val` no-reassignment check | none (`readonly` already parses as a parameter modifier, and C# reports an error without `ref`) | S | Med | Completes the mutability grid with #4 and #5. Replaces the `mutable` parameter in `fun`: immutable-by-default parameters and a `mutable` marker are a strictness ladder setting now, not syntax | [#23](https://github.com/ok3mad/roslyn/issues/23) |
| 15 | Inferred parameter types | `public infer Load(infer userId) => ...`, `infer timeout = TimeSpan.FromSeconds(5)` | method with the inferred types | none (`infer x` parses as a typed parameter) | S to XL, in steps P1 to P8 | Med | Copied from an overridden member, a default value, a function type or a call that pins one type, never from shape. Generic by default is open. Replaces untyped `fun` parameters inferred from arms | [#27](https://github.com/ok3mad/roslyn/issues/27) |

### Phase 2: functional core

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 12 | Implicit `it` | `xs.Select(Process(it, a))`, `when: it is X` | lambda `x => ...` | none | M–L | Med | Where the implicit lambda begins and ends without braces; innermost wins; an existing `it` keeps its C# meaning | [#24](https://github.com/ok3mad/roslyn/issues/24) |
| 13 | Pipe, without lifting | `x \|> F`, `x \|> F(it, y)` | nested call | yes (new `\|>` token) | M | Med–High | A bare stage versus a stage that uses `it` | [#25](https://github.com/ok3mad/roslyn/issues/25) |
| 14 | Parameter switch body | `int Sign(int i) => switch { <0 => -1, 0 => 0, >0 => 1 };` | `i switch { ... }`, or a switch over the parameter tuple | yes (`switch` with no governing expression) | M | Med | Ordered arms are decided, the spelling is the leading candidate. Parameter types stay explicit, so it needs no inference; exhaustiveness is C#'s. Replaces the `fun` block of arms, which a statement block could not be told apart from | [#26](https://github.com/ok3mad/roslyn/issues/26) |
| 16 | `try` expression | `try F(url) catch { HttpRequestException { ... } => ..., }` | try/catch with spilling | yes (`try` in expression position) | M–L | Med | The result type needs unions (#19); no `finally` | [#28](https://github.com/ok3mad/roslyn/issues/28) |
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
| 26 | Switch body on a lambda | `id => switch { 42 => ..., _ => ... }` | lambda with a switch expression | yes (shared with #14) | S once #14 exists | Med | The parameter switch body applied to lambdas | [#38](https://github.com/ok3mad/roslyn/issues/38) |
| 27 | `assume` escape hatch | `assume x is >0` | runtime check | yes | S–M | Low | Only an open question so far | [#39](https://github.com/ok3mad/roslyn/issues/39) |

### Proposed, to be discussed

| # | Syntax | Example | Lowers to | Parser change | Size | Certainty | Main risk or open point | Issue |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 28 | Discard arguments | `store.LoadAsync(_)` | the only fitting local or parameter in scope, such as the one `CancellationToken` | none (`_` parses as an identifier) | L | Low | Not yet discussed whether it is sound: implicit wiring, which candidates count, and interaction with overload resolution. See [Discard arguments](brainstorm.md#discard-arguments-proposed-not-yet-discussed) | [#40](https://github.com/ok3mad/roslyn/issues/40) |

Not listed: `union Name(A, B)` comes from C# 15, and `collect`, `Retry` and `Unexpected` are library code.

### Suggested order

1. `val` (#2, #3, #6), in three steps: `val x = e` locals (binder only), then deconstruction and designations (parser), then patterns (keyword). It exercises the gated parser, the first deliberate break with its `@val` escape, and new semantics on an existing symbol, none of which the first PoC touched. The no-reassignment check reuses the read-only local check behind CS1656.
2. `T[..]` (#7), done. Its binder rewrite onto a BCL type is the pattern for the other short type forms (see [Feature 3](#feature-3-sequence-type-t)).
3. `T~` (#8). Function types (#9) are done.
4. `readonly` parameters (#11), small, and the parameter switch body (#14, with #26), which needs no inference.
5. Type inference (#10 and #15), the first real inference work, in the steps of the
   [work breakdown](#type-inference-work-breakdown).
6. Rows 19 onward wait for the union representation and the purity design.

Decided before the type-syntax rows: `->` is not a lambda arrow, only the arrow of function types (see Decisions).

Rows 10, 11, 14 and 15 used to be parts of one `fun` declaration, and rows 24 and 25 used its keyword. They are separate features on ordinary methods now, and each can be built alone; see [Functions as separate elements](brainstorm.md#functions-as-separate-elements). `fun` itself is parked for a later "functional all in" mode, whose meaning is decided once these pieces have settled.

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
| R1 | `infer` as a return type: gating, a type named `infer` wins, CS0246 with the flag off. Expression bodies of private methods and local functions that call only methods with written return types | S–M | The error when nothing can be inferred (`null`, a method group) |
| R2 | Block bodies: several `return` statements with the best common type, no return value as `void`, iterators, `async` | M | Nullable annotation of the result when one `return` is maybe-null |
| R3 | Methods with inferred return types calling each other: binding order across members, cycle and recursion errors | L | Whether a recursive method with a non-recursive `return` is allowed later |
| R4 | Every member kind and accessibility: public and protected members, `virtual`, `partial`, operators, metadata emission, Edit and Continue (a body edit that changes the type is a signature change) | M | None for public members: they infer like any other (decided) |
| R5 | IDE: Quick Info and inlay hints show the inferred type, a fixer writes it out | M | |

Parameter types (#15):

| Step | Scope | Size | Open point |
| --- | --- | --- | --- |
| P1 | `infer` as a parameter type, with source 2: the default value's type. `null`, `default` and target-typed values give an error | S | |
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

## Feature 3: sequence type `T[..]`

`T[..]` is an alias for `IEnumerable<T>` in every type position, on branch `sharper/sequence-type`
([feature page](../features/sequence-type.md)). It adds no syntax node, bound node or public API, and needs no
lowering: the binder resolves the syntax to the constructed BCL type, so everything after binding sees
`IEnumerable<T>`.

### Rewriting a type form onto a BCL type

This is the pattern for Sharper's short type forms. Function types (#21) were built in parallel and follow it with a new syntax node: `BindFunctionType` constructs `Func` or `Action` with the same `ConstructNamedType` call. The task type (#20) is next.

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
   constructs the type through `ConstructNamedType`, which checks constraints now or leaves them to the declaring
   symbol where the binder suppresses checks (signatures), exactly as for the type written out. Set the nullable
   annotation from the form's last token, as the C# form would.
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

## Follow-ups

Sharper diagnostics were renumbered from `SharperErrorCodes.Base` (20000): CS9402, CS9403 and CS9404 became CS20001, CS20002 and CS20003, and this document uses the new numbers throughout.

The open items are marked in code as `// PROTOTYPE(sharper):` (`git grep "PROTOTYPE(sharper)"`).

- [x] Turn Sharper on for `.cx` files in the command-line compiler.
- [x] Turn Sharper on for `.cx` documents in the IDE workspace.
- [x] Treat `.cx` files as C# in the language server.
- [ ] Support `dotnet run app.cx`. File-based apps live in the SDK and only accept `.cs` files, so this needs an SDK change; until then the language server treats a loose `.cx` file as a miscellaneous file, never a file-based app.
- [x] Add `*.cx` to the default `Compile` items (the `Microsoft.Net.Compilers.Toolset` props).
- [ ] Decide whether `dynamic` should choose range or spread at runtime, under the sharpening rules. [#42](https://github.com/ok3mad/roslyn/issues/42)
- [ ] Decide whether async iterators should ever support spreads. [#43](https://github.com/ok3mad/roslyn/issues/43)
- [ ] Give ref-struct sources a spread-specific error instead of CS4007. [#44](https://github.com/ok3mad/roslyn/issues/44)
- [ ] Verify the spread with Edit and Continue: the generated try/finally and the yield share one syntax offset. [#45](https://github.com/ok3mad/roslyn/issues/45)
- [ ] Design a real IOperation shape once the syntax settles. [#46](https://github.com/ok3mad/roslyn/issues/46)
- [ ] Avoid the O(depth) cost of recursive spreads with a delegating state machine. [#47](https://github.com/ok3mad/roslyn/issues/47)
- [x] Number Sharper diagnostics from their own base, `SharperErrorCodes.Base` = 20000 (CS20001 and up), far from upstream.
- [x] Full C# compiler regression run (net10.0, Debug): 88,327 tests passed and 0 failed across 10 test projects. WinRT was not run; it targets .NET Framework only.
- [ ] Infer whether a struct member mutates `this`, and silence CS20003 for members that do not. Until then, every call to a non-`readonly` member of a `val` warns, including interface members on a generic `val` (`g.Get()`).
- [x] Copy and warn for the pattern-based members that bypassed the shared check: `await`/`GetAwaiter`, implicit Index and Range indexers, list patterns and property patterns. Collection spreads and `using` already copy their operand.
- [ ] Build the sharpening infrastructure and move Sharper to the `.cx` extension. The accepted breaks need it: `val` as a keyword in patterns, and `val (a, b) = e` as a declaration. The sharpener rewrites those to `@val`, and the extension keeps plain `.cs` files on C# semantics.
- [x] Give `val` its own messages: CS20004 and CS20005 (the `val` forms of CS8508 and CS8136), CS20006 for ref reassignment, and the 'val variable' cause also for `foreach` and `using` variables.
- [x] Script fields: `val` is a read-only inferred field, like `var` plus read-only.
- [x] Give `val` its own token kind: `SyntaxKind.ValKeyword` (8466, the top free slot below `ElifKeyword`, guarded by a test), a contextual keyword like `var`, used by val patterns.
- [ ] Sequence type: decide whether array creation should accept sequence elements (`new int[3][..]`, today CS0178, or `new int[..][3]`). [#64](https://github.com/ok3mad/roslyn/issues/64)
- [ ] Sequence type: report an invalid element type on the element type rather than the whole sequence type (same cause as #62 for function types). [#65](https://github.com/ok3mad/roslyn/issues/65)
- [ ] Sharpener: rewrite `F<a[..]>(x)` (two comparisons in C#) as `F < (a[..]) > (x)`, the sequence type's accepted break. [#66](https://github.com/ok3mad/roslyn/issues/66)
