// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.GoToDefinition;
using Microsoft.CodeAnalysis.InlineHints;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Sharper;

/// <summary>
/// Step R5 of Sharper type inference: Quick Info, inlay hints, classification and navigation for the <c>infer</c> return
/// and parameter types. Each test runs in a project with <c>/features:sharper</c> and in a <c>.cx</c> document of a
/// plain C# project; the same code in a plain C# document keeps the C# meaning of <c>infer</c>, a type name.
/// </summary>
[UseExportProvider]
[Trait(Traits.Feature, Traits.Features.QuickInfo)]
public sealed class SharperInferIdeTests
{
    public enum Mode
    {
        /// <summary>A <c>.cs</c> document of a project with <c>&lt;Features&gt;sharper&lt;/Features&gt;</c>.</summary>
        SharperProject,

        /// <summary>A <c>.cx</c> document of a plain C# project.</summary>
        CxDocument,

        /// <summary>A <c>.cs</c> document of a plain C# project: nothing Sharper.</summary>
        PlainCSharp,
    }

    private static TestWorkspace CreateWorkspace(string markup, Mode mode)
    {
        var features = mode == Mode.SharperProject ? """ Features="sharper" """ : "";
        var filePath = mode == Mode.CxDocument ? "Test.cx" : "Test.cs";
        return TestWorkspace.Create($"""
            <Workspace>
                <Project Language="C#" CommonReferencesNet8="true"{features}>
                    <Document FilePath="{filePath}">{SecurityElement.Escape(markup)}</Document>
                </Project>
            </Workspace>
            """, composition: FeaturesTestCompositions.Features);
    }

    private static async Task<string?> GetQuickInfoDescriptionAsync(string markup, Mode mode)
    {
        using var workspace = CreateWorkspace(markup, mode);
        var testDocument = workspace.Documents.Single();
        var document = workspace.CurrentSolution.GetRequiredDocument(testDocument.Id);
        var service = QuickInfoService.GetService(document);
        Contract.ThrowIfNull(service);
        var info = await service.GetQuickInfoAsync(document, testDocument.CursorPosition!.Value, SymbolDescriptionOptions.Default, CancellationToken.None);
        return info?.Sections.FirstOrDefault(s => s.Kind == QuickInfoSectionKinds.Description)?.Text;
    }

    /// <summary>The type hints as "offset:type [replaced text -> new text]", and the document's text (with its line breaks).</summary>
    private static async Task<(ImmutableArray<string> Hints, string Text)> GetTypeHintsAsync(string markup, Mode mode, bool displayAll = false)
    {
        using var workspace = CreateWorkspace(markup, mode);
        var document = workspace.CurrentSolution.GetRequiredDocument(workspace.Documents.Single().Id);
        var service = document.GetRequiredLanguageService<IInlineTypeHintsService>();
        var text = await document.GetTextAsync();
        // Display all (the hints shown while a key is held) replaces 'infer' only where the hints are off.
        var typeOptions = InlineTypeHintsOptions.Default with { EnabledForTypes = !displayAll };
        using var _ = Microsoft.CodeAnalysis.PooledObjects.ArrayBuilder<InlineHint>.GetInstance(out var hints);
        await service.AddInlineHintsAsync(document, new TextSpan(0, text.Length), typeOptions, SymbolDescriptionOptions.Default, displayAll, hints, CancellationToken.None);
        return (hints.SelectAsArray(hint => Describe(hint, text)), text.ToString());
    }

    private static string Describe(InlineHint hint, SourceText text)
        => $"{hint.Span.Start}:{string.Concat(hint.DisplayParts.Select(p => p.Text)).Trim()}" +
           (hint.ReplacementTextChange is { } change ? $" [{text.ToString(change.Span)} -> {change.NewText}]" : "");

    private static async Task<string?> GetClassificationAsync(string markup, Mode mode)
    {
        using var workspace = CreateWorkspace(markup, mode);
        var testDocument = workspace.Documents.Single();
        var document = workspace.CurrentSolution.GetRequiredDocument(testDocument.Id);
        var span = testDocument.SelectedSpans.Single();
        var spans = await Classifier.GetClassifiedSpansAsync(document, span, CancellationToken.None);
        return string.Join(", ", spans.Where(s => s.TextSpan == span && !ClassificationTypeNames.AdditiveTypeNames.Contains(s.ClassificationType)).Select(s => s.ClassificationType).Distinct());
    }

    #region Quick Info

    [Theory, CombinatorialData]
    public async Task QuickInfo_InferredReturnType_OnMethodName(bool cxDocument)
    {
        var description = await GetQuickInfoDescriptionAsync("""
            class C
            {
                public static infer Gree$$ting(int id) => $"Hello, {id}!";
            }
            """, cxDocument ? Mode.CxDocument : Mode.SharperProject);
        Assert.Equal("string C.Greeting(int id)", description);
    }

