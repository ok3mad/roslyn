# `val` locals

**Status:** Prototype, merged into `main`: local declarations (step 1, #2), deconstruction, `foreach` and `out`
(step 2, #4), and patterns (step 3). Requires `/features:sharper`.

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
- Members found by pattern run on a copy and warn too: `await` (`GetAwaiter`), implicit Index and Range indexers
  (`Length`, the indexer, `Slice`), and list and property patterns.

## Everywhere `var` declares a local

```csharp
val (a, b) = pair;                 // deconstruction
(val c, var d) = pair;             // per element
foreach (val x in xs) { }          // foreach variable
foreach (val (k, v) in pairs) { }  // foreach deconstruction
int.TryParse(s, out val n);        // out variable
if (o is val y) { }                // pattern, also case val y:, val y =>, (val p, val q), { P: val p }
```

Every one of these locals cannot be reassigned. A `foreach` variable keeps its existing message ("foreach iteration
variable").

## Accepted breaks

`val` is a keyword in two places even where `val` already has a C# meaning, as `var` is:

- `val (a, b) = e` is a declaration, even where a ref-returning method named `val` makes it an assignment in C#.
- `val x` in a pattern is a `val` pattern. If a type named `val` is in scope, that is error CS8508, as for a `var`
  pattern when a type named `var` exists, so the meaning never changes silently.

Code that means the method or the type writes `@val`. The sharpener will do that rewrite once it exists. `is val`
without a designation is still a type test.

## Not covered yet

Script fields still treat `val` as a type name, and every non-readonly member of a struct `val` warns, even one that
does not mutate (inferring which members mutate is a follow-up). The editor does not classify `val` as a keyword yet.
These are tracked as `PROTOTYPE(sharper)` comments and in the
[implementation plan](../planning/implementation-plan.md#follow-ups).
