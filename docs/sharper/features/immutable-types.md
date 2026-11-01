# Immutable types

**Status:** Prototype of slice 1. Requires `/features:sharper`. Issue: [#139](https://github.com/ok3mad/roslyn/issues/139),
designed in [#92](https://github.com/ok3mad/roslyn/issues/92) (implementation plan,
[Feature 10](../planning/implementation-plan.md#feature-10-immutable-types-slice-1-design)).

## Summary

`[Immutable]` claims that a type is deeply immutable, and the compiler checks the claim:

```csharp
[Immutable] sealed record Money(decimal Amount, string Currency);   // proven
[Immutable] sealed record Box<T>(T Value);                           // immutable when T is
[Immutable] record Person(string Name);                              // exact instances only
[Immutable, ImmutableHierarchy] abstract record Shape;               // every subclass must be immutable too

[Immutable] sealed record Order(List<string> Lines);
// error CS20035: 'Order' is marked [Immutable], but it cannot be proven immutable: 'Order.Lines' has type
// 'System.Collections.Generic.List<string>', which comes from another assembly and is not marked [Immutable].
```

`val` and `readonly` make a binding immutable; this makes a value deeply immutable, so that "depends only on its
arguments" can mean something for purity ([#36](https://github.com/ok3mad/roslyn/issues/36)). Immutable means
observationally immutable: hold everything outside the object fixed, and every repeated access returns an equivalent
result (the same exact type, every field equivalent, primitives compared by bits), and the same reference when the
result's type is not immutable. Identity is not observable to pure code. Slice 1 still checks observers against the
earlier, stricter rule that they return the same object, so they may not allocate
([#153](https://github.com/ok3mad/roslyn/issues/153)). Nothing changes at run time; the type is emitted as written, plus the attribute.

## The attributes

| Attribute | On | Meaning |
| --- | --- | --- |
| `[Immutable]` | class, struct | A conditional claim: the build fails unless the compiler proves it. It describes exact instances and puts no requirement on subclasses. |
| `[ImmutableHierarchy]` | class, interface | Every subclass at any depth, or every implementation, is immutable. On a class it needs `[Immutable]`. |
| `[UnobservableWrite]` | field | A hard claim: a cache field whose writes nobody can observe. It may stay non-`readonly`, and an observer may write it. |
| `[AssumePure]` | property, method | A hard claim: the observer (or operator, or method an observer calls) is pure. |
| `[Phantom]` | type parameter | The type parameter is not part of the state, so `Tagged<List<int>, int>` can still be immutable. Checked. |

The attributes live in the namespace `Sharper`. A Sharper file uses them without declaring them: when no type of
that name is in scope, `[Immutable]` binds to `Sharper.ImmutableAttribute`, which the compiler synthesizes and embeds
into the assembly (internal, with `[Embedded]`), as it does for `IsReadOnlyAttribute`. A type of that name in scope
wins, and a compilation that declares `Sharper.ImmutableAttribute` itself uses its own. Other assemblies recognize
them by name, so a library compiled with Sharper carries its claims to its consumers. In a C# file the names do not
bind (CS0246).

## What the compiler proves

A type is immutable for its exact instances when:

- **Fields.** Every instance field's type is immutable for every value of its static type (below), and, in a class,
  the field is `readonly` (or `[UnobservableWrite]`). A struct's own fields need not be `readonly`: a struct value has
  no aliases, so writes go to a copy. This covers auto-properties and captured primary constructor parameters.
- **No writes after construction.** In a class, no set accessor other than `init`, no non-`readonly` field and no
  captured primary constructor parameter (CS20036, "it is mutable"). An instance event is mutable in classes and
  structs. Construction ends after the object initializer, so `init` and `with` are part of it.
- **Base.** The base class is immutable for its exact instances (CS20037). `object` is.
- **Observers.** Instance getters and indexers, `Equals` and `GetHashCode` only read fields, constants, parameters and
  locals, and call other pure observers: those on `this`, on a value whose static type is immutable, and non-virtual
  ones of immutable types. Operators of the attested types count as pure when they return a value type (`decimal +`,
  `string ==`). Anything else, such as `DateTime.Now`, `new`, boxing, string concatenation, `HashCode.Combine` or a
  write to a field, is reported with the expression, unless the observer is `[AssumePure]`. Records' generated members
  and auto-property getters are pure. Ordinary methods are not observers and may be impure.

Every value of a static type `T` is immutable when `T` is immutable for its exact instances and is a value type, a
sealed class, or `[ImmutableHierarchy]`. An interface is only with `[ImmutableHierarchy]`. So a field of type
`Person` (an unsealed `[Immutable]` record) or `object` is not proven, but `Money` is. Arrays, delegates, pointers and
`dynamic` are never immutable, and read-only views (`IReadOnlyList<T>`) are not either.

**Generics.** A generic type is immutable for an instantiation when its type arguments are immutable for every value
of their static types, except those of `[Phantom]` type parameters. `Box<int>` is immutable, `Box<List<int>>` is not;
that is not an error, just not immutable. A `[Phantom]` type parameter used in a field or a base type argument is
CS20042. Conditions are declared, never inferred, so polymorphic recursion such as `Nested<T>(Nested<List<T>>? Next)`
terminates (and fails here, because `List<T>` is not immutable).

**Cycles.** `sealed record Node(int Value, Node? Next)` is immutable: types that depend on each other are assumed
immutable until one of them is disproven. The answer and the reason in each diagnostic do not depend on which type the
analysis reaches first; a type in a cycle names its own problem if it has one, and otherwise the member it depends on.

**Other assemblies.** Across assemblies only claims count: a type from a reference is immutable only when it carries
`[Immutable]` (trusted, never checked again) or is one of the built-in attestations:

- primitives, `string`, `decimal`, `nint`, `nuint`, enums, and `object` for its exact instances;
- `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `Half`, `Int128`, `UInt128`, `Index`,
  `Range`, `Version`, `BigInteger`, `Complex`;
- `Nullable<T>`, `ValueTuple` and `KeyValuePair<TKey, TValue>`, when their type arguments are;
- the immutable collections (`ImmutableArray<T>`, `ImmutableList<T>`, `ImmutableDictionary<TKey, TValue>` and the
  rest) and the frozen collections (`FrozenSet<T>`, `FrozenDictionary<TKey, TValue>`, with their hierarchies), when
  their type arguments are.

## Where the checks run

Only the project that writes a claim checks it, and only in Sharper files. A type in an `[ImmutableHierarchy]`,
whether marked or not, must be immutable: in a Sharper file that is error CS20038, in a C# file of the same compilation
warning CS20039 (only when the hierarchy is declared in a Sharper file of that compilation, so compilations without
Sharper behave as upstream). A subclass in another compilation, a mock or a proxy is trusted.

## Diagnostics

| Code | Severity | When |
| --- | --- | --- |
| CS20035 | Error | `[Immutable]` cannot be proven, with the reason. |
| CS20036 | Error | `[Immutable]` is disproven: state can be written after construction. |
| CS20037 | Error | `[Immutable]` with a base class that is not immutable. |
| CS20038 | Error | A Sharper type in an `[ImmutableHierarchy]` is not immutable. |
| CS20039 | Warning | The same for a C# type of the same compilation. |
| CS20040 | Error | `[ImmutableHierarchy]` on a class without `[Immutable]`. |
| CS20041 | Error | `[UnobservableWrite]` on a static or `readonly` field, or outside a class marked `[Immutable]`. |
| CS20042 | Error | A `[Phantom]` type parameter used in the state. |

## Not covered

- `where T : immutable` and the run-time guard for optimizations ([#140](https://github.com/ok3mad/roslyn/issues/140)).
  A type parameter is not known to be immutable outside its own type's analysis.
- A checked lazy-field construct to replace `[UnobservableWrite]` ([#141](https://github.com/ok3mad/roslyn/issues/141)).
- The attestation format, version ranges and trusted sources ([#142](https://github.com/ok3mad/roslyn/issues/142)); the
  attestations above are hard-coded.
- A keyword, the Edit and Continue rude edit for an edit that makes a type mutable, and a fixer that offers `sealed`.
- An internal `[Immutable]` class whose subclasses are all visible and immutable is not yet treated as closed
  (`PROTOTYPE(sharper)` in `SharperImmutability.IsClosed`).
- Inference across assemblies: an unmarked type from a reference is not immutable even if its fields would be.
- Observers that build an immutable result (`new`, string concatenation), and identity comparisons as impure
  ([#153](https://github.com/ok3mad/roslyn/issues/153)).
- The structural equivalence that optimizations and other assemblies use ([#154](https://github.com/ok3mad/roslyn/issues/154)).
- The immutability of a value is not used anywhere yet: purity, memoization and lazy `T[..]` come later.

## Code

`Symbols/SharperImmutability.cs` (under `src/Compilers/CSharp/Portable`), one instance per compilation
(`CSharpCompilation.SharperImmutability`):

- `IsImmutable(type, exact)` is the query. `GetInfo` caches a `DefinitionInfo` per type definition and finds cycles
  with Tarjan's algorithm on a thread-static stack; `ResolveComponent` and `Explain` make the result and its reason
  independent of the order. `Analyze` holds the field, base and observer rules, `PurityWalker` the observer bodies,
  bound with `TryGetBodyBinder().BindMethodBody` and the diagnostics discarded.
- `CheckDeclaration` is called from `SourceMemberContainerTypeSymbol.AfterMembersChecks`.
- The attributes: `TryGetAttributeKind` and `GetAttributeType`, called from
  `Binder.BindNonGenericSimpleNamespaceOrTypeOrAliasSymbol` when lookup finds nothing in a Sharper file;
  `SynthesizedEmbeddedSharperAttributeSymbol`; `PEAssemblyBuilder.GetEmbeddedTypes` emits the ones binding used.
- Tests: `SharperTests_ImmutableTypes.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics`), part of `SharperTests`.
