// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.CodeRefactorings;
using Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Diagnostics;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Sharper;

internal static class SharperTestOptions
{
    public static readonly CSharpParseOptions Sharper = CSharpParseOptions.Default.WithFeatures([new("sharper", "true")]);

    public static string Title(string type) => string.Format(CSharpFeaturesResources.Write_the_type_0, type);

    /// <summary>A workspace XML document has Windows line breaks.</summary>
    public static string CrLf(string text) => text.ReplaceLineEndings("\r\n");

    /// <summary>The markup as a <c>.cx</c> document of a plain C# project.</summary>
    public static string InCxDocument(string markup)
        => $"""
            <Workspace>
                <Project Language="C#" CommonReferences="true">
                    <Document FilePath="Test.cx">{System.Security.SecurityElement.Escape(markup)}</Document>
                </Project>
            </Workspace>
            """;
}

/// <summary>Step R5: on <c>infer</c>, write the inferred type out.</summary>
[Trait(Traits.Feature, Traits.Features.CodeActionsUseExplicitType)]
public sealed class SharperWriteInferredTypeRefactoringTests : AbstractCSharpCodeActionTest_NoEditor
{
    private static readonly TestParameters s_sharper = new(parseOptions: SharperTestOptions.Sharper);

    protected override CodeRefactoringProvider CreateCodeRefactoringProvider(TestWorkspace workspace, TestParameters parameters)
        => new SharperWriteInferredTypeCodeRefactoringProvider();

    [Fact]
    public Task ReturnType()
        => TestAsync("""
            class C
            {
                private static [||]infer Greeting(int id) => $"Hello, {id}!";
            }
            """, """
            class C
            {
                private static string Greeting(int id) => $"Hello, {id}!";
            }
            """, s_sharper);

    [Fact]
    public Task ReturnType_Generic_AddsNoUsing()
        => TestAsync("""
            using System.Collections.Generic;
            class C
            {
                private static inf[||]er Evens(int max) { for (var i = 0; i < max; i += 2) yield return i; }
            }
            """, """
            using System.Collections.Generic;
            class C
            {
                private static IEnumerable<int> Evens(int max) { for (var i = 0; i < max; i += 2) yield return i; }
            }
            """, s_sharper);

    [Fact]
    public Task ReturnType_Nullable()
        => TestAsync("""
            #nullable enable
            class C
            {
                static infer[||] F(string? s) => s;
            }
            """, """
            #nullable enable
            class C
            {
                static string? F(string? s) => s;
            }
            """, s_sharper);

    [Fact]
    public Task LocalFunction()
        => TestAsync("""
            class C
            {
                void M()
                {
                    [||]infer Twice(int x) => x * 2;
                }
            }
            """, """
            class C
            {
                void M()
                {
                    int Twice(int x) => x * 2;
                }
            }
            """, s_sharper);

    [Fact]
    public Task ParameterType()
        => TestAsync("""
            class C
            {
                void Fetch(string url, [||]infer retries = 3) { }
            }
            """, """
            class C
            {
                void Fetch(string url, int retries = 3) { }
            }
            """, s_sharper);

    [Fact]
    public Task ParameterType_FromUse()
        => TestAsync("""
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static infer Hello([||]infer id) => Load(id);
            }
            """, """
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static infer Hello(int id) => Load(id);
            }
            """, s_sharper);

    [Fact]
    public Task CopiedFromInterface()
        => TestAsync("""
            using System.Threading.Tasks;
            interface IProfiles { Task<string> Load(int userId); }
            class Profiles : IProfiles
            {
                public [||]infer Load(infer userId) => Task.FromResult("");
            }
            """, """
            using System.Threading.Tasks;
            interface IProfiles { Task<string> Load(int userId); }
            class Profiles : IProfiles
            {
                public Task<string> Load(infer userId) => Task.FromResult("");
            }
            """, s_sharper);

    [Fact]
    public Task CxDocument()
        => TestAsync(SharperTestOptions.InCxDocument("""
            class C
            {
                static [||]infer Greeting(int id) => $"Hello, {id}!";
            }
            """), SharperTestOptions.CrLf("""
            class C
            {
                static string Greeting(int id) => $"Hello, {id}!";
            }
            """), TestParameters.Default);

    [Fact]
    public Task Missing_AnonymousType()
        => TestMissingAsync("""
            class C
            {
                static [||]infer F() => new { A = 1 };
            }
            """, s_sharper);

