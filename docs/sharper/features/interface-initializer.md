# Interface initializer

**Status:** Prototype. Requires `/features:sharper`. Issue: [#30](https://github.com/ok3mad/roslyn/issues/30). Builds on
[lambda to interface](lambda-to-interface.md) (#29).

## Summary

`new I { ... }` with an object initializer creates an instance of a generated class that implements the interface,
where C# reports CS0144. It covers most stubbing without a mocking library:

```csharp
interface IProfiles
{
    Profile Load(int id);
    void Save(Profile p);
    string Source { get; }
    int Count => 0;
}

var saved = new List<Profile>();
var profiles = new IProfiles { Load = id => new Profile("Pro"), Save = saved.Add, Source = "test" };

// Strict: the members left unassigned throw when called.
var strict = new IProfiles { Load = id => id switch { 42 => new Profile("Pro"), _ => new Profile("Free") }, _ = Unexpected };
```

The C# it stands for:

```csharp
sealed class Profiles(Func<int, Profile> load, Action<Profile> save, string source) : IProfiles
{
    public Profile Load(int id) => load(id);
    public void Save(Profile p) => save(p);
    public string Source => source;
}

var profiles = new Profiles(id => new Profile("Pro"), saved.Add, "test");
```

## Rules

- **Only where C# reports an error** (design rule 2). C# cannot create an interface (CS0144), except a COM interface with
  a `CoClass`, which keeps its meaning. `new I()` without an initializer stays CS0144, and so does a target-typed
  `new() { ... }` for now.
- **Members.** A method is assigned a function: a lambda, a method group or a delegate. It converts to the delegate of
  the method, as for a [lambda converted to an interface](lambda-to-interface.md), so the lambda's parameter types come
  from the method. A property with a getter and no setter is assigned its value. Members of base interfaces count. A
  member with a default implementation may be assigned, which replaces the default (`Count = 7`), or left out, which
  keeps it.
- **What cannot be assigned** (CS20072): generic methods, overloaded names (two members called `Over`), properties
  with a setter, indexers and events. An unknown name is C#'s CS0117, a member assigned twice CS1912, and a nested
  initializer (`Load = { }`) CS0747.
- **Unassigned members.** Every member without a default implementation must be assigned (CS20071, which lists them).
  `_ = Unexpected` instead makes each unassigned one throw `InvalidOperationException` when it is called, with a message
  that names the member: `'IProfiles.Save(Profile)' is not assigned in the interface initializer that created this
  object ('_ = Unexpected').` This covers every kind, including those that cannot be assigned: setters, indexers, events
  and generic methods. `_ = Unexpected` is a form of this initializer only (there is no `Unexpected` symbol), and it
  needs no runtime library, because the class and the throw are generated into the assembly.
- **Static abstract members** make the interface impossible to instantiate this way (CS20073).
- **Order.** Values are evaluated in source order.
- **Nullable analysis** checks each value as an assignment to the member: `Name = null` for a `string Name { get; }`
  warns (CS8625), and a lambda's body is checked against the method's signature.
- **Expression trees** cannot contain an interface initializer (CS20074).

Argument matching uses a switch expression in the lambda (`id => id switch { 42 => ..., _ => ... }`), and interaction
verification captures a list (`Save = saved.Add`), so `When(...).Returns(...)` and `Verify(...)` need no library.

## Lowering

The initializer lowers to `new <IProfiles>Stub(value0, value1, ...)`, a private sealed class nested in the type being
compiled, generic over the interface definition's type parameters. It has a field per assigned member, in source
order, filled by its constructor. A method's implementation invokes its delegate, and a property's getter returns its
value. With `_ = Unexpected`, each unassigned member without a default throws. There is one class per interface
definition, list of assigned members and `_ = Unexpected` in a type, so two initializers that assign the same members
in the same order share it. Implementations are explicit, under the members' own names.

## Not covered yet

Each is a `PROTOTYPE(sharper)` comment or listed here:

- A target-typed `new() { ... }`.
- The semantic model finds no symbol for the member names on the left of `=`.
- Setters, indexers, events, generic methods and overloaded names cannot be assigned.
- Edit and Continue does not map the generated classes between generations.
- An IOperation shape: analyzers see an `OperationKind.None` node whose children are the values.

## Where the code is

| What | Where |
| --- | --- |
| Binding | `Binder/Binder_SharperInterfaceInitializer.cs`, called from `BindInterfaceCreationExpression` before CS0144 |
| Bound node | `BoundSharperInterfaceCreation` |
| Generated class | `Lowering/SharperInterfaceStub.cs`, kept per type in `TypeCompilationState.SharperInterfaceStubs` |
| Tests | `SharperTests_InterfaceInitializer.cs` |
