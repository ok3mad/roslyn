# Opaque patterns

**Status:** Prototype of slice 1. Requires `/features:sharper`. Issue: [#34](https://github.com/ok3mad/roslyn/issues/34),
built on [purity](purity.md). The design, with extractor patterns ([#35](https://github.com/ok3mad/roslyn/issues/35)),
is in the implementation plan, [Feature 13](../planning/implementation-plan.md#feature-13-patterns-on-pure-functions-design).

## Summary

Where C# requires a constant in a relational or constant pattern, Sharper accepts any pure expression:

```csharp
val max = config.MaxTransfer;
return amount switch { > 0 and <= max => Accept(amount), _ => Reject(amount) };
```

The C# it stands for:

```csharp
var max = config.MaxTransfer;
return amount switch { > 0 and var v when v <= max => Accept(amount), _ => Reject(amount) };
```

Unlike a `when` clause, an opaque test sits where it is written, so it works under `and`, `or` and `not`, in property
and list patterns, and in `is`, `switch` statements (`case < max:`, `case max:`) and switch expressions:

```csharp
x is not (<= max)
x is < min or > max
order is { Total: > limit, Lines: [_, ..] }
```

## Rules

- **Which patterns.** A relational pattern (`<`, `<=`, `>`, `>=`) or a constant pattern (`x is max`, `case max:`)
  whose operand is not a constant, in a Sharper file. In C# these are CS9135 or CS0150, so no valid C# changes meaning.
- **Which types.** The operand converts to the input type, or to its underlying type for a nullable input, as a
  constant would, and the type has a built-in comparison: the integer types, `char`, `decimal`, `float`, `double`,
  `nint`, `nuint` and enums, plus `string` and `bool` for equality. Equality on `float` and `double` keeps the C#
  error, because their constant patterns match `NaN` with `NaN`. An input of type `object`, a type parameter, a span or
  a union keeps the C# error too; narrow it with a type pattern first (`o is int and > max`).
- **Null.** An opaque test never matches `null`: `s is other` is false for a null `s`, and a nullable input is
  tested for a value first, as relational patterns do.
- **Purity.** The operand must be pure, by the rules of a [`[Pure]`](purity.md) body for an expression with no locals
  of its own: it may read locals, parameters, constants, fields through any object, array elements and auto-properties,
  and call `[Pure]` and attested members. It may not write anything (not even a local of the enclosing method), read
  mutable statics, or call members that are not known to be pure; that is CS20058 with the reason.
- **Order.** The order of pattern tests is not specified, as in C#: the decision DAG may evaluate an opaque test later
  than written, skip it when the outcome is already known, and evaluate it once for two identical tests. Purity makes
  that unobservable, apart from which exception surfaces when an operand throws. A test is never hoisted ahead of the
  arms; that would need `total`.
- **Exhaustiveness.** An opaque test narrows no value set, so an arm with one helps exhaustiveness no more than a
  `when` clause does, and a switch expression without a catch-all gets CS8509. The same test against the same local or
  parameter is one test: `x switch { <= max => 1, not <= max => 2 }` is exhaustive, and a second `<= max` arm is
  unreachable (CS8510, or CS8120 for `case`).
- **Not in refinements.** Declared domains such as `int i is >= 0` stay with constants
  ([Feature 11](../planning/implementation-plan.md#feature-11-refined-types-slice-1-design)).

## Lowering

The binder makes a `BoundSharperOpaquePattern`. The decision DAG turns it into an evaluation,
`BoundDagSharperOpaqueEvaluation`, which computes `input op operand` into a `bool` temp, followed by an ordinary test of
that temp for `true`, the shape a property pattern `{ P: true }` has. Two evaluations with the same operator and the same
local or parameter (through the same conversions) on the same input are equal, so the DAG shares them. Lowering emits
the comparison with the same helper as a constant relational test. IOperation shows the relational or constant pattern
with a non-constant value.

## Diagnostics

| Code | Severity | When |
| --- | --- | --- |
| CS20058 | Error | The operand of an opaque pattern is not pure, with the first expression that is not and why. |

## Not covered

- Equality on `float` and `double`, and inputs of type `object` or a type parameter.
- Symbolic reasoning about the same `val` (`<= max` then `> max` as exhaustive).
- The predicate form `x is IsEven` (slice 2) and extractors `s is Parse(val n)` (slice 3).

## Code

- Binding: `Binder/Binder_SharperPatterns.cs` (`IsSharperOpaquePatternOperand`, `MakeSharperOpaquePattern`), called
  from `BindExpressionForPatternContinued` and its callers in `Binder_Patterns.cs` and `Binder_Operators.cs`.
- Purity: `SharperImmutability.CheckOpaquePatternOperand` (`Symbols/SharperImmutability_Pure.cs`), the member mode of
  `PurityWalker` with `isPatternOperand`.
- DAG: `DecisionDagBuilder.MakeTestsForSharperOpaquePattern`; equality in `BoundDagSharperOpaqueEvaluation`
  (`BoundTree/BoundDagEvaluation.cs`); lowering in `LocalRewriter.PatternLocalRewriter.LowerEvaluation`.
- Tests: `SharperTests_OpaquePatterns.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics`), part of `SharperTests`.
