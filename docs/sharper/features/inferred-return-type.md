# Inferred return type (`infer`)

**Status:** Prototype of step R1, on branch `sharper/infer-return-r1`. Requires `/features:sharper`. Issues:
[#67](https://github.com/ok3mad/roslyn/issues/67) (R1), under [#22](https://github.com/ok3mad/roslyn/issues/22).

## Summary

`infer` in place of a return type says that the compiler finds the type from the body:

```csharp
private static infer Greeting(int id) => $"Hello, {id}!";   // string

void M()
{
    infer Twice(int x) => x * 2;                              // local function: int
}
```

The method is an ordinary method with that return type: callers, the semantic model, IOperation and metadata all see
`string Greeting(int)`. Step R1 covers expression bodies of private methods and local functions. Block bodies (R2),
inferred methods that use each other (R3) and other members (R4) are the next steps of the
[work breakdown](../planning/implementation-plan.md#type-inference-work-breakdown).

## Rules

- **What is inferred.** The return type is the type `var r = e;` would give `r`, where `e` is the expression body,
  or `void` when `e` is a call that returns nothing. A lambda or method group gets its natural function type
  (`infer F() => () => 1` returns `Func<int>`). A private method can return an anonymous type.
- **Nothing to infer from.** A body with no type of its own reports CS20013, where `var` reports CS0815, CS8917 or
  CS8716: `null`, `default`, a lambda or method group without a natural type, `throw`, `new()`, a collection
  expression, `(1, null)`. A body with its own errors reports only those, and the return type is an error type.
- **Nullable.** In a `#nullable enable` context, the top-level annotation comes from the flow state of the body, as
  for the return type of a lambda: `infer F(string? s) => s` returns `string?`, and `s ?? ""` or `s!` give `string`.
  Where nullable annotations are disabled at `infer`, the type is oblivious, as a written type would be.
- **A type named `infer`.** Like `var`, `infer` is a keyword only when no type named `infer` is in scope. With the
  flag off it is an ordinary type name: CS0246 when there is none.
- **Using itself.** A method whose body uses its own inferred return type, directly (`Fib` calling `Fib`) or as a
  method group, reports CS20017 and must write its return type, as in F#. This stays an error in later steps.
- **Using another inferred method.** A body that calls or converts another method whose return type is inferred
  reports CS20016 in R1. Step R3 orders the inference instead. Methods with written return types can call inferred
  ones freely.
- **The checks of a written return type** apply to the inferred one: file-local types in signatures (CS9051),
  restricted types (CS1599), and nullable and native integer attributes.

## Not supported yet

Each of these reports CS20014 ("not supported ... yet") or CS20015, instead of CS0246:

| Where | Error | Step |
| --- | --- | --- |
| Block bodies | CS20014 | R2 |
| `async` methods and local functions | CS20014 | R2 |
| Members that are not private (public, internal, protected, `override`) | CS20014 | R4 |
| Explicit interface implementations, partial methods, extension block members | CS20014 | R4 / P2 |
| `ref` returns | CS20014 | R4 |
| Properties and indexers | CS20014 | R4 (open point) |
| Operators and conversion operators | CS20014 | R4 |
| Members with no body (`abstract`, `extern`, interface members, partial definitions) | CS20015, permanent | |

Elsewhere (fields, locals, parameters, delegates, type arguments) `infer` is an ordinary type name. Parameters are
step P1.

## How it works

No syntax, bound node or lowering is added: `infer` parses as a type name, and the binder decides.

1. **Signature.** `Binder.BindTypeOrSharperInferKeyword` binds the return type syntax; like `var`, a successful type
   lookup wins. For a supported `infer`, `SourceOrdinaryMethodSymbol.MethodChecks` stores a
   `SharperInferredReturnType` instead of a return type, and skips the checks that need the type.
   `LocalFunctionSymbol.ComputeReturnType` does the same.
2. **Inference on first use.** `ReturnTypeWithAnnotations` asks the `SharperInferredReturnType`, which binds the
   expression body with the method body's own binder (`Binder.BindSharperInferredReturnType`), takes the natural type
   as `var` does, and runs `NullableWalker.AnalyzeSharperInferredReturnType` for the annotation. The body is bound again
   when the method is compiled, against the inferred type; that binding reports the body's errors.
3. **No lock.** `MethodChecks` holds a per-method lock, so inferring there could deadlock two methods that use each
   other on two threads. The inference runs outside of any lock and publishes the type once; binding is
   deterministic, so threads that race compute the same type, and only the winner reports diagnostics.
4. **Uses of inferred methods.** While a body is inferred, a thread-static frame records the inference in progress.
   Any read of an inferred return type during it (the method itself, or another inferred method) is recorded and gets
   an error type that is never cached, so the result never depends on which method was inferred first. R3 turns the
   frame into a stack: a nested read infers the other method, and a method already on the stack is a cycle.
5. **Semantic model.** `GetTypeInfo` and `GetSymbolInfo` on `infer` return the inferred type, with its nullable
   annotation, like `var`.

The design for the later steps, and why, is in the
[implementation plan](../planning/implementation-plan.md#feature-5-inferred-return-type-step-r1).

## Follow-ups

- A local function's body is analyzed for nullability on its own, so a captured `var` local counts as maybe null
  (`PROTOTYPE(sharper)` in `Binder_SharperInferredReturnType.cs`).
- CS20016 and CS20017 are reported on `infer`, not on the use in the body, because the use is found through the
  symbol, which has no syntax.
- The IDE does not know `infer` yet (R5, [#71](https://github.com/ok3mad/roslyn/issues/71)).

## Code

`Binder/Binder_SharperInferredReturnType.cs`, `Symbols/Source/SharperInferredReturnType.cs`, the `InferredReturnType`
classes nested in `SourceOrdinaryMethodSymbol` and `LocalFunctionSymbol`, and
`NullableWalker.AnalyzeSharperInferredReturnType`. Tests: `Infer_*` in `SharperTests.cs`.
