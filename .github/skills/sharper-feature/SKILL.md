---
name: sharper-feature
description: "Implement a Sharper language feature (or any new construct) in the C# compiler: gating, binding, a new bound node, flow and nullable analysis, IOperation, lowering, diagnostics, and tests. Use when: adding Sharper syntax, adding a bound node, extending the binder or LocalRewriter for a new construct, or asked to 'implement a Sharper feature'. Also use for: new BoundNodes.xml node, NullableWalker support for a new node, IOperation for a new node, new compiler error or warning code."
---

# Implementing a Sharper Feature in the C# Compiler

Sharper is a stricter C# dialect built inside Roslyn in this fork: `main` holds the merged features and `sharper/*`
branches the ones in progress (overview and feature status in `docs/sharper/README.md`). Its features are compiled
by the C# compiler itself: new syntax binds to bound nodes and lowers to ordinary constructs. This skill is the
checklist distilled from the first features: `yield return ..xs` (a new bound node; the commits merged from
`sharper/gating`), `val` locals (a contextual keyword on an existing symbol; merged from `sharper/val`) and the
sequence type `T[..]` (a type form that binds to a BCL type; `sharper/sequence-type`). See `SharperTests.cs` for all
three.

## When to Use

- Adding a Sharper feature, or any new construct, to the C# compiler
- Adding a node to `BoundNodes.xml`
- Adding a compiler error or warning code

## Sharper Ground Rules

1. **Gate everything on the feature.** `MessageID.IDS_FeatureSharper` maps to `/features:sharper`
   (`Errors/MessageID.cs`, registered in `src/Compilers/Core/Portable/CommandLine/Feature.cs`). Check it through the
   syntax node: `node.IsFeatureEnabled(MessageID.IDS_FeatureSharper)` or `CheckFeatureAvailability(node, ...)`.
   Never `Compilation.IsFeatureEnabled`: it only compares language versions and asserts on flag-gated features.
2. **No semantic breaking changes.** Where Sharper syntax could already mean something in C#, the C# meaning wins.
   Implement this by trial binding: bind as C# into a temporary diagnostic bag, and use the Sharper meaning only if
   that reported errors. Do not hand-write the rule; it misses cases (a `Range` boxes to `object`).
   For a contextual keyword in a type position (`val`, like `var`), name lookup is already that trial: route it
   through `BindTypeOrAliasOrKeyword`, which treats the identifier as the keyword only when no type of that name binds.
3. **Reuse the parser where possible.** New syntax nodes mean `Syntax.xml`, generated code and public API entries.
   Prefer reinterpreting existing syntax in the binder.
4. **Follow-ups are `// PROTOTYPE(sharper): ...` comments.** Plain `TODO` is banned in this repo;
   `eng/todo-check.ps1` blocks merging `PROTOTYPE` comments into `main`, so none are forgotten.
5. **Test each Sharper line next to its plain C# twin** (the C# it stands for) so diagnostics can be compared.

## Workflow

1. **Probe first.** Write tests for the target behavior and the traps, with empty expectations, and run them.
   Read the actual diagnostics and runtime output before deciding anything. For a new keyword the first run only
   shows "type not found"; the probe pays off once the smallest binding is in, so add that first and probe again.
2. **Pick the precedent**: the closest existing construct (for the spread: `foreach` and the collection-expression
   spread `[..xs]`). Model the new bound node on it.
3. **Find every mention of the precedent**, not just its visitors:
   ```bash
   git grep -n "VisitForEachStatement\|BoundKind.CollectionExpressionSpreadElement" -- src/Compilers/CSharp/Portable
   ```
   Kind checks (`BoundKind.X`, `is BoundX`) hide in flow analysis, lowering helpers and region analysis, and
   several of them throw or assert on unknown kinds.
4. Implement layer by layer (below), re-running the tests after each one. The next failure names the next layer.

## Layer Checklist

Paths are under `src/Compilers/CSharp/Portable/`.

