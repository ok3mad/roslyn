<h1 align="center">Sharper</h1>

<p align="center"><b>An experimental dialect of C#, built as a fork of <a href="https://github.com/dotnet/roslyn">Roslyn</a>.</b></p>

> [!IMPORTANT]
> This repository is **not** the official .NET Compiler Platform. It is a fork of
> [dotnet/roslyn](https://github.com/dotnet/roslyn) that is being turned into the compiler for **Sharper**.
> Sharper is experimental, unsupported and not affiliated with Microsoft or the .NET Foundation.
> For the official C# and Visual Basic compilers, go to [dotnet/roslyn](https://github.com/dotnet/roslyn).

## What is Sharper?

Sharper is C# with a few extra rules and constructs: a stricter, more concise dialect. It is not a new compiler.
Sharper features live inside the Roslyn C# compiler. New syntax binds to its own bound nodes and lowers to ordinary
C# constructs, so a Sharper program compiles to normal IL and runs on normal .NET.

Three rules hold for every feature:

- **Opt-in.** Every Sharper feature is off unless you enable it with `/features:sharper`. Without the flag the
  compiler behaves exactly like upstream Roslyn.
- **No semantic breaking changes.** If Sharper syntax already means something in C#, the C# meaning wins. Sharper
  only gives a meaning to code that would otherwise be an error.
- **Normal output.** Features lower to existing constructs. The emitted assemblies need no new runtime or library.

See **[docs/sharper](docs/sharper/README.md)** for the design rules, feature status and how features are built.

## Features

| Feature | Example | Status |
| --- | --- | --- |
| [Spread in `yield return`](docs/sharper/features/yield-return-spread.md) | `yield return ..xs;` | Prototype, merged into `main` |
| [`val` locals](docs/sharper/features/val-locals.md) | `val x = Compute();` | In progress on `sharper/val` |

```csharp
// csc /features:sharper
IEnumerable<int> Numbers(IEnumerable<int> middle)
{
    yield return 0;
    yield return ..middle;  // yields every element of middle
    yield return 4;
}
```

## Trying it

Sharper is not published as a package yet. Build it from source:

```bash
./build.sh                                   # Build.cmd on Windows
dotnet build Compilers.slnf                  # compilers only
```

Sharper files will use the `.cx` extension; until the compiler recognizes it, write Sharper code in `.cs` files.
Then compile with the built `csc` and pass `/features:sharper`. In an SDK-style project, set
`<Features>sharper</Features>` and point the build at the locally built compiler (see
[Bootstrap Builds](docs/contributing/Bootstrap%20Builds.md)).

To run the Sharper tests:

```bash
dotnet test src/Compilers/CSharp/Test/Semantic/Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests.csproj -f net10.0 --filter "FullyQualifiedName~SharperTests"
```

## Relationship to Roslyn

Almost all of this repository is upstream Roslyn: the C# and Visual Basic compilers, the Workspaces API, the IDE
features, the language server and Razor. Sharper changes are kept small and confined to the C# compiler
(`src/Compilers/CSharp`), so that changes from upstream can still be merged in.

Most documents under [`docs/`](docs/README.md) come from upstream. They still describe the code correctly, but
their process, contact, release and CI information belongs to dotnet/roslyn. Those documents carry a notice at the
top.

## Contributing

Report bugs and suggest Sharper features in this repository's
[issues](https://github.com/ok3mad/roslyn/issues). See [CONTRIBUTING.md](CONTRIBUTING.md) for how Sharper work is
organized. Problems that also happen with the official compiler, without `/features:sharper`, belong in
[dotnet/roslyn](https://github.com/dotnet/roslyn/issues).

## License

Sharper keeps Roslyn's [MIT license](License.txt). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for
third-party components.
