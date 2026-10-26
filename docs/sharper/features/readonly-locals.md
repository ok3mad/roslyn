# `readonly` locals

**Status:** Prototype, on branch `sharper/readonly-locals`. Requires `/features:sharper`. Issue:
[#16](https://github.com/ok3mad/roslyn/issues/16).

## Summary

`readonly` on a local declaration makes the local impossible to reassign, with an explicit or an inferred type:

```csharp
readonly int total = items.Sum();
readonly var name = user.Name;   // the same as: val name = user.Name;
total = 0;   // error CS1656: Cannot assign to 'total' because it is a 'readonly local'
```

It completes the [mutability grid](../planning/brainstorm.md#binding-mutability): `int x` and `var x` can be
reassigned, `readonly int x` and `readonly var x` cannot, and `val x` is shorthand for `readonly var x`. In C#,
`readonly` on a local is error CS0106; with the flag off it still is.

## Rules

`readonly` locals follow the [`val` rules](val-locals.md#rules), with messages that name the 'readonly local':

- Every write is an error: assignment, compound assignment, `++`/`--`, passing as `ref` or `out`, and writes to fields
  of a struct local (CS1656, CS1657, CS1654).
- A non-readonly member of a struct `readonly` local runs on a copy and warns: CS20007, the readonly-local form of
  CS20003. Members found by pattern (`GetEnumerator`, property patterns) do the same.
- A `readonly` local must have an initializer (CS20008), because nothing can assign it later. `readonly var x;` reports
  `var`'s CS0818 instead.
- `readonly var` reports the same errors as `var` where `var` cannot be used (no initializer, `null`, several
  declarators).
- `using readonly var d = ...;` and `using readonly D d = ...;` are using declarations that cannot be reassigned.
- Top-level statements work like method bodies.

## `ref`, `scoped` and `const`

`readonly` means "cannot be reassigned" for the local itself, and `ref readonly` keeps its C# meaning, "cannot write
through the ref". The combinations match C#'s `readonly ref` fields in ref structs:

| Declaration | Write through the ref (`r = 5`) | Ref reassignment (`r = ref j`) |
| --- | --- | --- |
| `ref int r` | allowed | allowed |
| `ref readonly int r` (C#) | error CS0131 | allowed |
| `readonly ref int r`, `readonly ref var r` | allowed | error CS1656 'readonly local' |
| `readonly ref readonly int r` | error CS0131 | error CS1656 'readonly local' |

- `readonly scoped Span<int> s = span;` is a scoped local that cannot be reassigned. `scoped readonly` is a syntax error,
  as for every modifier after `scoped`.
- `const readonly` and `readonly const` keep CS0106: a constant is already read-only.
- `readonly val x` is allowed and means `val x`.
- `readonly` on a local function keeps CS0106.

## Scripts

In a script, top-level declarations are fields, and `readonly int t = 1;` is already a C# readonly field (CS0191 on
assignment). Sharper does not change that.

## Not covered

These have no place for a modifier in the C# syntax tree, so they stay syntax errors. `val` covers the inferred cases.

- `for (readonly int i = 0; ...)`: `ForStatementSyntax` has no modifiers. Supporting it needs a public syntax API
  change, for a loop variable that the loop usually increments. `for (val i = ...; ...)` works.
- `foreach (readonly int x in xs)`: use `foreach (val x in xs)`. A foreach variable is already read-only in C#.
- `readonly var (a, b) = e` and `(readonly int a, readonly var b) = e`: use `val (a, b) = e`.
- `out readonly int n`: use `out val n`.

## Code

The parser (`ParseLocalDeclarationStatementModifiers` in `LanguageParser.cs`) skips CS0106 (CS9229 after `using`) for
`readonly` when the feature is on, and `TryParseLocalFunctionStatementBody` reports it for local functions.
`SourceLocalSymbol.IsSharperReadOnly` makes the local non-writable, which brings in the `val` checks
(`ReportReadonlyLocalError`, `Binder.IsSharperValValue`).