    [Theory, CombinatorialData]
    public async Task QuickInfo_InferredReturnType_OnInfer(bool cxDocument)
    {
        var description = await GetQuickInfoDescriptionAsync("""
            class C
            {
                public static inf$$er Greeting(int id) => $"Hello, {id}!";
            }
            """, cxDocument ? Mode.CxDocument : Mode.SharperProject);
        Assert.Equal("class System.String", description);
    }

    [Fact]
    public async Task QuickInfo_InferredReturnType_OnCall()
    {
        var description = await GetQuickInfoDescriptionAsync("""
            using System.Collections.Generic;
            class C
            {
                static infer Evens(int max) { for (var i = 0; i < max; i += 2) yield return i; }
                void M() { _ = Ev$$ens(10); }
            }
            """, Mode.SharperProject);
        Assert.Equal("IEnumerable<int> C.Evens(int max)", description);
    }

    [Fact]
    public async Task QuickInfo_InferredReturnType_LocalFunction()
    {
        var description = await GetQuickInfoDescriptionAsync("""
            class C
            {
                void M()
                {
                    infer Twice(int x) => x * 2;
                    _ = Tw$$ice(1);
                }
            }
            """, Mode.SharperProject);
        Assert.Equal("int Twice(int x)", description);
    }

    [Theory, CombinatorialData]
    public async Task QuickInfo_InferredParameterType_OnInfer(bool cxDocument)
    {
        var description = await GetQuickInfoDescriptionAsync("""
            class C
            {
                void Fetch(string url, inf$$er retries = 3) { }
            }
            """, cxDocument ? Mode.CxDocument : Mode.SharperProject);
        Assert.Equal("readonly struct System.Int32", description);
    }

    [Fact]
    public async Task QuickInfo_InferredParameterType_OnParameterAndMethod()
    {
        Assert.Equal($"({FeaturesResources.parameter}) int retries = 3", await GetQuickInfoDescriptionAsync("""
            class C
            {
                void Fetch(string url, infer ret$$ries = 3) { }
            }
            """, Mode.SharperProject));

        Assert.Equal("void C.Fetch(string url, [int retries = 3])", await GetQuickInfoDescriptionAsync("""
            class C
            {
                void Fetch(string url, infer retries = 3) { }
                void M() => Fe$$tch("u");
            }
            """, Mode.SharperProject));
    }

    [Fact]
    public async Task QuickInfo_InferredParameterType_FromUse()
    {
        // Step P4: the parameter takes the type of the one 'Load' it is passed to.
        var description = await GetQuickInfoDescriptionAsync("""
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static infer He$$llo(infer id) => Load(id);
            }
            """, Mode.SharperProject);
        Assert.Equal($"({CSharpFeaturesResources.awaitable}) Task<string> C.Hello(int id)", description);
    }

    [Fact]
    public async Task QuickInfo_PlainCSharp_InferIsATypeName()
    {
        var description = await GetQuickInfoDescriptionAsync("""
            class infer { }
            class C
            {
                static inf$$er Greeting(int id) => null;
            }
            """, Mode.PlainCSharp);
        Assert.Equal("class infer", description);
    }

    [Fact]
    public async Task QuickInfo_TypeNamedInferInScope()
    {
        // Like 'var', a type named 'infer' wins.
        var description = await GetQuickInfoDescriptionAsync("""
            class infer { }
            class C
            {
                static infer Gree$$ting(int id) => null;
            }
            """, Mode.SharperProject);
        Assert.Equal("infer C.Greeting(int id)", description);
    }

    #endregion

    #region Inlay hints

    [Theory, CombinatorialData]
    public async Task InlayHints_ReturnAndParameterTypes(bool cxDocument)
    {
        var markup = """
            class C
            {
                static infer Greeting(int id) => $"Hello, {id}!";
                void Fetch(string url, infer retries = 3) { }
                void M()
                {
                    infer Twice(int x) => x * 2;
                }
            }
            """;
        var (hints, text) = await GetTypeHintsAsync(markup, cxDocument ? Mode.CxDocument : Mode.SharperProject);
        AssertEx.Equal(
        [
            $"{text.IndexOf("Greeting")}:string [infer -> string]",
            $"{text.IndexOf("retries")}:int [infer -> int]",
            $"{text.IndexOf("Twice")}:int [infer -> int]",
        ], hints);
    }

