# `try` expression

**Status:** Prototype of slice 1. Requires `/features:sharper`. Issue: [#28](https://github.com/ok3mad/roslyn/issues/28).

## Summary

`try body catch { arms }` turns exceptions into values. The arms are switch expression arms over the caught
exception; an exception that no arm matches propagates as if there were no `try`:

```csharp
RateResult r = try await http.GetFromJsonAsync<RateTable>(url) catch
{
    HttpRequestException { StatusCode: HttpStatusCode.NotFound } => new NoRate(from, to),
    HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError and var s } => new FxUnavailable((int)s),
    TimeoutException => new FxUnavailable(0),
};

union RateResult(RateTable, NoRate, FxUnavailable);
```

The C# it stands for, apart from where the arms run:

```csharp
RateResult r;
try { r = await http.GetFromJsonAsync<RateTable>(url); }
catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { r = new NoRate(from, to); }
catch (HttpRequestException e) when (e.StatusCode >= HttpStatusCode.InternalServerError) { r = new FxUnavailable((int)e.StatusCode); }
catch (TimeoutException) { r = new FxUnavailable(0); }
```

The design is in the implementation plan,
[Feature 8](../planning/implementation-plan.md#feature-8-try-expression-slice-1-design).

## Rules

- **Syntax.** `try` is followed by the body, which runs up to `catch`, and by switch expression arms in braces:
  patterns, designations and `when` clauses, separated by commas, with an optional trailing comma.
- **Lowest precedence, like a lambda.** Nothing continues a try expression: `(try a catch { ... }).X` and
  `1 + (try a catch { ... })` need parentheses, and without them `try` is CS1525. It can be an initializer, an
  argument, a `return` or `=>` body, a conditional's branch and a switch arm's value.
- **Statements stay C#.** At the start of a statement, `try` is the try statement, and `try {` anywhere is the C#
  error for a statement in the wrong place. Without the flag, `try` in an expression is CS1525, as in C#.
- **Result type.** Target-typed first, as a switch expression: the body and each arm convert to the target, which
  reaches the C# 15 union conversion from each case. Without a target, the best common type of the body and arms
  (`var n = try int.Parse(s) catch { FormatException => 0L };` is a `long`). Neither: CS20029, which lists the types.
- **Arms.** The input is the caught `System.Exception`. An arm that can never match is CS8510, as in a switch
  expression; there is no exhaustiveness check, because unmatched exceptions propagate. A catch-all arm (`_`,
  `Exception e`) is allowed; a warning for it is [#135](https://github.com/ok3mad/roslyn/issues/135).
- **Unmatched exceptions are never caught.** The arms run in a catch filter, so an exception no arm matches keeps its
  stack trace, and inner `finally` blocks run after the filter decides, as for any exception a `catch` does not
  handle. There is no rethrow arm (an arm is an expression; `=> throw new Wrapped(e)` is an ordinary throw
  expression) and no `finally`.
- **`await`.** Allowed in the body and in an arm's value (a catch block), not in a pattern or `when` clause (CS7094,
  the filter). `stackalloc` is allowed in the body and is CS0255 in an arm's value.
- **An awaitable body.** `try F() catch { ... }` with an asynchronous `F` catches only what `F` throws before it
  returns the task; the rest surfaces when the task is awaited, outside the `try`. CS20030 warns for a body whose type
  is task-like; write `try await F()`. Lifting a task body is [#136](https://github.com/ok3mad/roslyn/issues/136).
- **Expression trees** cannot contain a try expression (CS20031).
- **Flow analysis.** An arm starts from the state before the body, joined with every state the body passes through,
  as a catch block does: a variable assigned in the body is not definitely assigned in an arm (CS0165), and a value
  checked for null in the body may be null again in an arm.

## Not supported yet

- IOperation: a try expression is an `OperationKind.None` node whose only child is the body; the arms are not in the
  operation tree or the control flow graph ([#144](https://github.com/ok3mad/roslyn/issues/144)).
- Debugging: no sequence points or code coverage for the arms, and no Edit and Continue tests or IDE matching for the
  new node ([#145](https://github.com/ok3mad/roslyn/issues/145)).
- A possibly null body or arm converted into a union does not warn (CS8604), the same as a target-typed switch
  expression or conditional in C# 15 ([#146](https://github.com/ok3mad/roslyn/issues/146)).
- Union results without a declared union wait for anonymous unions
  ([#31](https://github.com/ok3mad/roslyn/issues/31), slice 2). `ref` results are not supported.
- No IDE support (formatting, classification, completion after `catch {`).

## How it works

- **Syntax.** `TryExpressionSyntax` (`SyntaxKind.TryExpression` = 9903): `TryKeyword`, `Expression`, `CatchKeyword`,
  braces and a list of `SwitchExpressionArmSyntax`. The parser takes it where C# reports CS1525 for `try`
  (`ParseSharperTryExpression` in `LanguageParser.cs`), unless `{` follows; the arm loop is shared with the switch
  expression (`ParseSwitchExpressionArms`).
- **Binding** (`Binder_SharperTryExpression.cs`). Each arm has a `SwitchExpressionArmBinder` with no switch binder
  (`LocalBinderFactory.VisitTryExpression`): its input type is `System.Exception`, its pattern and `when` clause bind
  with `BinderFlags.InCatchFilter`, and its value with `InCatchBlock`. The decision DAG is the switch expression's
  (`CreateDecisionDagForSwitchExpression`) over a placeholder for the exception. The result is a
  `BoundUnconvertedTryExpression`, converted like a switch expression: `ConversionKind.SharperTryExpression` holds the
  conversions of the body and each arm, and `BoundConvertedTryExpression` keeps the natural type for the semantic
  model.
- **Flow analysis** (`AbstractFlowPass_SharperTryExpression.cs`, `NullableWalker_SharperTryExpression.cs`) follows the
  try statement for the states and the switch expression for the arms and the result type.
- **Lowering** (`LocalRewriter_SharperTryExpression.cs`), as a spill sequence, so earlier operands of the enclosing
  expression move to temps:

  ```csharp
  try { result = body; }
  catch (Exception e) when ({ arm = 0; /* the decision DAG over e sets arm and the pattern variables */ } arm != 0)
  {
      switch (arm) { case 1: result = value1; break; /* ... */ }
  }
  ```

  The try statement has the try expression's syntax, which `SyntaxBindingUtilities.BindsToTryStatement` accepts, and
  an `await` in an arm's value goes through `AsyncExceptionHandlerRewriter` like an `await` in any catch block.

Tests: `SharperTests_TryExpression.cs`, next to the C# twins; the union conversions it relies on are tested in
`UnionsTests.UnionConversion_59` to `_63` ([#137](https://github.com/ok3mad/roslyn/issues/137)).
