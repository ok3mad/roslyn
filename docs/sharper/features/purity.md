# Purity

**Status:** Prototype of slice 1, first part. Requires `/features:sharper`. Issue:
[#173](https://github.com/ok3mad/roslyn/issues/173), under [#36](https://github.com/ok3mad/roslyn/issues/36);
the rules for `out` in [#177](https://github.com/ok3mad/roslyn/issues/177).

## Summary

`[Pure]` claims that a member has no effect outside its own frame, and the compiler checks the claim:

```csharp
static class MathEx
{
    [Pure] public static int Fib(int i)
    {
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
        return i < 2 ? i : Fib(i - 1) + Fib(i - 2);
    }

    [Pure] public static int Sum(int[] xs)
    {
        int sum = 0;
        foreach (var x in xs) sum += x;
        return sum;
    }
}

class Counter
{
    private int _count;
    [Pure] public int Next() => _count + 1;
    [Pure] public int Bump() => _count++;
    // error CS20050: 'Counter.Bump()' is marked [Pure], but it is not pure: '_count++' writes state outside the member.
}
```

Nothing changes at run time; the member is emitted as written, plus the attribute.

## Definition

A `[Pure]` member writes nothing outside its own frame. Its result depends only on the **contents** of what it reads
through its arguments (`this` included), and on state that cannot change. It may throw, and it may not terminate.

Inputs are judged by their contents, with the structural equivalence of [immutable types](immutable-types.md), not by
identity. So `Sum(int[])` is pure, and so is `Counter.Next()`, although `Counter` is mutable: `this` is just another
argument. Calling a pure member changes nothing, the order of calls does not matter, and equivalent inputs give
equivalent results. When the arguments are immutable, the inputs cannot change between two calls either, which is what
caching a result needs; that is a fact about the call, not a second kind of purity.

Judging by contents is sound because pure code observes neither identity (`ReferenceEquals` is impure) nor aliasing,
which needs a write. Two caveats: an `out` argument that aliases another argument makes aliasing visible (see
below), and the guarantee assumes nobody writes the inputs while the call runs, which holds automatically for
immutable inputs and is the caller's responsibility otherwise.

## What the compiler checks

A `[Pure]` member's body may:

- read and write its own locals and by-value parameters, and write its `out` parameters;
- read anything reached through its arguments, mutable or not: fields, array elements, `in` and `ref readonly`
  parameters, auto-properties;
- read constants and `static readonly` fields of an immutable type, and get-only static auto-properties of one;
- call `[Pure]` members (not through a virtual slot), `[AssumePure]` members, the pure observers of immutable types,
  and the attested members of other assemblies (below);
- create objects of any type whose constructor is `[Pure]`, attested, or written by the compiler (a record's, a primary
  constructor, a default constructor) and pure itself; create arrays; box;
- set fields and auto-properties (or `[AssumePure]` setters) of a new object in its object initializer, and use `with`
  on a struct or a sealed record whose clone and copy constructor the compiler writes;
- use loops, `foreach` over arrays and strings, `switch`, `try`, `catch`, `finally`, `throw`, local functions,
  `nameof`, string concatenation and interpolation of strings and characters, and tuple swaps such as
  `(a, b) = (b, a + b)`.

It reports the first expression that does anything else (CS20050), with the reason:

- a write to a field, an array element, a static, a `ref` parameter or local, or a captured local;
- a call of a member that is not known to be pure, including virtual calls, delegate invocations and constructors
  from other assemblies;
- reading a mutable static, including a `static readonly` field of a mutable type: that is a global variable in
  disguise;
- observing identity (`ReferenceEquals`, reference `==`, `object.Equals` and `GetHashCode` that are not overridden,
  and the identity-based equality of `ImmutableArray<T>` and of tuples holding one, as for observers);
- formatting with the current culture (`x.ToString()`, `$"{x}"` and `s + x` for a number `x`);
- lambdas, `await`, `yield`, `lock`, `using`, pointers, nested object initializers, collection initializers, and
  collection expressions.

A constructor may also write the fields and auto-properties of its own `this`, and its instance initializers are
checked with it.

The operand of an [opaque pattern](opaque-patterns.md) (`x is <= max`) is checked by the same rules, as an expression
with no locals of its own (CS20058).

There is no inference: a call of an unmarked helper is an error naming the helper, even when its body is pure. A
local function is `[Pure]` only when marked, and is checked on its own.

**Attested members of other assemblies**, until the attestation format exists
([#142](https://github.com/ok3mad/roslyn/issues/142)): `System.Math` and `System.MathF`; the `string` members that do
not depend on the culture (`Substring`, `Contains`, `Replace`, `Trim`, `ToUpperInvariant`, `Split`, `Concat` of
strings, `IndexOf` and `StartsWith` of a character, and the like; never an overload with an object, a format provider
or a `StringComparison`); `Equals`, `CompareTo` and `GetHashCode` of the primitive types; the length and rank of an
array; and the constructors of exceptions.

## Declarations

- `[Pure]` goes on methods, accessors, operators, constructors (`[method: Pure]` for a primary constructor) and local
  functions. On a property it means the get accessor.
- An instance member of a struct must be `readonly`, or the struct `readonly` (CS20051). The existing errors then cover
  writes to `this`, and other assemblies see `[IsReadOnly]`. Auto-property getters already are.
- Virtual, abstract and override members cannot be `[Pure]` yet (CS20052): every override would have to be pure too.
- Only Sharper files are checked. In a C# file the name `Pure` does not bind (CS0246). A `[Pure]` that binds to another
  type, such as `System.Diagnostics.Contracts.PureAttribute` through a `using`, is not this attribute and is not
  checked.

## `out` arguments

A pure member may have `out` parameters, so the `TryParse` shape can be pure. An `out` argument that is not fresh lets
the member observe its own write through another argument:

```csharp
[Pure] static int F(Box b, out int r) { r = 1; return b.X; }

F(box, out box.X);
// warning CS20053: The out argument 'box.X' of the [Pure] member 'F(Box, out int)' is not a fresh local, so 'F(Box, out int)' may observe its own write through another argument.
```

Fresh are discards, `out var`, non-`ref` locals, by-value parameters, and the `out` parameters of a `[Pure]` caller
(whose own callers are checked). The warning is reported in Sharper files at every call of a `[Pure]` member. The rules
are to be made precise in [#177](https://github.com/ok3mad/roslyn/issues/177).

## Across assemblies

`[Pure]` is `Sharper.PureAttribute`, synthesized and embedded like [`[Immutable]`](immutable-types.md#the-attributes)
when no type of that name is in scope. Other assemblies recognize it by name and trust it without checking it again.
Purity is never inferred, so only what is declared crosses assemblies.
`System.Diagnostics.Contracts.PureAttribute` is not trusted, because nothing checks it.

## Diagnostics

| Code | Severity | When |
| --- | --- | --- |
| CS20050 | Error | A `[Pure]` member is not pure, with the first expression that is not and why. |
| CS20051 | Error | A `[Pure]` instance member of a struct is not `readonly`. |
| CS20052 | Error | `[Pure]` on a virtual, abstract or override member. |
| CS20053 | Warning | An `out` argument of a `[Pure]` member is not a fresh local. |

## Not covered

- The rule for overrides and implementations, and `[Pure]` on virtual and interface members (the second part of slice
  1).
- Inference inside one assembly, mutating an object the member created itself (escape analysis), and conditional purity
  (`pure(f)`, `pure(provider)`).
- `total` ([#33](https://github.com/ok3mad/roslyn/issues/33)), and a `pure` keyword, which could imply `readonly` on
  struct members.
- The observers of an immutable type count as pure for a `[Pure]` caller, but they are checked by the observer rule of
  [immutable types](immutable-types.md), which holds statics fixed: an observer that reads a `static readonly` field of
  a mutable type is accepted there.

## Code

`Symbols/SharperImmutability_Pure.cs` (under `src/Compilers/CSharp/Portable`), part of `SharperImmutability`:

- `IsDeclaredPure(method)` is the query, by attribute name.
- `CheckPureBodies` is called from `MethodCompiler.CompileMethod` after `DiagnosticsPass`, with the bound body (and a
  constructor's instance initializers), so nothing is bound twice. It checks the member and its `[Pure]` local
  functions with the member mode of `PurityWalker` (`Symbols/SharperImmutability.cs`), which the observers of
  immutable types share.
- `CheckPureDeclaration` runs from `SharperImmutability.CheckDeclaration`; `ReportPureOutArguments` from
  `Binder.BindInvocationExpressionContinued`.
- Tests: `SharperTests_Purity.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics`), part of `SharperTests`.