| Layer | Where | What to do |
| --- | --- | --- |
| Binder | `Binder/Binder_*.cs` | Trial-bind as C# (rule 2). Reuse shared lookups (e.g. `GetEnumeratorInfoAndInferCollectionElementType`). Every `GetInstance` of a pooled bag gets exactly one `Free` (`AddRangeAndFree` to keep, `Free` to drop). |
| Bound node | `BoundTree/BoundNodes.xml` | Shape it like the precedent. Mark template fields built on placeholders `SkipInVisitor="true"`. Add `DoesNotSurvive="LocalRewriting"` if lowering removes it. Regenerate with `dotnet run --file eng/generate-compiler-code.cs`; never edit `Generated/` by hand. |
| Flow analysis | `FlowAnalysis/AbstractFlowPass.cs` | Override `Visit<Node>`. Then search for the precedent's kind in `ControlFlowPass.cs`, `ExitPointsWalker.cs` and the try/finally handling in `AbstractFlowPass.cs`. |
| Nullable | `FlowAnalysis/NullableWalker.cs` | Always override here too (see trap 2). Reuse the precedent's analysis helpers. |
| IOperation | `Operations/CSharpOperationFactory.cs` | Until there is an API design, add the kind to the catch-all `NoneOperation` case and override `Children` in `BoundTree/Statement.cs`. |
| Lowering | `Lowering/LocalRewriter/` | Rewrite into existing nodes with the shared helpers (e.g. `RewriteForEachEnumerator`). Add the kind to the helpers' node-kind asserts and instrumentation switches, as the precedent does. |
| Diagnostics | `Errors/ErrorCode.cs` (a new cause string for an existing error: a `MessageID` plus a `.resx` entry and `UpdateXlf`, no `ErrorCode`) | Follow the checklist comment at the end of `ErrorCode.cs` (`ErrorFacts.IsBuildOnlyDiagnostic`, `GetWarningLevel` and `DiagnosticTest.WarningLevel_2` for warnings, `.resx` message and `_Title`), then regenerate code and run `dotnet msbuild src/Compilers/CSharp/Portable/Microsoft.CodeAnalysis.CSharp.csproj /t:UpdateXlf`. A warning only new syntax can trigger gets level 1. |

## Short Type Forms (Aliases onto a BCL Type)

`T[..]` is `IEnumerable<T>`, `(A) -> R` is `Func<A, R>` and `T~` is `Task<T>`; all three follow this pattern. Such a form
needs no bound node and no lowering: the binder resolves it to the constructed BCL type, so metadata, the semantic
model and IOperation see only the BCL type. The steps, with `T[..]` as the worked example (details in the
implementation plan, "Feature 3"):

1. **One gated syntax predicate.** Reuse a C# node that already parses the form and decide the Sharper reading in one
   place (`ArrayRankSpecifierSyntax.IsSharperSequence`). Exclude positions that must keep their C# meaning (array
   creation).
2. **Make `ScanType` accept it**, since statements, casts and generic arguments in expressions are decided by
   scanning. Do not return `ScanTypeFlags.MustBeType` if the form is also an expression, and check `ScanCast` and
   `ScanPossibleTypeArgumentList` for valid C# that would change meaning.
3. **Bind through `BindSharperTypeAlias`** (`Binder/Binder_SharperTypes.cs`), with the definition from
   `GetSpecialType` or `GetWellKnownType`. It constructs the type through `ConstructNamedType`, so constraint checks
   and their suppression in signatures behave as for the type written out.
4. **Find the code that reads the syntax directly**: `VisitRankSpecifiers` callers that bind sizes, and
   `CSharpSemanticModel.CanGetSemanticInfo` for sub-expressions that are now part of a type.
5. **Test against the C# twin** in every type position (compare member and local symbols), including inside a
   generic argument and a tuple element in a statement, and the overlapping C# expressions with every token that can
   follow them.

