# Inferred return type (`infer`)

**Status:** Prototype of steps R1 (merged into `main`) and R2 (branch `sharper/infer-return-r2`). Requires
`/features:sharper`. Issues: [#67](https://github.com/ok3mad/roslyn/issues/67) (R1),
[#68](https://github.com/ok3mad/roslyn/issues/68) (R2), under [#22](https://github.com/ok3mad/roslyn/issues/22).

## Summary

`infer` in place of a return type says that the compiler finds the type from the body:

```csharp
private static infer Greeting(int id) => $"Hello, {id}!";   // string

void M()
{
    infer Twice(int x) => x * 2;                              // local function: int
}

private static infer Classify(int n)                          // string
{
    if (n < 0) return "negative";
    return n == 0 ? "zero" : "positive";
}
private static infer Evens(int max) { for (var i = 0; i < max; i += 2) yield return i; }   // IEnumerable<int>
private static async infer LoadAsync(int id) { await Task.Yield(); return id; }          // Task<int>
```

The method is an ordinary method with that return type: callers, the semantic model, IOperation and metadata all see
`string Greeting(int)`. Steps R1 and R2 cover the bodies of private methods and local functions: expression bodies
(R1), block bodies, iterators and `async` (R2). Inferred methods that use each other (R3) and other members (R4) are
the next steps of the [work breakdown](../planning/implementation-plan.md#type-inference-work-breakdown).

## Rules

- **What is inferred.** The return type is the type `var r = e;` would give `r`, where `e` is the expression body,
  or `void` when `e` is a call that returns nothing. A lambda or method group gets its natural function type
  (`infer F() => () => 1` returns `Func<int>`). A private method can return an anonymous type.
- **Block bodies.** The return type is the one a lambda with the same body infers: the best common type of the
  values of the `return` statements, as for an implicitly typed array (`return 1;` and `return 2L;` give `long`,
  `return "a";` and `return null;` give `string`). With one `return`, it is the value's natural type, as for an
  expression body. A body with no `return` value (no `return`, `return;`, or only `throw`) returns `void`. Returns in
  lambdas and local functions in the body do not count.
- **Iterators** return `IEnumerable<T>`, where `T` is the best common type of the `yield return` values. A spread
  `yield return ..xs` yields values of the element type of `xs`; if `xs` is not a sequence, `..xs` is the range it is
  in C#. An iterator with only `yield break` reports CS20025.
- **`async`** methods and local functions return `Task`, or `Task<T>` of the best common type of the returned values,
  as `async` lambdas do. This also holds for expression bodies: `async infer F() => await G()` returns `Task<int>` when
  `G` returns `Task<int>`.
- **Nothing to infer from.** A single value with no type of its own reports CS20014, where `var` reports CS0815,
  CS8917 or CS8716: `null`, `default`, a lambda or method group without a natural type, `throw`, `new()`, a
  collection expression, `(1, null)`. Several values with no best common type (`return 1;` and `return "a";`) report
  CS20024, which lists their types; the error asks for the type to be written. A returned value with its own errors
  reports only those, and the return type is an error type. Other errors in a block body do not stop the inference.
- **`return;` next to `return x;`** infers from `x`, and the `return;` then reports CS0126, as it would with the type
  written.
- **Nullable.** In a `#nullable enable` context, the annotations come from the flow state of the returned values, as
  for the return type of a lambda: `infer F(string? s) => s` returns `string?`, and `s ?? ""` or `s!` give `string`.
  With several values, one maybe-null value makes the type annotated: `if (s is null) return null; return s;` returns
  `string?`. The same holds for the element of an iterator (`IEnumerable<string?>`) and the result of a task
  (`Task<string?>`); the sequence or task itself is not null. Where nullable annotations are disabled at `infer`, the
  type is oblivious, as a written type would be.
- **A type named `infer`.** Like `var`, `infer` is a keyword only when no type named `infer` is in scope. With the
  flag off it is an ordinary type name: CS0246 when there is none.
- **Using itself.** A method whose body uses its own inferred return type, directly (`Fib` calling `Fib`) or as a
  method group, reports CS20018 and must write its return type, as in F#. This stays an error in later steps.
- **Using another inferred method.** A body that calls or converts another method whose return type is inferred
  reports CS20017 in R1 and R2. Step R3 orders the inference instead. Methods with written return types can call inferred
  ones freely.
- **The checks of a written return type** apply to the inferred one: file-local types in signatures (CS9051),
  restricted types (CS1599), and nullable and native integer attributes.

## Not supported yet

Each of these reports CS20015 ("not supported ... yet") or CS20016, instead of CS0246:

| Where | Error | Step |
| --- | --- | --- |
| Async iterators (`async` with `yield return`) | CS20015 | Later: lambdas cannot be iterators, so there is no lambda rule to follow |
| Members that are not private (public, internal, protected, `override`) | CS20015 | R4 |
| Explicit interface implementations, partial methods, extension block members | CS20015 | R4 / P2 |
| `ref` returns | CS20015 | R4 |
| Properties and indexers | CS20015 | R4 (open point) |
| Operators and conversion operators | CS20015 | R4 |
| Members with no body (`abstract`, `extern`, interface members, partial definitions) | CS20016, permanent | |

Elsewhere (fields, locals, delegate return types, type arguments) `infer` is an ordinary type name. A parameter type
`infer` takes the type of the default value ([inferred parameter type](inferred-parameter-type.md), step P1).

## How it works

No syntax, bound node or lowering is added: `infer` parses as a type name, and the binder decides.

1. **Signature.** `Binder.BindTypeOrSharperInferKeyword` binds the return type syntax; like `var`, a successful type
   lookup wins. For a supported `infer`, `SourceOrdinaryMethodSymbol.MethodChecks` stores a
   `SharperInferredReturnType` instead of a return type, and skips the checks that need the type.
   `LocalFunctionSymbol.ComputeReturnType` does the same.
2. **Inference on first use.** `ReturnTypeWithAnnotations` asks the `SharperInferredReturnType`, which binds the
   body with the method body's own binder (`Binder.BindSharperInferredReturnType`). An expression body takes the
   natural type as `var` does. A block body is bound like the body of a lambda whose return type is being inferred:
   while it is bound, the binder sees no return type for the method (`Binder.IsSharperReturnTypeBeingInferred`), so
   `return` and `yield return` values keep their own types. The collected values go to the lambda's inference
   (`BoundLambda.InferSharperReturnType`, which wraps `async` results in `Task`), and an iterator's element type is
   wrapped in `IEnumerable<T>`. `NullableWalker.AnalyzeSharperInferredReturnType` collects the flow state of the
   same values for the annotations. The body is bound again when the method is compiled, against the inferred type;
   that binding reports the body's errors.
3. **No lock.** `MethodChecks` holds a per-method lock, so inferring there could deadlock two methods that use each
   other on two threads. The inference runs outside of any lock and publishes the type once; binding is
   deterministic, so threads that race compute the same type, and only the winner reports diagnostics.
4. **Uses of inferred methods.** While a body is inferred, a thread-static frame records the inference in progress.
   Any read of an inferred return type during it (the method itself, or another inferred method) is recorded and gets
   an error type that is never cached, so the result never depends on which method was inferred first. R3 turns the
   frame into a stack: a nested read infers the other method, and a method already on the stack is a cycle.
5. **Semantic model.** `GetTypeInfo` and `GetSymbolInfo` on `infer` return the inferred type, with its nullable
   annotation, like `var`.

The design for the later steps, and why, is in the implementation plan:
[step R1](../planning/implementation-plan.md#feature-6-inferred-return-type-step-r1) and
[step R2](../planning/implementation-plan.md#feature-6-inferred-return-type-step-r2).

## Follow-ups

- A local function's body is analyzed for nullability on its own, so a captured `var` local counts as maybe null
  (`PROTOTYPE(sharper)` in `Binder_SharperInferredReturnType.cs`).
- An inferred local function declared in the body of another inferred method or local function counts as a use of it
  (CS20017), even when it is never called, because binding its declaration reads its return type. R3 infers it in a
  nested frame instead.
- Values with no best common type are an error; an inferred union of their types waits for unions
  ([#31](https://github.com/ok3mad/roslyn/issues/31), parked).
- Async iterators, which would return `IAsyncEnumerable<T>`.
- CS20017 and CS20018 are reported on `infer`, not on the use in the body, because the use is found through the
  symbol, which has no syntax.
- The IDE does not know `infer` yet (R5, [#71](https://github.com/ok3mad/roslyn/issues/71)).

## Code

`Binder/Binder_SharperInferredReturnType.cs`, `Symbols/Source/SharperInferredReturnType.cs`, the `InferredReturnType`
classes nested in `SourceOrdinaryMethodSymbol` and `LocalFunctionSymbol`, and
`NullableWalker.AnalyzeSharperInferredReturnType`, the binder's checks of `IsSharperReturnTypeBeingInferred` (return
statements, `yield return` and the async helpers in `Binder_Statements.cs`, `InMethodBinder.GetIteratorElementType`)
and `BoundLambda.InferSharperReturnType`. Tests: `Infer_*` and `Infer_Block_*` in `SharperTests.cs`.
