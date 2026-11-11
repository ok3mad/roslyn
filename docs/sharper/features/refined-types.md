# Refined types

**Status:** Prototype of slice 1: steps 1, 2 and 5 (syntax, binding, the entry guard and the metadata attribute) are
merged into `main`; step 3 (inferred return refinements) is on `sharper/refined-returns`. Requires `/features:sharper`. Issue:
[#33](https://github.com/ok3mad/roslyn/issues/33). The design is in the implementation plan,
[Feature 11](../planning/implementation-plan.md#feature-11-refined-types-slice-1-design).

**Calls are not checked yet.** A refinement is parsed, bound and checked, the parameter is readonly, and the method
checks it at entry: `Page(-1)` compiles, and throws `ArgumentOutOfRangeException` when it runs. Switches behave as in
C#. The return of an `infer` method and a `val` local already carry an inferred refinement
([Inferred refinements](#inferred-refinements)), which nothing reads yet; call sites and switches (step 4) come next.

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

## Inferred refinements

Nothing is written on a return. The return of a method or local function declared with
[`infer`](inferred-return-type.md), and a `val` local, get the union of what the value can be:

```csharp
static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };   // int is -1 or 0 or 1
static infer Clamp(int x, int i is >=10) { if (x < 0) return -1; return i; }   // int is -1 or >= 10
val s = Sign(x);                                                     // int is -1 or 0 or 1
```

| Value | Its refinement |
| --- | --- |
| A constant, also `null` and a folded constant (`Limit * 2 + 1`) | That value |
| A refined parameter, a `val` local with a refinement | Its refinement; they are readonly, so the fact holds wherever they are read |
| A call of a method whose return has a refinement, from source or metadata | The return's refinement |
| A switch expression, a conditional expression `c ? a : b` | The union of its arms; an arm that throws adds nothing |
| Anything else: a plain parameter, a `var` local, a field, arithmetic, a call of a C# method | None, and then the whole value has none |

A method's refinement is the union over every `return` of its body. A widened body widens the refinement, which step 4
turns into errors at the callers; one `return` with no refinement leaves the method with none.

The union has a normal form, which is also the attribute text: `null`, then the constants in ascending order (strings
by ordinal, `false` before `true`, `NaN` last), each once, then the other patterns in the order they are found, each
once. A constant that another pattern allows is dropped (`3` next to `>= 0`). There is no refinement when the union
has more than 64 alternatives (`SharperRefinement.MaxInferredAlternatives`) or allows every value (`true or false`). A
single refined value passed on unchanged keeps its refinement as it is written (`>= 0 and < 10 or 42`).

| Case | Result |
| --- | --- |
| A value whose type is not the return type (`return 1;` next to `return 2L;`, a block body's `x switch { 0 => 1, _ => 2L }`, `(long)i`) | None. An expression body's switch expression is converted before it is read, so `=> x switch { 0 => 1, _ => 2L }` is `long is 1 or 2` |
| A type without a value set (`object`, `int?`, tuples) | None |
| A string return with `return null;` | `null or "x"`; the refinement matches null (`MatchesNull`) |
| Iterators and `async` methods | None: the return is a sequence or a task. A refinement of the element or the awaited value needs a slot there, which no slice has |
| Virtual and abstract members, overrides, interface members | None: callers do not know which body runs. Overrides and implementations copy their type and infer nothing |
| A recursive `infer` method, or a cycle of them | The error of the inferred return type (CS20018, CS20033), and no refinement. A method with a written type has nothing inferred |
| Partial methods | The implementation part infers; the definition, which callers bind to, has the same refinement |
| Operators and conversion operators | Inferred and written to metadata; `-c` and a cast do not read it yet, only calls do |
| `val` in a deconstruction, a pattern, `foreach` or `out`; a local with a written type (`readonly int x = 3;`) | None yet |

The refinement is inferred together with the type, from the same binding of the body
(`Binder.BindSharperInferredReturnType`), and published with it (`SharperInferredReturnType`), so it starts no inference
of its own and adds no cycle: reading a refinement is reading the return type. A `val` local's refinement is set when
its declaration is bound. The code is `SharperRefinement.OfValues` and `OfExpression`
(`Symbols/Source/SharperRefinement_Inferred.cs`); the slots are `MethodSymbol.SharperReturnRefinement` and
`LocalSymbol.SharperRefinement`, and a call's result is read with `SharperRefinement.OfExpression`.

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
| Iterator, `async` method, `async` iterator | In the kickoff method, before the state machine is created: the call throws, before anything is enumerated or awaited. With runtime async on, such a method keeps the compiler's state machine: a runtime-async method has no kickoff method, and the runtime would put the guard's exception into the returned task |
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

An inferred return refinement goes where the inferred type goes: the same attribute on the return, on every
accessibility (the design predates `infer` on visible members; whoever wants a stable public API writes the type out).
`PEMethodSymbol.SharperReturnRefinement` reads it back, except on virtual and interface members. A local function's
closure method carries none.

| Source | Attribute on the return |
| --- | --- |
| `infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 }` | `-1 or 0 or 1` |
| `infer Plain(int x) => x`: a return that can have a refinement and has none | `_`, the discard pattern, read back as no refinement |
| `infer Make() => new object()`, an `async` or virtual method: a return that cannot have one | No attribute |

The `_` is there for Edit and Continue: the delta writer cannot add or remove the metadata row of a return when a body
is edited, only rewrite its attribute. With the attribute always present, a body edit that changes only the refinement
is a body update (an edit that changes the type is a rude edit, as before).

## Open points

- An implicit implementation of an interface member, `public void M(int i is >=0)` for `I.M(int i)`, is not
  reported yet; it needs the interface map.
- A default value from `[DefaultParameterValue]` is not checked.
- Nothing reads an inferred refinement yet. When step 4 makes callers depend on it, a body edit that changes it must
  become a rude edit, as a changed inferred type is: callers are not recompiled. The IDE cannot see the refinement
  through the public API today.
- Changing a written return type to `infer` during Edit and Continue adds the attribute to the return, which the delta
  writer cannot do.
- `val` variables of deconstructions, patterns, `foreach` and `out`, and locals with a written type, have no refinement.
- The semantic model does not bind a refinement's pattern yet: `GetSymbolInfo` on a constant in it finds nothing.
- A method with refined parameters does not use runtime async (see the guard table); a synthesized wrapper that checks
  and then calls a runtime-async body would let it ([#224](https://github.com/ok3mad/roslyn/issues/224)).
- The guard always runs; eliding it when every caller is proven is an optimization for later.

## Tests

`src/Compilers/CSharp/Test/Semantic/Semantics/SharperTests_Refinements.cs` (`Refinement_*`): parsing with the flag on
and off, the default-value ambiguity, the factory overloads, every position, the value sets, each diagnostic, and
runtime behavior next to the C# twin.

`SharperTests_RefinementReturns.cs` (`ReturnRefinement_*`, `ReturnRefinementMetadata_*`): constants of every type, a
refined parameter returned unchanged, switch and conditional expressions, block bodies whose returns widen the set,
every case with no refinement, `val` locals, local functions, the recursion errors, every order of inference and several
threads, every member kind, the attribute and its round trip through metadata (image and compilation references),
untrusted text, runtime output next to the C# twin, and the flag off.
`EditAndContinueSharperTests.InferredReturnRefinement_BodyEdit` (Emit2) covers body edits that change, remove and add a
refinement.

`SharperTests_RefinementGuards.cs` (`RefinementGuard_*`, `RefinementMetadata_*`): the guard on every supported type
and member, its exception (parameter name, value, message) next to the C# twin, iterators and `async` throwing
eagerly (also with runtime async on), constructors before field initializers, a C# caller, the IL in Debug and Release, the missing exception, the
flag off; the attribute's shape, round trips through metadata for every type and member, and text that is not trusted.
