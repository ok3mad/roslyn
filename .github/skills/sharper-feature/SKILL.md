---
name: sharper-feature
description: "Implement a Sharper language feature (or any new construct) in the C# compiler: gating, binding, a new bound node, flow and nullable analysis, IOperation, lowering, diagnostics, and tests. Use when: adding Sharper syntax, adding a bound node, extending the binder or LocalRewriter for a new construct, or asked to 'implement a Sharper feature'. Also use for: new BoundNodes.xml node, NullableWalker support for a new node, IOperation for a new node, new compiler error or warning code."
---

# Implementing a Sharper Feature in the C# Compiler

Sharper is a stricter C# dialect built inside Roslyn, on the `sharper/*` branches of this fork. Its features are
compiled by the C# compiler itself: new syntax binds to bound nodes and lowers to ordinary constructs. This skill is
the checklist distilled from the first feature, `yield return ..xs` (see `SharperTests.cs` and the commits on
`sharper/gating`).

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
3. **Reuse the parser where possible.** New syntax nodes mean `Syntax.xml`, generated code and public API entries.
   Prefer reinterpreting existing syntax in the binder.
4. **Follow-ups are `// PROTOTYPE(sharper): ...` comments.** Plain `TODO` is banned in this repo;
   `eng/todo-check.ps1` blocks merging `PROTOTYPE` comments into `main`, so none are forgotten.
5. **Test each Sharper line next to its plain C# twin** (the C# it stands for) so diagnostics can be compared.

## Workflow

1. **Probe first.** Write tests for the target behavior and the traps, with empty expectations, and run them.
   Read the actual diagnostics and runtime output before deciding anything.
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
| Diagnostics | `Errors/ErrorCode.cs` | Follow the checklist comment at the end of `ErrorCode.cs` (`ErrorFacts.IsBuildOnlyDiagnostic`, `GetWarningLevel` and `DiagnosticTest.WarningLevel_2` for warnings, `.resx` message and `_Title`), then regenerate code and run `dotnet msbuild src/Compilers/CSharp/Portable/Microsoft.CodeAnalysis.CSharp.csproj /t:UpdateXlf`. A warning only new syntax can trigger gets level 1. |

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
