# `val` locals

**Status:** Prototype for local declarations (step 1), merged into `main` in #2. Requires `/features:sharper`.

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
- A non-readonly member of a struct `val` (a method, getter, indexer, `GetEnumerator`, `Deconstruct` or event
  accessor) runs on a copy, so it cannot change the `val`, and warns (CS9404). Readonly members, readonly structs and
  `ref val` are unaffected.
- `ref val` and `scoped val` are allowed: the local cannot be reassigned, but writes through the ref still work.
- A `val` local reports the same errors as `var` where `var` cannot be used: no initializer, a `null` initializer,
  several declarators, or `const`.
- `val` works in `using` declarations and `for` initializers.

## Not covered yet

Only local declarations recognize `val` so far. Deconstruction (`val (a, b) = ...`), `out val`, `foreach (val x in
...)`, pattern designations and script fields still treat it as an identifier. Those are tracked as
`PROTOTYPE(sharper)` comments. The design decisions and the remaining steps are in the
[implementation plan](../planning/implementation-plan.md#feature-2-step-1-val-locals).
