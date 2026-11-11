# Refined types

**Status:** Prototype of slice 1: steps 1 and 2 (syntax and binding) are merged into `main`; step 5 (the entry guard
and the metadata attribute) is on `sharper/refined-parameters-guard`. Requires `/features:sharper`. Issue:
[#33](https://github.com/ok3mad/roslyn/issues/33). The design is in the implementation plan,
[Feature 11](../planning/implementation-plan.md#feature-11-refined-types-slice-1-design).

**Calls are not checked yet.** A refinement is parsed, bound and checked, the parameter is readonly, and the method
checks it at entry: `Page(-1)` compiles, and throws `ArgumentOutOfRangeException` when it runs. Switches behave as in
C#. Return refinements (step 3) and call sites and switches (step 4) come next.

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

## Runtime guard

Every method with refined parameters checks them at entry, in order, on every accessibility. `Page` above runs as if
it were written:

```csharp
static int Page(int i)
{
    if (!(i is >=0)) throw new ArgumentOutOfRangeException(nameof(i), i, "The value does not satisfy the refinement 'is >= 0'.");
    ...
}
```

The guard covers every caller the compiler cannot check: C# callers, delegates, reflection. It is lowered through the
`is`-pattern lowering and has no sequence points of its own. The exception is the BCL's, looked up by name in the core
library (CS0656 at the refinement if it is missing); behavior lowers to C#, so the guard needs nothing from the
Sharper [runtime library](../README.md#runtime-library).

| Member | Where the guard runs |
| --- | --- |
| Method, operator, conversion, indexer accessor | First in the body |
| Iterator, `async` method, `async` iterator | In the kickoff method, before the state machine is created: the call throws, before anything is enumerated or awaited |
| Constructor | Before field initializers and the `base` or `this` call |
| Primary constructor of a class or struct | Before the captured parameters are stored and before field initializers |
| Local function | In its closure method (in the kickoff method if it is an iterator or `async`) |
| Extension block member | In its implementation method |

The code is `SharperRefinementGuard` (`Lowering/SharperRefinementGuard.cs`): `MethodCompiler` lowers the guard on its
own against the parameters of the method that is emitted, and puts it first in the body after state machine
rewriting.

## Metadata

A refined parameter carries an embedded `Sharper.SharperRefinementAttribute(string pattern)`, made like the
[`[Immutable]`](immutable-types.md#the-attributes) attributes: internal, `[Embedded]`, synthesized only when a
refinement needs it. The compiler writes it; the name does not bind in source. The string is the normalized pattern:

| Source | Attribute text |
| --- | --- |
| `int i is >=0 and <10 or 42` | `>= 0 and < 10 or 42` |
| `string s is not ("" or " ")` | `not ("" or " ")` |
| `Color k is Color.Red or Color.Green` | `Color.Red or Color.Green` |
| `Flags f is (Flags)3` | `(Flags)3`, for a value with no member |
| `decimal m is >Min`, with `const decimal Min = 0.01m` | `> 0.01m`, the constant's value |
| `double d is not double.NaN` | `not double.NaN` |

Constants are written as literals of the parameter type, so the text is valid C# for it and reads without binding
any name. A metadata parameter reads the attribute back (`PEParameterSymbol.SharperRefinement`), matched by namespace
and name and trusted as `[Immutable]` is, and gets the same value set and null rule as the source symbol; text that
does not parse or does not fit the type gives no refinement. A C# caller sees the plain type and meets the guard.

## Open points

- An implicit implementation of an interface member, `public void M(int i is >=0)` for `I.M(int i)`, is not
  reported yet; it needs the interface map.
- A default value from `[DefaultParameterValue]` is not checked.
- Recursive `infer` returns (an error in the design) wait for return refinements, step 3.
- The semantic model does not bind a refinement's pattern yet: `GetSymbolInfo` on a constant in it finds nothing.
- Runtime async: the guard is the first statement of the method itself, and whether the runtime throws to the caller or
  faults the task is not tested.
- The guard always runs; eliding it when every caller is proven is an optimization for later.

## Tests

`src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests_Refinements.cs` (`Refinement_*`): parsing with the flag on
and off, the default-value ambiguity, the factory overloads, every position, the value sets, each diagnostic, and
runtime behavior next to the C# twin.

`SharperTests_RefinementGuards.cs` (`RefinementGuard_*`, `RefinementMetadata_*`): the guard on every supported type
and member, its exception (parameter name, value, message) next to the C# twin, iterators and `async` throwing
eagerly, constructors before field initializers, a C# caller, the IL in Debug and Release, the missing exception, the
flag off; the attribute's shape, round trips through metadata for every type and member, and text that is not trusted.
