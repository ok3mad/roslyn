# `readonly` parameters

**Status:** Prototype. Requires `/features:sharper`. Issues: [#23](https://github.com/ok3mad/roslyn/issues/23),
[#97](https://github.com/ok3mad/roslyn/issues/97).

## Summary

`readonly` as the first modifier of a parameter makes the parameter impossible to reassign in the body:

```csharp
int Clamp(readonly int value, readonly int max)
{
    value = Math.Min(value, max);   // error CS1656: Cannot assign to 'value' because it is a 'readonly parameter'
    return value;
}
```

It completes the [mutability grid](../planning/brainstorm.md#binding-mutability) for parameters, next to
[`readonly` locals](readonly-locals.md) and `(val x) => ...` lambda parameters ([`val` locals](val-locals.md)). In C#,
`readonly` without `ref` before it is error CS9190; with the flag off it still is.

It works on the parameters of methods, local functions, lambdas, anonymous methods, constructors, primary
constructors (also of records), indexers, operators and delegates, and on `params` and extension method `this`
parameters (`readonly this int x`).

## Rules

`readonly` parameters follow the [`readonly` local rules](readonly-locals.md#rules), with messages that name the
'readonly parameter':

- Every write is an error: assignment, compound assignment, `++`/`--`, deconstruction, passing as `ref` or `out`,
  `ref` locals, and writes to fields of a struct parameter (CS1656, CS1657, CS1654), also from lambdas and local
  functions.
- A non-readonly member of a struct `readonly` parameter runs on a copy and warns: CS20013, the parameter form of
  CS20010. Members found by pattern (`GetEnumerator`, property patterns, `await`) do the same, also in async methods.
- `(readonly x) => ...` is an implicitly typed lambda parameter that cannot be reassigned, the same as `(val x) => ...`.

## Not part of the signature

`readonly` is a rule about the body. Nothing is emitted to metadata: the parameter has the same type, `RefKind` and
attributes as without it, so C# callers and other compilers see an ordinary parameter. It plays no part in
overriding, interface implementation, partial methods, delegate conversions or overload resolution:

- An override or implementation may add or drop `readonly`; each body follows its own declaration.
- For a partial method, the implementing declaration decides. `readonly` on the defining declaration does nothing.
- `void H(int x)` and `void H(readonly int x)` are the same signature (CS0111).
- On a member without a body (abstract and interface members, delegates) `readonly` is allowed and has no effect.

C# treats `scoped` differently: it changes what callers may pass, so it is part of the signature and is checked
when overriding, implementing and converting. `readonly` changes nothing a caller can observe.

## `ref`, `in`, `out` and `scoped`

`readonly` first means "cannot be reassigned"; `ref readonly` keeps its C# meaning, "cannot write through the ref".
As for locals, the combinations match C#'s `readonly ref` fields:

| Declaration | Write through the ref (`r = 5`) | Ref reassignment (`r = ref j`) |
| --- | --- | --- |
| `ref int r` | allowed | allowed |
| `ref readonly int r` (C#) | error CS8331 | allowed |
| `readonly ref int r` | allowed | error CS1656 'readonly parameter' |
| `readonly ref readonly int r` | error CS8331 | error CS1656 'readonly parameter' |
| `readonly in int r` | error CS8331 | error CS1656 'readonly parameter' |
| `readonly out int r` | allowed (and required) | error CS1656 'readonly parameter' |

- `readonly scoped Span<int> s` is a scoped parameter that cannot be reassigned. `scoped readonly` keeps C#'s errors,
  as `scoped` must be followed by `ref`, `in` or `out`.
- `readonly` must be the first modifier. Anywhere else (`in readonly`, `params readonly`, `this readonly`) it keeps
  C#'s CS9190, and a second `readonly` that is not right after `ref` is CS1107.

## Primary constructors

A `readonly` primary constructor parameter cannot be written in field initializers or members. When members capture
it, its compiler-generated field is `readonly` (`initonly`), as it is in a `readonly struct`, so the field matches
the rule. A record's `readonly` parameter is only read by the generated property's initializer.

## Not covered

- Extension block receivers (`extension(readonly int x)`) and function pointer parameters (`delegate*<readonly int,
  void>`) keep CS9190: a receiver is shared by the members of the block, and a function pointer has no body.
- `IParameterSymbol` has no read-only flag, as `ILocalSymbol` has none for `val` and `readonly` locals
  ([#88](https://github.com/ok3mad/roslyn/issues/88)). The IDE does not know `readonly` parameters yet.
- CS20013 warns for every non-readonly member, like CS20003 and CS20010
  ([#84](https://github.com/ok3mad/roslyn/issues/84)).
- Immutable-by-default parameters, with a marker for mutable ones, are a strictness ladder setting for later, not
  syntax ([#23](https://github.com/ok3mad/roslyn/issues/23)). `readonly` stays the opt-in form either way.

## Code

`ParameterHelpers.IsSharperReadOnly` decides from syntax: a leading `readonly`, the feature on, and not an extension
block receiver. `ParameterHelpers.CheckParameterModifiers` skips CS9190 for that modifier. Such a parameter is always
a `SourceComplexParameterSymbolBase` (simple parameter symbols keep no syntax), whose `IsSharperReadOnly` flag
brings in the `val` checks: `CheckParameterValueKind` reports the 'readonly parameter' cause
(`MessageID.IDS_READONLYPARAMETER`), and `Binder.IsSharperValValue` makes the struct copies. The primary constructor
backing field is made readonly in `Binder.CapturedParametersFinder`.
