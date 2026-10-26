# Strict C# Dialect for Coding Agents: Initial Brainstorm

> [!NOTE]
> This is the language design draft, maintained here; it started as the "Strict C# Dialect for Coding Agents:
> Brainstorm" planning doc. The syntax in it is a working guess, not a specification. How the ideas are being built
> is tracked in the [implementation plan](implementation-plan.md).


## Idea

A new language that feels like C# but adds stricter compile-time checking and functional-style features. The main motivation is coding agents: a stricter compiler gives an agent fast, precise feedback and rules out whole classes of mistakes before anything runs.

## Approach: Lowering

The C# compiler already uses lowering, where newer constructs are desugared into simpler, pre-existing C# constructs. The same idea applies here: any new syntax in this language lowers to plain C# and Roslyn handles code generation. This keeps the implementation straightforward in compiler terms (not necessarily easy), and the .NET ecosystem and tooling come along for free.

**Caveat:** Once lowered to plain C#, the guarantees are gone. The strictness has to be enforced by the language's own checker before lowering, and error messages need to point back at the original source.

## Where C# Stands Today

- C# already has pattern matching, records and switch expressions.
- Discriminated unions are available in the C# 15 preview.
- Still missing: deep immutability, stricter type checking, and tracked purity or effects.

## Mental Model: Nullable Reference Types

Nullable reference types are effectively a second type system layered on top of the CLR. The runtime sees one type, while the compiler tracks an annotation plus flow analysis, and emits attributes so other assemblies can see it. Immutability could work the same way: a compile-time-only layer, erased on lowering, with metadata attributes so guarantees carry across assembly boundaries.

## Initial Scope (Phase 1)

- More flexible and friendly type inference.
- Explicit support for immutability.

## Deferred (Later Phases)

Purity and effect tracking, and stricter type checking in general. These are where the real value is, but they are a lot of work, so they are not the starting point.

## Design Principles

- It should feel like C# at heart, and C# is a pragmatic language.
- Strictness is opt-in with escape hatches, so ordinary mutable code keeps working.
- Possible adoption path, similar to nullable reference types: warnings first, then errors once a project is ready.

## Name

The working name is **Sharper** (the language) or **SharperLang** (for public naming); branding is deferred.

