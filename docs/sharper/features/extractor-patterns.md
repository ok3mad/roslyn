# Extractor patterns

**Status:** Prototype of slice 3 of patterns on pure functions, on `sharper/extractor-patterns`. Requires
`/features:sharper`. Issue: [#35](https://github.com/ok3mad/roslyn/issues/35); the design is in the implementation plan,
[Feature 13](../planning/implementation-plan.md#feature-13-patterns-on-pure-functions-design). The predicate form
(`x is IsEven`) is slice 2, [predicate patterns](predicate-patterns.md). C# 15 unions as extractors (shape 4) wait for
Sharper's union story ([#31](https://github.com/ok3mad/roslyn/issues/31)).

## Summary

A positional pattern whose name is no type but a pure method calls the method with the input and matches its
sub-patterns on what the method gives back:

```csharp
static class Ex
{
    [AssumePure] public static bool TryParse(string s, out int value) => int.TryParse(s, out value);
    [Pure] public static int Length(string s) => s.Length;
}

bool Positive(string s) => s is TryParse(var n and > 0);                 // bool TryX(T, out U): matches when it returns true
string Size(string s) => s switch { Length(0) => "empty", Length(1) => "one", _ => "many" };   // U F(T): the result
bool FirstIsNumber(Order o) => o is { Lines: [Ex.TryParse(_), ..] };
bool Within(string s, Range r) => s is r.TryParseWithin(var n);         // an instance method on another receiver
```

In C# these are errors: a positional pattern names a type, so `TryParse(var n)` is CS0246 ("The type or namespace name
'TryParse' could not be found"), `Ex.TryParse(...)` CS0426, `System.Nope(...)` CS0234 and `r.TryParseWithin(...)` CS0118.
The C# that comes closest is a `Try` call (`TryParse(s, out var n) && n > 0`) or a call in a `when` guard, which only
works after all of an arm's tests. An extractor sits where it is written, under `and`, `or` and `not`, in property,
positional and list patterns, in `is`, switch statements and switch expressions.

## Meaning

- **Lookup.** `Name(...)` is an extractor only where binding `Name` as a type fails with "not found" (CS0246, CS0426,
  CS0234) or "is a variable/method, not a type" (CS0118), and the name binds as an expression to a method group. A type
  of that name always wins, silently: with a class `Parse` in scope, `x is Parse(var n)` deconstructs it, and with
  `using System;`, `x is Half(var h)` is the type `System.Half`. Simple names (also `this`'s instance methods and
  `using static` imports), generic names, `Type.Method` and `receiver.Method` work. The pattern must have only the
  positional part: `Name(...) { P: 1 }` and `Name(...) v` keep C#'s error.
- **Shapes, in order.** With `k` sub-patterns, the pattern first binds the call `Name(x, out _, ..., out _)` with `k`
  discards: if overload resolution finds a method that returns `bool`, it is the Try shape (shape 2), which matches when
  the method returns true, with sub-pattern `i` on the `i`th `out` value. Otherwise, for one sub-pattern without a name,
  it binds `Name(x)`: a method that returns a value is the value shape (shape 3), with the sub-pattern on the result.
  So when a name has both shapes (`bool Get(string, out int)` and `int Get(string)`), `Get(var g)` is the Try shape,
  and `Name()` is a Try method with no `out` values, which matches when it returns true. Several overloads of one shape
  are chosen by overload resolution; an ambiguity is reported (CS0121). Nothing applicable, or a method of neither shape
  (`void V(T, out U)`, `int TwoOuts(T, out A, out B)`, a `ref` return, default values filled in, `params` expansion),
  keeps C#'s error.
- **Names.** A Try shape's sub-pattern may name its `out` parameter, `TryParse(value: var n)`; another name is CS8517, as
  for `Deconstruct`. The value shape takes no names.
- **The result as it is.** The value shape's sub-pattern matches the result, `null` included: `Find({ } f)` for a
  nullable result, `Find(null)` for no result. `null` has no special meaning.
- **The input** is the call's first argument, never the receiver, and converts as an argument would (implicit
  conversions only). A generic method infers its type arguments from the input (`TryFirst(var f)` on an `int[]`), or
  takes them written out (`Id<object>(string)`). An extension method counts when called on another receiver or by name.
  An `object` input does not call `TryParse(string, out int)`: write `o is string and TryParse(var n)`. The method is
  called with the input as it is, `null` included: no null test comes first.
- **Purity.** The call must be pure, by the rules of a [`[Pure]`](purity.md) body: the method is `[Pure]` (also through
  a `[Pure]` virtual or interface slot), `[AssumePure]`, or an attested BCL member, and a receiver may read locals,
  parameters, fields and `this`. Anything else is CS20075, with the reason:

  ```csharp
  static bool TryParse(string s, out int n) { n = 0; return true; }
  bool M(string s) => s is TryParse(var n);
  // error CS20075: The method of an extractor pattern must be pure, but it is not: 'TryParse' calls 'C.TryParse(string, out int)', which is not known to be pure.
  ```

  The pattern's `out` values are fresh temporaries, so a `[Pure]` Try method never gets CS20053 here (#177).
- **Designations** are assigned when the pattern matches, as for `Deconstruct`; the call is made before any of the
  pattern's variables is assigned, so `(TryParse(var n), > n)` is CS0165.

## Evaluation order and exhaustiveness

The call is an evaluation in the decision DAG, like a `Deconstruct` call, with C#'s freedom: the match may make it in a
different order than written, skip it when the outcome is already decided, and make it once for two identical calls
(the same method, called the same way, on the same value). It is never made ahead of the arms that need it.

Its return value and `out` values are values the DAG knows, so the sub-patterns on them narrow as any sub-pattern does;
nothing is learned about the input:

- `s switch { TryParse(_) => 1, not TryParse(_) => 2 }` is exhaustive; `s switch { TryParse(> 0) => 1, TryParse(<= 0) => 2 }`
  is not (CS8509), since it leaves out the inputs for which `TryParse` returns false.
- In `s switch { TryParse(> 0) => 1, TryParse(> 5) => 2, _ => 3 }` the second arm is unreachable (CS8510). With a `when`
  guard twin neither happens.
- `s switch { TryParse(> 10) => 1, TryParse(> 0) => 2, TryParse(_) => 3, _ => 4 }` calls `TryParse` once; the twin with
  three guards calls it three times.

## Lowering

The pattern binds to a `BoundExtractorPattern`: the call `TryParse(placeholder, out _)` over a placeholder for the input,
its sub-patterns, and the shape. The decision DAG makes a `BoundDagExtractorEvaluation` on the input (after a type test
when the check of `or` operands widened the input type), whose outputs are the return value (temp index -1) and the
`out` values (0 to n-1); the Try shape then tests the return value for `true`. The decision DAG rewriter replaces the
placeholder with the input temp and the discards with the `out` temps, and assigns the result to its temp.

IOperation shows an `IRecursivePatternOperation` whose `DeconstructSymbol` is the method, `MatchedType` the input type and
`DeconstructionSubpatterns` the sub-patterns, as the pattern is written; the call, its receiver and its arguments do not
appear. The control flow graph needs nothing new.

## Diagnostics

| Code | Severity | When |
| --- | --- | --- |
| CS20075 | Error | The method of an extractor pattern is not pure, with the first expression that is not and why. |

## Not covered

- C# 15 unions as extractors (shape 4, after [#31](https://github.com/ok3mad/roslyn/issues/31)), union matching and
  `dynamic` inputs keep C#'s error.
- Lambdas and delegate-typed values as extractors (`s is parse(var n)` with a `Func<string, int> parse`).
- A property clause or a designation on the extractor itself (`TryParse(var n) { } x`).
- Refinements: a [refined parameter](refined-types.md) `string s is TryParse(_)` is CS20058; the method's purity is not
  checked there.
- The check of `or` operands (redundant or unreachable operands) treats an extractor as one operand, as it does an opaque
  pattern: `or` patterns inside its sub-patterns are not checked.
- An extractor's call cannot be intercepted (interceptors).

## Code

Under `src/Compilers/CSharp/Portable`:

- `Binder/Binder_SharperExtractorPatterns.cs`: `TryBindSharperExtractorPattern`, from `BindRecursivePattern`
  (`Binder/Binder_Patterns.cs`); `IsSharperExtractorTypeNotFound` is the type trial, and
  `IsInSharperExtractorPatternName` tells the primary constructor capture finders (`MethodCompiler`,
  `Binder.IdentifierUsedAsValueFinder`) that the name may read a parameter. Purity:
  `SharperImmutability.CheckExtractorPatternCall` (`Symbols/SharperImmutability_Pure.cs`).
- `BoundTree/BoundDagExtractorEvaluation.cs` (outputs, `IsEquivalentTo` through `BoundDagOpaqueTest.IsSameValue`);
  `Binder/DecisionDagBuilder.cs`: `MakeTestsAndBindingsForExtractorPattern`, and the evaluation in the temp replacement
  of `RemoveEvaluationAndUpdateTempReferences`; `Binder/DecisionDagBuilder_CheckOrReachability.cs`.
- `Lowering/LocalRewriter/LocalRewriter.PatternLocalRewriter.cs`: `LowerEvaluation`.
- Flow analysis: `AbstractFlowPass.VisitSharperOpaquePatternOperands` reads the call before the match, and a pattern that
  calls a method on the input matches null or not (`callsSharperMethodOnInput`); `DefiniteAssignment.VisitPattern` and
  `VariablesDeclaredWalker` assign and note the sub-patterns' variables; `NullableWalker_Patterns.cs` analyzes the call
  at the DAG node and types the outputs from the method's annotations; `Binder/RefSafetyAnalysis.cs` gives the
  placeholder the input's escape scope.
- `Operations/CSharpOperationFactory.cs`: `CreateBoundExtractorPatternOperation`.
- Tests: `SharperTests_ExtractorPatterns.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics`), part of `SharperTests`.
