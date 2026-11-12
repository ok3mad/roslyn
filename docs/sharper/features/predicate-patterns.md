# Predicate patterns

**Status:** Prototype of slice 2 of patterns on pure functions, on `sharper/predicate-patterns`. Requires
`/features:sharper`. Issue: [#35](https://github.com/ok3mad/roslyn/issues/35); the design is in the implementation plan,
[Feature 13](../planning/implementation-plan.md#feature-13-patterns-on-pure-functions-design). Extractors with
sub-patterns (`s is Parse(val n)`) are slice 3.

## Summary

A method group that names a pure `bool M(T)` is a pattern that matches when the method returns true for the input:

```csharp
static class Checks
{
    [Pure] public static bool IsEven(int x) => x % 2 == 0;
}

bool M(int x) => x is Checks.IsEven;
string Kind(int x) => x switch { IsEven and > 10 => "big even", IsEven => "even", _ => "odd" };
bool Valid(Order o) => o is { Lines: [_, ..], Customer: not string.IsNullOrEmpty };
bool Accepts(int x, Rule rule) => x is rule.Accepts;      // an instance method on another receiver
```

In C# these are errors: a method group is neither a constant nor a type (CS0428 "Cannot convert method group 'IsEven' to
non-delegate type 'int'", or CS0150 when the input is `object`). The C# that comes closest is a `when` guard,
`var a when IsEven(a)`, which only works after all of an arm's tests. A predicate pattern sits where it is written, under
`and`, `or` and `not`, in property, positional and list patterns, in `is`, switch statements (`case IsEven:`) and switch
expressions.

## Meaning

- `x is IsEven` is the call `IsEven(x)`, bound as that call would be: simple names (also `this`'s instance methods and
  `using static` imports), `Type.Method` and `receiver.Method`, with the input as the only argument. The input is never
  the receiver: an extension method counts when it is called on another receiver (`x is range.Above` is
  `range.Above(x)`), or by its name as a static method.
- The input converts as an argument would: an `int` input calls `bool M(long)`, a `string` input `bool M(object)`, an
  `int` input `bool M(int?)`, and a generic method infers its type arguments from the input (`x is Is<int>` gives them
  too). An `object` input does not call `bool M(int)`: write `o is int and IsEven`.
- Overloads are chosen by overload resolution on that call. An ambiguity among applicable methods is reported (CS0121);
  if nothing is applicable, C#'s error stays.
- The method found must return `bool` (not `bool?`, not by reference) and take the input as its only argument: no
  default values filled in and no `params` expansion. Anything else (`int F(int)`, `bool M(int, int y = 0)`) keeps C#'s
  error; `U F(T)` will be an extractor in slice 3.
- A type or constant of that name keeps the C# meaning: with a class `IsEven` in scope, `o is IsEven` is a type
  pattern, and with a constant `IsOdd`, `x is IsOdd` compares with it. An instance method named from a static context
  keeps C#'s error.
- The method is called with the input as it is, `null` included: no null test comes first, so
  `s is string.IsNullOrEmpty` matches `null`, as `string.IsNullOrEmpty(s)` does. Write `s is { } and IsEmpty` or
  `s is not null and IsEmpty` for a method that does not take `null`; nullable analysis warns as for the call
  (CS8604).
- The call must be pure, by the rules of a [`[Pure]`](purity.md) body: the method is `[Pure]` (also through a `[Pure]`
  virtual or interface slot), `[AssumePure]`, or an attested BCL member, and a receiver may read locals, parameters,
  fields and `this`. Anything else is CS20069, with the reason:

  ```csharp
  static bool IsEven(int x) => x % 2 == 0;
  bool M(int x) => x is IsEven;
  // error CS20069: The method of a predicate pattern must be pure, but it is not: 'IsEven' calls 'C.IsEven(int)', which is not known to be pure.
  ```

## Evaluation order and exhaustiveness

A predicate is an [opaque test](opaque-patterns.md#evaluation-order-and-exhaustiveness): the match may call it in a
different order than written, skip it when the outcome is already decided, and call it once for two identical tests
(the same method, called the same way, on the same value). The compiler knows nothing of the outcome except that an
identical call has the same one:

- `x switch { IsEven => 1, not IsEven => 2 }` is exhaustive, and in `x switch { IsEven => 1, IsEven => 2, _ => 3 }` the
  second arm is unreachable (CS8510). With a `when` guard twin neither happens.
- `x switch { IsEven => 1, IsSmall => 2 }` warns CS8509 without a catch-all, and a predicate never subsumes a later arm.

## Lowering

The pattern binds to a `BoundOpaquePattern` with a `PredicateArgument`: a placeholder for the input, and the call
`IsEven(placeholder)` as its value. The decision DAG tests it with a `BoundDagOpaqueTest` that carries the same
placeholder, and the decision DAG rewriter replaces the placeholder with the DAG temp it tests and emits the call.
IOperation shows an `IConstantPatternOperation` whose value is an `IMethodReferenceOperation` for the method (with its
receiver as the instance), as the pattern is written; the call and its argument do not appear.

## Diagnostics

| Code | Severity | When |
| --- | --- | --- |
| CS20069 | Error | The method of a predicate pattern is not pure, with the first expression that is not and why. |

## Not covered

- Union matching and `dynamic` inputs keep C#'s error.
- Refinements: a [refined parameter](refined-types.md) `int i is IsEven` is CS20058; the method's purity is not checked
  there.
- Lambdas and delegate-typed values as predicates (`x is isValid` with a `Func<int, bool> isValid` is a constant
  pattern, and an error).
- Extractors with sub-patterns (`s is Parse(val n)`, `TryX(T, out U)`, `U F(T)`): slice 3.
- A predicate's call cannot be intercepted (interceptors), as the target of a method group conversion cannot.

## Code

Under `src/Compilers/CSharp/Portable`:

- `Binder/Binder_SharperOpaquePatterns.cs`: `TryBindSharperPredicatePattern` binds the call where a constant pattern
  binds to a method group, from `BindConstantPatternWithFallbackToTypePattern` (`Binder/Binder_Patterns.cs`, also
  `case` labels) and `x is M` (`BindExpressionForPattern`, from `Binder_Operators.cs`), and checks it with
  `SharperImmutability.CheckPredicatePatternCall` (`Symbols/SharperImmutability_Pure.cs`).
- `Binder/DecisionDagBuilder.cs`: `MakeTestsForOpaquePattern` makes the test on the input itself;
  `BoundTree/BoundDagOpaqueTest.cs` compares two calls (`IsSameComparison`, the placeholders count as the same input).
- `Lowering/LocalRewriter/LocalRewriter.PatternLocalRewriter.cs`: `LowerTest` replaces the placeholder.
- `FlowAnalysis/NullableWalker_Patterns.cs` gives the placeholder the input's state where the DAG calls the method;
  `Binder/RefSafetyAnalysis.cs` gives it the input's escape scope.
- `Operations/CSharpOperationFactory.cs`: `CreateBoundOpaquePatternOperation`.
- Tests: `SharperTests_PredicatePatterns.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics`), part of `SharperTests`.
