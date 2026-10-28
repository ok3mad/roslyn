# Implicit `it`

**Status:** Prototype. Requires `/features:sharper`. Issue: [#24](https://github.com/ok3mad/roslyn/issues/24).

## Summary

When a method takes a function of one argument, the argument can be written as the function's body alone, with the
parameter called `it`:

```csharp
xs.Select(it.Name)                                // xs.Select(x => x.Name)
xs.Select(Process(it, a))                         // xs.Select(x => Process(x, a))
Retry(load, times: 3, when: it is Unavailable)    // when: r => r is Unavailable
```

The argument becomes an ordinary lambda `it => argument`: type inference, overload resolution, nullable analysis,
expression trees and lowering see the lambda C# would see. `it` is read-only, like a `val` lambda parameter.

## Rules

The decisions are in the brainstorm's [Decided](../planning/brainstorm.md#decided) list.

- **Only where C# finds no `it`.** A local, parameter, field, type or lambda parameter named `it` keeps its C# meaning,
  and `@it` always does. With the flag off, an unknown `it` is CS0103; with it on, an `it` that no argument claims is
  CS20019.
- **The innermost function argument.** An `it` belongs to the innermost argument around it whose parameter, in the
  chosen overload, is a delegate (or an expression tree of one) with one parameter. That whole argument is the lambda.
  `Process(it, a)` goes to `Select`, because `Process` takes a value. A bare `it` argument is never a lambda by
  itself.
- **Explicit lambdas never supply `it`.** In `x => ... it ...`, `it` belongs to a slot further out, so naming the
  parameter is the escape hatch: `xs.Select(x => ys.Where(it > x))`. Thunks are skipped the same way:
  `users.Select(Render(it, () => Load(it.Id)))` uses the user twice.
- **One meaning per lambda.** If an implicit lambda uses its `it` and also contains another implicit lambda, it is
  CS20020: `orders.Where(it.Lines.Any(it.Qty > 5))`. Telling the two apart needs every signature in between.
- **Value or function.** When an outer `it` exists and the method takes a value or a function at that argument (both
  readings bind), it is CS20021, so an overload added to a library can never move `it` silently. Without an outer
  `it`, the function is used: `users.FirstOrDefault(it.IsAdmin)` is the predicate.
- **Arguments only.** `var f = it * 2;`, a `return` and LINQ query clauses have no `it` (CS20019). The pipe `|>`
  ([#25](https://github.com/ok3mad/roslyn/issues/25)) will add stages as the outermost slot.
- **No declarations.** A variable declared in an argument list that contains an implicit lambda (`out var v`,
  `is int n`) is CS20022, and the arguments bind as values. `await` in an implicit lambda is CS20023: an implicit
  lambda cannot be `async`.

## Not supported yet

- Calls only: method calls, extension methods and delegate invocations. Object creation (`new C(it.X)`), indexers,
  constructor initializers and attributes report CS20019 for now.
- `F(Make(it), out var v)`: a declaration anywhere in the arguments turns the lambda off.
- The semantic model finds the lambda (`GetSymbolInfo` and `GetTypeInfo` on `it`, `GetOperation`), but
  `LookupSymbols` inside an implicit lambda does not list `it`, and speculative binding inside one sees the outer
  scope.
- Edit and Continue does not see implicit lambdas, because the IDE's matcher finds lambdas by their syntax.

## How it works

The binder decides at each call (`Binder_SharperImplicitIt.cs`), with no parser change:

1. `HasSharperImplicitItArgument` looks for an argument with a free `it`: one that C# lookup does not find, or finds
   only as the parameter of an outer implicit lambda. `it` declared inside the argument and member names (`x.it`,
   `it:`) do not count.
2. `SharperCandidateTakesFunction` keeps only the arguments where some candidate, instance or extension, has a
   one-parameter delegate at that position. Without this filter every call with an `it` would bind twice, and a failed
   call binds its lambdas again for error recovery, which multiplied with the nesting depth.
3. The call binds with those arguments as implicit lambdas (`SharperImplicitItUnboundLambdaState`, modeled on query
   clauses' `QueryUnboundLambdaState`: an `UnboundLambda` whose syntax is the argument expression). It wins when it
   binds without errors and every lambda uses its `it` (`AllSharperImplicitItLambdasUseIt`), unless an outer `it` is
   in scope.
4. Otherwise the call binds again with the arguments as values, so that their `it` belongs further out. If both bind,
   CS20021. If neither does, the lambda trial is reported when its lambdas use `it`, and the value trial otherwise.

The lambda body binds in an `ExecutableCodeBinder` rooted at the argument expression, as a query clause's does, so the
body gets its own scope. After binding, the body checks CS20020 (`SharperItUseFinder`) and CS20022.

`LambdaSymbol.IsSharperImplicitIt` and `LambdaParameterSymbol.IsSharperImplicitIt` mark the lambda and its `it`. The
parameter reuses the `val` lambda parameter flag for read-only. Lowering, closures and expression trees need no
change; three assertions that expect lambda syntax accept an argument expression in a Sharper file
(`LambdaUtilities.IsSharperImplicitItLambdaBody`).

## Follow-ups

See the `PROTOTYPE(sharper)` comments: declarations next to an implicit lambda, `LookupSymbols` and Edit and Continue.
Object creation and the other argument lists are the next step, then pipe stages with #25.

## Code

| What | Where |
| --- | --- |
| Binder | `src/Compilers/CSharp/Portable/Binder/Binder_SharperImplicitIt.cs`; the call in `Binder_Invocation.cs` (`bindArgumentsAndInvocation`); argument binding in `Binder_Expressions.cs` (`BindArgumentAndName`); CS20019 in `BindIdentifier`; CS20023 in `Binder_Await.cs` |
| Symbols | `LambdaSymbol.IsSharperImplicitIt`, `LambdaParameterSymbol.IsSharperImplicitIt`, `UnboundLambdaState.IsSharperImplicitIt` |
| Assertions | `LambdaUtilities.IsSharperImplicitItLambdaBody`, used by `BoundLambda`, `ClosureConversion.GetLambdaId` and `SynthesizedClosureEnvironment` |
| Tests | `SharperTests.ImplicitIt_*` |