    [Fact]
    public Task Missing_NothingInferred()
        => TestMissingAsync("""
            class C
            {
                void F([||]infer x) { }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_TypeNamedInfer()
        => TestMissingAsync("""
            class infer { }
            class C
            {
                static [||]infer F() => null;
            }
            """, s_sharper);

    [Fact]
    public Task Missing_PlainCSharp()
        => TestMissingAsync("""
            class infer { }
            class C
            {
                static [||]infer F() => null;
            }
            """);
}

/// <summary>Step R5: replace a written type with <c>infer</c> when the compiler infers the same type.</summary>
[Trait(Traits.Feature, Traits.Features.CodeActionsUseImplicitType)]
public sealed class SharperUseInferRefactoringTests : AbstractCSharpCodeActionTest_NoEditor
{
    private static readonly TestParameters s_sharper = new(parseOptions: SharperTestOptions.Sharper);

    protected override CodeRefactoringProvider CreateCodeRefactoringProvider(TestWorkspace workspace, TestParameters parameters)
        => new SharperUseInferCodeRefactoringProvider();

    [Fact]
    public Task ReturnType()
        => TestAsync("""
            class C
            {
                private static [||]string Greeting(int id) => $"Hello, {id}!";
            }
            """, """
            class C
            {
                private static infer Greeting(int id) => $"Hello, {id}!";
            }
            """, s_sharper);

    [Fact]
    public Task ReturnType_CaretInsideGenericType()
        => TestAsync("""
            using System.Collections.Generic;
            class C
            {
                static IEnumerable<i[||]nt> Evens(int max) { for (var i = 0; i < max; i += 2) yield return i; }
            }
            """, """
            using System.Collections.Generic;
            class C
            {
                static infer Evens(int max) { for (var i = 0; i < max; i += 2) yield return i; }
            }
            """, s_sharper);

    [Fact]
    public Task ReturnType_CaretAfterType()
        => TestAsync("""
            class C
            {
                static string[||] Greeting(int id) => $"Hello, {id}!";
            }
            """, """
            class C
            {
                static infer Greeting(int id) => $"Hello, {id}!";
            }
            """, s_sharper);

    [Fact]
    public Task LocalFunction()
        => TestAsync("""
            class C
            {
                void M()
                {
                    [||]int Twice(int x) => x * 2;
                }
            }
            """, """
            class C
            {
                void M()
                {
                    infer Twice(int x) => x * 2;
                }
            }
            """, s_sharper);

    [Fact]
    public Task ParameterWithDefault()
        => TestAsync("""
            class C
            {
                void Fetch(string url, [||]int retries = 3) { }
            }
            """, """
            class C
            {
                void Fetch(string url, infer retries = 3) { }
            }
            """, s_sharper);

    [Fact]
    public Task ParameterFromUse()
        => TestAsync("""
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static Task<string> Hello([||]int id) => Load(id);
            }
            """, """
            using System.Threading.Tasks;
            class C
            {
                static Task<string> Load(int userId) => Task.FromResult("");
                public static Task<string> Hello(infer id) => Load(id);
            }
            """, s_sharper);

    [Fact]
    public Task CxDocument()
        => TestAsync(SharperTestOptions.InCxDocument("""
            class C
            {
                static [||]string Greeting(int id) => $"Hello, {id}!";
            }
            """), SharperTestOptions.CrLf("""
            class C
            {
                static infer Greeting(int id) => $"Hello, {id}!";
            }
            """), TestParameters.Default);

    [Fact]
    public Task Missing_DifferentType()
        // The body gives 'List<int>', not the written 'IEnumerable<int>'.
        => TestMissingAsync("""
            using System.Collections.Generic;
            class C
            {
                static [||]IEnumerable<int> F() => new List<int>();
            }
            """, s_sharper);

    [Fact]
    public Task Missing_DifferentNullability()
        => TestMissingAsync("""
            #nullable enable
            class C
            {
                static [||]string? F(string s) => s;
            }
            """, s_sharper);

    [Fact]
    public Task Missing_ValueTask()
        => TestMissingAsync("""
            using System.Threading.Tasks;
            class C
            {
                static async [||]ValueTask<int> F() { await Task.Yield(); return 1; }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_Recursive()
        // CS20018: a body that uses its own inferred return type.
        => TestMissingAsync("""
            class C
            {
                static [||]int Fib(int i) => i < 2 ? 1 : Fib(i - 1) + Fib(i - 2);
            }
            """, s_sharper);

