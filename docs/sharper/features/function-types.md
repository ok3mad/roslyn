# Function types

**Status:** Prototype, merged into `main`. Requires `/features:sharper`. Issue:
[#21](https://github.com/ok3mad/roslyn/issues/21).

## Summary

`(A, B) -> R` is a short spelling of `System.Func<A, B, R>`, and `(A, B) -> void` of `System.Action<A, B>`:

```csharp
(int, int) -> int add = (a, b) => a + b;   // Func<int, int, int>
() -> string thunk = () => "ready";        // Func<string>
(string) -> void print = Console.Write;    // Action<string>
() -> void tick = () => { };               // Action

static (int) -> int Compose((int) -> int f, (int) -> int g) => x => g(f(x));
```

A function type works wherever C# accepts a type: locals, fields, properties, parameters, return types, generic
arguments (`List<(int) -> int>`), `typeof`, `default`, casts, `as`, `is`, `using` aliases, explicitly typed lambda
parameters and patterns:

```csharp
if (handler is (int) -> int f) { ... }
var name = o switch { () -> void => "action", (int) -> int f => "f" + f(1), _ => "other" };
```

## Rules

- **It is the delegate.** `(int) -> int` and `Func<int, int>` are the same type: they convert to each other, clash as
  overloads (CS0111), and `typeof` gives the same `Type`. Variance comes from `Func`, so a `(object) -> string`
  converts to a `(string) -> object`. Metadata, Quick Info and error messages show `Func<...>`.
- **Lambdas keep `=>`.** The arrow `->` is only used in types. In expressions it keeps its C# meaning, pointer member
  access, so `x -> F(x)` is still `(*x).F(x)`.
- **The arrow is right-associative:** `(int) -> (int) -> int` is `Func<int, Func<int, int>>`.
- **A suffix applies to the return type:** `(int) -> int?` is `Func<int, int?>`, and `(int) -> int[]` is
  `Func<int, int[]>`. The same goes for `T[..]` (#19) and `T~` (#20).
- **Parentheses apply a suffix to the function type itself:** `((int) -> int)?` is `Func<int, int>?`,
  `((int) -> int)[]` is `Func<int, int>[]`, `((int) -> int)~` is `Task<Func<int, int>>` and `((int) -> int)[..]` is
  `IEnumerable<Func<int, int>>`. Such parentheses are allowed only around a function type, once: `((int))` and
  `(((int) -> int))` are not types.
- **Parameters are types,** without names or modifiers. A parameter of tuple type needs its own parentheses:
  `((int, int)) -> int`.
- **Constraints are those of `Func` and `Action`:** a pointer or a static type cannot be a parameter or return type
  (CS0306, CS0718), and a ref struct can only be one where `Func` allows it (.NET 9 and later). The error is reported
  where the `Func` spelling reports it: on the offending parameter or return type.
- **Nullable annotations** flow into the type arguments, with the same warnings as the `Func` spelling.

### Parsing

`( types ) ->` in a type position starts a function type. Where C# already gives such tokens a meaning, C# wins:

- `(p)->F(x);`, `(p)->F = 1;` and `x = (p)->F;` stay pointer member accesses.
- `((p)->F)(x)` stays an invocation through a pointer member access, not a cast to a function type.
- A parenthesized pointer member access keeps its meaning wherever C# can continue it: `((p)->F)[i]`, `((p)->F)?.X`,
  `((p)->F)?[i]`, `((p)->F) ? a : b` and `(((p)->F))(x)`. The parenthesized type `((p)->F)` is as ambiguous as
  `(p)->F`, so `((p)->F)? x;` and `((p)->F)[] x;` are declarations, which C# rejects.
- `(p) -> F x;` is a declaration, because it is never valid C#, the same way that `p.F x;` is a declaration.
- In a pattern, `( types ) ->` starts a function type, as in `x is (int) -> int f`, `case () -> void a:` and
  `{ P: (int) -> int f }`. In C# a parenthesized pattern or expression cannot be followed by `->`, so all of these
  are syntax errors there, `e is (p)->F` included; the constant pattern through a pointer is written `e is p->F`,
  which keeps its meaning. Where the type cannot be followed by what comes next, the pattern is parsed as in C#.
  `x is (int) -> int ? a : b` is a conditional, as `x is int ? a : b` is.
- In a generic argument list, `F<(int) -> int>(x)` is a generic invocation, as C# resolves `F<A, B>(x)`. This changes
  the meaning of `F < (p)->b > (x)`, which compares a pointer member with user-defined operators, and is accepted as a
  theoretical break.

## Diagnostics

| Code | Message |
| --- | --- |
| CS20007 | A function type cannot name its parameters. Remove the name '{0}'. |
| CS20008 | A function type parameter cannot be '{0}', because a function type is a 'System.Func' or 'System.Action' delegate. |
| CS20009 | A function type cannot have more than {0} parameters, because no 'System.Func' or 'System.Action' delegate has more. |

## Limitations

- **`ref`, `out` and `in` parameters, ref returns and more than 16 parameters** have no `Func` or `Action`, and are
  errors for now (CS20008, CS20009; a ref return reports CS1073). The design is decided
  ([#56](https://github.com/ok3mad/roslyn/issues/56)), see [Planned: ref parameters](#planned-ref-parameters).
- **Parameter names** such as `(int width, int height) -> int` are not supported yet (CS20007), because `Func` cannot
  carry them. They are planned the way tuples carry their element names, in four steps under
  [#57](https://github.com/ok3mad/roslyn/issues/57): names in the syntax, ignored by the binder
  ([#212](https://github.com/ok3mad/roslyn/issues/212)); names on the `Func` or `Action` type symbol within a compilation
  ([#213](https://github.com/ok3mad/roslyn/issues/213)); signature help, Quick Info and inline hints
  ([#214](https://github.com/ok3mad/roslyn/issues/214)); and names in metadata through a synthesized attribute
  ([#215](https://github.com/ok3mad/roslyn/issues/215)). Names will never change conversions or overload resolution.
- **IDE:** the editor knows nothing about Sharper yet, so completion and formatting may not handle function types.

## Planned: ref parameters

Decided in [#56](https://github.com/ok3mad/roslyn/issues/56), not built yet. A function type that `Func` and
`Action` cannot express is the anonymous delegate C# synthesizes for a lambda's natural type:

```csharp
(ref int, out string) -> bool tryRead = (ref int pos, out string s) => { s = ""; return true; };
() -> ref int slot = () => ref s_value;
var f = (ref int x) => x++;
(ref int) -> void g = f;                      // the same type: one anonymous delegate per signature
```

- **What it covers:** `ref`, `out`, `in`, `ref readonly` and `scoped` parameters, `ref` and `ref readonly` returns,
  and more than 16 parameters. Everything else stays `Func` or `Action`, so `(int) -> int` keeps meaning
  `Func<int, int>`. `params` and `this` stay errors.
- **Identity:** within a compilation, every function type with the same parameter types, ref kinds, `scoped`
  and return is one type, the same one `var` gives a lambda with that signature (`GetMethodGroupOrLambdaDelegateType`,
  `Binder_Expressions.cs`; anonymous delegates compare by signature). Like all anonymous delegates it is invariant, and
  like any delegate it does not convert to a named delegate of the same shape; `f.Invoke` does.
- **Visibility:** the anonymous delegate is internal, so these function types are allowed wherever an internal type
  is: locals, private and internal members, public members of internal types, and type arguments in them. This is the
  boundary `infer` already has for anonymous types (CS0050). A member visible outside the assembly reports a new Sharper
  error that says to declare a named delegate, instead of the inconsistent accessibility errors (CS0050 to CS0053),
  and so does an `infer` member whose inferred type is an anonymous delegate.
- **Friend assemblies** (`InternalsVisibleTo`) see the other assembly's anonymous delegate as an unnamed type: they
  can call it and pass lambdas to it, but their own function type of the same shape is a different type.
- **Parameter names** ([#57](https://github.com/ok3mad/roslyn/issues/57)) must not become part of the anonymous
  delegate's identity, as they are not part of `Func`'s.

Rejected, because no other representation gives one type across assemblies:

- **A public delegate synthesized in each assembly.** Two assemblies that use the same shape each define a copy, and
  the CLR keeps them distinct, so a delegate from one does not pass to the other, and `List<F>` or `F[]` of them never
  convert. Reusing a referenced assembly's copy when there is exactly one makes any private use of a function type a
  public type others bind to, which its owner can no longer remove, and lets a new reference change what a type means.
  Converting by wrapping one delegate in another allocates and breaks `==` and event removal.
- **Type equivalence as in NoPIA** (`TypeIdentifierAttribute`): it needs one canonical definition in a referenced
  assembly (`MetadataDecoder.cs`), does not allow generic types (CS1768), and the runtime supports it only on
  Windows.
- **A runtime library of delegates.** Ref kinds cannot be type arguments, so each combination needs its own type:
  5ⁿ·4 shapes for n parameters, 3,114 up to four parameters. Any cut-off is arbitrary, and the library goes against
  "no new library".

## Implementation

The function type is the syntax node `FunctionTypeSyntax` (`Syntax.xml`), Sharper's first new syntax node. Its kind,
`SyntaxKind.FunctionType`, is 9900: Sharper syntax nodes count up from there, apart from upstream's kinds. Its
parameters reuse `FunctionPointerParameterSyntax`.

The parser (`LanguageParser.cs`) changes only when Sharper is on:

- `ScanTupleOrSharperFunctionType` replaces the tuple-type lookahead (`ScanTupleType`). It scans tuples and function
  types in one pass, so nested parentheses are not scanned twice, and returns the return type's flags for the
  ambiguous shape `(name) -> name`, so that the statement and type-argument rules treat it like a qualified name.
- `IsSharperFunctionTypeStart` and `ParseSharperFunctionType` parse the type in `ParseUnderlyingType`.
- `LooksLikeTypeOfPattern` (`LanguageParser_Patterns.cs`) accepts a function type start, so patterns parse it as their
  type. In a pattern the return type is parsed in the pattern's type mode, which keeps `? a : b` a conditional.
- `ScanCast` checks `IsAmbiguousSharperFunctionType` first, so `((p)->F)(x)` is never a cast. No C# type that starts
  with `(` is ambiguous, so an ambiguous one is a parenthesized `(p)->F` and is never a cast either: `(((p)->F))(x)`.
- A parenthesized function type is the node `ParenthesizedTypeSyntax` (`SyntaxKind.ParenthesizedType`, 9902), which
  the binder binds to the type it encloses. `ScanTupleOrSharperFunctionType` reads one element in parentheses as a
  parenthesized type when it is a function type, which `ScanSharperFunctionTypeElement` tells from where the element
  ends: a suffix after a function type belongs to its return type, so a function type ends where its return type does,
  while a tuple or parenthesized type ends at its own `)` or suffix.

The binder (`Binder_Symbols.cs`, `BindFunctionType`) binds the parameter and return types and constructs the
well-known `Func` or `Action` through `BindSharperTypeAlias` (`Binder_SharperTypes.cs`), checking constraints as for
any generic type, with each parameter and return type as the location of its type argument. There is no bound node, so flow
analysis, lowering and IOperation need no changes. Tests are the `FunctionType_*` tests in `SharperTests.cs`.