- Closest existing names: [SharpLang](https://github.com/xen2/SharpLang) (an archived .NET-to-LLVM compiler front-end), [Sharpy](https://www.nuget.org/packages/SharpyLang.Core) (an active language with NuGet packages), and the NuGet package [Sharper](https://www.nuget.org/packages/Sharper) (toolkit extensions).
- No project called SharperLang turned up. GitHub org, domain and NuGet prefix availability are not checked yet, and the findings come from search results only.

## Syntax so far

Sharper should read like C# with less ceremony; the sketches below are guesses to react to, not a spec, and the syntax is far from consistent: for example the examples spell inferred union types as A | B, which was never discussed (patterns use or, and C# 15 declares union Name(A, B)). Everything else is plain C#, so existing code stays valid.

- **Functions are ordinary methods.** What the earlier `fun` declaration bundled is split into separate features that each work on any method, local function or lambda (see [Functions as separate elements](#functions-as-separate-elements)): `infer` in place of a return or parameter type, `readonly` parameters, and a parameter switch body `=> switch { ... }` with ordered arms. `fun` itself is parked for a later "functional all in" mode.
- **`val`** is an immutable binding, used in declarations and patterns: `val (small, large) = ...`.
- **`it`** is the implicit parameter of any single-parameter lambda or pipe stage, as in Kotlin. Innermost wins, and an explicit parameter is the escape hatch.
- **`|>`** is the pipe. A stage that expects `T` can receive a `Task<T>`, a sequence of `T`, or a union containing `T`, and the pipe lifts it. A stage containing `it` is an expression; a bare stage is a function applied to the value.
- **`try expr catch { ... }`** turns exceptions into union values, with switch-style arms. Unmatched exceptions propagate, and there is no `finally`.
- **Refined members** such as `record Money(decimal Amount is >0, Currency Currency)`, so a value can only be built through a door that proves the refinement.

## Binding mutability

A binding has two independent axes: whether it can be reassigned, and whether its type is explicit or inferred. This is binding immutability only; whether the value itself is immutable is the separate type-level marker. The explicit corners will rarely be written, but they keep every combination spellable and keep "immutable" and "inferred" apart, so `val x` is just shorthand for `readonly var x`.

Locals and parameters, mutable by default as in C#:

|  | explicit type | inferred |
| --- | --- | --- |
| mutable | `int x` | `var x` for locals, `x` for lambda parameters |
| immutable | `readonly int x` | `readonly var x` or `val x` for locals, `(val x) => ...` for lambda parameters |

Method parameters have no inferred corner, because a method's signature must be known before its body is bound (overload resolution, metadata, callers in other assemblies). Lambda parameters are inferred from the target type, and C# 14 already allows modifiers on implicitly typed lambda parameters, so `val` fits there. `readonly int x` works on method, local function and lambda parameters alike.

- **`var` stays on the inference axis,** so it cannot double as the mutable marker without tangling the grid again. An inferred return or parameter type is spelled `infer`, not `var`, because neither is a variable (see [Functions as separate elements](#functions-as-separate-elements)).
- **No immutable-by-default declaration form.** An earlier draft made the parameters of a `fun` immutable by default, with `mutable int x` as the opt-out. That tied a default to a declaration keyword, so moving code between a method and a `fun` changed its meaning. Defaults now belong to the strictness ladder only: a strict file or project could report a parameter or local that is reassigned without being marked, and only then would a `mutable` marker be needed (`mutable` versus `mut` is open). Until then, `readonly` is opt-in and breaks nothing.
- **One meaning of `readonly`.** For a by-value binding it means "cannot be reassigned", and for `ref readonly` it means "cannot write through the ref". Both are "cannot assign", and the spec should say so explicitly.
- **The same statement means the same thing everywhere.** `var total = 0; total += x;` and `for (var i = 0; ...; i++)` behave as in C# in every declaration. Immutability is nudged instead: an analyzer flags a `var` local that is never reassigned and a fixer turns it into `val`, and the strictness ladder can raise that to an error. Purity inference does not depend on it, since flow analysis already sees a `var` that is never reassigned.

## Functions as separate elements

An earlier draft had a `fun` declaration that did everything at once. It declared a method, inferred the return
type, made parameters immutable by default (with `mutable` to opt out), took a body of ordered arms, inferred
parameter types from those arms, marked the scope where the strictness ladder applies, carried the `pure` and
`total` modifiers, could be declared on another type (`static fun Currency.Parse`), and was the syntax of the
sidecar bindings. Its modifiers were never written, so it was not even clear whether a `fun` was `static` or
`public`. The block of arms and a statement block looked the same (`fun Fib(i) { 0 or 1 => 1, ... }` versus
`fun Validate(Quoted q) { val ...; return ...; }`), so the parser had to guess from the contents.

That is too much for one keyword: no part of it could ship, be tested or be argued about alone. So each idea is a
separate feature on ordinary methods, local functions and, where it fits, lambdas. Modifiers, accessibility and
`static` stay exactly as in C#. The last column is the row in the
[syntax roadmap](implementation-plan.md#syntax-roadmap).

| Element | Example | Lowers to | Depends on | Roadmap |
| --- | --- | --- | --- | --- |
| Inferred return type | `static infer Hello(int id) => $"Hello, {id}!";` | method with the inferred type | nothing | #10 |
| Inferred parameter types | `public infer Load(infer userId) => ...` in a class implementing `IProfiles` | method with the inferred types | depends on the source, see below | #15 |
| `readonly` parameters | `void F(readonly int x)` | plain parameter plus the `val` no-reassignment check | nothing | #11 |
| Parameter switch body | `int Sign(int i) => switch { <0 => -1, 0 => 0, >0 => 1 };` | `i switch { ... }`, or `(a, b) switch { ... }` for several parameters | nothing | #14 |
| Switch body on a lambda | `id => switch { 42 => new Profile(), _ => new Unavailable() }` | lambda with a switch expression | the parameter switch body | #26 |
| `pure` and `total` | `total int Fib(int i is >=0) => ...` | erased, plus attributes | the effect checker | #24 |
| Member declared on another type | `static infer Currency.Parse(string s) => ...` | C# 14 extension block | nothing | #25 |

The work breakdown for the two inference rows is in the
[implementation plan](implementation-plan.md#type-inference-work-breakdown).

### `infer`: the type is inferred

`infer` stands where a type name would be, in a return type or a parameter type, and says that the compiler
finds the type: `static infer Hello(int id) => ...`, `infer Fetch(infer url, infer timeout = TimeSpan.FromSeconds(5))`.

- **Why not `var`.** `var` means variable. A return type is not a variable, and reusing the word only because it
  already means "inferred" for locals reads badly. `infer` says what happens, wherever it stands.
- **Why not `_`.** A discard means "I do not care about this", but an inferred type matters as much as a written
  one.
- **Why not leaving the type out.** `static Hello(int id) => ...` looks like a constructor, and as a local function
  `Hello(int id) => x;` looks like a call.
- **No breaking change.** `infer` is a contextual keyword resolved like `var`: a type named `infer` in scope wins,
  and with the flag off it is an unknown type (CS0246).
- **Combines with `readonly`:** `readonly infer x` is a parameter whose type is inferred and that cannot be
  reassigned. Whether `val x` should be accepted as its shorthand on method parameters, as it is on lambda
  parameters, is open.

### Inferred return type

The return type is inferred from the body: from the expression of an expression body, and from the `return`
statements of a block body.

- **Several `return` statements** use C#'s best common type, as for an implicitly typed array or a lambda. No
  return value gives `void`. An iterator gives `IEnumerable<T>` and an `async` method `Task<T>`, as for lambdas.
  An inferred union of the returned types waits for unions (#19).
- **Overrides and interface implementations** take the return type of the member they override or implement, not
  their body (parameter source 1 below).
- **Ordering and cycles.** Return types are part of signatures, so binding one body can require binding another
  body first. A method that needs its own inferred return type, directly or through other methods, is an error and
  must write its return type, as in F#. A recursive method with a non-recursive `return` to start from could be
  allowed later.
- **Any member, without a nudge.** Public members infer like any other, with no warning, even though their
  signature then follows the body. Inferring a type is the author's choice about their API: whoever cares about a
  published API writes its types out, and most code (anything but a published library) does not need to care. The
  existing public API analyzers (`PublicAPI.Shipped.txt`) still catch an accidental change.

### Inferred parameter types

A parameter type is never guessed from the shape of its uses. It is copied from a type that a signature or a value
already names, in this order, and two sources that disagree are an error that names both:

| # | Source | Example | Result |
| --- | --- | --- | --- |
| 1 | The member that is overridden or implemented | `public infer Load(infer userId) => result;` in a class implementing `IProfiles` | `int`, and the return type too. Nothing is really inferred, only copied |
| 2 | The default value | `infer timeout = TimeSpan.FromSeconds(5)` | `TimeSpan`, as `var` infers a local. `null` and `default` give nothing |
| 3 | A function type the method is converted to | `xs.Select(Format)`, `(int) -> string f = Format;` | the delegate's parameter types, as for a lambda. Local functions and private members only, where every conversion is visible |
| 4 | Uses in the body that pin one type | `infer Hello(infer id) => users.Find(id);` | `int`, when every candidate of `Find` takes `int` at that position. Member access (`id.Length`) and operators pin nothing |
| 5 | Nothing pins it | `infer Pair(infer a, infer b) => (a, b);` | open: a generic type parameter (`(T1, T2) Pair<T1, T2>(T1 a, T2 b)`), or an error |

- **Each member alone.** Every source is in the member's own signature or body, or in a signature that is already
  known. A member's types never depend on its callers, so editing a caller never changes a callee.
- **Any member, without a nudge,** as for return types: source 4 works in a public method just as well, for
  example `public infer Hello(infer id) => profiles.Load(id);`.
- **Source 3 is limited for another reason.** A conversion of the method to a function type is a use somewhere
  else, so beyond local functions and private members it would be inference from callers, which is dropped.
- **Source 4 is the valuable one and the expensive one.** Real code learns that `id` is an `int` from passing it to
  `Find(int)`. But Roslyn binds a body only after it knows the parameter types, so this needs binding with a
  placeholder type and collecting what overload resolution requires of it. It is built in small steps, starting
  with a call that has exactly one candidate.
- **Doubts about source 5.** A generic method is what C++ makes of `auto f(auto x)`, and it is sound on the CLR,
  because generics are real at runtime. But a method silently becomes generic, its type parameters appear in
  metadata and in stack traces, and a later use that pins the type changes its arity. The alternative is an error
  that asks for the type. Undecided.

### Parameter switch body

The ordered arms become an expression body: a switch expression with no governing expression, whose subject is the
parameter list (one parameter alone, several as a tuple). The first matching arm wins, exactly as in C#.

```
static Outcome Apply(State state, Event ev) => switch
{
    (Draft s, Submit e) => ...,
    (State s, Cancel e) => ...
};
```

- **It is visibly a switch**, so a C# reader knows the semantics at once, and it ends with `;` like any expression
  body. A statement block never looks like a block of arms, so the parser does not have to guess.
- **No breaking change.** `switch` at the start of an expression is an error in C# today.
- **Parameter types stay explicit,** so the arms are checked against them, and exhaustiveness is C#'s (CS8509,
  which the strictness ladder may raise to an error). A Sharper error that lists every missing case is a separate
  improvement to the exhaustiveness check, not part of this syntax.
- **Arms are not a source of parameter types.** The earlier draft inferred a parameter's type from the arm
  patterns, but that mixed two questions: the CLR type and the refined domain (`int is >=0`). The CLR type comes
  from the sources above; inferring a domain from the arms belongs to refined types (#21), if anywhere, and has its
  own trap (a forgotten arm narrows the domain instead of reporting an error).
- **The same form works on lambdas,** which settles arms as a lambda body: `id => switch { 42 => ..., _ => ... }`.
- **`val i` in an arm** is a binding pattern, since arms only hold patterns. That answers the open question about
  `val i` in a clause.
- **Which parameters are switched on.** All of them, in order. A function that matches on only some of its
  parameters writes an ordinary `(a, b) switch { ... }` body. Whether an attribute or modifier could exclude a
  parameter is open.

### What happened to the rest

- **Immutable-by-default parameters** and `mutable`: a strictness ladder setting, not a declaration form (see
  [Binding mutability](#binding-mutability)).
- **The strict scope:** the ladder applies per file or project (`.cx` files), never per declaration.
- **`pure` and `total`** are modifiers on any method, and **sidecar bindings** get their own declaration syntax,
  not designed yet.
- **Member declared on another type** keeps its own row. `static infer Currency.Parse(...)` reads like an explicit
  interface implementation, which C# spells the same way, so the spelling needs another look.

### `fun`, parked

`fun` is free now, and it is too good a keyword to spend on one small feature. It is kept for later as the way to
go functional all in: a declaration that opts into the functional defaults together, such as immutable parameters,
purity and totality checks and ordered arms. What exactly "all in" means is decided only after those pieces exist
and have settled, and it has to stay a combination of them, never a meaning of its own. Until then `fun` has no
meaning in Sharper, and code that uses it as a name keeps working.

## Type syntax

Inference keeps most types out of sight, but the wrapper types show up in public signatures constantly, so they get short forms. These are aliases for the BCL types, so lowering is trivial, the sharpener never introduces them, and an analyzer with a fixer can suggest them.

- **`T[..]`** is `IEnumerable<T>`: a lazy, unsized sequence, next to `T[]`, a real array.
- **`T~`** (the tilde) is `Task<T>`, and `void~` is the non-generic `Task`. `T~` is a running task (hot), not a lazy value. Tried on real signatures (#20): in six open-source applications (eShop, eShopOnWeb, CleanArchitecture, bitwarden, nopCommerce, jellyfin) it covers 99.6% of the async declarations. The common results (`IActionResult~`, `bool~`, `Order?~`, `IList<Order>~`) read well. `void~` is a third of them and is the weakest form, but it keeps a file from mixing `Task` with the short forms.
- **Why not the hat.** `T^` was the earlier candidate. `^` is already XOR and index-from-end, and XOR is valid C# in tuples (`(a ^ b, c)`), after `is` and `as`, so the hat could not be used there. `~` has no binary or postfix meaning in C#, so every `T~` is a syntax error today and the suffix works in every type position. Its other meanings are weak for C# readers: Haskell lazy patterns (hence the "hot, not lazy" note above) and pre-1.0 Rust owned pointers. `async T` breaks the reading flow, and trailing dots are too close to `T[..]`. `$`, `#`, `` ` ``, `@`, `'`, `!`, `%` and `&` were checked and ruled out.
- **`(A, B) -> R`** is `Func<A, B, R>`, and `() -> T` is a thunk. `=>` stays reserved for bodies, lambdas included: `->` is only a type arrow, because `x -> F(x)` is already a pointer member access in C#.
- **Stacking** reads left to right, innermost first: `int[..]~` is `Task<IEnumerable<int>>`, `int~[..]` is `IEnumerable<Task<int>>`, `User?~` is `Task<User?>`, `User~?` is a nullable `Task<User>`, and `(int) -> int~` is `Func<int, Task<int>>`.
- **Parsing:** a postfix `~` after a type, next to `?`, `*` and `[]`. Unlike `T* x`, which C# already reads as a declaration because the multiplication can never be a statement, `~` has no binary meaning at all, so there is nothing to disambiguate.
- **Not sugared:** `ValueTask<T>` stays spelled out. It is 0.4% of the async declarations in the six applications above, mostly where an interface imposes it (`DisposeAsync`, EF Core interceptors); it is common only in performance-sensitive libraries such as Roslyn (22%). `IAsyncEnumerable<T>` is avoided in practice. A stream is inferred in a pipeline when needed, and async streams are not a focus.

## Sequences and yield

Sequences (`T[..]`) get two additions: spreading a whole sequence into an iterator, and a rule for when a sequence expression may be lazy.

- **`yield return ..xs`** yields every element of a sequence, like `yield from` in Python or `yieldAll` in Kotlin. The spread marker is needed because a plain `yield return xs` is ambiguous when the element type is itself a sequence. It lowers to `foreach (var x in xs) yield return x;`.
- **Recursion cost:** with that lowering a recursive tree walk stays O(depth) per element. A delegating state machine could remove that later, and it is a lowering detail, not a language one.
- **Eager by semantics.** A collection expression typed `T[..]`, such as `[..Walk(left), node, ..Walk(right)]`, behaves as if it were built eagerly. The compiler may make it lazy when every spread source and element is total, because then the difference cannot be observed. Pure alone is not enough, since a lazy version would move a throw to enumeration time or drop it.
- **Iterators are lazy by semantics.** Infinite sequences are not total, so they are written as explicit iterators with `yield return`. Expressions are eager by semantics and iterators lazy by semantics, and the optimizer bridges the gap.
- **No promise either way.** `T[..]` does not promise laziness or eagerness, so consumers cannot depend on it, which is sound because only total expressions may be made lazy.
- **Lazify only when it pays,** for example a single consumer that does not materialize everything, such as a `Take(5)` pipeline. If the result is enumerated twice, a lazy version recomputes unless it memoizes.

## Interfaces and functions

Idiomatic C# passes interfaces where a functional style would pass functions, partly because spelling out function types is painful without inference. Sharper converts between the two, borrowing Java's single-method interface conversion.

- **Lambda to interface.** `Render(user, id => new Profile { Plan = "Pro" })` works where `IProfiles` is expected. It lowers to a generated sealed class implementing the interface. Eligible interfaces have exactly one abstract method, default members do not count, and generic methods are excluded.
- **Purely additive.** The conversion applies only where C# currently reports an error, so a lambda with a delegate overload still binds to the delegate. This keeps the no-semantic-breaking-changes rule, and the conversion never appears in sharpened code.
- **Details.** Return types lift (a plain `Profile` where `(Profile | Unavailable)~` is expected), effects are checked against a `pure` contract on the interface method, captured variables become fields, non-capturing lambdas can be cached as singletons, and the generated class gets a readable name for stack traces.
- **Reverse direction.** A single-method interface converts to the matching function type, and for a multi-method interface you name the method group, as in `profiles.Load`, which C# already converts to a delegate. With `Render` taking `(int) -> (Profile | Unavailable)~`, the call site becomes `users.Find(id) |> Render(it, profiles.Load)`, with no thunk.
- **Initializer shape.** `new IProfiles { Load = id => ... }` lowers to the same kind of generated class and covers most stubbing, which makes most mocking libraries unnecessary for that purpose. An unassigned member is a compile error unless the interface supplies a default, and a standard `Unexpected` marker (`_ = Unexpected`) gives the strict mode of mock libraries, throwing with the member name.
- **Argument matching.** The ordered-arms block from the state machine could serve as a lambda body, so `When(42).Returns(...)` becomes `{ 42 => new Profile { ... }, _ => new Unavailable() }`. The syntax for arms as a lambda body is not designed yet.
- **What it does not cover:** interaction verification (was `Load` called twice?), which works by capturing a list in the lambda, and auto-mocking of very large interfaces.

## Discard arguments (proposed, not yet discussed)

Tracked in [#40](https://github.com/ok3mad/roslyn/issues/40).

A `_` in argument position asks the compiler to fill in the argument from the caller's scope. The motivating case is
`CancellationToken`: most async code receives exactly one token and forwards it to every call, so the name is
repeated everywhere and is easy to forget.

```
async void~ Sync(Store store, CancellationToken ct)
{
    val items = await store.LoadAsync(_);       // ct is the only CancellationToken in scope
    await store.SaveAsync(items, _);
}
```

- **The rule, as a starting point.** For a `_` argument, collect the locals and parameters in scope whose type fits
  the parameter it binds to. Exactly one candidate means it is passed; none or several is an error that lists the
  candidates.
- **No breaking change.** Today `_` in an argument is either a real variable named `_` (then C# wins) or an error
  (CS0103), so Sharper only gives a meaning to code that does not compile.
- **Not to be confused** with the existing discards: `out _`, `var _`, `_ =` and `(_, _) =>` lambda parameters.

To discuss before this goes further:

- **Is it a sound idea?** It is an implicit argument, much like Scala's implicits, and implicit wiring tends to read
  well and debug badly: a new local of the same type turns working code into an error, or worse, changes which
  value is passed if the rule were ever relaxed. For coding agents, explicit names may be easier to check.
- **Which candidates count:** only locals and parameters, or also fields and properties of `this`? Exact type, or
  any implicit conversion? `CancellationToken` only, or any type?
- **Overload resolution.** A `_` argument has no type of its own, so each overload candidate may pick a different
  value. This makes the feature an L in the roadmap's terms, not an S.
- **Prior art in C#:** the CA2016 analyzer already asks to forward a `CancellationToken`, and an optional
  `CancellationToken ct = default` parameter compiles when it is left out, which is exactly the bug `_` would
  make visible.
- **Alternatives:** an analyzer and fixer that inserts the name; a marker on the parameter (for example an
  attribute) that opts it into automatic forwarding, so call sites stay unchanged; or nothing.

## Patterns on pure functions

C# patterns need compile-time constants, so `is <= max` fails when `max` is only readonly. Sharper could allow any pure expression in a pattern without growing the type system, by splitting patterns into two tiers.

- **Closed patterns,** which the checker understands: constants, relational, `and`/`or`/`not`, type and property patterns. Only these can appear in declared domains such as parameter types, because a type must be provable at call sites.
- **Opaque patterns:** any pure function or non-constant expression, for example `> 0 and <= max` with `val max = Config.MaxTransfer`. They are allowed in match arms only, and to the checker they are black-box predicates, like a `when` guard.
- **Lowering:** constant parts stay real patterns and each non-constant test moves into a guard (`var v when v > 0 && v <= max`). Because the expressions are pure, a call that is evaluated anyway can be shared and evaluated once per match, while hoisting one ahead of the arms needs a total function (see Purity levels).
- **Purity is required.** An impure call in a pattern is a compile error, since otherwise the result would depend on arm order. Pure is enough while opaque tests run as ordered guards; letting the decision DAG reorder, skip or merge them needs total.
- **Exhaustiveness is best effort.** With opaque arms the checker asks for a catch-all, as C# does with guards. Relational patterns against the same immutable `val` can still be reasoned about symbolically, which is another reason `val` matters.
- **Two flavors, as with F# active patterns:** a predicate (`x is IsEven`) is just a guard, and an extractor applies the function and matches its result with a sub-pattern (`s is Parse(val n and >0)`).
- **Not now:** refinements that depend on a runtime value, such as `int Page(int i is < pageCount)`. That is dependent-types territory, so declared domains stay with constants.
- **Performance:** constant patterns compile to jump tables while guards run in sequence, so hot paths should keep constants.

## Effects and lifting

The base case is a pure function, and every effect (async, sequences, deferred evaluation, errors, may-throw) is a wrapper around it: lifting adds an effect, and a handler removes it.

- **Lift arguments, not just returns.** Passing a `Task<T>`, a sequence of `T`, or a union containing `T` to a function that expects `T` returns the lifted result, with no `await`, `Select` or error check at the call site.
- **Lifting is one-directional and ordered.** Pure embeds into any effect; going back needs a handler (await, enumerate, `try`). `Task<IEnumerable<T>>`, `IEnumerable<Task<T>>` and `IAsyncEnumerable<T>` are different things, so the pipeline order defines the nesting. A small, fixed set of safe conversions (such as `Task<IEnumerable<T>>` to `IAsyncEnumerable<T>`) may be inferred; anything else needs a manual converter.
- **One argument at a time.** No multi-argument lifting, which avoids the zip versus cross-product ambiguity. Combining two sequences uses explicit library functions, and pipe plus `it` replaces partial application.
- **Errors are values.** Expected failures are union cases that pass through pipe stages untouched, so there is no `Result<T>` wrapper. Bugs (overflow, null, bad index, stack overflow) are not tracked in types. Calls into existing .NET code take their may-throw bit from the bindings (see below), so a call the bindings show cannot throw needs no handler, and only members the analysis cannot resolve default to may-throw. `try ... catch` at the boundary is the handler.
- **Purity is inferred, and so are the bindings.** A function that reads no ambient state and calls only pure functions is pure. Local bindings do not matter, whether `val` or var, as long as they do not escape the function. Calls into .NET take their purity from the bindings, which come from IL analysis plus a small hand-written overlay, so much of the BCL counts as pure (`Math.Max`, `string.Length`, `Select` with a pure lambda). Only unresolved members, such as native code, reflection and unknown virtual calls, default to impure. Lifting needs a pure function to preserve semantics, and a lazy lift over a sequence needs a total one: a lazy version moves a throw to enumeration time, repeats it on every enumeration, or drops it when the sequence is never enumerated, and the same goes for non-termination. A lift of a function that is only pure is lowered eagerly.
- **Why tracking purity pays, in one example.** `decimal.TryParse(s)` reads the current culture, so it is impure, and `"1,5"` parses as 1.5 or as 15 depending on the machine. The checker flags it, and a fixer passes `CultureInfo.InvariantCulture`. The invariant culture is an immutable singleton, so that overload counts as pure, but only conditionally (`pure(provider)`), because a custom `IFormatProvider` could do anything. `Try*` methods map to a union such as `T | NoValue` in the bindings.
- **Unions need a representation.** Inferred structural unions are the leading idea, with a named union required at public boundaries (like anonymous types and tuples). The alternative, erasing to `object` and keeping the types only in Sharper, works but is not ideal.
- **Later idea: virtual-thread feel.** Infer "may suspend" as an effect and lower to async state machines, so `async` and `await` are never written.

## Purity levels

There are three levels, each stronger than the one before.

- **Impure:** reads or writes ambient state such as the current culture, the clock, randomness, mutable fields or I/O.
- **Pure:** the result depends only on the arguments and nothing observable happens. It may still throw deterministically or not terminate.
- **Total:** pure, defined on its whole declared domain, and terminating.

What each level allows:

- **Pure** allows sharing evaluations that happen anyway, memoization, and use in match-arm patterns.
- **Total** adds reordering evaluations (when calls may throw, the order decides which exception surfaces), speculative evaluation and hoisting, dropping unused calls, parallel and lazy lifting that behaves like the eager version, and use inside refinements, since a predicate in a type must not throw.
- **Compile-time evaluation:** a total, pure function applied to constants is itself a constant, so `is <= Max()` can stay a closed, checker-understood pattern.
- **Not Turing-complete by design.** Only total functions run at compile time, so the compiler cannot hang on user code. Total functions can still be slow, so compile-time evaluation needs a budget.
- **Proving totality:** within Sharper a domain violation is a call-site error, not a runtime throw. Termination comes from structural recursion on a decreasing measure over a refined domain: `Fib` over `int is >=0` is bounded below and recurses on smaller values. There is no general termination prover, and the checker is conservative.
- **Inference and annotation:** `total` is inferred for non-recursive compositions of total functions whose arguments are proven to be in each callee's domain. The `total` modifier is a checked assertion, and a compile error if the checker cannot prove it.
- **Caveats:** untracked bugs (overflow, stack overflow, out of memory) and the boundary with C#.

## Bindings for existing CLR code

The pure core needs to call existing .NET code, so Sharper needs a way to know which CLR members are pure, may throw, or belong to immutable types. Without it every C# call is assumed impure.

- **Sidecar declarations,** inspired by TypeScript `.d.ts` files, written in Sharper syntax and compiled into a binding assembly. They carry purity, totality, may-throw, immutable types and refined return types, for example that `string.Length` is pure and returns `int is >=0`. Their declaration syntax is not designed yet.
- **IL analysis as the baseline,** possibly sufficient on its own. It can infer purity transitively, immutable types from their fields, and conditional purity from delegate parameters, so `Select` comes out as pure if its lambda is pure.
- **What IL cannot give:** members with no IL body (`Math.*`, intrinsics, P/Invokes) need a small hand-written seed list. Virtual calls are impure unless the type is sealed, reflection and native code are marked unknown, and refinements such as `Length is >=0` need a hand-written overlay. Local mutation that does not escape needs escape analysis, and the may-throw bit needs a policy on which exception types count as bugs.
- **Inference describes today's code, not intent,** so every effect fact carries one of three provenance levels:
  - **Inferred:** observed in this exact binary's IL.
  - **Attested:** a judgment, by an agent or a human, that the behavior is intended and stable, backed by evidence from outside the code (documentation, the spec, release notes, maintainer statements, version history). It records the evidence, the version range, who judged, and when.
  - **Declared:** the author's own contract, in an attribute or a Sharper signature.
- **How much each level can be relied on.** Hints and diagnostics accept any level. Behavior-preserving build optimizations accept inferred facts for a binary shipped with the app, but need attested facts for the BCL, since the runtime can roll forward past the version compiled against. Type-level reliance (refinements, compile-time evaluation, speculative hoisting) needs attested or declared facts. Inferred facts never become a library's contract automatically; a tool lists what is inferred but not declared so it can be promoted.
- **Resolution order:** a project override wins over declared and attested facts, which win over inferred ones, which win over the conservative default of impure.
- **Shipping:** declarations and attestations are source files in any repo, compiled into binding NuGet packages. Inferred facts stay a local cache keyed by the assembly's MVID, while attested and declared facts are durable, reviewable files. On each version bump the inferred facts are diffed, and any attestation the new IL contradicts is flagged.
- **Open:** whether bindings are shared across users in common packages (like DefinitelyTyped) or kept per project, and how a project chooses which attestation sources to trust.

## Sharpening: converting C# to Sharper

Sharpening turns a `.cs` file into Sharper source and lowering turns it back. The rule is that every breaking change needs a sharpener that works 100% of the time, so lowering a sharpened file returns the original.

- **Prior art:** the C# `field` keyword. A contextual keyword changed the meaning of existing code, and C# handled it with a language-version gate, a warning, and `@field` or `this.field` to keep the old meaning.
- **The sharpener is minimal.** It aims for correct Sharper, not nice Sharper. Explicit types stay explicit, and clashing identifiers get escapes such as `@it` and `@val`.
- **Cleanup is separate.** Analyzers suggest idiomatic changes and fixers apply them, each only when the meaning is provably preserved. For example, an explicit type is dropped only when Sharper infers the same one. Fixers that touch public signatures are flagged, since they tighten the contract for callers.
- **No semantic breaking changes, only new keywords.** Checked arithmetic by default was dropped as slower and not pragmatic. Checked arithmetic in Debug and test builds stays a possible later option.
- **The guarantee is one-way.** Only `cs` to Sharper to `cs` is guaranteed; Sharper-only constructs such as pipes and parameter patterns collapse into plain C# when lowered.
- **Testing:** run the round trip over large corpora (Roslyn, the runtime repo, popular NuGet packages) and compare the compiled IL. Each breaking change ships with its sharpener and a property test.
- **Files:** Sharper-only syntax needs its own extension, and it is `.cx` (decided; `.csx` is already the C# script extension). No other .NET tool uses `.cx`, and GitHub Linguist does not map it to any language. Strictness could follow a per-file or per-project ladder (off, warnings, errors), like nullable reference types.

## Examples

Four samples, roughly from smallest to largest. The syntax is a working guess.

### Fib

Each element can be added on its own. With only the parameter switch body, the parameter is a plain `int`, so the arms must cover negative numbers too:

```
static int Fib(int i) => switch
{
    0 or 1 => 1,
    >=2    => Fib(i - 1) + Fib(i - 2),
    _      => throw new ArgumentOutOfRangeException(nameof(i))
};
```

With refined types, the domain is written out, and `Fib(-1)` is an error at the call site rather than a missing case:

```
static int Fib(int i is >=0) => switch
{
    0 or 1 => 1,
    >=2    => Fib(i - 1) + Fib(i - 2)
};
```

The return type stays explicit because `Fib` is recursive. In the lowered C# of the refined version, the switch needs a final unreachable arm because C# only sees `int`. An earlier draft also left out the type of `i` and inferred `int is >=0` from the arms; arms are no longer a source of parameter types (see [Parameter switch body](#parameter-switch-body)), so the domain is written out.

### Order state machine

Ordered arms pay off when the arms are long. A parameter switch body is a switch expression over the parameters, so the first matching arm wins, exactly as in C#. The records for states, events and outcomes are elided in both versions; `Submitted` has a computed `Total`.

Sharper:

```
union State(Draft, Submitted, Paid, Shipped, Cancelled);
union Event(Submit, Pay, Ship, Cancel);

static infer Apply(State state, Event ev) => switch
{
    (Draft s, Submit e) =>
        s.Items.Count > 0
            ? new Submitted(s.Items, s.Customer, e.At)
            : new EmptyOrder(s),

    (Submitted s, Pay e) =>
        e.Amount >= s.Total
            ? new Paid(s, e.PaymentId, e.At)
            : new Underpaid(s.Total - e.Amount),

    (Paid s, Ship e) =>
        new Shipped(s, e.Carrier, e.TrackingNo, e.At),

    // terminal states reject everything, so this arm must come before the cancel arms
    (Shipped or Cancelled, _) => new InvalidTransition(state, ev),

    (Paid s, Cancel e) => new Cancelled(s, e.Reason, e.At, refund: true),

    // the general rule: any other state can be cancelled, including states added later
    (State s, Cancel e) => new Cancelled(s, e.Reason, e.At)
};
```

A stricter exhaustiveness check lists the remaining gaps in the transition table, because the general cancel arm only fills the Cancel column:

```
error: Apply is not exhaustive. Not covered:
  (Draft, Pay), (Draft, Ship), (Submitted, Submit),
  (Submitted, Ship), (Paid, Submit), (Paid, Pay)
```

C# 15 (union types, shipping with .NET 11 in November 2026):

```csharp
public union State(Draft, Submitted, Paid, Shipped, Cancelled);
public union Event(Submit, Pay, Ship, Cancel);
public union Outcome(Draft, Submitted, Paid, Shipped, Cancelled,
                     EmptyOrder, Underpaid, InvalidTransition);

static Outcome Apply(State state, Event ev) => (state, ev) switch
{
    (Draft s, Submit e)       => OnSubmit(s, e),
    (Submitted s, Pay e)      => OnPay(s, e),
    (Paid s, Ship e)          => OnShip(s, e),
    (Shipped or Cancelled, _) => new InvalidTransition(state, ev),   // before the cancel arms
    (Paid s, Cancel e)        => new Cancelled(s, e.Reason, e.At, Refund: true),
    (State s, Cancel e)       => new Cancelled(s, e.Reason, e.At)
};

static Outcome OnSubmit(Draft s, Submit e) =>
    s.Items.Count > 0
        ? new Submitted(s.Items, s.Customer, e.At)
        : new EmptyOrder(s);

static Outcome OnPay(Submitted s, Pay e) =>
    e.Amount >= s.Total
        ? new Paid(s, e.PaymentId, e.At)
        : new Underpaid(s.Total - e.Amount);

static Outcome OnShip(Paid s, Ship e) =>
    new Shipped(s, e.Carrier, e.TrackingNo, e.At);
```

An honest comparison:

- **Shape.** Nearly one to one. Ordered arms are a C# switch expression, and the Sharper body only drops the return type and the tuple construction. The arm bodies are the same, long or short.
- **Declarations.** Sharper's `union` deliberately matches C# 15's, so those lines convert directly.
- **The outcome union.** C# needs `Outcome` declared with all eight cases and kept in sync by hand, while Sharper infers the result type from the arm bodies. That inference is the structural-union question parked earlier.
- **Exhaustiveness.** Both are exhaustive over unions without a `_` arm. When a pair is missing, C# gives a warning (CS8509) that names one example pattern unless warnings are promoted to errors, while Sharper reports an error that lists all the missing pairs. I have not verified how RC1 treats tuples of two unions.
- **Order is semantics in both.** The terminal arm must precede the general cancel arm, and neither language flags a partial overlap, only an arm that can never match. An optional overlap hint is a possible Sharper extra.
- **Where C# is fine.** For this example C# is close, and the real gains are the inferred outcome union and the stricter, more precise exhaustiveness error. Sharper's larger wins show up in lifting over `Task` and error cases.

### Greeter: lifting over effects

Load a user, load their profile, and render a greeting. There are two I/O stages that can each fail with their own error. The pipe lifts over `Task` and passes error cases through, and `Render` takes a restartable recipe so that it can retry.

Four versions follow, as a ladder: C# with an interface, C# in the functional style, Sharper with the same structure as the interface version, and Sharper in the functional style.

C# 15 with an interface, the way most developers would write it:

```csharp
public union HelloResult(string, NotFound, Unavailable);
public union ProfileResult(Profile, Unavailable);

public interface IProfiles
{
    Task<ProfileResult> Load(int userId);
}

static async Task<HelloResult> Render(User user, IProfiles profiles)
{
    var result = await Retry(() => profiles.Load(user.Id), times: 3, when: r => r is Unavailable);
    return result switch
    {
        Profile p     => $"Hello, {user.Name} ({p.Plan})!",
        Unavailable u => u
    };
}

static async Task<HelloResult> Hello(int id, IUsers users, IProfiles profiles)
{
    switch (await users.Find(id))
    {
        case NotFound nf: return nf;
        case User user: return await Render(user, profiles);
    }
    throw new UnreachableException();
}

sealed class FakeProfiles(ProfileResult result) : IProfiles
{
    public Task<ProfileResult> Load(int userId) => Task.FromResult(result);
}

[Fact]
public async Task RendersThePlan()
{
    var result = await Render(new User { Name = "Ada" },
        new FakeProfiles(new Profile { Plan = "Pro" }));
    Assert.Equal("Hello, Ada (Pro)!", result.Value);
}

[Fact]
public async Task GivesUpWhenProfilesStayUnavailable()
{
    var result = await Render(new User { Name = "Ada" },
        new FakeProfiles(new Unavailable()));
    Assert.IsType<Unavailable>(result.Value);
}
```

C# 15 in the functional style. It can be done, but you will suffer:

```csharp
static async Task<HelloResult> Render(User user, Func<Task<ProfileResult>> load)
{
    var result = await Retry(load, times: 3, when: r => r is Unavailable);
    return result switch
    {
        Profile p     => $"Hello, {user.Name} ({p.Plan})!",
        Unavailable u => u
    };
}

static async Task<HelloResult> Hello(int id, IUsers users, IProfiles profiles)
{
    switch (await users.Find(id))
    {
        case NotFound nf: return nf;
        case User user: return await Render(user, () => profiles.Load(user.Id));
    }
    throw new UnreachableException();
}

[Fact]
public async Task RendersThePlan()
{
    var result = await Render(new User { Name = "Ada" },
        () => Task.FromResult<ProfileResult>(new Profile { Plan = "Pro" }));
    Assert.Equal("Hello, Ada (Pro)!", result.Value);
}

[Fact]
public async Task GivesUpWhenProfilesStayUnavailable()
{
    var result = await Render(new User { Name = "Ada" },
        () => Task.FromResult<ProfileResult>(new Unavailable()));
    Assert.IsType<Unavailable>(result.Value);
}
```

Sharper with the same structure as the interface version, after sharpening and the fixers. The interface stays, and the task tilde, the pipe and the inferred result union do the rest:

```
interface IProfiles { (Profile | Unavailable)~ Load(int userId); }

static infer Render(User user, IProfiles profiles) =>
    Retry(() => profiles.Load(user.Id), times: 3, when: it is Unavailable)
        |> $"Hello, {user.Name} ({it.Plan})!";

static infer Hello(int id, IUsers users, IProfiles profiles) =>
    users.Find(id) |> Render(it, profiles);

class FakeProfiles((Profile | Unavailable) result) : IProfiles
{
    public infer Load(infer userId) => result;     // types copied from IProfiles.Load; a plain value is lifted into the task
}

[Fact]
public void~ RendersThePlan() =>
    Assert.Equal("Hello, Ada (Pro)!",
        Render(new User { Name = "Ada" }, new FakeProfiles(new Profile { Plan = "Pro" })));
```

Sharper in the functional style, where the interface becomes a function parameter:

```
static infer Render(User user, () -> (Profile | Unavailable)~ load) =>
    Retry(load, times: 3, when: it is Unavailable)
        |> $"Hello, {user.Name} ({it.Plan})!";

static infer Hello(int id, IUsers users, IProfiles profiles) =>
    users.Find(id)
        |> Render(it, () => profiles.Load(it.Id));
    // inferred: (string | NotFound | Unavailable)~

[Fact]
public void~ RendersThePlan() =>
    Assert.Equal("Hello, Ada (Pro)!",
        Render(new User { Name = "Ada" }, new Profile { Plan = "Pro" }));

[Fact]
public void~ GivesUpWhenProfilesStayUnavailable() =>
    Assert.IsType<Unavailable>(
        Render(new User { Name = "Ada" }, new Unavailable()));
```

What it shows:

- **The ladder.** Sharpening the interface version changes nothing visible, because the sharpener only adds escapes. The fixers give the slightly nicer Sharper version with the interface kept. Functionalizing is the step that makes it beautiful, and it is a refactoring, not a fixer, because it changes a public signature and the tests. An analyzer could still suggest it when an interface parameter is only used through one method, offering "narrow to a function". Each rung gets a different tool, and a team can stop at any of them.
- **The honest C# baseline is the interface.** Most C# developers would write `IProfiles`, partly for DI conventions and partly because, without inference, passing functions means spelling out `Func<int, Task<ProfileResult>>` and `Task.FromResult` everywhere. The functional C# version is structurally better, narrower and easier to test, but it hurts the eyes.
- **That is the pitch.** Sharper does not forbid interfaces. It makes the narrow, functional version as cheap to write as the interface one, so the better structure stops costing readability.
- **One stage is fine in C#.** Each stage costs an `await`, a test and a forwarding `return`, so the function grows linearly and nests. Forwarding an error needs a named case in every stage, and the tidier `is not User user` idiom throws the error away. The trailing `throw` exists because switch statements over unions also have to cover `null`; that is as reported for RC1 and not verified here.
- **Parameter types decide how much a function owns (current direction).** Lifting only happens on a type mismatch, so a wrapper-typed parameter opts out of it. `T` is a finished value and the pipe lifts. `T~` is a running task, hot and one-shot, so two task parameters run concurrently and the body combines them explicitly (for example with `collect`). `() -> T` is a lazy recipe. `() -> T~` is a restartable async recipe, which is what retry, timeout and fallback need. Whether the parameter type alone decides, or an explicit marker is needed, is still open.
- **`it` inside a thunk** still means the stage value, because `it` only binds in single-parameter lambdas and a thunk has none.
- **Tests.** In the functional Sharper version a plain value embeds into any effect, so the test passes `new Profile { Plan = "Pro" }` where a restartable async recipe is expected, and `new Unavailable()` becomes a recipe that always fails. The interface versions still need a fake class (in Sharper its `Load` can return a plain value, which is lifted), and the functional C# version needs `Task.FromResult` plumbing. That a test boundary fails on an error case is a runner convention that is not settled.
- **Interfaces as parameters.** Passing an interface hands over every method, while a recipe hands over one capability, and the checker cannot see the implementation behind an interface call. Interface methods would default to impure, and a Sharper-declared interface could mark a method `pure` as a contract for its implementers. A lambda converting to a single-method interface, as in Java, is an open idea.
- **Purity.** A function that takes a recipe is only as pure as the recipe, which is the same conditional purity as `pure(f)` in the bindings.
- **A shortcut in the C# sketches:** one `HelloResult` serves both functions, because I have not verified how C# 15 converts between different unions.

### Batch bank

The showcase example: a list of transfer orders (from, to, amount, currency) is parsed into raw types, wrapped in domain types, matched to accounts with their base currency, priced with FX rates, validated, and returned as a list of transfers or errors.

```
// raw input and domain
record RawOrder(string From, string To, string Amount, string Currency);

enum Currency { USD, EUR, CZK }
record Order(AccountId From, AccountId To, Money Amount);
record Account(AccountId Id, Currency Base, decimal Balance is >=0);
record Transfer(AccountId From, AccountId To, Money Debit, Money Credit);

// errors as values; a single error converts to Invalid([e])
record BadInput(string Field, string Value);
record UnknownAccount(AccountId Id);
record NoRate(Currency From, Currency To);
record FxUnavailable(int Status);      // retryable
record SameAccount(AccountId Id);
record InsufficientFunds(AccountId Id, decimal Missing);
union TransferError(BadInput, UnknownAccount, NoRate, FxUnavailable, SameAccount, InsufficientFunds);
record Invalid(TransferError[] Errors)
{
    static implicit operator Invalid(TransferError e) => new([e]);
}

// 1. parse (pure): Parse takes raw strings only, constructors take typed values
record AccountId(string Value)
{
    static infer Parse(string s) => s switch
    {
        { Length: >0 } => new AccountId(s),
        _              => new BadInput("AccountId", s)
    };
}

record Money(decimal Amount is >0, Currency Currency)
{
    static infer Parse(string amount, string currency) =>
        collect(ParseAmount(amount), Currency.Parse(currency))
            |> (a, c) => new Money(a, c);

    static infer ParseAmount(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture) switch
    {
        val a and >0 => a,                  // a: decimal is >0
        _            => new BadInput("Amount", s)
    };
}

static infer Currency.Parse(string s) => Enum.TryParse<Currency>(s) switch
{
    Currency c => c,
    _          => new BadInput("Currency", s)
};

// collect: all parts succeed -> a tuple; otherwise one Invalid with every error
static infer ParseOrder(RawOrder raw) =>
    collect(AccountId.Parse(raw.From),
            AccountId.Parse(raw.To),
            Money.Parse(raw.Amount, raw.Currency))
        |> (from, to, money) => new Order(from, to, money);

// 2. accounts and 3. FX (the only I/O): fail fast, each step needs the last
record Resolved(Order Order, AccountPair Accounts);
record Quoted(Resolved Ctx, RateTable Rates);

static infer Resolve(Order o, IAccounts accts) =>
    accts.Find(o.From, o.To) |> new Resolved(o, it);

static infer Quote(Resolved r, IFx fx) =>
    fx.Table(r.Order.Amount.Currency, r.Accounts.From.Base, r.Accounts.To.Base)
        |> new Quoted(r, it);

// 4. validate and convert (pure): independent checks, so accumulate
static infer CheckDifferent(Order o) => o.From == o.To ? new SameAccount(o.From) : o;

static infer CheckFunds(Account from, Money debit) =>
    debit.Amount > from.Balance
        ? new InsufficientFunds(from.Id, debit.Amount - from.Balance)
        : debit;

static infer Validate(Quoted q)
{
    val (o, a) = (q.Ctx.Order, q.Ctx.Accounts);
    val debit  = q.Rates.Convert(o.Amount, a.From.Base);
    val credit = q.Rates.Convert(o.Amount, a.To.Base);

    return collect(CheckDifferent(o), CheckFunds(a.From, debit))
        |> (_, d) => new Transfer(o.From, o.To, d, credit);
}

// the batch
static infer Process(RawOrder raw, IAccounts accts, IFx fx) =>
    raw |> ParseOrder
        |> Resolve(it, accts)
        |> Quote(it, fx)
        |> Validate;

static infer ProcessBatch(List<RawOrder> raws, IAccounts accts, IFx fx) =>
    raws.Select(Process(it, accts, fx)) |> Task.WhenAll;
    // inferred: Task<(Transfer | Invalid)[]>
```

Inside the real FX adapter, a failing REST call becomes a value at the boundary:

```
try http.GetFromJsonAsync<RateTable>(url) catch
{
    HttpRequestException { StatusCode: 404 }             => new NoRate(from, to),
    HttpRequestException { StatusCode: >=500 and val s } => new FxUnavailable((int)s),
    TimeoutException                                     => new FxUnavailable(0)
}
```

What it shows, and what it still needs:

- Parsing lives on the types. `Parse` takes only raw strings and constructors take only typed values, so `new Money(...)` is the one door the checker guards and `Parse` is the untrusted-text path to it. Plain C# parameter declarations are used wherever there is nothing to refine.
- Each stage accepts only its success type. Error cases pass through the pipe untouched, and `Task` is lifted the same way, so there is no `await` and no error plumbing.
- Errors accumulate across independent pure checks (`ParseOrder`, `Validate`) through the plain function `collect`, and fail fast between stages, because each later stage needs the result of the one before. `collect` flattens nested `Invalid` results.
- Only `Resolve` and `Quote` touch a `Task`. Parsing and validation are pure, so they can be tested without mocks, and retrying is safe because nothing has moved money yet.
- `Task.WhenAll` is an explicit choice. The pipe cannot pick concurrent or sequential for you, and a real app would use bounded parallelism.
- `Convert` has to keep `Money.Amount is >0`. Positive times positive is not always positive for `decimal`: rounding to the currency's minor unit can give 0.00, so `Convert` should return `Money` or an `AmountTooSmall` error. That case is not in the sample yet.

## Lessons from TypeScript

TypeScript was a big inspiration, including its attitude of taming a messy legacy. JavaScript had no static types, so TypeScript had to invent a type system for patterns nobody designed for typing, which is where conditional and mapped types came from. The CLR already has nominal, static types, so the mess Sharper tames is behavioral: hidden side effects, null, exceptions, mutation by default, async, and the object-rooted type system. That is why Sharper's strictness lives in an effects layer rather than a heavy type-level language, as the next step after nullable reference types.

- **Copy the adoption playbook:** a strictness ladder per file or project, file-by-file migration, `.d.ts`-style bindings, and an escape hatch.
- **Model `assume` on `@ts-expect-error`.** It suppresses a check and then fails the build once the checker can prove the fact itself, so escape hatches do not rot and stay searchable.
- **Be careful with non-erasable syntax.** As I recall, TypeScript later added a flag to forbid features that need real transformation (enums, namespaces), because they broke the "types are just erased" promise. Nearly everything Sharper adds is non-erasable by nature (arm blocks, pipes, `it`, the task tilde), so each addition must earn its place with a readable lowering and the round-trip guarantee, and the total count should stay small.
- **The object root is C#'s deepest legacy.** `ToString`, `GetHashCode`, `Equals` and the ability to lock on any object come with every type. For the effect system it means virtual calls on root members, such as string interpolation of a value, would count as impure by default. An idea to explore: attest the root members as pure by contract (the .NET design guidelines say they should not throw or have side effects), flag overrides that violate it, and allow `lock` only on a dedicated lock type.

## Decisions, dropped ideas and parked topics

What is settled so far, what was tried and dropped, and what waits for a later session.

### Decided

- Lower to plain C# through Roslyn, with Sharper's own checker enforcing the strictness before lowering.
- Phase one is friendlier inference plus explicit immutability (`val`), leaning towards proper deep immutability.
- A multi-case function is one declaration with ordered arms, a switch expression over its parameters, so evaluation order is the same as in C#. The leading spelling is the parameter switch body `=> switch { ... }`. Arms are not a source of parameter types. Plain C# parameter declarations stay valid and mean the same thing when there is nothing to refine.
- Functions are ordinary methods, and the parts of the earlier `fun` are separate features (see [Functions as separate elements](#functions-as-separate-elements)). `fun` is parked for a later "functional all in" mode.
- `infer` stands in place of an inferred return or parameter type. Parameter types are copied from a type that a signature or a value names (an overridden member, a default value, a function type, a call that pins one type), never from the shape of their uses.
- Inferred types are allowed on public members, with no warning: whoever cares about a published API writes its types out.
- A general default arm after the specific ones expresses a domain rule that also covers cases added later, such as "anything can be cancelled".
- `it` works in any single-parameter lambda, and lifting through pipes happens one argument at a time.
- Errors accumulate through the plain function `collect` across independent pure checks, and fail fast between dependent stages. It is a function, not new syntax.
- `Parse` functions live on the types and take only raw strings, while constructors take typed values.
- Exceptions are handled with `try expr catch { ... }`, and there is no `finally`; low-level code lives in C#.
- Sorting and similar functions take an explicit comparator function instead of generic constraints.
- Short type forms: `T[..]` for sequences, `T~` for `Task<T>` (`void~` for `Task`; `ValueTask` stays spelled out), and `() -> T` for functions.
- `->` is only the arrow of function types; lambdas keep `=>`. A function type is `Func` or `Action`, never a synthesized delegate (see [function types](../features/function-types.md)).
- Purity has three levels (impure, pure, total), and `total` is a checked assertion with inference for easy cases.
- The sharpener is minimal and total, cleanup belongs to analyzers and fixers, and lowering undoes sharpening.
- IL analysis is the baseline for bindings to existing CLR code, with a small hand-written overlay on top and three provenance levels (inferred, attested, declared).

### Dropped

- Auto-upgrading `Fib(3L)` to wider numeric types: too much machinery for the benefit.
- Checked arithmetic by default: slower than C# and not pragmatic.
- `finally` and multi-argument lifting.
- Overloads declared through arms, and separate repeated `fun` clauses, which are replaced by one switch body of ordered arms.
- The `fun` declaration. It bundled return type inference, immutable-by-default parameters, ordered arms, inferred parameter domains, a strict scope and the effect modifiers, so none of them could be built or judged alone. Each is now a separate element on ordinary methods (see [Functions as separate elements](#functions-as-separate-elements)), and the defaults moved to the strictness ladder. The keyword itself is parked, not dropped.
- `var` as an inferred return type: `var` means variable, and a return type is not one. Replaced by `infer`.
- Parameter types inferred from a function's callers: editing one caller would change the callee, and that is whole-program inference.
- Structural parameter inference in the TypeScript style: start from the members a body uses (`x.Length`, `x.Name`), find the types that have them, and infer that type, or a union with a runtime dispatch on every access when several match. It can be squeezed onto the CLR, but the inferred type would depend on which assemblies are referenced, every access would hide a type test, C# callers would see a union or `object`, and a C# reader expects the signature, not the body, to say what a method accepts.
- Grade and FizzBuzz as showcase examples (a C# switch is already fine for trivial, uniform arms), and quicksort and merge sort as introductory examples (they open too many rabbit holes).

### Parked

- Discard arguments (`F(x, _)` passes the only fitting local), to be discussed: see [Discard arguments](#discard-arguments-proposed-not-yet-discussed).
- Exceptions beyond the basics: one "may throw" bit versus typed throws.
- The boundary with C#: runtime guards on public entry points, and how refined parameter types lower.
- A virtual-thread feel for concurrency by inferring "may suspend".
- The lowering rule for `..rest` on types that are not sliceable.
- Local mutation that does not escape (in-place quicksort), including escape analysis and a "mutates its argument" effect.
- Structural unions versus erasure to `object`, and parallel versus sequential batches.
- A few more passes through all the concepts before any syntax is finalized: how union types are spelled, the deep-dive questions in the open questions, and the unsettled spellings (arms as a lambda body, a lift marker).

## Open Questions

- Immutability depth: shallow readonly marker, or deep immutability where an immutable value can only reach other immutable things? Leaning towards a flexible design that allows proper deep immutability.
- Is immutability declared per type, per member, or both?
- How are guarantees (immutability, refined parameter types) preserved across assembly boundaries, through metadata attributes and runtime guards at C# entry points?
- How does the checker's flow analysis tie into the more flexible type inference?
- What escape hatches are needed to keep the language pragmatic, for example an `assume` that lowers to a runtime check?
- How are diagnostics mapped back to original source after lowering?
- Which effect conversions are safe enough to infer automatically, and which need a manual converter?
- **Untagged unions and generics (deep dive).** If an error case passes through pipes, generic code over `T | NotFound` cannot tell whether `T` is itself `NotFound`, so behavior changes with the instantiation and nested options collapse. C# 15 unions have the same trap. One candidate is to let only declared error cases pass through, treat an unconstrained `T` as never an error, and reject instantiating it with one.
- **Implicit lifting versus an explicit marker (deep dive).** Should the parameter type alone decide whether a pipe stage lifts, or should lifting be spelled out? Implicit lifting is a type-directed coercion, errors from a lift that did not fire will be confusing, and inlay hints in the IDE only partly help.
- **`T~` is a hot task (deep dive).** Creating it starts the effect, so the type does not describe a computation. `~` means "may suspend", not "impure", and purity is a separate flag. How this relates to cold recipes (`() -> T~`) and to a single coherent effect story is open. It does not block the spelling: `T~` is a plain alias for `Task<T>`, which is already hot in C#. Shipping it does fix one thing, though: `~` can never later become a different type (a cold computation, `ValueTask` or "any awaitable") without breaking every signature written with it.
- **Structural unions versus reified generics (deep dive).** TypeScript can be structural because erasure is free in JavaScript, but on the CLR generics, overloads, reflection and C# callers all see real types. `A | B` needs a canonical CLR type, yet in generic code the normal form of `T | NotFound` depends on `T` (it should collapse when `T` is `NotFound`), and `Profile~` to `(Profile | Unavailable)~` or `List<Profile>` to `List<Profile | X>` are not CLR conversions. This is a choice, not a fix: nominal or tagged unions in generic positions, erasure to `object` (boxing, opaque to C#), or copying conversions. It subsumes the untagged-unions question above.
- **Purity needs deep immutability (deep dive).** "Depends only on its arguments" is undefined when an argument is a reference to mutable data, so memoization, hoisting, lazy collection expressions and the rule that `T[..]` promises neither laziness nor eagerness all break on a mutable `List`, and any `List` converts to `T[..]` because it aliases `IEnumerable<T>`. Nullable reference types get away with an unsound erased layer because it never changes codegen, while Sharper's facts would drive optimizations. The choice: make immutability the foundation and treat purity as meaningful only over representationally immutable data (readonly all the way down, verified in IL), or keep the facts advisory and give up the optimizations.
- **Implicit lifting stops at generics and `object` (deep dive).** Lifting fires on a type mismatch, but a generic `Log<T>(T x)` or an `object` parameter accepts a `Task` with no mismatch, and with the object root every value fits there. A library adding an overload can silently flip a call site between lifting and not lifting. At minimum generic and `object` positions need an explicit lift marker, which largely answers the implicit-versus-explicit question above.
- **Inferred domains swallow exhaustiveness.** If a refined domain were ever inferred from arm patterns, a forgotten arm would narrow the domain instead of raising a non-exhaustive error, and the errors would surface at call sites instead. That is why arms are not a source of parameter types; domains are written out.
- **Generic by default (source 5).** Should a parameter that nothing pins become a generic type parameter, or be an error? See [Inferred parameter types](#inferred-parameter-types).
- **Lowering is really binding.** Lifting and union inference interleave with overload resolution, so Sharper cannot be a syntax-desugaring pass in front of Roslyn. It needs its own binder or a Roslyn fork, with ongoing upstream tracking.
- **Lifting impure stages over sequences must be eager.** Lowering to a lazy `Select` would repeat the I/O on every enumeration, which contradicts "eager by semantics".
