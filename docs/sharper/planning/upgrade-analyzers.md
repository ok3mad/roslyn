# Upgrade analyzers and fixers

**Status:** Proposal. Nothing here is implemented yet.

Sharper has nine constructs on `main` that say something shorter or stricter than the C# they stand for. Today a
developer only finds out about them by reading these docs. This page proposes IDE analyzers and code fixes that find
C# code with a better Sharper equivalent and rewrite it, so that a `.cx` file (or a project with
`/features:sharper`) can be upgraded one lightbulb, or one Fix All, at a time.

This is the cleanup step of [sharpening](brainstorm.md#sharpening-converting-c-to-sharper): "Analyzers suggest
idiomatic changes and fixers apply them, each only when the meaning is provably preserved." The sharpener itself
(escaping `@val`, parenthesizing `F<a[..]>(x)`, [#66](https://github.com/ok3mad/roslyn/issues/66),
[#86](https://github.com/ok3mad/roslyn/issues/86), [#114](https://github.com/ok3mad/roslyn/issues/114)) is a
separate tool and is not covered here.

## Summary

| ID | Analyzer | Before | After | Default | Order |
| --- | --- | --- | --- | --- | --- |
| IDE20001 | Use `val` / `readonly` for a local that is never reassigned | `var total = xs.Sum();` | `val total = xs.Sum();` | Suggestion | 1 |
| IDE20002 | Simplify `readonly var` to `val` | `readonly var x = e;` | `val x = e;` | Suggestion | 1 |
| IDE20003 | Use the sequence type | `IEnumerable<int>` | `int[..]` | Suggestion | 2 |
| IDE20004 | Use the task type | `Task<User>`, `Task` | `User~`, `void~` | Suggestion | 2 |
| IDE20005 | Use a function type | `Func<int, string>`, `Action<int>` | `(int) -> string`, `(int) -> void` | Suggestion | 2 |
| IDE20006 | Use spread in `yield return` | `foreach (var x in xs) yield return x;` | `yield return ..xs;` | Suggestion | 3 |
| IDE20007 | Use implicit `it` | `xs.Select(x => x.Name)` | `xs.Select(it.Name)` | Silent | 4 |
| IDE20008 | Use a `try` expression | `try { r = F(); } catch (E) { r = d; }` | `r = try F() catch { E => d };` | Silent | 5 |
| IDE20009 | Use an inferred return type | `private static string Greet(int id) => $"..."` | `private static infer Greet(int id) => $"..."` | Silent | 6 |
| IDE20010 | Use an inferred parameter type | `int retries = 3` | `infer retries = 3` | Silent | 6 |
| IDE20011 | Use a `readonly` parameter | `int F(int x)` (never reassigned) | `int F(readonly int x)` | None (opt-in) | 7 |

"Default" is the severity before any `.editorconfig` setting: *Suggestion* shows dots under the code and a
lightbulb, *Silent* shows only the lightbulb, *None* is off. "Order" is the proposed implementation order (see
[Rollout](#rollout)).

## Common rules

These apply to every analyzer on this page.

### Gating

- An analyzer reports only in a syntax tree whose parse options have the Sharper feature: a `.cx` document (the
  workspace gives its tree the feature, see [File extension](../README.md#file-extension)) or any document of a project
  with `<Features>sharper</Features>`. One helper decides it for all of them, for example
  `SharperFacts.IsSharperEnabled(SyntaxTree)`, which reads `CSharpParseOptions.Features`. With the flag off nothing is
  reported, and each analyzer has a test that says so.
- **C# wins** in the IDE too. Where the compiler would keep a C# meaning (a type named `val`, `infer` or `it` in
  scope, `..xs` that is a valid range), the analyzer does not report.

### Meaning is preserved, and the fixer checks it

Each analyzer is conservative and cheap. Each fixer then checks its own output before offering it, because the
Sharper parser and binder have many "C# wins" rules that are easy to get wrong in a syntax rewrite:

1. Rewrite the node and reparse the statement or member with the document's parse options. The new node must have the
   expected kind (`FunctionTypeSyntax`, a spread `YieldStatementSyntax`, a `TryExpressionSyntax`, ...), so that a
   `(A[..])(b)` that became an element access, or a `(p)->F` that stayed a pointer access, is caught.
2. Bind it speculatively (`SemanticModel.TryGetSpeculativeSemanticModel`) and compare with the original: the same
   type symbol for a type form, the same method for a call, no new diagnostics. For member signatures (`infer`), where
   speculative binding does not reach, fork the compilation with the new tree and compare the member's symbol.
3. If either check fails, offer nothing. A missed upgrade costs nothing; a changed meaning is a bug.

The checks are the fixer's job, not the analyzer's, so that typing stays fast. An analyzer may therefore report a
handful of spots that have no fix; each such case found in testing becomes a rule in the analyzer.

### Public signatures are flagged

Rewrites that change how a declaration is spelled but not its symbol (type forms, `readonly` parameters) are safe on
public members. Rewrites that make a public signature follow the body (`infer` on a return or parameter type) are
offered on private members and local functions only, unless an option turns them on for the rest: they tighten the
contract for callers, as the brainstorm warns.

### Fix All, trivia and formatting

- Every fixer supports Fix All in document, project and solution.
- Comments and whitespace around the rewritten node are kept.
- The formatter does not know `->`, `~` or `[..]` yet. Fixers build the new syntax with the spacing the feature pages
  use (`(int) -> int`, `int~`, `int[..]`), and the tests compare exact text, so a formatter that mangles the new nodes
  shows up as a test failure. Formatting rules for the new nodes are part of step 2.

### Where the code goes

- Analyzers in `src/Analyzers/CSharp/Analyzers/Sharper/`, fixers in `src/Analyzers/CSharp/CodeFixes/Sharper/` and
  tests in `src/Analyzers/CSharp/Tests/Sharper/`, one folder each, so that upstream merges never touch them.
- The `src/Analyzers` code is also built into the CodeStyle NuGet package
  (`src/CodeStyle/CSharp/Analyzers` imports `CSharpAnalyzers.projitems`). That package runs inside whatever compiler a
  project builds with, which has no Sharper syntax nodes, so the Sharper files are left out of it (`#if !CODE_STYLE`,
  or a condition in the `.projitems`) and ship with the IDE and the language server only.
- Diagnostic IDs: **IDE20001 and up**, in a Sharper block at the end of `IDEDiagnosticIds.cs`. Upstream IDE IDs are
  below IDE3100, and the same reasoning as for `CS20001` (see [Diagnostics](../README.md#diagnostics)) leaves them
  decades of room. Five-digit IDs work in `.editorconfig`.
- Options are `.editorconfig` code style options named `sharper_style_*`, with the usual `true:suggestion` form,
  stored next to the C# ones in `CSharpCodeStyleOptions`.
- Tests use `VerifyCSharp.VerifyAnalyzerAsync`/`VerifyCodeFixAsync` with the Sharper parse options, plus one test per
  analyzer with the flag off and one with a `.cx` document in a project without the flag.

---

## IDE20001: Use `val` or `readonly` for a local that is never reassigned

Tracked in [#89](https://github.com/ok3mad/roslyn/issues/89). The brainstorm's
[mutability grid](brainstorm.md#binding-mutability) nudges immutability with exactly this analyzer, instead of a
default that changes the meaning of `var`.

```csharp
// Before                                         // After
var total = items.Sum();                          val total = items.Sum();
var (min, max) = Range(xs);                       val (min, max) = Range(xs);
int.TryParse(s, out var n);                       int.TryParse(s, out val n);
if (o is Order { Lines: var lines }) ...          if (o is Order { Lines: val lines }) ...
using var stream = File.OpenRead(path);           using val stream = File.OpenRead(path);
int count = xs.Count;                             readonly int count = xs.Count;
ref var slot = ref span[i];                       ref val slot = ref span[i];
```

**Reported when** a local declared with `var`, or with an explicit type, is written only by its declaration. Every
place where `var` declares a local is covered: declarations, `using` declarations, `for` initializers, deconstruction
(all elements, or per element in `(var a, var b) = e`), `out var`, and `var` patterns. An explicitly typed local gets
`readonly T x`, which keeps the type; when the `csharp_style_var_*` preferences would turn it into `var` anyway, the
fix offers `val` too.

**Not reported:**

- Any write after the declaration: assignment, compound assignment, `++`/`--`, `ref` or `out` argument, `ref` local
  or `ref` return of it, `&x`, a write in a lambda or local function, and for a struct, a write to one of its fields.
  `DataFlowAnalysis.WrittenOutside` over the local's scope, plus `ref` reassignment for `ref var`, finds them.
- **Struct copies.** On a struct `val`, a non-readonly member runs on a copy (CS20003), so `var s = new Counter();
  s.Increment();` would silently stop counting. The analyzer skips a struct local that is not a `readonly struct` if
  any non-readonly member is used on it, also through a pattern: `foreach` (`GetEnumerator`), `await`, `Deconstruct`,
  indexers, Index and Range members and property patterns. The fixer's speculative check rejects any new CS20003.
- A type named `val` in scope (CS20004, CS20005), `foreach` variables (already read-only in C#; `foreach (val x in xs)`
  adds nothing), `const` locals and locals of a `fixed` statement.
- A declaration with several declarators (`int a = 1, b = 2;`) where only some are never reassigned: `val` allows one
  declarator, so the fix splits the statement only when all of them qualify.

**Options:** `sharper_style_prefer_val = true:suggestion` (for `var` locals) and
`sharper_style_prefer_readonly_local = true:suggestion` (for explicitly typed locals).

## IDE20002: Simplify `readonly var` to `val`

`readonly var x = e;` and `readonly val x = e;` are `val x = e;` ([`readonly` locals](../features/readonly-locals.md)).
A one-token simplification, reported as an unnecessary-code fade on `readonly var`. Not reported for
`readonly ref var` (no shorter form: `ref val` is the same) and when a type named `val` is in scope.

## IDE20003, IDE20004, IDE20005: Use the short type forms

The brainstorm says it directly: the short type forms are aliases, "the sharpener never introduces them, and an
analyzer with a fixer can suggest them." Each is its own diagnostic ID and option, so a team can take `T~` and keep
`Func<...>`, but all three share **one fixer** that rewrites a whole type at once. Nested forms are common
(`Task<IEnumerable<Order>>` is `Order[..]~`, `Func<int, Task<string>>` is `(int) -> string~`), and fixing nested
nodes one diagnostic at a time makes Fix All produce overlapping edits.

```csharp
// Before                                          // After
Task<IEnumerable<Order>> LoadAsync(int id);        Order[..]~ LoadAsync(int id);
Task SaveAsync(Order order);                       void~ SaveAsync(Order order);
IEnumerable<int?> Values { get; }                  int?[..] Values { get; }
Func<Order, bool> filter;                          (Order) -> bool filter;
Action<string> log = Console.WriteLine;            (string) -> void log = Console.WriteLine;
Func<int, Func<int, int>> curried;                 (int) -> (int) -> int curried;
Func<int, int>? maybe;                             ((int) -> int)? maybe;
List<Func<(int, int), int>> ops;                   List<((int, int)) -> int> ops;
```

**Reported when** a type syntax binds to exactly `System.Collections.Generic.IEnumerable<T>` (IDE20003),
`System.Threading.Tasks.Task<T>` or `Task` (IDE20004), or `System.Func<...>` or `System.Action<...>` (IDE20005), in
any type position: members, locals, parameters, generic arguments, casts, `typeof` of a constructed type, `is`/`as`,
patterns, `using` aliases, lambda parameter and return types.

**The rewrite** follows the stacking rules of each feature page:

- A suffix after a function type needs parentheses around it: `Func<int, int>?` is `((int) -> int)?`, and the same for
  `[]`, `[..]` and `~`.
- A tuple parameter of a function type needs its own parentheses: `((int, int)) -> int`.
- A nullable annotation stays where it was: `Task<string?>` is `string?~`, `Task<string>?` is `string~?`,
  `IEnumerable<string>?` is `string[..]?`.
- Arrays keep their order: `IEnumerable<int[]>` is `int[][..]`, `IEnumerable<int>[]` is `int[..][]`.

**Not reported** where the short form means something else or does not parse (each has a test):

- Unbound generic types (`typeof(IEnumerable<>)`, `typeof(Func<,>)`), the non-generic `IEnumerable`, `ValueTask`,
  `IAsyncEnumerable<T>` and custom delegate types. These have no short form, by design.
- Object and array creation: `new Task<int>(...)`, `new Func<int, int>(F)` and `new IEnumerable<int>[3]` (where
  `new int[..]` is CS0029, [#64](https://github.com/ok3mad/roslyn/issues/64)).
- A static member access on the type, such as `Task.FromResult(1)` or `Task<int>.Factory`: the type is an expression
  there.
- A cast of a parenthesized expression to a named sequence type: `(IEnumerable<A>)(b)` would become the element access
  `(A[..])(b)`. The fixer writes `(A[..])b` when it can drop the parentheses, and otherwise leaves the cast.
- The tuple rule of the task type: in a deconstruction declaration `(List<User>~ users, int count) = ...` does not
  declare, so `Task<List<User>>` stays spelled out there.
- `Func` and `Action` with `ref`-like or pointer arguments, or more than 16 parameters: they have no function type
  (CS20008, CS20009).
- Unsafe code where a function type could be read as a pointer member access, `(p)->F`. The reparse check catches it.

**Options:** `sharper_style_prefer_sequence_type`, `sharper_style_prefer_task_type` and
`sharper_style_prefer_function_type`, each `true:suggestion` by default.

## IDE20006: Use spread in `yield return`

```csharp
// Before                                          // After
foreach (var x in xs)                              yield return ..xs;
    yield return x;

foreach (var child in node.Children)               foreach (var child in node.Children)
{                                                      yield return ..Walk(child);
    foreach (var x in Walk(child))
        yield return x;
}
```

**Reported when** a `foreach` statement's body is a single `yield return v;` (with or without braces), `v` is the
iteration variable itself, and the variable is `var`, `val`, or an explicit type that the element type converts to by
identity or an implicit reference conversion. Nested loops reduce from the inside out, as above.

**Not reported:**

- `await foreach`: spreading an asynchronous source is CS20001, by design ([yield return
  spread](../features/yield-return-spread.md#limitations)). A synchronous `foreach` in an async iterator is fine.
- An explicit iteration type that needs an explicit conversion (`foreach (string s in objects)`): `foreach` casts, a
  spread does not.
- A deconstructing `foreach`, or a body that yields anything but the bare variable (`yield return x.Name;`), or does
  anything else.
- A source whose enumerator is a ref struct (spans, inline arrays, CS20012).
- A source where `..xs` is a valid range, so the spread would bind as a range: a `dynamic` source (CS20002), or a type
  with an implicit conversion to `System.Index`.

**Debugging note:** a breakpoint on the `foreach` line and the hidden sequence points of a spread differ; the fix is
still a suggestion, because stepping over a spread is what users want. Changing the statement during a debug session
is a rude edit for a suspended iterator ([#45](https://github.com/ok3mad/roslyn/issues/45)).

**Option:** `sharper_style_prefer_yield_spread = true:suggestion`.

## IDE20007: Use implicit `it`

```csharp
// Before                                          // After
users.Select(u => u.Name)                          users.Select(it.Name)
orders.Where(o => o.Total > limit)                 orders.Where(it.Total > limit)
xs.Select(x => Process(x, a))                      xs.Select(Process(it, a))
xs.Select(x => ys.Where(y => y > x))               xs.Select(ys.Where(y => y > it))
```

This one is a matter of taste more than the others: a good parameter name is documentation. It is **Silent** by
default, and the option has a middle setting for the most common, least controversial shape.

**Reported when** an argument of a method call, extension method call or delegate invocation is a lambda with:

- one parameter, implicitly typed and without modifiers or attributes (`x => ...` or `(x) => ...`), used at least
  once in the body;
- an expression body, not `async`, not `static`, and no explicit return type.

**Not reported:**

- The body is the bare parameter (`x => x`): a bare `it` argument is never a lambda.
- `it` already means something here: a local, parameter, field or type named `it`, or the `it` of an enclosing
  implicit lambda (that would be CS20020).
- The argument list declares a variable (`out var v`, `is T n`), CS20022 and [#125](https://github.com/ok3mad/roslyn/issues/125).
- The call is an object creation, indexer, constructor initializer or attribute
  ([#122](https://github.com/ok3mad/roslyn/issues/122)).
- The parameter is used inside a nested call whose own parameter at that position takes a one-parameter delegate,
  because `it` would then belong to that inner call: `xs.Select(x => ys.Where(F(x)))` must not become
  `xs.Select(ys.Where(F(it)))`. Explicit lambdas in between are fine, as in the fourth example: they never supply `it`.
- The method has an overload that takes a value at that position, so the rewrite would be CS20021 or would pick
  another overload. The fixer's speculative check compares the chosen method and that each `it` binds to the new
  implicit lambda's parameter.

**Option:** `sharper_style_prefer_implicit_it = member_access:silent | always:silent | never`. `member_access` reports
only bodies of the form `x.Member` or `x.Member(args)` with no other use of `x`. The reverse refactoring,
"name the parameter", is part of [#128](https://github.com/ok3mad/roslyn/issues/128).

## IDE20008: Use a `try` expression

The pattern this replaces is common, and the statement form hides the value in assignments spread over several
blocks:

```csharp
// Before
int port;
try { port = int.Parse(text); }
catch (FormatException) { port = 80; }
catch (OverflowException e) when (text.StartsWith('-')) { port = 0; }

// After
int port = try int.Parse(text) catch
{
    FormatException => 80,
    OverflowException e when text.StartsWith('-') => 0,
};
```

```csharp
// Before                                          // After
try { return await LoadAsync(id); }                return try await LoadAsync(id) catch
catch (HttpRequestException) { return null; }      {
                                                       HttpRequestException => null,
                                                   };
```

**Reported when** a try statement has no `finally`, its `try` block is a single statement, and each `catch` block is a
single statement of the same form:

- all `return e;`, which becomes `return try ... catch { ... };`; or
- all assignments `r = e;` to the same local, which becomes `r = try ... catch { ... };`, folded into `r`'s
  declaration when it is declared just before the try statement and not used in between.

Each `catch (E e) when (c)` becomes the arm `E e when c => value`; a catch without a variable becomes `E => value`;
`catch (Exception)` and a bare `catch` become `_ =>` (see below).

**Not reported:**

- A catch block that does anything else, such as logging, or that rethrows (`throw;`): an arm is an expression, and
  there is no rethrow arm. `throw new X(e);` is fine; it becomes the throw expression `=> throw new X(e)`.
- A body whose type is task-like without `await`: the try expression warns (CS20030), and a statement-to-expression fix
  should not add a warning.
- A `when` clause that has side effects other than calls the compiler cannot see through. The arms run in one filter,
  so the order of `when` evaluations can differ from the statement's. Conservatively, a `when` clause must be a
  comparison, pattern or property access; other calls make the analyzer skip.
- A bare `catch` in an assembly with `[RuntimeCompatibility(WrapNonExceptionThrows = false)]`, where it also catches
  objects that are not exceptions.
- A `when` clause in an enclosing catch filter (CS20032) or an expression tree (CS20031).

**Default severity is Silent** until debugging catches up: the arms have no sequence points yet, so a breakpoint in a
catch block would be lost ([#145](https://github.com/ok3mad/roslyn/issues/145)). Raise it to Suggestion when that is
fixed. **Option:** `sharper_style_prefer_try_expression`.

## IDE20009: Use an inferred return type

```csharp
// Before                                          // After
private static string Greeting(int id)             private static infer Greeting(int id)
    => $"Hello, {id}!";                                => $"Hello, {id}!";

int Twice(int x) => x * 2;                         infer Twice(int x) => x * 2;   // local function
```

**Reported when** the return type that [`infer`](../features/inferred-return-type.md) would give is the written
return type exactly: the same type, nullable annotations and tuple element names. The analyzer approximates it
cheaply: the type and flow state of an expression body, or of every `return` value of a block body when they are all
the same type. The fixer confirms it by compiling the member with `infer` (forked compilation) and comparing the
symbol.

**Not reported:**

- The inferred type differs: `IEnumerable<int> F() => new List<int>()` (infers `List<int>`), `string? F(string s) => s`
  (infers `string`), `IEnumerator<T>` iterators (infer `IEnumerable<T>`), `async ValueTask<T>` (infers `Task<T>`).
- `void` methods: `infer` saves nothing there.
- Members without a body (abstract, `extern`, interface members without a body, the defining declaration of a partial
  method), recursion (CS20018) and cycles (CS20033).
- Overrides and implementations: once step P2 is merged they copy `infer` from the member they override, and a
  separate analyzer can suggest that form.
- Public, protected and internal members, unless `sharper_style_prefer_inferred_return_type` is set to
  `all_members`: an inferred public signature changes when its body does (see
  [Public signatures are flagged](#public-signatures-are-flagged)).

**Wait for R5.** Removing a written type is a readability loss until Quick Info and inlay hints show the inferred
one ([#71](https://github.com/ok3mad/roslyn/issues/71)). This analyzer and the next ship after R5, with R5's reverse
fixer ("write the return type") next to it.

**Option:** `sharper_style_prefer_inferred_return_type = private_members:silent | all_members:silent | never`.

## IDE20010: Use an inferred parameter type

```csharp
// Before                                          // After
string Fetch(string url, int retries = 3,          string Fetch(string url, infer retries = 3,
             DayOfWeek day = DayOfWeek.Monday)                  infer day = DayOfWeek.Monday)
```

**Reported when** the type of the default value is the parameter type exactly
([inferred parameter type](../features/inferred-parameter-type.md), step P1).

**Not reported:** a default value of another type (`long x = 3`, `double d = 1`), `null`, `default` or `new()`
(nothing to infer from, CS20026), a `string?` with a non-null default (infers `string`), caller information attributes,
and public signatures unless the option allows them. It waits for R5 like IDE20009.

**Option:** `sharper_style_prefer_inferred_parameter_type`, same values as IDE20009.

## IDE20011: Use a `readonly` parameter

```csharp
// Before                                          // After
int Clamp(int value, int max)                      int Clamp(readonly int value, readonly int max)
    => Math.Min(value, max);                           => Math.Min(value, max);
```

**Reported when** a by-value parameter of a member with a body is never written, under the same rules as IDE20001
(struct copies included, CS20013). `readonly` is [not part of the signature](../features/readonly-parameters.md#not-part-of-the-signature),
so it is safe on public members, overrides and implementations.

**Off by default.** Marking every parameter is noisy, and immutable-by-default parameters are a
[strictness ladder](brainstorm.md#binding-mutability) setting for later, not syntax. This analyzer is for teams that
want the marker today; the ladder would replace it. Not reported for lambdas (`(val x) =>` is IDE20001's business if
ever), abstract members, extension block receivers and function pointer parameters.

**Option:** `sharper_style_prefer_readonly_parameter = false`.

---

## Not proposed

Sharper equivalents that are not upgrades, or not yet:

- `foreach (var x in xs)` to `foreach (val x in xs)`: a `foreach` variable is already read-only.
- `ValueTask<T>`, `IAsyncEnumerable<T>`, the non-generic `IEnumerable` and custom delegates: no short form, on purpose.
- Explicit lambda parameter types to `(val x) =>`: the fix would drop a type someone chose to write.

## Later, with features not yet on `main`

- **Pipe** (`sharper/pipe`, [#159](https://github.com/ok3mad/roslyn/issues/159)): `F(G(x))` to `x |> G |> F`, and
  `xs.Where(...).Select(...)` are already readable, so only deep nesting of free functions qualifies.
- **`infer` from the overridden member** (step P2): an override or implementation that repeats the base signature to
  `public infer Load(infer userId)`.
- **Immutable types** (slice 1, [#139](https://github.com/ok3mad/roslyn/issues/139)): "Add `[Immutable]`" on a type the
  compiler can already prove immutable, and "Make the type `sealed`" for CS20035 on a non-sealed class.
- **Lambda to single-method interface** ([#29](https://github.com/ok3mad/roslyn/issues/29)): replace a one-off class
  that implements a single-method interface with a lambda at its only use.

## Fixers for Sharper's own diagnostics

Not upgrades, but the same infrastructure, and cheap next to it:

| Diagnostic | Fix |
| --- | --- |
| CS20034 (`val x => x`) | Add the parentheses: `(val x) => x`. |
| CS20030 (try expression over a task) | Insert `await` after `try`. |
| CS20011 (`readonly` local without initializer) | Remove `readonly`. |
| CS20003, CS20010, CS20013 (member runs on a copy) | Change `val`/`readonly` back to `var` or the type, or remove `readonly` from the parameter. |
| CS20014, CS20024, CS20026, CS20027 (nothing to infer) | Write the type (the type the error message lists, or the best guess). Built for CS20027 and CS20057 in step R5, without the upgrade infrastructure ([IDE](../features/inferred-return-type.md#ide-step-r5)); the others are [#201](https://github.com/ok3mad/roslyn/issues/201). |
| CS20019 (`it` with no lambda) | Turn the enclosing argument into `x => ...` where one call is close. |
| CS20004, CS20005 (`val` refers to a type) | Escape it as `@val`. |

## Rollout

Each step is one PR with its tests, and gets an issue under the tracking issue
[#12](https://github.com/ok3mad/roslyn/issues/12).

1. **Infrastructure and `val`** (IDE20001, IDE20002): the gating helper, the ID block, the option plumbing, the
   CodeStyle exclusion, the reparse-and-bind check, and the analyzer that the brainstorm asks for first
   ([#89](https://github.com/ok3mad/roslyn/issues/89)).
2. **Type forms** (IDE20003 to IDE20005): one shared fixer, plus formatter rules for `->`, `~` and `[..]`. The largest
   win in signatures and the safest rewrite, since the symbol never changes.
3. **Yield spread** (IDE20006).
4. **Implicit `it`** (IDE20007), with the "name the parameter" refactoring from
   [#128](https://github.com/ok3mad/roslyn/issues/128).
5. **`try` expression** (IDE20008), Silent until [#145](https://github.com/ok3mad/roslyn/issues/145).
6. **`infer`** (IDE20009, IDE20010), after R5 ([#71](https://github.com/ok3mad/roslyn/issues/71)).
7. **`readonly` parameters** (IDE20011), opt-in.

The fixers for Sharper's own diagnostics can be picked up at any point after step 1.

## Testing

- For each analyzer: positive cases from the examples above, each "Not reported" bullet as a negative test, the flag
  off, a `.cx` document, Fix All in a document, and trivia.
- A corpus run, like [`sharper-self-build`](../../../.github/skills/sharper-self-build/SKILL.md): apply every fix to a
  large C# codebase (Roslyn itself) compiled with `/features:sharper`, then check that it still compiles without new
  diagnostics, and that the emitted IL is unchanged for the rewrites that are pure spelling (type forms, `val`,
  `readonly`, `infer`). Any difference is a missing "Not reported" rule. The spread, `it` and `try` rewrites lower
  to different but equivalent IL, so for them the run checks compilation and the unit tests.
