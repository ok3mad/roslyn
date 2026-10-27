# Sharper in VS Code

The C# extension for VS Code runs Roslyn's language server. Pointing it at a server built from this repository makes the
editor understand Sharper: diagnostics, hover, go to definition and find references work on `.cx` files. Completion,
classification and refactorings know nothing about Sharper syntax yet, so `val` is colored as an identifier.

Two pieces come from this repository:

- **The language server** (`Microsoft.CodeAnalysis.LanguageServer`), which the C# extension runs instead of the one it
  ships with.
- **The compiler toolset package** (`Microsoft.Net.Compilers.Toolset`). A project that references it builds with the
  Sharper compiler, and gets `**/*.cx` in its `Compile` items, which is also how the language server learns that the
  `.cx` files belong to the project.

## Releases

The [Sharper release](../../.github/workflows/sharper-release.yml) workflow builds both and publishes them as a
GitHub release named `sharper-<date>.<run>`. Run it from the Actions tab, or push a tag named `sharper-*`. Each release
has:

- `sharper-vscode.tar.gz`: `server/` (the language server, framework-dependent on .NET 10, for every OS),
  `packages/` (the toolset package) and `VERSION`.
- The toolset `.nupkg` on its own, versioned `<Roslyn version>-sharper.<build>`.

## The devcontainer

[`.devcontainer/sharper`](../../.devcontainer/sharper/devcontainer.json) sets everything up:

1. Clone this repository and open it in VS Code with the
   [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension
   (Docker required), or create a GitHub Codespace from it.
2. Run **Dev Containers: Reopen in Container** and pick **Sharper playground**.
3. The container (the .NET 10 SDK image) installs the latest release into `/opt/sharper` with
   [`setup.sh`](../../.devcontainer/sharper/setup.sh), and opens the [playground](playground/) project with the C#
   extension pointed at `/opt/sharper/server`.
4. Open `Program.cx`. Uncomment `first = 10;` to see a Sharper error. `dotnet run` builds and runs it.

To update, run `.devcontainer/sharper/setup.sh` (or `setup.sh sharper-<id>` for a given release) and then
**Developer: Reload Window**.

The playground has its own `global.json`, `nuget.config`, `Directory.Build.*` and `.editorconfig` so that none of the
Roslyn repository's build settings apply to it. Its `nuget.config` takes the toolset package only from
`/opt/sharper/packages`, and the project references it with the floating version `*-*`, so it always builds with the
installed release.

## Without the devcontainer

The same setup works on any machine with the .NET 10 SDK:

1. Download and extract `sharper-vscode.tar.gz` from a release.
2. In VS Code settings, set `dotnet.server.path` to `<extracted>/server/Microsoft.CodeAnalysis.LanguageServer.dll`
   and map `.cx` to C#:

   ```json
   "files.associations": { "*.cx": "csharp" }
   ```

   Without the mapping VS Code does not send `.cx` files to the C# extension.
3. Add `<extracted>/packages` as a package source and reference `Microsoft.Net.Compilers.Toolset` from the project,
   with `PrivateAssets="all"`. Do not also add `*.cx` to `Compile` yourself.

C# Dev Kit loads projects with its own project system, which has not been tried with Sharper; the devcontainer leaves
it out.

## With a local build

While working on the compiler, use the "launch vscode with language server" task in `.vscode/tasks.json`. It builds the
language server and starts VS Code with `DOTNET_ROSLYN_SERVER_PATH` pointing at it.
