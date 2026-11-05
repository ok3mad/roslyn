# Inferred parameter type (`infer`)

**Status:** Prototype of steps P1 to P5, merged into `main`; P6 on branch `sharper/infer-parameter-p6`. Requires
`/features:sharper`. Issues: [#72](https://github.com/ok3mad/roslyn/issues/72) (P1),
[#73](https://github.com/ok3mad/roslyn/issues/73) (P2), [#166](https://github.com/ok3mad/roslyn/issues/166) (P2 for the
other member kinds), [#74](https://github.com/ok3mad/roslyn/issues/74) (P3),
[#75](https://github.com/ok3mad/roslyn/issues/75) (P4), [#76](https://github.com/ok3mad/roslyn/issues/76) (P5),
[#77](https://github.com/ok3mad/roslyn/issues/77) (P6), under
[#27](https://github.com/ok3mad/roslyn/issues/27).

## Summary

`infer` in place of a parameter type says that the type is the type of the default value:

```csharp
string Fetch(string url, infer retries = 3, infer name = "n", infer day = DayOfWeek.Monday) => ...;
// string Fetch(string url, int retries = 3, string name = "n", DayOfWeek day = DayOfWeek.Monday)
```

An override or an interface implementation copies its `infer` parameter and return types from the member it overrides or
implements:

```csharp
interface IProfiles { Task<ProfileResult> Load(int userId); }

class FakeProfiles(ProfileResult result) : IProfiles
{
    public infer Load(infer userId) => Task.FromResult(result);   // Task<ProfileResult> Load(int userId)
}
```

The parameter is an ordinary parameter with that type: callers, overload resolution, the semantic model, IOperation and
metadata all see `int retries = 3`. Step P1 takes the type from the default value (source 2 of the
[brainstorm](../planning/brainstorm.md#inferred-parameter-types)), step P2 from the overridden or implemented member
(source 1). Step P3 combines both with [`readonly`](readonly-parameters.md): `readonly infer x` is inferred and cannot
be reassigned. Step P4 takes the type from a use in the body (source 4): the parameter passed directly to a call
with exactly one candidate (`profiles.Load(id)` with one `Load(int)`) gets that candidate's parameter type. Step P5
extends it to overloaded and generic calls: `Format(n, "N2")` pins `n` when every `Format` that accepts a string second
takes an `int` first, and `dict.GetValueOrDefault(key)` pins `TKey`. Step P6 adds the other uses: an assignment to a
typed local, field or property (`this.Name = name`), `return x` from a member with a written return type, and `ref`,
`out` and `in` arguments. A function type the member is converted to (P7) is the next step of the
[work breakdown](../planning/implementation-plan.md#type-inference-work-breakdown).

## Rules

- **What is inferred.** The type `var x = e;` would give `x`, where `e` is the default value: `3` is `int`, `10L` is
  `long`, `(short)1` is `short`, `E.A` is `E`, `default(int?)` is `int?`, a constant `K` has the constant's type. A
  lambda or method group gets its natural function type, as for `var`.
- **Then the C# rules apply.** The default value is checked against the inferred type as against a written one. A value
  that is not a constant gives its type and then reports CS1736, like its C# twin:
  `infer timeout = TimeSpan.FromSeconds(5)` is a `TimeSpan` with CS1736. `ref` and `out` parameters cannot have a
  default value (CS1741), `params` and `this` parameters neither (CS1751, CS1743), and caller information attributes
  check the inferred type (`[CallerMemberName] infer m = 0` reports CS4019).
- **Nothing to infer from.** A default value with no type of its own reports CS20026: `null`, `default`, `new()`, a
  collection expression, a lambda or method group without a natural type, a call that returns `void`, `(1, null)`. A
  default value with errors of its own reports only those. In both cases the parameter type is an error type, and the
  default value reports nothing more.
- **No default value.** `infer x` without a default value takes its type from the member its method overrides or
  implements (below), or else from the uses in the body (step P4, below). A parameter with none of them reports
  CS20027, which says why nothing pinned it. It never becomes a generic type parameter
  (decided in [#79](https://github.com/ok3mad/roslyn/issues/79)): write the type, or make the method generic in
  source (`(TA, TB) Pair<TA, TB>(TA a, TB b)`). Once uses in the body are a source (P4), CS20027 says why nothing
  pinned the parameter, and the IDE (R5) offers both fixes.
- **Nullable.** In a `#nullable enable` context, a default value that is a null constant gives an annotated type
  (`infer s = default(string)` is `string?`), and any other default value a type that is not annotated (`infer s = "a"`
  is `string`, so `M(null)` warns). Where nullable annotations are disabled at `infer`, the type is oblivious, as a
  written type would be.
- **Every member.** Methods of any accessibility, local functions, constructors, primary constructors (and the
  properties of a positional record), indexers, delegates, extension methods, and lambdas with an explicit
  parameter list (`(infer x = 10L) => x`). Overrides, interface implementations, partial methods and overloads compare
  the inferred types like written ones: `override void V(infer x = 1)` overrides `V(int)`, not `V(long)`.
- **A type named `infer`.** Like `var`, `infer` is a keyword only when no type named `infer` is in scope. With the
  flag off it is an ordinary type name: CS0246 when there is none.
- **Cycles.** A default value that depends on the signature of its own member, directly (`M(infer x = M())`) or
  through other members (`A(infer x = B())` and `B(infer x = A())`), reports CS20028 on every parameter of the
  cycle, whichever member is used first. Such a default value is a call, so it is never a valid constant anyway.
  `nameof(M)` and constants such as `const string K = nameof(M)` do not read the signature and are no cycle.
- Function pointer types (`delegate*<infer, void>`) have no default values, and `infer` there is an ordinary type
  name.

## From the overridden or implemented member (step P2)

- **What is copied.** The type of each `infer` parameter without a default value, and an `infer` return type, are those
  of the member, as written there: nullable annotations, tuple element names, the type arguments of a generic base
  (`class C : B<string>` copies `T` as `string`) and the member's method type parameters, renamed to the method's own.
  A parameter that writes no ref kind copies the member's (`ref`, `out`, `in`, `ref readonly`), and copies `params`.
  The return type's `ref` is written, not copied. Nothing is inferred from the body, so the return type has none of the
  limits of an [inferred return type](inferred-return-type.md): such members may be abstract or without a body.
- **Which members.** A method declared `override` copies from its base classes, an explicit implementation
  (`infer IProfiles.Load(infer userId)`) from the interface it names, and a public method from the interfaces of its
  type that have a member with its name and parameter count.
- **Every member kind** ([#166](https://github.com/ok3mad/roslyn/issues/166)). Properties, indexers, operators,
  conversion operators and events copy the same way, from a member of their own kind:

  ```csharp
  abstract class B { public abstract string Name { get; } public abstract int this[string key] { get; } public virtual event EventHandler Changed; }

  class C : B
  {
      public override infer Name => "c";              // string Name
      public override infer this[infer key] => 0;     // int this[string key]
      public override event infer Changed;            // event EventHandler Changed
  }

  interface IAdd<T> where T : IAdd<T> { static abstract T operator +(T a, T b); }
  struct S : IAdd<S> { public static infer operator +(infer a, infer b) => a; }   // S operator +(S a, S b)
  ```

  An indexer copies its parameter types and its type, a property and an event their type, and an operator that
  implements a static abstract or static virtual interface operator, implicitly or explicitly
  (`static infer IAdd<S>.operator +(infer a, infer b)`), its parameter and return types. The accessors follow the
  copied type: `get`, `set` and `init`, `add` and `remove`, with expression or block bodies, and the backing field of
  an auto-property or a field-like event. The candidates are matched by kind and name (an indexer's name is `this`,
  an operator's is its kind: `op_Addition`, `op_CheckedUnaryNegation`, `op_Implicit`) and by parameter count, and the
  written types narrow them as for methods.
- **Properties and events with no member to copy from.** A property or an indexer that is neither an override nor an
  implementation keeps R4's error (CS20015, "not supported on a property or indexer yet"): inferring it from the
  getter waits for R4's open point ([#70](https://github.com/ok3mad/roslyn/issues/70)). Such an event reports CS20055,
  since it has no body to infer from either. An operator with no interface operator to copy from infers its return
  type from its body (R4), and its `infer` parameters need a default value (CS20027).
- **Finding the member.** The member is matched before the method's types are known: by name, arity, parameter count
  and `static`, among the members it could override (virtual, abstract or override, and accessible) or implement.
  The types the method writes, its ref kinds, and the types of `infer` parameters with a default value (P1) narrow the
  candidates: `override infer M(int a, infer b)` copies from `M(int, string)`, not from `M(long, string)`. A virtual
  member and its overrides count once, as one signature; so do interface members with the same signature.
- **Errors.** No candidate left is CS20043; more than one (overloads with the same parameter count, or two interfaces
  whose members differ) is CS20044, which lists them. Both ask for the types. When the written types rule out every
  member with the name and parameter count, CS20043 names each one and what ruled it out, and points at the written
  part (an additional location): `override string M(int a, infer b)` over `M(long, string)` reports
  "'B.M(long, string)' (it takes 'long' for 'a')". An `infer` that the error leaves without a type is an error type,
  so the members it would override or implement report nothing more about it.
- **Not an override or implementation.** A method that is not `override`, not an explicit implementation, and not
  public, or a public method whose type's interfaces have no member with its name and parameter count, has no such
  source: an `infer` parameter needs a default value (CS20027), and an `infer` return type is inferred from the body as
  before.
- **Binding order and cycles.** A member whose own types are `infer` (from a default value, or copied from its own
  base) is completed first, whichever member is used first. A member whose signature depends on the method that copies
  from it (`virtual int M(infer x = new D().M(1))` in the base of `D`, which declares `override int M(infer x)`) is a
  cycle: CS20045 on the copying method and CS20028 on the default value, whichever is used first. The default value's
  call to a member of the cycle reports no conversion error (CS1503) for its arguments, whose parameters have no type.
  An indexer whose base's default value indexes it (`virtual int this[int i, infer j = new D()[1, 2]]`) is the same
  cycle.
- **Records.** An override of a member that a base record synthesizes (`protected override infer PrintMembers(infer
  builder)`) or of `object`'s members (`public override infer ToString()`) copies like any other. A record's own
  declaration of a member it would otherwise synthesize (`virtual bool Equals(R? other)`) overrides nothing, so its
  types are written.

## `readonly infer` (step P3)

`readonly` before `infer` makes the parameter a [`readonly` parameter](readonly-parameters.md) whose type is inferred,
from either source:

```csharp
void Fetch(readonly infer retries = 3) { retries = 0; }   // error CS1656: Cannot assign to 'retries' because it is a 'readonly parameter'

class Square(double side) : Shape
{
    public override infer Area(readonly infer scale) => side * side * scale;   // double Area(readonly double scale)
}
```

- **The rules of both.** The type is inferred as without `readonly`, and the parameter follows the `readonly` parameter
  rules, with the same diagnostics as its twin with the type written out: writes are CS1656, CS1657 and CS1654, also
  from lambdas and local functions, and a non-readonly member of a struct runs on a copy and warns (CS20013).
- **Every member.** Methods, local functions, constructors, primary constructors and lambdas with a default value (P1),
  and overrides and implementations (P2).
- **`ref`, `in` and `out`.** A ref kind copied from the overridden member (P2) reads like a written one:
  `readonly infer r` over `ref int r` is `readonly ref int r`, which forbids ref reassignment and allows writes through
  the ref. With a default value, `readonly in infer i = 1` works, and `ref` and `out` keep CS1741 as in C#.
- **Flag off.** `readonly` keeps C#'s CS9190 and `infer` is a type name (CS0246).
- **No `val` shorthand.** `val x` is not accepted on method parameters; whether it should be is
  [#178](https://github.com/ok3mad/roslyn/issues/178).

Nothing is added for it: `readonly` is read from the parameter syntax (`ParameterHelpers.IsSharperReadOnly`), the type
from the `infer` branch of `MakeParameter`, and the two never meet.

## From uses in the body (step P4)

A parameter with neither a default value nor a member to copy from takes its type from the body, when the body passes
it directly to a call that has exactly one candidate:

```csharp
interface IProfiles { ProfileResult Load(int userId); }
record Point(long X, long Y);

class C(IProfiles profiles)
{
    public infer Hello(infer id) => profiles.Load(id);         // ProfileResult Hello(int id)
    public Point Make(infer x, infer y) => new Point(x, Y: y); // Point Make(long x, long y)
    public int Call(Func<string, int> f, infer s) => f(s);     // int Call(Func<string, int> f, string s)
    infer Len(infer s) => s.Length;                            // error CS20027: ... a member access pins nothing
}
```

- **What pins a type.** The parameter itself, as a by-value argument (by position or by name) of a method call, an
  object creation (also target-typed `new(x)`), a constructor initializer (`: base(x)`, `: this(x)`) or a delegate
  invocation, when the call has exactly one candidate: one method of that name, one constructor, or the delegate's
  `Invoke`. The parameter gets that candidate's parameter type as declared there, with its nullable annotation and the
  type arguments of the candidate's type substituted (`IRepo<User>.Put(T)` pins `User`). An `in` or `ref readonly`
  parameter pins its type. `Ext.Twice(x)` pins, and so does an argument of the extension call `p.Pad(x)`.
- **What pins nothing.** Shape never pins: member access (`s.Length`, also as the receiver of an extension method,
  `x.Twice()`), operators, type tests (`is`, `as`, patterns, `switch`), interpolations, casts and other expressions
  around the parameter. Calls with more than one candidate, type parameters of the candidate and `params` parameters are
  step P5, and `ref`, `out` and `in` arguments, assignments and returns step P6 (below).
- **Several uses** must agree: the same type, ignoring nullability and tuple element names (`string?` and `string`
  give `string`, which both accept; `(int a, int b)` and `(int x, int y)` give `(int, int)`). Uses that need different
  types report CS20057, which names two of them with their candidates (`'int' for 'C.Take(int)' and 'long' for
  'C.TakeLong(long)'`) and points at both.
- **Nothing pins it.** CS20027 says why, from the first use that pins nothing: "a member access pins nothing", "an
  operator pins nothing", "a type test pins nothing", "an interpolation pins nothing", "'Generic' takes a type
  parameter there, which pins nothing yet", "a params parameter pins nothing yet", "an argument whose ref kind its
  parameter does not take pins nothing" (until P6, "a ref or out argument pins nothing yet"), "no use passes it directly
  to a call with one candidate", or "the body does not use it", and from P5 on the
  candidates of an overloaded call (below). The use is an additional location of the error. Members whose body is no
  source keep "nothing else gives its type". The parameter never becomes generic
  ([#79](https://github.com/ok3mad/roslyn/issues/79)).
- **Precedence.** The member that is overridden or implemented (P2), then the default value (P1), then the body (a
  function type, P7, will come before the body). The body is consulted only when neither gives the type; a use that does
  not accept the type of a higher source reports the C# error of the twin with the type written (CS1503 names both
  types).
- **Lambdas and local functions in the body** count like the rest of it: `xs.Select(x => profiles.Load(id))` pins
  `id`. A lambda or local function parameter with the same name hides the parameter and is not a use of it. A lambda's
  or local function's own `infer` parameter without a default value still reports CS20027.
- **Recursion.** A call to the method itself, or to one of its overloads, binds to a placeholder for the method, so
  `string Recursive(infer x, int n) => n == 0 ? Take(x) : Recursive(x, n - 1);` infers `x` from `Take`. The method's own
  parameters being inferred pin nothing, and its written ones pin as any other (`Swap(int a, infer b) => ...
  Swap(b, a - 1)` gives `b` the type `int`).
- **Cycles.** A body that needs its own member's signature through other members whose types are inferred
  (`P(infer x) => Q(x)` and `Q(infer y) => P(y)`, or a return type inferred from the member, R3) reports CS20056 on every
  parameter inferred from a body of the cycle, whichever member is used first. Overload resolution reads every
  candidate's signature, so two constructors with `infer` parameters whose initializers call the other overloads are a
  cycle too.
- **Members.** Methods of any accessibility, also extension methods, constructors, operators and conversion operators.
  Lambdas, local functions, indexers, primary constructors and delegates have no body source yet (CS20027).
- **Generics.** The method's own type parameters are in scope (`Owner<T>(IRepo<T> r, infer x) => r.Put(x)` gives `x`
  the type `T`); a type parameter of a local function or a lambda in the body pins nothing.

## From overloaded and generic calls (step P5)

A call with several candidates pins the parameter when every candidate that is applicable with the parameter in place
takes the same type at its position:

```csharp
class C(Dictionary<string, int> dict)
{
    static string Format(int n, string format) => n.ToString(format);
    static string Format(double d, int digits) => d.ToString();
    static string Format(string s) => s;
    static int Sum(params int[] xs) => xs.Sum();

    infer Show(infer n) => Format(n, "N2");                // string Show(int n)
    infer Pick(infer key) => dict.GetValueOrDefault(key);  // int Pick(string key): TKey is fixed by dict
    infer Twice(infer x) => Sum(1, x);                     // int Twice(int x): the expanded form
    void Log(infer x) => Console.WriteLine(x);             // error CS20027: ... the candidates of a call to 'WriteLine' take different types there: ...
}
```

- **Applicable with the parameter in place.** The other arguments are bound and converted as for any call, and the
  parameter is assumed to fit its position: a candidate is applicable when it would be if the parameter had the type
  the candidate takes there. Argument count, names, optional parameters and the ref kind still decide (a by-value
  argument rules out a `ref` or `out` parameter, and fits an `in` one), and the C# rules of the call apply: instance
  methods first, then the extension method scopes from the innermost, and the first set with an applicable candidate
  is the one that counts. Better and worse candidates are not compared: the parameter has no type to compare them by.
- **Generic candidates.** Type inference fixes the candidate's type parameters from the other arguments and the
  receiver (`list.Add(x, 1)` on a `List<long>`, `First("s", x)` for `First<T>(T a, T b)`, `d.Get(key)` for
  `Get<TKey, TValue>(this IDictionary<TKey, TValue>, TKey)`), and the parameter gets the substituted type. A type
  parameter that nothing else fixes pins nothing ("'First' takes a type parameter there, which pins nothing yet"), and
  a candidate whose inference fails only on other arguments is not applicable.
- **`params`.** The normal and the expanded form are two candidates: `Sum(1, x)` has only the expanded form and pins
  `int`; `Sum(x)` has both, `int[]` and `int`, and pins nothing ("a params parameter pins nothing yet").
- **A group with one member** pins as in P4, whether or not it is applicable, except where its parameter type is one of
  its type parameters or `params`, which P5 decides as above.
- **Candidates that disagree** pin nothing. CS20027 lists every applicable candidate and the type it takes: "the
  candidates of a call to 'Over' take different types there: 'C.Over(int)' takes 'int', 'C.Over(string)' takes
  'string'", with "in its expanded form" for the expanded form of `params`. With no applicable candidate it says "no
  candidate of a call to 'Over' accepts the other arguments". Another use can still pin the parameter.
- **Several uses** must agree, as in P4: a use in an overloaded call is a use like any other, its type merged with the
  others ignoring nullability and tuple names; CS20057 names the first candidate of an overloaded use.
- **Recursion.** An overload of the method itself is a candidate like any other (`Overload(infer x) => Overload(x, 0)`
  with `Overload(string x, int level)` gives `x` the type `string`); the placeholder's own parameters being inferred
  pin nothing.

## From assignments, returns and `ref` arguments (step P6)

The other uses that pin one type:

```csharp
class C
{
    public string Name { get; set; } = "";
    static void Inc(ref long value) => value++;
    static void Take(int x) { }
    static void TakeLong(long x) { }

    void Rename(infer name) { this.Name = name; }      // string, from the property
    static int Clamp(infer x) { return x; }            // int, from the written return type
    static double Half(infer x) { double d = x; return d / 2; }   // double, from the local
    static long Bump(infer x) { Inc(ref x); return x; }           // long: both uses agree
    void Both(infer x) { Take(x); TakeLong(x); }       // error CS20057: ... 'int' for 'C.Take(int)' and 'long' for 'C.TakeLong(long)'
}
```

- **Assignments.** The parameter assigned to a local or a parameter whose type is known (`y = x`, `int y = x;`, also
  `ref int r = ref x;`), a field, a property or an indexer (`this.Name = name`, `this[i] = x`, also in an object
  initializer, `new Person { Name = name }`) gets the type of the target, as declared there. A `var` local, an array
  element, an event, a deconstruction and an assignment to the parameter itself (`x = 1`) pin nothing.
- **Returns.** `return x;` and an expression body `=> x` pin the written return type of the member, local function or
  lambda (`int () => x`) they return from: `T` for `async Task<T>`, and the referenced type of a `ref` return. An `infer`
  return type, a lambda without a written return type (`Func<int> f = () => x;`), `void`, `async Task`, an iterator and
  `yield return` pin nothing.
- **`ref`, `out` and `in` arguments** pin the exact type of a parameter that takes their ref kind (`Inc(ref x)`);
  with several candidates, P5 counts those that take the ref kind. A by-value argument to a `ref` or `out` parameter, or
  a `ref` argument to a by-value one, pins nothing ("an argument whose ref kind its parameter does not take pins
  nothing").
- **Only the parameter itself**, through the target's implicit conversion; a cast (`(int)x`) or any other expression
  around it pins nothing, as for arguments.
- **Several uses** must agree with each other and with the calls, as in P4, ignoring nullability and tuple names
  (`string? a = x; string b = x;` gives `string`). CS20057 names the first two that differ, each with its type and what it
  is: a call's candidate, an assignment's target (`'C.Name'`, `'y'`), or a return (`'the return of C.Clamp'`, `'the return
  of L'`, `'the return of a lambda expression'`), and points at both.
- **Precedence** is unchanged: an overridden member (P2) or a default value (P1) gives the type, and the body is not
  consulted. A use that disagrees with it reports the error of the twin, which names both types:
  `string Default(infer x = 1) => x;` reports CS0029 ("Cannot implicitly convert type 'int' to 'string'"), as
  `string Default(int x = 1) => x;` does.
- **Lambdas and local functions** in the body count as in P4; a `return` there pins the return type of the lambda or
  local function it returns from, when it is written.

## How it works

No syntax, bound node or lowering is added: `infer` parses as a type name, and the binder decides.

1. **The type.** `ParameterHelpers.MakeParameter`, which every member kind uses, binds the parameter type with
   `Binder.BindTypeOrSharperInferKeyword` (the R1 recognition of `infer`). For the keyword it asks
   `SharperInferredParameterType.GetType`, which binds the default value with the binder the default value itself
   uses, and takes the natural type as `var` does (`Binder.BindSharperInferredParameterType`). The parameter symbol
   is then created with that type. Lambdas do the same in `Binder.BindLambdaParameterType`.
2. **Bound once more.** The default value is bound again against the inferred type when the parameter's default value
   is computed, as for a written type; that binding reports its errors (CS1736 and the like), and its result is what
   metadata, IOperation and nullable analysis see.
3. **Outside the signature lock.** Methods and constructors compute their signature under a per-member lock
   (`LazyMethodChecks`), and indexers under another (`EnsureSignature`). Binding a default value there could read the
   signature of a member that is computing its own on another thread, and deadlock. Both infer their `infer`
   parameters first, before the lock (`SharperInferredParameterType.InferBeforeSignature`); the results are kept by
   the compilation per parameter syntax, published once, and the signature reads them. Local functions, delegates
   and lambdas hold no such lock and infer on demand.
4. **Cycles.** The parameters being inferred on a thread are kept on a stack. A parameter asked for while it is on the
   stack closes a cycle: every parameter from it to the top of the stack gets the CS20028 result at once, before the
   signature that asked for it completes. That keeps a thread that later finds that signature complete from inferring
   the rest of the cycle without the error. The results of the whole cycle are decided before any is published, and a
   thread that computed a member of the cycle without seeing it publishes the decided result instead of its own.
5. **The overridden or implemented member** (P2) is searched for before the signature lock too
   (`SharperInferredParameterType.GetOverriddenSignature`, called from `InferBeforeSignature`), since matching reads the
   candidates' signatures. The result is kept by the compilation per member syntax (the declarator of a field-like
   event) and published once; `MakeParameter` takes the parameter types, ref kinds and `params` from it, and the
   member's own signature code the return, property or event type and the errors of the search:
   `MakeParametersAndBindReturnType` of methods and of operators, `SourcePropertySymbol.ComputeType` for properties
   and indexers, and `SourceEventSymbol.BindEventType` for events. The search is on the same stack as default values,
   so a cycle through both is found from either end, and every member of it gets its error at once. One description
   per member kind (`MemberDeclaration`: the cache key, the candidates' name and kind, the parameters and the type
   syntax) feeds the same matching and copying.
6. **Events bind their type in their constructor**, before they are members of their type, so their search runs there.
   It reads only base types and interfaces, whose members are complete by then, and binds the written types with the
   binder of the containing type, without looking the event up.
7. **Uses in the body** (P4). The body is bound once more, before the signature, with a stand-in for the member
   (`SharperPlaceholderMethodSymbol`): its parameters have the types the signature will have where they are known,
   and an error type, the placeholder, for the parameters being inferred. The placeholder converts to nothing, so a
   call it pins fails overload resolution, and the binder's error recovery keeps the single candidate and the call's
   type; the rest of the body binds as it will. The uses are read from the bound tree (`UsesCollector`), so a lambda's
   trial bindings for rejected delegate types do not count. A call whose candidates include the member itself binds
   to the placeholder (`ReplaceOriginal`, in overload resolution and constructor lookup), so recursion reads no
   signature being inferred. The result is kept per parameter list, on the stack of default values and overridden
   members (`GetBodyInference`, from `InferBeforeSignature`), and published once; while the placeholder body is bound,
   return type inference (R3) is suspended, so its reads start their own frames, and the default values of lambdas and
   local functions in that body are not cached. The body is then bound normally, against the real types, when the
   member is compiled, and reports its errors there.
8. **Overloaded and generic calls** (P5). In a body bound with the placeholder, overload resolution of a method call
   or a constructor call whose arguments include a parameter being inferred records the candidates that are applicable
   with it in place (`OverloadResolution.RecordSharperPlaceholderCandidates`). The placeholder type converts to nothing,
   so the call fails and the complete results are at hand: a candidate whose only bad arguments are such parameters,
   passed with a ref kind their parameter accepts, is applicable with them in place, and type inference has already
   fixed its type parameters from the other arguments (an error type gives no bound). Overload resolution keeps one
   form of a `params` candidate, so the other form is computed there again. The candidates are kept on the placeholder
   per argument syntax and per lambda, local function or placeholder whose binding made the call, since a lambda is
   bound once per delegate type and bound nodes may be cloned; `UsesCollector` reads those of the binding the bound
   tree holds. No binding changes: the call still fails, and P4's error recovery binds the rest of the body as before.
9. **Other uses** (P6). The same collector reads assignments, local declarations and `return` statements of the
   placeholder binding: the value is the parameter behind the target's implicit conversion, which the binder creates
   with an error, and the target is the local, parameter, field, property or indexer, or the written return type of the
   member, lambda or local function that the collector is walking. `ref`, `out` and `in` arguments take the path of
   by-value ones, with the parameter's ref kind checked as overload resolution checks it; P5's records already counted
   only the candidates that take the argument's ref kind.
10. **Semantic model.** `GetTypeInfo` and `GetSymbolInfo` on a parameter's `infer`, and on a copied `infer` return,
   property, indexer or event type, return the type, with its nullable annotation, like `var`.

## Follow-ups

- The default value is bound twice: once for the type and once against it. The inference keeps only the type.
- A lambda's default value is bound for inference with the enclosing member as its containing member, since the
  lambda symbol does not exist yet when its parameter types are bound.
- In a group of members that depend on each other in more than one cycle, which members report CS20028 can depend on
  the order of binding. Every such member has a call as its default value, which reports CS1736 once the type is
  written.
- A property or an indexer that overrides or implements nothing does not infer its type from its getter yet (R4's open
  point, [#70](https://github.com/ok3mad/roslyn/issues/70)).
- A property, an indexer or an event whose search fails gets an error type, and C# then reports its own errors about
  matching the member as well (CS0534, CS0535, CS0738, CS0506, CS0115), as for methods.
- A copied type is checked like a written one only after it is copied; a member that is not found because the written
  types do not match is CS20043, not the C# error for the mismatch (CS0115, CS0535). CS20043 names the members that
  were ruled out ([#167](https://github.com/ok3mad/roslyn/issues/167)).
- The race in publishing a cycle's results has no deterministic test: [#168](https://github.com/ok3mad/roslyn/issues/168).
- Edit and Continue does not see a signature change that comes from the overridden or implemented member:
  [#169](https://github.com/ok3mad/roslyn/issues/169).
- P4 binds a member's body once more, without flow analysis, when it has an `infer` parameter that neither a default
  value nor an overridden member gives a type, once per compilation. Nothing else is rebound, but every new
  compilation (an edit) infers again.
- A call elsewhere to a member of a body cycle (CS20056) reports CS1503 for the argument whose parameter is an error
  type, after the cycle error.
- Edit and Continue does not see a signature change that comes from a body edit that changes a pinning use, as for
  the overridden member ([#169](https://github.com/ok3mad/roslyn/issues/169)).
- P5 binds the lambda arguments of a failed call to a `params` candidate once more for its other form, in the
  placeholder binding only.
- The P4 reasons "no use passes it directly to a call with one candidate", "'Generic' takes a type parameter there,
  which pins nothing yet" and "a params parameter pins nothing yet" keep their wording, although overloaded calls,
  fixed type parameters and the expanded form of `params` now pin; rewording them would change P4's diagnostics.
- An overloaded call that succeeds with a parameter being inferred (no such case is known: the placeholder type
  converts to nothing) records no candidates and is read as P4 reads a call that binds.
- P6: a return from a lambda without a written return type, `yield return`, array elements and deconstruction pin
  nothing. CS20057 names the first two uses that differ, not every use.
- The IDE does not know `infer` yet (R5, [#71](https://github.com/ok3mad/roslyn/issues/71)).

## Code

`Binder/Binder_SharperInferredParameterType.cs`, `Symbols/Source/SharperInferredParameterType.cs`, the `infer`
branch of `ParameterHelpers.MakeParameter`, `Binder.BindLambdaParameterType`, the `InferBeforeSignature` calls in
`SourceMemberMethodSymbol.LazyMethodChecks` and `SourcePropertySymbolBase.EnsureSignature`, and
`CSharpSemanticModel.GetSharperInferredParameter`. Step P2: `Symbols/Source/SharperInferredParameterType_OverriddenMember.cs`,
the `GetOverriddenSignature` calls in `ParameterHelpers`, `SourceOrdinaryMethodSymbol.MakeParametersAndBindReturnType`,
`SourceUserDefinedOperatorSymbolBase.MakeParametersAndBindReturnType`, `SourcePropertySymbol.ComputeType` and
`SourceEventSymbol.BindEventType`, `SourceOrdinaryMethodOrUserDefinedOperatorSymbol.IsSharperReturnTypeFromOverriddenMember`
and `CSharpSemanticModel.GetSharperInferredPropertyOrEventType` for the semantic model. Tests: `InferParameter_*` in
`SharperTests.cs`, `InferOverridden_*` in `SharperTests_InferFromOverridden.cs`, P2's other member kinds'
`InferMemberKinds_*` in `SharperTests_InferMemberKinds.cs`, and step P3's `ReadonlyInfer_*` in
`SharperTests_ReadonlyInfer.cs`.
Step P4: `Symbols/Source/SharperInferredParameterType_Body.cs`, `Symbols/Source/SharperPlaceholderMethodSymbol.cs`,
the `ReplaceOriginal` calls in `OverloadResolution.MethodInvocationOverloadResolution` and
`Binder.GetAccessibleConstructorsForOverloadResolution`, and `SharperInferredReturnType.SuspendInference`; tests
`InferFromBody_*` in `SharperTests_InferFromBody.cs`. Step P5: `Binder/Semantics/OverloadResolution/OverloadResolution_Sharper.cs`,
called from `OverloadResolution.MethodInvocationOverloadResolution` and `ObjectCreationOverloadResolution`,
`SharperPlaceholderMethodSymbol.RecordCandidates` and `GetCandidates`, and `UsesCollector.AddUseFromCandidates`; tests
`InferFromOverloads_*` in `SharperTests_InferFromOverloads.cs`. Step P6: `UsesCollector.VisitAssignmentOperator`,
`VisitLocalDeclaration` and `VisitReturnStatement` in `SharperInferredParameterType_Body.cs`; tests `InferFromUses_*` in
`SharperTests_InferFromUses.cs`.
