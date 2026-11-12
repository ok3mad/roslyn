# Lambda to single-method interface

**Status:** Prototype, step a. Requires `/features:sharper`. Issue: [#29](https://github.com/ok3mad/roslyn/issues/29).
The interface initializer that builds on it is [#30](https://github.com/ok3mad/roslyn/issues/30).

## Summary

A lambda or method group converts to an interface with exactly one abstract method, where C# reports an error:

```csharp
interface IProfiles { Profile Load(int id); }

static string Render(int user, IProfiles profiles) => profiles.Load(user).Plan;

Render(user, id => new Profile("Pro"));     // a lambda
Render(user, LoadProfile);                  // a method group
IProfiles cached = id => cache[id];         // a variable, capturing 'cache'
var set = new SortedSet<string>((a, b) => b.CompareTo(a));   // IComparer<string>
using (IDisposable undo = () => Restore()) { ... }
```

The C# it stands for:

```csharp
sealed class Profiles(Func<int, Profile> load) : IProfiles
{
    public Profile Load(int id) => load(id);
}

Render(user, new Profiles(id => new Profile("Pro")));
```

## Rules

- **Only where C# reports an error** (design rule 2). C# binds first. Assignments, initializers and returns retry
  with the conversion when C# finds no conversion. Calls and object creations retry overload resolution when C#
  finds no applicable candidate. So a delegate overload always wins (`M(IF)` next to `M(Func<int, int>)`), and so does
  `object` (`N(object)` next to `N(IF)` with a typed lambda). An extension method that takes a delegate wins over an
  instance method that takes an interface, because the retry covers instance and extension methods together.
  `ICloneable` and `ISerializable` keep their C# meaning: every delegate implements them, and C# already converts a
  lambda with a natural type to them.
- **Eligible interfaces.** Exactly one abstract member, which is a non-generic instance method, across the interface
  and its base interfaces. Members with a default implementation do not count, and neither does a base member that an
  interface in between implements with a default. An abstract property, event or indexer, a static abstract member, a
  generic method or a second abstract method makes the interface ineligible, and C#'s CS1660 stays. Generic interfaces
  are eligible: `IComparer<T>` and `IDisposable` are, `IEqualityComparer<T>` (two methods) and `IEnumerable<T>` are not.
- **Through a delegate.** The function converts to the delegate for the interface's method, `Func`, `Action` or a
  synthesized delegate for `ref`, `out` and `in` parameters, exactly as it would to that delegate. The lambda's
  parameter types come from the method. Method groups, `static`, `async` (for a `Task`-returning method), implicit `it`
  and pipe stages (`x |> Show(it, Hex)`) all work. Nullable analysis checks the body against the method's signature.
- **Errors.** A lambda whose parameters do not match the method keeps C#'s CS1660. One whose parameters match but whose
  body has errors reports the body's errors, as for a delegate. In a call, if the retry finds applicable candidates,
  its errors are reported (CS0121 between two interface overloads); otherwise C#'s are. An expression tree cannot
  contain the conversion (CS20070).
- **Type inference.** Not yet (step b): a type parameter that appears only in the interface must be fixed by another
  argument. `Apply<T>(T x, IMap<T> map)` works with `Apply(3, x => x + 1)`, because `3` fixes `T`.

## Lowering

The conversion lowers to `new <IProfiles>Lambda(delegate)`, a private sealed class nested in the type being compiled.
It is generic over the interface definition's type parameters, without their variance, and has one per interface
definition and type. It stores the delegate in a field and implements the method explicitly, under the method's own
name (`Load`), by invoking it. Nesting gives the class access to private and protected interfaces. Two interfaces of
the same name in one type get `<IProfiles>Lambda` and `<IProfiles>Lambda_1`. The delegate lowers as usual, so a lambda
that captures nothing keeps its cached delegate. The class itself is allocated on every conversion.

A stack trace shows the lambda's own frame (`C.<>c.<Main>b__0_0`). The class's forwarding frame is often missing,
because the JIT devirtualizes and inlines the call on a sealed class.

## Semantic model and IOperation

`GetTypeInfo` on the lambda has no `Type` and the interface as `ConvertedType`, like a lambda converted to a delegate.
The lambda's parameters have the method's types. `GetConversion` returns an implicit conversion of an internal kind:
`IsAnonymousFunction` and `IsMethodGroup` are false. The IOperation tree has an implicit `OperationKind.None` node typed
as the interface, whose child is the delegate creation.

## Not covered yet

Each is a `PROTOTYPE(sharper)` comment or listed here:

- Type inference through the interface's method (step b).
- Casts, `?:`, switch expressions, delegate invocations and indexers: the conversion is only tried in assignments,
  initializers, returns, method calls and object creations (including `: base(...)`).
- A delegate-typed value (not a lambda or method group) converting to an interface.
- Edit and Continue does not map the generated classes between generations.
- An `IConversionOperation` with a public conversion kind.
- A cached singleton for lambdas that capture nothing, return lifting, and `[Pure]` on the interface method.

## Where the code is

| What | Where |
| --- | --- |
| Eligibility, the delegate type, classification, the retries | `Binder/Binder_SharperFunctionToInterface.cs` |
| Retry sites | `GenerateConversionForAssignment` and `CreateReturnConversion` (`Binder_Statements.cs`), `BindMethodGroupInvocation` (`Binder_Invocation.cs`), `TryPerformOverloadResolutionWithConstructorSubset` (`Binder_Expressions.cs`) |
| Conversion kind | `ConversionKind.SharperFunctionToInterface`, classified only by `Conversions.WithSharperFunctionToInterface()` |
| Bound node | `BoundSharperFunctionToInterface` |
| Generated class | `Lowering/SharperInterfaceAdapter.cs`, kept per type in `TypeCompilationState.SharperInterfaceAdapters` |
| Tests | `SharperTests_LambdaToInterface.cs` |