    [Fact]
    public async Task InlayHints_DisplayAll_ReplacesInfer()
    {
        var markup = """
            class C
            {
                static infer Greeting(int id) => $"Hello, {id}!";
            }
            """;
        var (hints, text) = await GetTypeHintsAsync(markup, Mode.SharperProject, displayAll: true);
        Assert.Equal($"{text.IndexOf("infer")}:string [infer -> string]", Assert.Single(hints));
    }

    [Fact]
    public async Task InlayHints_OperatorsAndCopiedTypes()
    {
        var markup = """
            interface IProfiles { int Load(string userId); }
            class Money
            {
                public decimal Amount;
                public static infer operator +(Money a, Money b) => new Money { Amount = a.Amount + b.Amount };
                public static implicit operator infer(Money m) => m.Amount;
            }
            class Profiles : IProfiles
            {
                public infer Load(infer userId) => 0;
            }
            """;
        var (hints, text) = await GetTypeHintsAsync(markup, Mode.SharperProject);
        AssertEx.Equal(
        [
            $"{text.IndexOf("operator +")}:Money [infer -> Money]",
            $"{text.IndexOf("(Money m)")}:decimal [infer -> decimal]",
            $"{text.IndexOf("Load(infer")}:int [infer -> int]",
            $"{text.IndexOf("userId) => 0")}:string [infer -> string]",
        ], hints);
    }

    [Fact]
    public async Task InlayHints_NoHintWhenNothingInferred()
    {
        // CS20027: nothing gives the type, so there is nothing to show.
        var (hints, _) = await GetTypeHintsAsync("""
            class C
            {
                void F(infer x) { }
            }
            """, Mode.SharperProject);
        Assert.Empty(hints);
    }

    [Fact]
    public async Task InlayHints_PlainCSharp_None()
    {
        var (hints, _) = await GetTypeHintsAsync("""
            class infer { }
            class C
            {
                static infer Greeting(int id) => null;
                void Fetch(infer retries = null) { }
            }
            """, Mode.PlainCSharp);
        Assert.Empty(hints);
    }

    [Fact]
    public async Task InlayHints_TypeNamedInferInScope_None()
    {
        var (hints, _) = await GetTypeHintsAsync("""
            class infer { }
            class C
            {
                static infer Greeting(int id) => null;
            }
            """, Mode.SharperProject);
        Assert.Empty(hints);
    }

    #endregion

    #region Classification

    [Theory, CombinatorialData]
    public async Task Classification_InferIsKeyword(bool cxDocument)
    {
        var mode = cxDocument ? Mode.CxDocument : Mode.SharperProject;
        Assert.Equal(ClassificationTypeNames.Keyword, await GetClassificationAsync("""
            class C
            {
                static [|infer|] Greeting(int id) => $"Hello, {id}!";
            }
            """, mode));
        Assert.Equal(ClassificationTypeNames.Keyword, await GetClassificationAsync("""
            class C
            {
                void Fetch([|infer|] retries = 3) { }
            }
            """, mode));
        Assert.Equal(ClassificationTypeNames.Keyword, await GetClassificationAsync("""
            class C
            {
                void M()
                {
                    [|infer|] Twice(int x) => x * 2;
                }
            }
            """, mode));
    }

    [Fact]
    public async Task Classification_InferWithError_IsKeyword()
    {
        // CS20027: still the keyword, though nothing gives the type.
        Assert.Equal(ClassificationTypeNames.Keyword, await GetClassificationAsync("""
            class C
            {
                void F([|infer|] x) { }
            }
            """, Mode.SharperProject));
    }

    [Fact]
    public async Task Classification_TypeNamedInfer_IsClass()
    {
        Assert.Equal(ClassificationTypeNames.ClassName, await GetClassificationAsync("""
            class infer { }
            class C
            {
                static [|infer|] Greeting(int id) => null;
            }
            """, Mode.SharperProject));
    }

    [Fact]
    public async Task Classification_InferElsewhere_NotKeyword()
    {
        // A field type 'infer' is an ordinary type name, also in Sharper.
        Assert.Equal(ClassificationTypeNames.Identifier, await GetClassificationAsync("""
            class C
            {
                [|infer|] f;
            }
            """, Mode.SharperProject));
    }

    [Fact]
    public async Task Classification_PlainCSharp_NotKeyword()
    {
        Assert.Equal(ClassificationTypeNames.Identifier, await GetClassificationAsync("""
            class C
            {
                static [|infer|] Greeting(int id) => null;
            }
            """, Mode.PlainCSharp));
        Assert.Equal(ClassificationTypeNames.ClassName, await GetClassificationAsync("""
            class infer { }
            class C
            {
                static [|infer|] Greeting(int id) => null;
            }
            """, Mode.PlainCSharp));
    }

    #endregion

    #region Go to definition and Find all references

