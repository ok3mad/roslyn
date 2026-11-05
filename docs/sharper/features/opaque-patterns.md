# Opaque patterns

**Status:** Prototype of slice 1 of patterns on pure functions. Requires `/features:sharper`. Issue:
[#34](https://github.com/ok3mad/roslyn/issues/34); the design is in the implementation plan,
[Feature 13](../planning/implementation-plan.md#feature-13-patterns-on-pure-functions-design).

## Summary

A relational or constant pattern may compare with a value that is not a constant, if computing it is pure:

```csharp
string Review(int amount, int max) => amount switch
{
    > 0 and <= max => "accept",
    _ => "reject",
};

bool IsLimit(int x, Account a) => x is a.Limit;          // a constant pattern with a field of a parameter
bool Big(Order o, int max) => o is { Lines: [_, > max, ..] };
```

In C# these are errors (CS9135 "A constant value of type 'int' is expected", or CS0150 when the input is `object` or a
type parameter). The C# that comes closest is a `when` guard, `var a when a > 0 && a <= max`, which only works after all
of an arm's tests. An opaque pattern sits where it is written, under `and`, `or` and `not`, in property, positional and
list patterns, in `is`, switch statements (`case < max:`) and switch expressions.

## Meaning

- The operand converts to the input type as a constant would (an `int` operand for a `long` input; a `long` operand for
  an `int` input is CS0266). For an input of type `object`, `System.ValueType` or a type parameter, the match first tests
  for the operand's type, as `o is 5` tests for `int`: `o is s` with a `string` `s` is `o is string t && t == s`.
- The comparison is the one C# uses for `input op operand` with that type: the numeric types, `char`, enums and
  native integers for `<`, `<=`, `>`, `>=` and `==`; `bool` and `string` for `==` only. So `x is d` with a `double` `d`
  that is NaN does not match, as `x == d` is false; only the constant pattern `double.NaN` tests for NaN. Other operand
  types keep C#'s error.
- The operand must be pure, by the rules of a [`[Pure]`](purity.md) body: it may read locals, parameters, fields reached
  through any object, constants and `static readonly` fields of immutable types, and call `[Pure]`, `[AssumePure]` and
  attested members. Anything else is CS20058, with the reason:

  ```csharp
  bool M(int x) => x is > Next();
  // error CS20058: The operand of a pattern that is not a constant must be pure, but it is not: 'Next()' calls 'C.Next()', which is not known to be pure.
  ```

  Reading a mutable instance field is fine: nothing in a pure match can write it while the match runs. A mutable static
  is rejected, as in a `[Pure]` body.
- The operand is evaluated while the pattern is matched, before any of the pattern's variables is assigned:
  `t is (var a, > a)` is CS0165, "Use of unassigned local variable 'a'".

## Evaluation order and exhaustiveness

The match may evaluate an opaque test in a different order than written, skip it when the outcome is already decided,
and evaluate two identical tests (the same operator and operand, written the same way, on the same value) once. C#
gives `Deconstruct` and property reads in patterns the same freedom; for a pure operand it can only change which
deterministic exception surfaces. A test is never evaluated ahead of the arms that need it.

The compiler knows nothing of an opaque test's outcome except that an identical test has the same one:

- An arm with an opaque test counts for exhaustiveness no more than a guarded arm does, so
  `x switch { > max => 1, <= max => 2 }` warns CS8509 without a catch-all.
- It never subsumes a later arm: `x switch { > max => 1, > 5 => 2, _ => 3 }` is fine.
- The same test is decided once: `x switch { > max => 1, not > max => 2 }` is exhaustive, and in
  `x switch { > max => 1, > max => 2, _ => 3 }` the second arm is unreachable (CS8510). With a `when` guard twin neither
  happens, because a guard can be anything.

## Lowering

The pattern binds to a `BoundOpaquePattern` and the decision DAG tests it with a `BoundDagOpaqueTest`, a comparison of
a DAG temp with the operand that leaves the value sets alone. The decision DAG rewriter lowers the operand where the
test is evaluated and emits the comparison the constant tests use (`PatternLocalRewriter.MakeComparison`). IOperation
shows an `IRelationalPatternOperation` or `IConstantPatternOperation` whose value is not a constant.

## Diagnostics

| Code | Severity | When |
| --- | --- | --- |
| CS20058 | Error | The operand of an opaque pattern is not pure, with the first expression that is not and why. |

## Not covered

- Union matching: a pattern on a union's value keeps C#'s error.
- Refinements (Feature 11) will reject opaque patterns, since a declared domain must stay closed.
- Symbolic reasoning over the same operand, such as `<= max` then `> max` being exhaustive.
- The predicate form `x is IsEven` and extractors `s is Parse(val n)` (slices 2 and 3,
  [#35](https://github.com/ok3mad/roslyn/issues/35)).

## Code

Under `src/Compilers/CSharp/Portable`:

- `Binder/Binder_SharperOpaquePatterns.cs`: `TryConvertSharperOpaqueOperand` decides, where `BindExpressionForPatternContinued`
  (`Binder/Binder_Patterns.cs`) would report CS9135 or CS0150, and `MakeSharperOpaquePattern` checks the operand with
  `SharperImmutability.CheckOpaquePatternOperand` (`Symbols/SharperImmutability_Pure.cs`). Callers: relational patterns,
  constant patterns (also `case` labels) and `x is max` (`Binder_Operators.cs`).
- `Binder/DecisionDagBuilder.cs`: `MakeTestsForOpaquePattern`; `CheckConsistentDecision` and `SplitValues` for the
  test; `BoundTree/BoundDagOpaqueTest.cs` compares operands (`IsSameComparison`).
- `Lowering/LocalRewriter/LocalRewriter.PatternLocalRewriter.cs`: `LowerTest`.
- Flow analysis: `AbstractFlowPass.VisitSharperOpaquePatternOperands` reads the operands in the state before the match;
  `NullableWalker_Patterns.cs` analyzes the operand where the DAG tests it, as a `when` clause.
- Tests: `SharperTests_OpaquePatterns.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics`), part of `SharperTests`.
