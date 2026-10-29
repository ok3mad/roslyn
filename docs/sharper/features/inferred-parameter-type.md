# Inferred parameter type (`infer`)

**Status:** Prototype of step P1, on branch `sharper/infer-parameter-p1`. Requires `/features:sharper`. Issues:
[#72](https://github.com/ok3mad/roslyn/issues/72) (P1), under [#27](https://github.com/ok3mad/roslyn/issues/27).

## Summary

`infer` in place of a parameter type says that the type is the type of the default value:

```csharp
string Fetch(string url, infer retries = 3, infer name = "n", infer day = DayOfWeek.Monday) => ...;
// string Fetch(string url, int retries = 3, string name = "n", DayOfWeek day = DayOfWeek.Monday)
```

The parameter is an ordinary parameter with that type: callers, overload resolution, the semantic model, IOperation and
metadata all see `int retries = 3`. Step P1 takes the type from the default value only (source 2 of the
[brainstorm](../planning/brainstorm.md#inferred-parameter-types)). The overridden or implemented member (P2),
`readonly infer` (P3) and uses in the body (P4 to P6) are the next steps of the
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
- **No default value.** `infer x` without a default value reports CS20027. Step P2 takes such types from the
  overridden or implemented member.
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
   the rest of the cycle without the error.
5. **Semantic model.** `GetTypeInfo` and `GetSymbolInfo` on a parameter's `infer` return the inferred type, with its
   nullable annotation, like `var`.

## Follow-ups

- The default value is bound twice: once for the type and once against it. The inference keeps only the type.
- A lambda's default value is bound for inference with the enclosing member as its containing member, since the
  lambda symbol does not exist yet when its parameter types are bound.
- In a group of members that depend on each other in more than one cycle, which members report CS20028 can depend on
  the order of binding. Every such member has a call as its default value, which reports CS1736 once the type is
  written.
- The IDE does not know `infer` yet (R5, [#71](https://github.com/ok3mad/roslyn/issues/71)).

## Code

`Binder/Binder_SharperInferredParameterType.cs`, `Symbols/Source/SharperInferredParameterType.cs`, the `infer`
branch of `ParameterHelpers.MakeParameter`, `Binder.BindLambdaParameterType`, the `InferBeforeSignature` calls in
`SourceMemberMethodSymbol.LazyMethodChecks` and `SourcePropertySymbolBase.EnsureSignature`, and
`CSharpSemanticModel.GetSharperInferredParameter`. Tests: `InferParameter_*` in `SharperTests.cs`.
