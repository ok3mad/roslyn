---
coverage: IDE-layer (src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}) test base classes & authoring conventions
---

# IDE — Testing

Layer-specific test guidance for the IDE/Workspaces stack under
`src/{Features,Analyzers,EditorFeatures,...}`.

## Test workspace (MEF-dependent tests)

```csharp
[UseExportProvider]
public class MyTests
{
    [Fact]
    public async Task TestSomething()
    {
        var workspace = EditorTestWorkspace.CreateCSharp("class C { }");
        var document = workspace.Documents.Single();
    }
}
```

## Conventions

- Use `[UseExportProvider]` for any test that depends on MEF services (a missing
  attribute typically surfaces as an unrelated-looking failure).
- Analyzer tests inherit from
  `AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor` (and the VB
  equivalents).
- For analyzer/code-fix tests, use `TestInRegularAndScriptAsync` /
  `TestMissingInRegularAndScriptAsync`.
- Prefer raw string literals (`"""..."""`) over verbatim strings (`@"..."`) for
  test source code.
- Keep tests focused — avoid unnecessary intermediary assertions; use `.Single()`
  rather than asserting a count then indexing.
- When a test needs a feature waiter, retrieve the concrete
  `AsynchronousOperationListenerProvider` from the export provider and call
  `GetWaiter`; do not retrieve the interface and cast the listener.
- Language Server orchestration tests can pass additional MEF parts to
  `LanguageServerTestComposition.GetSharedExportProvider`. A controllable
  `PartNotDiscoverable` project loader can provide deterministic design-time
  build timing and results without invoking MSBuild.

## Where IDE tests run

- `src/EditorFeatures/{CSharpTest,Test,Test2}` (Quick Info, classification and inlay hint suites among them) target
  `net472` and use WPF, so they build on Windows only. On Linux, use `src/Features/CSharpTest` (`net10.0`; it has
  `TestWorkspace` and the code action bases), `src/EditorFeatures/CSharpTest2` (keyword recommenders) and
  `src/LanguageServer/ProtocolUnitTests`, and call the services (`QuickInfoService`, `IInlineTypeHintsService`,
  `Classifier`) directly.
- A `TestWorkspace` built from `<Workspace>` XML has Windows line breaks in its documents: take offsets from the
  document's text, not from the markup.
- Sharper IDE tests: `Features="sharper"` on `<Project>` turns Sharper on for the project, and
  `<Document FilePath="Test.cx">` for one document of a plain C# project; test both, and a plain `.cs` document that
  must not change. Examples: `src/Features/CSharpTest/Sharper/`.
