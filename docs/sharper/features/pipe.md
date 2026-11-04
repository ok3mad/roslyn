# Pipe `|>`

**Status:** Prototype, without lifting. Requires `/features:sharper`. Issue: [#25](https://github.com/ok3mad/roslyn/issues/25).

## Summary

`x |> stage` passes `x` to the stage. A stage that uses `it` is an expression whose `it` is `x`; any other stage is a
function applied to `x`:

```csharp
static infer Process(RawOrder raw, IAccounts accts, IFx fx) =>
    raw |> ParseOrder              // ParseOrder(raw)
        |> Resolve(it, accts)      // Resolve(ParseOrder(raw), accts)
        |> Quote(it, fx)
        |> Validate;
```

The C# it stands for:

```csharp
static Transfer Process(RawOrder raw, IAccounts accts, IFx fx) =>
    Validate(Quote(Resolve(ParseOrder(raw), accts), fx));
```

There is no lifting yet: a stage that takes a `T` gets exactly what its left side is, and a `Task<T>`, a sequence of
`T` or a union containing `T` is an ordinary type error. Lifting is [#32](https://github.com/ok3mad/roslyn/issues/32).
The design is in the implementation plan, [Feature 12](../planning/implementation-plan.md#feature-12-pipe-without-lifting).

## Rules

- **The token.** `|>` is a `|` directly followed by a `>`, which the parser merges, as it does `>>`. No C# expression
  starts with `>`, so `a |> b` is always an error in C# (CS1525), and with the flag off it stays one. `a | > b` with a
  space is not a pipe. Generic stages work: `x |> F<int> |> G`, because `|` is one of the tokens that make `F<int>` a
  generic name.
- **Precedence.** Left-associative, looser than `??` and every binary operator, tighter than `?:`, lambdas and
  assignment. Everything to the left of `|>`, up to a conditional, lambda or assignment, is the value, so a chain reads
  left to right as data flow:

  | Source | Means |
  | --- | --- |
  | `a + b \|> F` | `F(a + b)` |
  | `s ?? "default" \|> Parse` | `Parse(s ?? "default")` |
  | `x \|> it.Name ?? "none"` | the stage is `it.Name ?? "none"` |
  | `x \|> IsValid ? "ok" : "bad"` | `IsValid(x) ? "ok" : "bad"` |
  | `c ? a : b \|> F` | `c ? a : F(b)` |
  | `y = x \|> F` | `y = F(x)` |
  | `xs.Select(x => x \|> F)` | the lambda body is `x \|> F` |
  | `await x \|> F` | `F(await x)`; write `await (x \|> F)` to await the result |
- **A stage with `it`.** A stage that contains an `it` that C# does not find (the same test as for an implicit
  lambda's `it`) is an expression with `it` as a read-only variable holding the value: `x |> it * it`,
  `order |> Resolve(it, accounts)`, `user |> $"Hello, {it.Name}"`. Its type is the stage's type.
- **Target typing.** A stage with `it` is target-typed, as a switch expression's arms are
  ([#187](https://github.com/ok3mad/roslyn/issues/187)): `int? r = x |> (it > 0 ? 1 : null)`,
  `Func<int, int> f = x |> (y => y + it)`, `int[] a = x |> [it, it + 1]`, `List<int> l = x |> new() { it }`. The stage's
  natural type comes first: `object o = x |> (it > 0 ? 1 : 2)` boxes an `int`. With no natural type and no target,
  the pipe is an error, as the stage would be: `var r = x |> (it > 0 ? 1 : null)` is CS0173, and a stage that does not
  convert reports the stage's error (`string s = x |> (it > 0 ? 1 : null)` is CS0029 on `1`). In a call, each
  candidate classifies the stage's conversion, with no betterness rule of its own: `F(x |> (it > 0 ? 1 : null))` picks
  `F(int?)` over `F(long?)`, as `F(c ? 1 : null)` does. Type inference does not look through the pipe, as C# does not
  look through `?:` or a switch: `Id(x |> (it > 0 ? 1 : null))` is CS0411, while `Id(x |> it + 1)` infers `int` from
  the natural type. A bare stage is a call and has the call's type.
- **A bare stage.** Any other stage is a function: a method group (`x |> Parse`, `x |> list.Add`, `x |> F<int>`), a
  delegate value, or an expression of delegate type such as `x |> MakeParser(culture)`. It is called with the value as
  its only argument, with C#'s overload resolution, type inference and extension methods (`x |> Enumerable.Sum`).
  `dynamic` binds at runtime. Anything else is CS20048: `x |> y.Length` instead of `x |> it.Length`.
- **Evaluation order.** The left side is evaluated once, before anything in the stage, including a bare stage's
  receiver (`x |> GetPrinter().Print` evaluates `x` first) and a delegate expression (`x |> MakeParser(culture)`).
- **The innermost `it` wins.** A stage is the outermost slot for `it`, as the implicit `it` decisions say: an `it` in
  an argument that takes a one-parameter function belongs to that argument's implicit lambda. If that leaves the stage
  with no `it` of its own, it is CS20047 (`x |> xs.Where(it > 0)`); name the inner parameter or use the value outside.
  Explicit lambdas never supply `it`, so `user |> Render(it, () => Load(it.Id))` uses the user twice.
- **One meaning of `it` per stage.** A stage that uses its `it` and contains an implicit lambda is CS20020, as for an
  implicit lambda: `xs |> it.Select(it * 2)`. An inner pipe is not a second meaning: a stage owns every `it` in it,
  which the syntax shows, so `xs.Select(it.Id |> it * 10)` and `x |> F(it, y |> G(it))` are fine. With an outer
  `it` and a method that takes a value or a function at that argument, CS20021 applies with the stage's `it` as the
  outer one.
- **An existing `it` keeps its C# meaning.** With a local, parameter, field or type named `it` in scope, the stage has
  no `it` of its own and is bare; `@it` is always the C# name.
- **A stage is its own scope**, like a lambda body. Variables declared in it (`out var v`, `is T t`) are not visible
  after it, and since `?:` is looser than `|>`, `x |> TryParse(it, out var n) ? n : 0` is CS0103; write
  `x |> (TryParse(it, out var n) ? n : 0)`.
- **`await`.** A stage is not a lambda, so `await` works in it in an `async` method: `id |> await Load(it)`, and
  `x |> F |> await it` awaits a stage's result. In an implicit lambda it is CS20023, as anywhere in one.
- **The value must have a type.** A `void` call, `null` or a lambda on the left is CS20046.
- **Statements.** A pipe is a statement when its stage would be: a bare stage (a call) or a stage that is a call,
  assignment, increment or `await` (`x |> list.Add;`, `x |> Log(it, "sent");`). Anything else is CS0201.
- **`it` is read-only** (CS1656), like an implicit lambda's.
- **Ref structs.** `span |> Count` works; the value's escape scope is the left side's, so returning
  `stackSpan |> it` is CS8352.
- **Expression trees** cannot contain a pipe (CS20049): the stage's `it` is a variable.
- **Nullable.** `it` is inferred like a `var` local: its state is the left side's, so `maybe |> it.Length` warns and
  `maybe |> Length` warns for the argument.

## Not supported yet

- No lifting over `Task`, sequences and unions ([#32](https://github.com/ok3mad/roslyn/issues/32)).
- A bare lambda stage is CS20048. Decided: `x |> (y => y + 1)`, one parameter and an expression body, will name the
  value, as `x |> it + 1` with `y` for `it` ([#186](https://github.com/ok3mad/roslyn/issues/186)).
- IOperation: an `OperationKind.None` node whose only child is the left side; the stage is not in the control flow
  graph ([#155](https://github.com/ok3mad/roslyn/issues/155)).
- The semantic model finds `it` by `GetSymbolInfo`, but `LookupSymbols` and speculative binding inside a stage do not
  see the stage's scope ([#156](https://github.com/ok3mad/roslyn/issues/156)).
- No sequence points in a stage, no Edit and Continue support, and a bare stage's call cannot be intercepted
  ([#158](https://github.com/ok3mad/roslyn/issues/158)).
- No IDE support: classification, formatting, completion, Quick Info ([#159](https://github.com/ok3mad/roslyn/issues/159)).

## How it works

- **Parser.** `GetExpressionOperatorTokenKindAndExpressionKind` recognizes `|` `>` with no trivia between them, behind
  the flag (`IsAtSharperPipeToken`), and `EatExpressionOperatorToken` merges them into a `BarGreaterThanToken`. The
  node is `PipeExpressionSyntax` (`Left`, `OperatorToken`, `Stage`) at the precedence `SharperPipe`, between
  `Conditional` and `Coalescing`.
- **Binder** (`Binder_SharperPipe.cs`). The left side binds to its natural type, into a `SharperPipeItLocalSymbol`
  named `it`. The stage binds under a `SharperPipeStageBinder`, which declares `it` when the stage has a free one
  (`FindSharperFreeIt`, shared with implicit lambdas) and builds the binder map for the scopes inside the stage, since
  the member's map leaves stages out (`LocalBinderFactory`, `ExpressionVariableFinder`). A bare stage binds as a method
  group or value, then as an invocation with the local as its argument (`BindInvocationExpression`), whose syntax is
  the stage. For implicit lambdas, the pipe's `it` counts as an outer `it` (`IsSharperImplicitItSymbol`).
- **Target typing**, as for the Sharper try expression. A stage with `it` binds to a `BoundUnconvertedSharperPipe`
  whose type is the stage's natural type, if any. `ConversionKind.SharperPipe`, with the stage's conversion as its only
  underlying conversion, comes last in `ClassifyImplicitConversionFromExpression`, after the switch and try conversions.
  `ConvertSharperPipe` converts the stage (`CreateConversion` with the pipe conversion, `BindToNaturalType` or the
  inferred delegate type without one) and checks the stage's `it` rules (CS20047, CS20020) only then, since a lambda in
  the stage is bound by its conversion. A target-typed pipe is a `BoundConversion` of kind `SharperPipe` over a
  `BoundSharperPipe { WasTargetTyped: true }` whose stage holds the conversion; the semantic model reports the stage's
  natural type (`NaturalTypeOpt`) as the pipe's type and the target as its converted type.
- **Bound node.** `BoundSharperPipe` (`Local`, `Left`, `Locals`, `Stage`, `IsBareStage`, `NaturalTypeOpt`,
  `WasTargetTyped`), with `BoundUnconvertedSharperPipe` sharing the base `BoundSharperPipeBase`. Flow analysis assigns
  the local after the left side; the nullable walker infers it like a `var` local, and converts a target-typed stage
  when the target type and its annotations are known (`TargetTypedAnalysisCompletion`), as for a switch arm; ref safety
  gives it the left side's escape scope.
- **Lowering.** A spill sequence: `it = x;` goes before the enclosing statement, the stage stays in place, and the
  spiller moves earlier operands to temps, as for a switch expression. `it` and the stage's variables are user
  variables, which codegen keeps in statements rather than in an expression's sequence. In optimized builds the
  local is a stack value when it is read once.

## Code

| What | Where |
| --- | --- |
| Syntax | `Syntax/Syntax.xml` (`PipeExpressionSyntax`), `SyntaxKind.BarGreaterThanToken` (8231), `SyntaxKind.PipeExpression` (9904); `Parser/LanguageParser.cs` (`IsAtSharperPipeToken`, `Precedence.SharperPipe`) |
| Binder | `Binder/Binder_SharperPipe.cs`, `Binder/SharperPipeStageBinder.cs`, `Symbols/SharperPipeItLocalSymbol.cs`; implicit `it` in `Binder_SharperImplicitIt.cs` |
| Analysis | `FlowAnalysis/AbstractFlowPass_SharperPipe.cs`, `DefiniteAssignment_SharperPipe.cs`, `NullableWalker_SharperPipe.cs`, `Binder/RefSafetyAnalysis.cs` |
| Lowering | `Lowering/LocalRewriter/LocalRewriter_SharperPipe.cs` |
| Tests | `SharperTests_Pipe.cs` (`SharperTests.Pipe_*`) |
