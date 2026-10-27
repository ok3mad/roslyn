# `val` locals

**Status:** Prototype, merged into `main`: local declarations (step 1, #2), deconstruction, `foreach` and `out`
(step 2, #4), and patterns (step 3). Requires `/features:sharper`. Issues: [#14](https://github.com/ok3mad/roslyn/issues/14) (locals),
[#15](https://github.com/ok3mad/roslyn/issues/15) (deconstruction) and [#18](https://github.com/ok3mad/roslyn/issues/18) (patterns); `val` lambda parameters are [#17](https://github.com/ok3mad/roslyn/issues/17).

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
  and writes to fields of a struct local (CS1656, CS1657, CS1654, all naming the 'val variable').
- A non-readonly member of a struct `val` (a method, getter, indexer, `GetEnumerator`, `Deconstruct` or event
  accessor) runs on a copy, so it cannot change the `val`, and warns (CS20003). Readonly members, readonly structs and
  `ref val` are unaffected.
- `ref val` and `scoped val` are allowed: the local cannot be reassigned, but writes through the ref still work.
  Pointing a `ref val` at another variable is CS20006.
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

Every one of these locals cannot be reassigned, and the error names the 'val variable', also for `foreach` and
`using` variables.

## Scripts

In a script, top-level declarations are fields, and `var` there declares a field whose type is inferred. `val` is the
same plus read-only: `val x = e;`, and `out val`, `val (a, b)` and `val` pattern variables in top-level statements,
cannot be reassigned after their declaration, and non-readonly struct members run on a copy, as for locals.

## Accepted breaks

`val` is a keyword in two places even where `val` already has a C# meaning, as `var` is:

- `val (a, b) = e` is a declaration, even where a ref-returning method named `val` makes it an assignment in C#.
- `val x` in a pattern is a `val` pattern. If a type named `val` is in scope, that is error CS20004 (the `val` form of
  CS8508, which a `var` pattern gets when a type named `var` exists), so the meaning never changes silently. With a
  type named `val`, `val (a, b) = e` reports CS20005.

Code that means the method or the type writes `@val`. The sharpener will do that rewrite once it exists. `is val`
without a designation is still a type test.

## Not covered yet

- **Noisy CS20003:** every non-readonly member of a struct `val` warns, even one that does not mutate. Inferring which
  members mutate is [#84](https://github.com/ok3mad/roslyn/issues/84); the warning's default severity until then is
  [#85](https://github.com/ok3mad/roslyn/issues/85).
- **Sharpener:** escaping `val` as `@val` for the accepted breaks is [#86](https://github.com/ok3mad/roslyn/issues/86).
- **IDE:** the editor does not treat `val` like `var` yet: classification, Quick Info, completion, hints and
  refactorings ([#87](https://github.com/ok3mad/roslyn/issues/87)). An analyzer that suggests `val` for locals that are
  never reassigned is [#89](https://github.com/ok3mad/roslyn/issues/89).
- **Public API:** nothing public tells a `val` local from a `var` local ([#88](https://github.com/ok3mad/roslyn/issues/88)).
- **Edit and Continue** has not been verified with `val` ([#90](https://github.com/ok3mad/roslyn/issues/90)).
- **Deeper immutability:** `val` makes the binding read-only, and a struct value with it, but not the object behind a
  reference. A type-level marker is an open question ([#92](https://github.com/ok3mad/roslyn/issues/92)).
- **Not started:** `readonly` locals ([#16](https://github.com/ok3mad/roslyn/issues/16)) and `val` lambda parameters
  ([#17](https://github.com/ok3mad/roslyn/issues/17)).
