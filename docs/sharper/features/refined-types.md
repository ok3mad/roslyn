# Refined types

**Status:** Prototype of slice 1, steps 1 and 2 (syntax and binding), on `sharper/refined-parameters-syntax`. Requires
`/features:sharper`. Issue: [#33](https://github.com/ok3mad/roslyn/issues/33). The design is in the implementation
plan, [Feature 11](../planning/implementation-plan.md#feature-11-refined-types-slice-1-design).

**Runtime behavior is unchanged in this step.** A refinement is parsed, bound and checked, and the parameter is
readonly, but nothing reads it yet: there is no entry guard, calls are not checked, and switches behave as in C#.
`Page(-1)` compiles and runs with `i == -1`. Return refinements (step 3), call sites and switches (step 4), and the
guard and metadata (step 5) come next.

## Summary

A refinement is a closed pattern over constants written after a parameter's name. Every value of the parameter
satisfies it:

```csharp
static int Page(int i is >=0) => ...;
static string Day(int d is >=1 and <=7) => ...;
void Fetch(string url, infer retries is >=0 = 3) { }
```

The refinement comes before any default value, because `bool b = 0 is >=0` is valid C#: a default value of
`(0 is >=0)`. Sharper reads that line as C# does.

## Syntax

`RefinementClauseSyntax` (`SyntaxKind.RefinementClause`, 9905) holds the `is` keyword and a pattern. It is the
optional `Refinement` field of `ParameterSyntax`, between `Identifier` and `Default`, since a node's fields are in
source order. The overloads that existed before it, `SyntaxFactory.Parameter(attributeLists, modifiers, type,
identifier, @default)` and `ParameterSyntax.Update` with the same five arguments, are kept by hand (shipped public API),
and make or keep a parameter without changing its refinement.

The parser reads the clause in every parameter list it parses with `ParseParameter`: methods, local functions,
constructors, primary constructors, indexers, operators, delegates and anonymous methods. A lambda's parameter list
is scanned before it is parsed, and the scan does not take a refinement, so `(int i is >0) => i` stays a syntax error.
With the flag off, `is` after a parameter name is C#'s syntax error (CS1003).

## Rules

| Rule | Diagnostic |
| --- | --- |
| The pattern is made of constant and relational patterns, `and`, `or`, `not` and parentheses. Type, declaration, `var`, recursive, list and discard patterns are not closed over constants, nor are the opaque patterns of [#34](https://github.com/ok3mad/roslyn/issues/34) | CS20058 |
| The parameter type has a value set: an integer type, `nint`, `nuint`, `char`, `bool`, `float`, `double`, `decimal`, `string` or an enum. Nullable value types, `object`, type parameters and other types are not supported | CS20059 |
| The default value satisfies the refinement: `int i is >=0 = -1` is an error, as is a `null` default for `string s is "a" or "b"` (but not for `string s is not "x"`, which matches `null`) | CS20060 |
| A refined parameter is implicitly [`readonly`](readonly-parameters.md): every write is CS1656 or CS1657, with the 'readonly parameter' cause | CS1656, CS1657 |
| Not supported yet: virtual, abstract and override members, interface members, explicit interface implementations, partial members, extern members, delegates, lambdas and anonymous methods, extension block receivers, positional parameters of records, and `ref`, `out` and `in` parameters | CS20061 |

A refinement is not part of the signature; the members where callers do not know which body runs are errors so that
overriding rules can be added later without a break. A write through a `ref` would break the fact the refinement
states, and records copy their positional parameters to properties whose `with` and `init` bypass the constructor.
An `infer` parameter gets its type from its default value, then its refinement binds against that type.

The value set of a refinement is computed once per parameter symbol (`SharperRefinement`, in
`Symbols/Source/SharperRefinement.cs`, reached through `SourceComplexParameterSymbolBase.SharperRefinement`), with
`ValueSetFactory` and the same relations the decision DAG uses. A refined parameter is never a
`SourceSimpleParameterSymbol`, which keeps no syntax.

## Open points

- An implicit implementation of an interface member, `public void M(int i is >=0)` for `I.M(int i)`, is not
  reported yet; it needs the interface map.
- A default value from `[DefaultParameterValue]` is not checked.
- Recursive `infer` returns (an error in the design) wait for return refinements, step 3.
- The semantic model does not bind a refinement's pattern yet: `GetSymbolInfo` on a constant in it finds nothing.

## Tests

`src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests_Refinements.cs` (`Refinement_*`): parsing with the flag on
and off, the default-value ambiguity, the factory overloads, every position, the value sets, each diagnostic, and
runtime behavior next to the C# twin.
