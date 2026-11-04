# Inferred parameter type (`infer`)

**Status:** Prototype of steps P1 to P3, merged into `main`; P2 for properties, indexers, operators and events on
branch `sharper/infer-p2-member-kinds`. Requires `/features:sharper`. Issues:
[#72](https://github.com/ok3mad/roslyn/issues/72) (P1), [#73](https://github.com/ok3mad/roslyn/issues/73) (P2),
[#166](https://github.com/ok3mad/roslyn/issues/166) (P2 for the other member kinds),
[#74](https://github.com/ok3mad/roslyn/issues/74) (P3), under [#27](https://github.com/ok3mad/roslyn/issues/27).

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
be reassigned. Uses in the body (P4 to P6) are the next steps of the
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
  implements (below). A parameter with neither reports CS20027.
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
7. **Semantic model.** `GetTypeInfo` and `GetSymbolInfo` on a parameter's `infer`, and on a copied `infer` return,
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