    private static readonly SymbolDisplayFormat s_signatureFormat = SymbolDisplayFormat.CSharpErrorMessageFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters);

    private static async Task<(ISymbol? Symbol, TestWorkspace Workspace, Document Document)> GetDefinitionSymbolAsync(string markup, Mode mode)
    {
        var workspace = CreateWorkspace(markup, mode);
        var testDocument = workspace.Documents.Single();
        var document = workspace.CurrentSolution.GetRequiredDocument(testDocument.Id);
        var service = document.GetRequiredLanguageService<IGoToDefinitionSymbolService>();
        var semanticModel = await document.GetRequiredSemanticModelAsync(CancellationToken.None);
        var (symbol, _, _) = await service.GetSymbolProjectAndBoundSpanAsync(document, semanticModel, testDocument.CursorPosition!.Value, CancellationToken.None);
        return (symbol, workspace, document);
    }

    [Theory, CombinatorialData]
    public async Task GoToDefinition_CallToInferredMember(bool cxDocument)
    {
        var markup = """
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static infer Hello(infer id) => Load(id);
                static infer Greeting(int id, infer suffix = "!") => $"Hello, {id}{suffix}";
                async Task M()
                {
                    _ = await Hel$$lo(1) + Greeting(2);
                }
            }
            """;
        var (symbol, workspace, _) = await GetDefinitionSymbolAsync(markup, cxDocument ? Mode.CxDocument : Mode.SharperProject);
        using var _ = workspace;
        var method = Assert.IsAssignableFrom<IMethodSymbol>(symbol);
        Assert.Equal("System.Threading.Tasks.Task<string> C.Hello(int)", method.ToDisplayString(s_signatureFormat));
        var location = Assert.Single(method.Locations);
        Assert.True(location.IsInSource);
        Assert.Equal("Hello", (await location.SourceTree.GetTextAsync()).ToString(location.SourceSpan));
        Assert.Contains("infer Hello(infer id)", location.SourceTree.GetText().Lines.GetLineFromPosition(location.SourceSpan.Start).ToString());
    }

    [Fact]
    public async Task GoToDefinition_InferredParameter()
    {
        var markup = """
            class C
            {
                static infer Greeting(int id, infer suffix = "!") => $"Hello, {id}{suf$$fix}";
            }
            """;
        var (symbol, workspace, _) = await GetDefinitionSymbolAsync(markup, Mode.SharperProject);
        using var _ = workspace;
        var parameter = Assert.IsAssignableFrom<IParameterSymbol>(symbol);
        Assert.Equal(SpecialType.System_String, parameter.Type.SpecialType);
        var location = Assert.Single(parameter.Locations);
        Assert.Equal("suffix", location.SourceTree!.GetText().ToString(location.SourceSpan));
        Assert.Equal("infer suffix", location.SourceTree.GetText().ToString(TextSpan.FromBounds(location.SourceSpan.Start - "infer ".Length, location.SourceSpan.End)));
    }

    [Theory, CombinatorialData]
    public async Task FindReferences_MemberWithInferredSignature(bool cxDocument)
    {
        var markup = """
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static infer He$$llo(infer id) => Load(id);
                async Task M()
                {
                    _ = await Hello(1);
                    System.Func<int, Task<string>> f = Hello;
                    _ = nameof(Hello);
                }
            }
            """;
        var (symbol, workspace, document) = await GetDefinitionSymbolAsync(markup, cxDocument ? Mode.CxDocument : Mode.SharperProject);
        using var _ = workspace;
        Assert.NotNull(symbol);
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, CancellationToken.None);
        var referenced = Assert.Single(references);
        Assert.Equal(symbol, referenced.Definition);
        var text = await document.GetTextAsync();
        var spans = referenced.Locations.Select(l => l.Location.SourceSpan).OrderBy(s => s.Start).ToArray();
        Assert.Equal(3, spans.Length);
        Assert.All(spans, span => Assert.Equal("Hello", text.ToString(span)));
    }

    [Fact]
    public async Task FindReferences_InferredReturnTypeFromUse()
    {
        // The callers of a method with an inferred return type are found from the inferred type's uses, too: 'Name'
        // is used by 'Hello', whose own return type depends on it.
        var markup = """
            class User { public string First = "", Last = ""; }
            class C
            {
                private static infer Na$$me(User u) => u.First + " " + u.Last;
                private static infer Hello(User u) => $"Hello, {Name(u)}!";
                void M(User u) { string s = Name(u); _ = Hello(u).Length; }
            }
            """;
        var (symbol, workspace, document) = await GetDefinitionSymbolAsync(markup, Mode.SharperProject);
        using var _ = workspace;
        Assert.NotNull(symbol);
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, CancellationToken.None);
        Assert.Equal(2, Assert.Single(references).Locations.Count());
    }

    #endregion
}