## Traps Found So Far

1. **Kind lists outside the visitors.** `ExitPointsWalker` throws on unknown pending-branch kinds, and the lowering
   loop helpers assert on unknown loop kinds. Only grepping the precedent's kind finds them.
2. **A base-class default switches off a safety net.** `AbstractFlowPass.DefaultVisit` asserts "Should Visit<X> be
   overridden?". Overriding there makes every subclass, `NullableWalker` included, silently inherit the override.
   Override in `NullableWalker` as well, and test with `#nullable enable`.
3. **Placeholder fields are checked, never visited.** `NullableWalker.DebugVerifier` asserts if nullability is
   recorded for a node the standard walker cannot reach. Check a template conversion with
   `BoundNode.GetConversion(conversion, placeholder)` and `VisitConversion`, as foreach does for its element.
4. **If you report an error, set `hasErrors`.** `GenerateConversionForAssignment` can report an error without
   flagging its result; later passes then analyze broken code. Convert into a temporary bag and check
   `HasAnyErrors()`.
5. **Do not copy the precedent blindly.** A foreach iteration variable gets an explicit cast (`forCast: true`); a
   yielded or assigned value must convert implicitly.
6. **Synthesized `try` blocks carry syntax.** The async rewriter and Edit and Continue accept only certain syntax for
   a `try` (`SyntaxBindingUtilities.BindsToTryStatement`). Lowering a new construct into try/finally inside a state
   machine needs care, and an Edit and Continue check.
7. **`dynamic` defers errors to runtime**, so a "C# wins unless it errors" rule rarely fires for a dynamic operand.
   Decide what Sharper does there explicitly.
8. **A keyword's spelling is checked far from its binder.** `var` is tested syntactically (`TypeSyntax.IsVar`,
   `IsIdentifierVar()`) in the parser (deconstruction, designations, patterns), `SourceLocalSymbol`, the foreach
   binder, the semantic model and script fields. Grep the spelling checks, not just the binder method, and decide for
   each whether the new keyword joins it now or is left with a `PROTOTYPE(sharper)` comment.
9. **Look for an existing flag before writing an analysis.** Read-only locals already existed for foreach, using
   and fixed variables: `LocalSymbol.IsWritableVariable` plus a cause in `ReportReadonlyLocalError` covered every
   write (`=`, `+=`, `++`, `ref`/`out`, ref reassignment, struct members, lambdas) with no new analysis.
10. **Lower before closures and state machines see it.** A local captured by a lambda or hoisted across `await` or
   `yield` becomes a writable field before codegen, so a rule that codegen applies to locals (a defensive copy in
   `CodeGenerator.HasHome`) silently stops applying. Make such copies in `LocalRewriter` (`BoundPassByCopy`).
11. **Pattern-based members are bound on placeholders.** `await`, implicit Index and Range indexers, list and property
   patterns bind their members against a placeholder, not the real receiver, so a check on the receiver misses them.
   `BoundImplicitIndexerReceiverPlaceholder.Receiver` points back to it; for patterns, walk the decision DAG for
   evaluations whose `Input.IsOriginalInput`. Pattern lowering also reuses a plain local as its input, assuming members
   do not mutate it.
12. **A keyword that already has C# rules inherits them.** Making `val` parse like `var` (one gated parser helper,
   `IsIdentifierVarOrSharperVal`, at every spelling check) brought `var`'s existing errors along: CS8136 for
   `val (...)` with a type named `val`, CS8508 for a `val` pattern. Reusing `VarPatternSyntax` with a `ValKeyword`
   token avoided a syntax node; pattern variables have no type syntax, so the local finds its `val` through the
   enclosing pattern.
