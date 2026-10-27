# Task type

**Status:** Prototype, merged into `main`. Requires `/features:sharper`. Issue:
[#20](https://github.com/ok3mad/roslyn/issues/20).

## Summary

`T~` is a short spelling of `System.Threading.Tasks.Task<T>`, and `void~` of the non-generic `Task`:

```csharp
User~ FindAsync(int id);                     // Task<User>
void~ SaveAsync(User user);                  // Task
Order?~ GetOrderByIdAsync(int orderId);      // Task<Order?>
IList<Order>~ GetOrdersByIdsAsync(int[] ids);

static async int~ Answer() { await Task.Yield(); return 42; }
```

A task type works wherever C# accepts a type: locals, fields, properties, parameters, return types (including
`async` methods, local functions and lambdas with an explicit return type), generic arguments (`List<int~>`), arrays,
tuple elements, `typeof`, `default`, casts, `as`, type patterns (`o is int~ t`), `out` variables, `foreach`
variables and `using` aliases.

## Rules

- **It is the task.** `int~` and `Task<int>` are the same type: they convert to each other, clash as overloads
  (CS0111), and `typeof` gives the same `Type`. Metadata, Quick Info and error messages show `Task<...>`.
- **A running task, not a lazy value.** `T~` is a `Task<T>`, which is hot in C#: creating it starts the work.
- **Suffixes stack left to right, innermost first**, like `?` and `[]`: `int~[]` is `Task<int>[]`, `int[]~` is
  `Task<int[]>`, `int[..]~` is `Task<IEnumerable<int>>`, `int~[..]` is `IEnumerable<Task<int>>` and `int~~` is
  `Task<Task<int>>`. In a function type the suffix applies to the return type: `(int) -> int~` is
  `Func<int, Task<int>>`, and `() -> void~` is `Func<Task>`.
- **Nullable annotations:** `string?~` is `Task<string?>` and `string~?` is a nullable `Task<string>`, with the same
  warnings as the `Task` spelling.
- **Constraints are those of `Task<T>`:** a pointer, a static type or a ref struct cannot be the result type (CS0306,
  CS0718, CS9244).
- **Not sugared:** `ValueTask<T>` and `IAsyncEnumerable<T>` stay spelled out. In six open-source applications,
  `ValueTask` is 0.4% of async declarations, mostly where an interface imposes it (`DisposeAsync`).

### Parsing

`~` is only a prefix operator in C# (bitwise complement), so no C# expression continues with `~` after an operand.
Every form of `T~` in a type position is therefore a syntax error in C#, or a statement C# rejects, and the task type
needs no disambiguation: `User~ x = y;` is a declaration, as `User* x = y;` already is in C#. The places where `~`
keeps its C# meaning:

- `c ? ~x : y` stays a conditional, also after `is` and `as` (`x is T ? ~a : b`) and in `using (c ? ~x : y)`.
- `(T)~x` stays a cast of a complement.
- In a tuple, `(A < B > ~c, d)` stays a tuple of comparisons, so a generic name whose type arguments are all names
  takes no `~` there: `(List<User>~ users, int count) = ...` does not declare. Write `val (users, count) = ...`, or
  spell the type `Task<List<User>>`. With a predefined type argument, `(List<int>~ a, int b) = ...` declares.

## Implementation

The task type is the syntax node `TaskTypeSyntax` (`Syntax.xml`), with an element type and the `~` token. Its kind,
`SyntaxKind.TaskType`, is 9901, next to `FunctionType` in Sharper's range for node kinds.

The parser (`LanguageParser.cs`) changes only when Sharper is on:

- `ScanType` and `ParseTypeCore` accept a `~` suffix next to `?`, `*` and `[]` (`IsSharperTaskTypeSuffix` for the
  tuple rule above). After `T?` the scan keeps the nullable flag, so `c ? ~x : y` stays a conditional.
- `IsPossibleTypedIdentifierStart` scans on after `name ~`, and `IsSharperVoidTask` lets `void` through before `~`,
  as before `*`.

The binder (`Binder_SharperTypes.cs`, `BindSharperTaskType`) constructs the well-known `Task<T>` through
`BindSharperTypeAlias`, or returns `Task` for `void~`. There is no bound node, so flow analysis, lowering and
IOperation need no changes. Tests are the `TaskType_*` tests in `SharperTests.cs`.
