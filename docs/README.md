# Documentation

This repository is the **Sharper** fork of [dotnet/roslyn](https://github.com/dotnet/roslyn). See the
[repository README](../README.md) for what that means.

## Sharper

- [Sharper overview](sharper/README.md): goals, design rules, feature status, and where the code is.
- [Features](sharper/features/): one document per Sharper feature.

## Inherited from Roslyn

Everything else in this folder comes from upstream Roslyn. It still describes the compilers, Workspaces, IDE and
tooling that Sharper is built on, and is kept so that it stays in step with upstream. Process, ownership, release,
support and CI information in it belongs to dotnet/roslyn, not to Sharper; documents where that matters carry a
notice at the top.

- [contributing](contributing/): building, debugging and testing the repository.
- [compilers](compilers/): compiler design and behavior notes.
- [features](features/): design notes for C# and VB language features.
- [analyzers](analyzers/), [roslyn-analyzers](roslyn-analyzers/): analyzer APIs and the Roslyn analyzer packages.
- [ide](ide/), [razor](razor/): IDE and Razor tooling.
- [infrastructure](infrastructure/), [wiki](wiki/), [specs](specs/): build infrastructure, the old GitHub wiki, and
  older specifications.