13. **A keyword needs its own kind, numbered away from upstream.** A `VarKeyword` token with the text `val` worked in
   the compiler, but `SyntaxFactory.Token(kind)` rebuilds it as `var`. Tokens get their text from their kind only up
   to `EndOfFileToken`, so a new keyword kind must sit in the keyword range: take free slots from the top
   (`ValKeyword` = 8466, below `ElifKeyword`) and test that no other `SyntaxKind` has the value, because duplicate enum
   values compile silently. Diagnostics have no such limit: Sharper codes start at `SharperErrorCodes.Base` (CS20001).
14. **A new syntax node is public API and hides in type switches.** When no existing node fits (function types), add it
   to `Syntax.xml` with a `SyntaxKind` from 9900 up (Sharper node kinds count up from there, apart from upstream),
   regenerate, and copy the build's RS0016 list into `PublicAPI.Unshipped.txt`. A new type node also needs
   `SyntaxFacts.IsTypeSyntax`, `SyntaxFacts.IsInTypeOnlyContext` (for the semantic model) and
   `SyntaxExtensions.VisitRankSpecifiers`, which throws on unknown type kinds. In the parser, gate every lookahead
   change on Sharper and scan nested parentheses in one pass: trying a second scan after a failed one is exponential in
   the nesting depth.

15. **The lexer has no `..` token.** It produces two `.` tokens that the parser merges (`IsAtDotDotToken`,
   `EatDotDotToken`); `CurrentToken.Kind == SyntaxKind.DotDotToken` never matches.
16. **Accepting a type form in `ScanType` moves ambiguities.** The cast heuristic reads an ambiguous type before `(`,
   `!` or an identifier as a cast, which would turn the valid element accesses `(a[..])(b)` and `(a?[..])` into casts.
   The type argument rule makes `F<a[..]>(x)` a generic call (accepted: in C# it needs user-defined comparisons).
17. **A suffix with no expression meaning still meets the generic-name ambiguity.** `~` is only a prefix operator, so
   `T~` needs no disambiguation, except after a possibly generic name in a tuple: `(A<B>~ c, d)` would turn the valid
   tuple of comparisons `(A < B > ~c, d)` into a declaration. The scan refuses the suffix there
   (`IsSharperTaskTypeSuffix`), and `IsPossibleTypedIdentifierStart` must not stop at `name ~` before scanning.
18. **`GetTypeInfo` on a type syntax drops the top-level nullable annotation.** Compare
   `TypeWithAnnotations` of the declared symbols (`ToTestDisplayString(includeNonNullable: true)`), and compare
   with a C# twin rather than guess the display order of mixed array ranks.

## Testing

- Tests live in `src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests.cs`. Use
  `TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper)` as parse options.
- Compile against `TargetFramework.Net90`; the default test framework lacks types such as `System.Range`. Guard
  runtime output with `ExecutionConditionUtil.IsCoreClr ? "<output>" : null`.
- Wrap diagnostic-only sources in `class C { ... }` so unused local functions do not add warnings.
- Cover runtime output, diagnostics, `#nullable enable` warnings, the operation tree
  (`VerifyOperationTreeAndDiagnosticsForTest`), the flow graph (`VerifyFlowGraphAndDiagnosticsForTest`) and region
  analysis (`AnalyzeControlFlow`, `AnalyzeDataFlow`).
- Run the Sharper tests with `ROSLYN_TEST_IOPERATION=1` as well: every compilation then also validates IOperation,
  the semantic model and the control flow graph.
- Run tests in Debug. The debug-only checks (`PipelinePhaseValidator`, `DebugVerifier`, pool-leak tracking,
  `Feature.AssertValidFeature`) catch most mistakes in a new node.

```bash
export PATH=$PWD/.dotnet:$PATH DOTNET_ROOT=$PWD/.dotnet
dotnet test src/Compilers/CSharp/Test/Semantic/Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests.csproj -f net10.0 --filter "FullyQualifiedName~SharperTests"
ROSLYN_TEST_IOPERATION=1 dotnet test src/Compilers/CSharp/Test/Semantic/Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests.csproj -f net10.0 --filter "FullyQualifiedName~SharperTests"
```
