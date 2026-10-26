# `val` locals

**Status:** In progress on the `sharper/val` branch. Requires `/features:sharper`.

## Summary

`val` declares an implicitly typed local that cannot be reassigned:

```csharp
val total = items.Sum();
total = 0;   // error CS1656: Cannot assign to 'total' because it is a 'val variable'
```

It is `var` plus the read-only rule that C# already applies to `foreach` and `using` variables.

## Rules

- **C# wins.** If a type named `val` is in scope, `val` means that type, just as a type named `var` takes precedence
  over the `var` keyword.
- Every write is an error: assignment, compound assignment, `++`/`--`, passing as `ref` or `out`, ref reassignment,
  and writes to fields of a struct local (CS1656, CS1657, CS1654).
- A `val` local reports the same errors as `var` where `var` cannot be used: no initializer, a `null` initializer,
  several declarators, or `const`.
- `val` works in `using` declarations and `for` initializers.

## Not covered yet

Only local declarations recognize `val` so far. Deconstruction (`val (a, b) = ...`), `out val`, `foreach (val x in
...)`, pattern designations and script fields still treat it as an identifier. Those are tracked as
`PROTOTYPE(sharper)` comments on the branch.
