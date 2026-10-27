# Sequence type `T[..]`

**Status:** Prototype, merged into `main`. Requires `/features:sharper`. Issue: [#19](https://github.com/ok3mad/roslyn/issues/19).

## Summary

`T[..]` is `IEnumerable<T>`: a sequence of `T`, next to `T[]`, a real array.

```csharp
int[..] Evens(int[..] xs) => xs.Where(x => x % 2 == 0);
```

It stands for:

```csharp
IEnumerable<int> Evens(IEnumerable<int> xs) => xs.Where(x => x % 2 == 0);
```

`T[..]` promises neither laziness nor eagerness (see [Sequences and yield](../planning/brainstorm.md#sequences-and-yield)).

## Rules

- **An alias, not a new type.** `T[..]` binds to `System.Collections.Generic.IEnumerable<T>`, so metadata, the
  semantic model, IOperation and symbol display all show `IEnumerable<T>`. C# callers see ordinary
  `IEnumerable<T>` members, `void M(int[..] xs)` collides with `void M(IEnumerable<int> xs)`, and an `int[..]`
  member implements an `IEnumerable<int>` interface member.
- **Every type position:** locals, fields, properties, parameters, return types, generic arguments, tuple elements,
  constraints, base types, `using` aliases, casts, `typeof`, `default`, `is` and `as`, lambda parameters and return
  types, local functions, `foreach` variables, `out` variables.
- **Stacking reads left to right, innermost first**, as the brainstorm's type syntax does: each `[..]` wraps
  everything to its left. Plain array ranks between two `[..]` keep their C# order.

  | Sharper | C# |
  | --- | --- |
  | `int[..][]` | `IEnumerable<int>[]` |
  | `int[][..]` | `IEnumerable<int[]>` |
  | `int[..][..]` | `IEnumerable<IEnumerable<int>>` |
  | `int[,][..][][,]` | `IEnumerable<int[,]>[][,]` |
  | `int?[..]` | `IEnumerable<int?>` |
  | `int[..]?` | `IEnumerable<int>?` |
  | `string?[..]` | `IEnumerable<string?>` |

- **Only a lone `..`.** `int[1..]`, `int[..1]` and `int[.., ..]` are array sizes, with the C# error CS0270. So is
  `int[. .]`, with a space between the dots.
- **Invalid elements** report what `IEnumerable<T>` reports: CS0718 for a static class, CS0306 for a pointer. The error
  is on the element, everything to the left of the `[..]` (`System.Console` in `System.Console[..] x`), where the
  `IEnumerable<T>` spelling reports it. A missing `IEnumerable<T>` is CS0518.
- **C# wins in expressions.** `a[..]` and `a?[..]` are also element accesses with a range, and keep that meaning:
  `(a[..])`, `(a[..]) - 1`, `(a[..])(b)`, `(a?[..]).b`, `(a[..])!`, `(a[..]) with { }` and `a[..] = b` are unchanged.
  A parenthesized `(A[..])` is a cast only before an identifier or a literal: `(A[..])b` is a cast.
  `(int[..])(b)` is always a cast, since `int[..]` cannot be an expression.
- **Flag off:** unchanged C#. `int[..]` reports CS0270, and in a local declaration also CS0029 for the range; a
  statement that starts with `Func<int[..], int>` is not parsed as a declaration.

## Limitations

- **Array creation keeps its C# meaning.** `new int[..]` is CS0029, and `new int[3][..]` CS0178. Write
  `new IEnumerable<int>[3]` for an array of sequences. Whether to change that is
  [#64](https://github.com/ok3mad/roslyn/issues/64).
- **A cast of a parenthesized expression to a named sequence type** is read as an element access: `(A[..])(b)`
  invokes `A[..]`, as C# does. Write `(A[..])b` or `(IEnumerable<A>)(b)`. The same holds for `int?[..]`.
- **Accepted break:** `F<a[..]>(x)` is a generic call, as `F<a>(x)` already is in C#. In C# it is two comparisons,
  which compile only with user-defined comparison operators. The sharpener will parenthesize it as
  `F < (a[..]) > (x)` ([#66](https://github.com/ok3mad/roslyn/issues/66)).
- **IDE:** completion, classification, Quick Info and refactorings know nothing about `T[..]`. Symbol display shows
  `IEnumerable<T>`.

## Implementation

The syntax is the C# array type syntax: a rank specifier whose only size is a range with no operands
(`ArrayRankSpecifierSyntax.IsSharperSequence`). The parser change is in `ScanType` (`LanguageParser.cs`), which
accepts `[..]` so that a statement can start with the type, and in `ScanCast`. The binder
(`Binder_SharperTypes.cs`, `BindSharperSequenceType`) wraps the ranks in `IEnumerable<T>` with
`BindSharperTypeAlias`, the shared helper for Sharper's short type forms. Local declarations skip the `..` when they
bind invalid array dimensions, and the semantic model gives it no semantic info. Nothing is lowered. Tests are the
`SequenceType_*` tests in `SharperTests.cs`.