    [Fact]
    public Task Missing_ParameterPinnedByNothing()
        // CS20027.
        => TestMissingAsync("""
            class C
            {
                static void F([||]object x) { }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_Void()
        => TestMissingAsync("""
            class C
            {
                static [||]void F() { }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_Field()
        => TestMissingAsync("""
            class C
            {
                [||]int f = 1;
            }
            """, s_sharper);

    [Fact]
    public Task Missing_TypeNamedInferInScope()
        => TestMissingAsync("""
            class infer { }
            class C
            {
                static [||]string Greeting(int id) => $"Hello, {id}!";
            }
            """, s_sharper);

    [Fact]
    public Task Missing_PlainCSharp()
        => TestMissingAsync("""
            class C
            {
                private static [||]string Greeting(int id) => $"Hello, {id}!";
            }
            """);
}

/// <summary>Step R5 and P8: the fix for CS20027 and CS20057 writes the parameter type.</summary>
[Trait(Traits.Feature, Traits.Features.CodeActionsUseExplicitType)]
public sealed class SharperWriteInferredTypeCodeFixTests(ITestOutputHelper logger) : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor(logger)
{
    private static readonly TestParameters s_sharper = new(parseOptions: SharperTestOptions.Sharper);

    internal override (DiagnosticAnalyzer?, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
        => (null, new SharperWriteInferredTypeCodeFixProvider());

    [Fact]
    public Task CS20027_NotUsed_WritesObject()
        => TestAsync("""
            class C
            {
                static void F([|infer|] x) { }
            }
            """, """
            class C
            {
                static void F(object x) { }
            }
            """, s_sharper);

    [Fact]
    public Task CS20027_MemberAccess_WritesObject()
        => TestExactActionSetOfferedAsync("""
            class C
            {
                static int F([|infer|] x) => x.Length;
            }
            """, [SharperTestOptions.Title("object")], s_sharper);

    [Fact]
    public Task CS20027_CandidatesDisagree_OffersEachType()
        => TestExactActionSetOfferedAsync("""
            class C
            {
                static string Format(int value, string format) => "";
                static string Format(double value, string format) => "";
                static string F([|infer|] n) => Format(n, "N2");
            }
            """, [SharperTestOptions.Title("int"), SharperTestOptions.Title("double")], s_sharper);

    [Fact]
    public Task CS20027_CandidatesDisagree_WritesChosenType()
        => TestAsync("""
            class C
            {
                static string Format(int value, string format) => "";
                static string Format(double value, string format) => "";
                static string F([|infer|] n) => Format(n, "N2");
            }
            """, """
            class C
            {
                static string Format(int value, string format) => "";
                static string Format(double value, string format) => "";
                static string F(double n) => Format(n, "N2");
            }
            """, s_sharper.WithIndex(1));

    [Fact]
    public Task CS20057_UsesDisagree_OffersEachType()
        => TestExactActionSetOfferedAsync("""
            using System.Collections.Generic;
            class C
            {
                static void TakesInt(int i) { }
                static void TakesList(List<string> l) { }
                static void F([|infer|] x) { TakesInt(x); TakesList(x); }
            }
            """, [SharperTestOptions.Title("int"), SharperTestOptions.Title("List<string>")], s_sharper);

    [Fact]
    public Task CS20057_UsesDisagree_WritesChosenType()
        => TestAsync("""
            using System.Collections.Generic;
            class C
            {
                static void TakesInt(int i) { }
                static void TakesList(List<string> l) { }
                static void F([|infer|] x) { TakesInt(x); TakesList(x); }
            }
            """, """
            using System.Collections.Generic;
            class C
            {
                static void TakesInt(int i) { }
                static void TakesList(List<string> l) { }
                static void F(List<string> x) { TakesInt(x); TakesList(x); }
            }
            """, s_sharper.WithIndex(1));

    [Fact]
    public Task CxDocument()
        => TestAsync(SharperTestOptions.InCxDocument("""
            class C
            {
                static void F([|infer|] x) { }
            }
            """), SharperTestOptions.CrLf("""
            class C
            {
                static void F(object x) { }
            }
            """), TestParameters.Default);
}
