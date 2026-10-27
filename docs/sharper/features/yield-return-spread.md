# Spread in `yield return`

**Status:** Prototype, merged into `main`. Requires `/features:sharper`. Issue: [#13](https://github.com/ok3mad/roslyn/issues/13).

## Summary

`yield return ..xs;` yields every element of `xs`, in order, from an iterator:

```csharp
IEnumerable<int> Spread(IEnumerable<int> xs)
{
    yield return 0;
    yield return ..xs;
    yield return 4;
}
```

It stands for the loop that C# needs today:

```csharp
foreach (var x in xs)
    yield return x;
```

The spread works with anything `foreach` accepts (arrays, strings, `IEnumerable<T>`, types with a `GetEnumerator`
pattern) and reads naturally in recursive iterators, such as a tree walk:

```csharp
IEnumerable<T> Walk(Node<T> node)
{
    yield return node.Value;
    foreach (var child in node.Children)
        yield return ..Walk(child);
}
```

## Rules

- **C# wins.** `..xs` is already a valid range expression in some iterators: in an iterator of `System.Range`, or of
  `object`, where the range is boxed. There it stays a range. It is a spread only where binding it as a range reports
  an error.
- **Elements convert implicitly** to the iterator element type, as a `yield return` value does. Unlike a `foreach`
  iteration variable, there is no explicit cast.
- **The source's enumerator is disposed**, both when the source is exhausted and when the consumer stops early,
  as with `foreach`.
- The usual `yield` restrictions apply: no spread in a `finally` or `catch` clause, or in a `try` block with a
  `catch`.
- Nullable analysis treats each element like a `foreach` element; a maybe-null collection or maybe-null elements
  warn as they would there.
- In the IOperation API, the statement is a `None` operation for now, with the collection as its child. A real
  shape is [#46](https://github.com/ok3mad/roslyn/issues/46).

## Limitations

- **Async iterators:** not supported (CS20001), whether the source is synchronous or asynchronous. An async source
  would mean a hidden `await foreach`. Whether to support them is [#43](https://github.com/ok3mad/roslyn/issues/43).
- **`dynamic` operands:** `..d` converts `d` to `Index` at runtime, so it is valid C# and stays a range. The compiler
  warns (CS20002); convert `d` to a sequence type to spread it. Where a range is not valid (for example an iterator of
  `int`), `..d` spreads and enumerates `d` dynamically. Deciding at runtime instead is [#42](https://github.com/ok3mad/roslyn/issues/42).
- **Ref struct sources** such as spans are rejected, with an error about the span's enumerator (CS4007). A clearer
  error is [#44](https://github.com/ok3mad/roslyn/issues/44).
- **Edit and Continue** works for methods with spreads: edits before, after and inside a spread keep its states and
  its hoisted enumerator, so a suspended iterator continues where it was. As with `foreach`, changing the type of the
  source creates new hoisted fields, so an iterator suspended inside that spread cannot continue it. Replacing a
  `yield return x;` with `yield return ..xs;` (or the reverse) is a delete and an insert, so an iterator suspended
  there throws when resumed ([#45](https://github.com/ok3mad/roslyn/issues/45)).
- **Debugging:** the source of a spread is evaluated, and the enumerator advanced, under hidden sequence points; only
  the yield of each element stops on `yield return ..xs;`.
- **Recursion:** each element of a recursive spread passes through every level, so a tree walk costs O(depth) per
  element ([#47](https://github.com/ok3mad/roslyn/issues/47)).

## Implementation

The binder (`Binder_Statements.cs`, `BindYieldReturnStatement`) trial-binds the operand as a C# range and creates a
`BoundYieldReturnSpreadStatement` if that fails. Lowering (`LocalRewriter_Yield.cs`) rewrites the node into a
`foreach` of plain yield returns before the iterator state machine is built. The generated `try`/`finally` and the
yield return share the statement's syntax, so the iterator rewriter (`IteratorMethodToStateMachineRewriter.PushFrame`)
gives the finalize state the second relative ordinal of that syntax node, which Edit and Continue uses to map states.
The IDE's Edit and Continue matcher (`SyntaxComparer` in `src/Features/CSharp/Portable/EditAndContinue`) never matches
a spread with a yield return of a single value. Tests are the `YieldReturnSpread_*` tests in `SharperTests.cs`,
`EditAndContinueSharperTests.cs` (`src/Compilers/CSharp/Test/Emit2`) and `SharperStatementMatchingTests.cs`
(`src/Features/CSharpTest`).
