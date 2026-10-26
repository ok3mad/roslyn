# Function types

**Status:** Prototype, on branch `sharper/function-types`. Requires `/features:sharper`. Issue:
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
arguments (`List<(int) -> int>`), `typeof`, `default`, casts, `as`, `using` aliases and explicitly typed lambda
parameters.

## Rules

- **It is the delegate.** `(int) -> int` and `Func<int, int>` are the same type: they convert to each other, clash as
  overloads (CS0111), and `typeof` gives the same `Type`. Variance comes from `Func`, so a `(object) -> string`
  converts to a `(string) -> object`. Metadata, Quick Info and error messages show `Func<...>`.
- **Lambdas keep `=>`.** The arrow `->` is only used in types. In expressions it keeps its C# meaning, pointer member
  access, so `x -> F(x)` is still `(*x).F(x)`.
- **The arrow is right-associative:** `(int) -> (int) -> int` is `Func<int, Func<int, int>>`.
- **A suffix applies to the return type:** `(int) -> int?` is `Func<int, int?>`, and `(int) -> int[]` is
  `Func<int, int[]>`. The same goes for `T^` (#20) and `T[..]` (#19) once they exist.
- **Parameters are types,** without names or modifiers. A parameter of tuple type needs its own parentheses:
  `((int, int)) -> int`.
- **Constraints are those of `Func` and `Action`:** a pointer or a static type cannot be a parameter or return type
  (CS0306, CS0718), and a ref struct can only be one where `Func` allows it (.NET 9 and later).
- **Nullable annotations** flow into the type arguments, with the same warnings as the `Func` spelling.

### Parsing

`( types ) ->` in a type position starts a function type. Where C# already gives such tokens a meaning, C# wins:

- `(p)->F(x);`, `(p)->F = 1;` and `x = (p)->F;` stay pointer member accesses.
- `((p)->F)(x)` stays an invocation through a pointer member access, not a cast to a function type.
- `(p) -> F x;` is a declaration, because it is never valid C#, the same way that `p.F x;` is a declaration.
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

- **`ref`, `out` and `in` parameters, and more than 16 parameters,** have no `Func` or `Action` (CS20008, CS20009).
  The lambda's natural type would synthesize an anonymous delegate, but that type is internal to one compilation and
  cannot appear in a public signature. What to do instead is [#56](https://github.com/ok3mad/roslyn/issues/56).
- **Parameter names** such as `(int width, int height) -> int` are not supported (CS20007), because `Func` cannot
  carry them ([#57](https://github.com/ok3mad/roslyn/issues/57)).
- **No suffix on the whole function type:** `((int) -> int)?` and `((int) -> int)[]` do not parse yet; write
  `Func<int, int>?` ([#58](https://github.com/ok3mad/roslyn/issues/58)).
- **Patterns:** `x is (int) -> int f` does not parse yet; write `x is Func<int, int> f`
  ([#59](https://github.com/ok3mad/roslyn/issues/59)).
- **Constraint errors in a local** span the whole function type instead of the offending parameter type.
- **IDE:** the editor knows nothing about Sharper yet, so completion and formatting may not handle function types.

## Implementation

The function type is the syntax node `FunctionTypeSyntax` (`Syntax.xml`), Sharper's first new syntax node. Its kind,
`SyntaxKind.FunctionType`, is 9900: Sharper syntax nodes count up from there, apart from upstream's kinds. Its
parameters reuse `FunctionPointerParameterSyntax`.

The parser (`LanguageParser.cs`) changes only when Sharper is on:

- `ScanTupleOrSharperFunctionType` replaces the tuple-type lookahead (`ScanTupleType`). It scans tuples and function
  types in one pass, so nested parentheses are not scanned twice, and returns the return type's flags for the
  ambiguous shape `(name) -> name`, so that the statement and type-argument rules treat it like a qualified name.
- `IsSharperFunctionTypeStart` and `ParseSharperFunctionType` parse the type in `ParseUnderlyingType`.
- `ScanCast` checks `IsAmbiguousSharperFunctionType` first, so `((p)->F)(x)` is never a cast.

The binder (`Binder_Symbols.cs`, `BindFunctionType`) binds the parameter and return types and constructs the
well-known `Func` or `Action`, checking constraints as for any generic type. There is no bound node, so flow
analysis, lowering and IOperation need no changes. Tests are the `FunctionType_*` tests in `SharperTests.cs`.
