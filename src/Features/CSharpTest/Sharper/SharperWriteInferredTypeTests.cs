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
    public Task CS20057_ConversionsDisagree_OffersEachParameterType()
        => TestExactActionSetOfferedAsync("""
            using System;
            class C
            {
                void M()
                {
                    Func<int, string> f = F;
                    Func<long, string> g = F;
                    string F([|infer|] x) => "";
                }
            }
            """, [SharperTestOptions.Title("int"), SharperTestOptions.Title("long")], s_sharper);

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

/// <summary>#201: the fix for CS20027 offers exactly the types its message lists.</summary>
[Trait(Traits.Feature, Traits.Features.CodeActionsUseExplicitType)]
public sealed class SharperWriteInferredTypeExactListTests(ITestOutputHelper logger) : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor(logger)
{
    private static readonly TestParameters s_sharper = new(parseOptions: SharperTestOptions.Sharper);

    internal override (DiagnosticAnalyzer?, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
        => (null, new SharperWriteInferredTypeCodeFixProvider());

    [Fact]
    public Task CS20027_CandidatesDisagree_LeavesOutRuledOutCandidates()
        // The third 'Format' takes no string as its second argument, so the compiler lists only 'int' and 'double'.
        => TestExactActionSetOfferedAsync("""
            class C
            {
                static string Format(int value, string format) => "";
                static string Format(double value, string format) => "";
                static string Format(long value, int width) => "";
                static string F([|infer|] n) => Format(n, "N2");
            }
            """, [SharperTestOptions.Title("int"), SharperTestOptions.Title("double")], s_sharper);

    [Fact]
    public Task CS20027_CandidatesDisagree_KeepsNullableAnnotation()
        => TestExactActionSetOfferedAsync("""
            #nullable enable
            class C
            {
                static string Format(string? value, string format) => "";
                static string Format(int value, string format) => "";
                static string Format(long value, int width) => "";
                static string F([|infer|] n) => Format(n, "N2");
            }
            """, [SharperTestOptions.Title("string?"), SharperTestOptions.Title("int")], s_sharper);

    [Fact]
    public Task CS20027_CandidatesDisagree_GenericType()
        => TestExactActionSetOfferedAsync("""
            using System.Collections.Generic;
            class C
            {
                static void Add(List<int> list, string s) { }
                static void Add(int[] array, string s) { }
                static void Add(long[] array, int i) { }
                static void F([|infer|] items) => Add(items, "");
            }
            """, [SharperTestOptions.Title("List<int>"), SharperTestOptions.Title("int[]")], s_sharper);
}

/// <summary>#201 and P8: "Make 'F' generic" for CS20027.</summary>
[Trait(Traits.Feature, Traits.Features.CodeActionsUseExplicitType)]
public sealed class SharperMakeGenericCodeFixTests(ITestOutputHelper logger) : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor(logger)
{
    private static readonly TestParameters s_sharper = new(parseOptions: SharperTestOptions.Sharper);

    private static string Title(string name) => string.Format(CSharpFeaturesResources.Make_0_generic, name);

    internal override (DiagnosticAnalyzer?, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
        => (null, new SharperMakeGenericCodeFixProvider());

    [Fact]
    public Task NotUsed()
        => TestAsync("""
            class C
            {
                static void F([|infer|] x) { }
            }
            """, """
            class C
            {
                static void F<T>(T x) { }
            }
            """, s_sharper);

    [Fact]
    public Task Title_NamesTheMethod()
        => TestExactActionSetOfferedAsync("""
            class C
            {
                static void Log([|infer|] x) { }
            }
            """, [Title("Log")], s_sharper);

    [Fact]
    public Task PassedToGenericMethod()
        => TestAsync("""
            using System.Collections.Generic;
            class C
            {
                static List<U> Wrap<U>(U u) => [u];
                static int Count([|infer|] value) => Wrap(value).Count;
                static int M() => Count(1) + Count("");
            }
            """, """
            using System.Collections.Generic;
            class C
            {
                static List<U> Wrap<U>(U u) => [u];
                static int Count<T>(T value) => Wrap(value).Count;
                static int M() => Count(1) + Count("");
            }
            """, s_sharper);

    [Fact]
    public Task Interpolation()
        => TestAsync("""
            class C
            {
                static string Show([|infer|] x) => $"<{x}>";
            }
            """, """
            class C
            {
                static string Show<T>(T x) => $"<{x}>";
            }
            """, s_sharper);

    [Fact]
    public Task AlreadyGeneric()
        => TestAsync("""
            class C
            {
                static void F<U>(U u, [|infer|] x) { }
            }
            """, """
            class C
            {
                static void F<U, T>(U u, T x) { }
            }
            """, s_sharper);

    [Fact]
    public Task LocalFunction()
        => TestAsync("""
            class C
            {
                void M()
                {
                    void Log([|infer|] x) { }
                    Log(1);
                }
            }
            """, """
            class C
            {
                void M()
                {
                    void Log<T>(T x) { }
                    Log(1);
                }
            }
            """, s_sharper);

    [Fact]
    public Task NameClash_UsesParameterName()
        => TestAsync("""
            class C<T>
            {
                static void F([|infer|] value) { }
            }
            """, """
            class C<T>
            {
                static void F<TValue>(TValue value) { }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_NameClash_BothNames()
        => TestMissingAsync("""
            class C<T, TValue>
            {
                static void F([|infer|] value) { }
            }
            """, s_sharper);

    [Fact]
    public Task NameClash_Local_UsesParameterName()
        => TestAsync("""
            class C
            {
                static void F([|infer|] x) { var T = 1; }
            }
            """, """
            class C
            {
                static void F<TX>(TX x) { var T = 1; }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_MemberAccess()
        // 'T' has no 'Length'.
        => TestMissingAsync("""
            class C
            {
                static int F([|infer|] x) => x.Length;
            }
            """, s_sharper);

    [Fact]
    public Task Missing_CandidatesDisagree()
        => TestMissingAsync("""
            class C
            {
                static string Format(int value, string format) => "";
                static string Format(double value, string format) => "";
                static string F([|infer|] n) => Format(n, "N2");
            }
            """, s_sharper);

    [Fact]
    public Task Missing_CallGivesNoTypeArgument()
        // F(null) cannot infer 'T'.
        => TestMissingAsync("""
            class C
            {
                static void F([|infer|] x) { }
                static void M() => F(null);
            }
            """, s_sharper);

    [Fact]
    public Task Missing_Virtual()
        => TestMissingAsync("""
            class C
            {
                public virtual void F([|infer|] x) { }
            }
            """, s_sharper);

    [Fact]
    public Task Missing_Lambda()
        => TestMissingAsync("""
            using System;
            class C
            {
                Action<int> a = ([|infer|] x) => { };
            }
            """, s_sharper);

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
                static void F<T>(T x) { }
            }
            """), TestParameters.Default);

    [Fact]
    public Task Missing_PlainCSharp()
        // Without Sharper, 'infer' is a type name (CS0246), and there is nothing to fix here.
        => TestMissingAsync("""
            class C
            {
                static void F([|infer|] x) { }
            }
            """);
}

/// <summary>#201: write a type for CS20014, CS20024 and CS20026 (nothing to infer).</summary>
[Trait(Traits.Feature, Traits.Features.CodeActionsUseExplicitType)]
public sealed class SharperWriteTypeForNothingToInferCodeFixTests(ITestOutputHelper logger) : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor(logger)
{
    private static readonly TestParameters s_sharper = new(parseOptions: SharperTestOptions.Sharper);

    internal override (DiagnosticAnalyzer?, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
        => (null, new SharperWriteTypeForNothingToInferCodeFixProvider());

    [Fact]
    public Task CS20014_Null()
        => TestAsync("""
            class C
            {
                static infer F() => [|null|];
            }
            """, """
            class C
            {
                static object F() => null;
            }
            """, s_sharper);

    [Fact]
    public Task CS20014_Null_NullableEnabled()
        => TestAsync("""
            #nullable enable
            class C
            {
                static infer F() => [|null|];
            }
            """, """
            #nullable enable
            class C
            {
                static object? F() => null;
            }
            """, s_sharper);

    [Fact]
    public Task CS20014_Default_Block()
        => TestAsync("""
            class C
            {
                static infer F() { return [|default|]; }
            }
            """, """
            class C
            {
                static object F() { return default; }
            }
            """, s_sharper);

    [Fact]
    public Task CS20014_Throw()
        => TestAsync("""
            using System;
            class C
            {
                static infer F() => [|throw new InvalidOperationException()|];
            }
            """, """
            using System;
            class C
            {
                static object F() => throw new InvalidOperationException();
            }
            """, s_sharper);

    [Fact]
    public Task CS20014_Async()
        => TestAsync("""
            #nullable enable
            using System.Threading.Tasks;
            class C
            {
                static async infer F() { await Task.Yield(); return [|null|]; }
            }
            """, """
            #nullable enable
            using System.Threading.Tasks;
            class C
            {
                static async Task<object?> F() { await Task.Yield(); return null; }
            }
            """, s_sharper);

    [Fact]
    public Task CS20014_LocalFunction()
        => TestAsync("""
            class C
            {
                void M()
                {
                    infer F() => [|null|];
                }
            }
            """, """
            class C
            {
                void M()
                {
                    object F() => null;
                }
            }
            """, s_sharper);

    [Fact]
    public Task CS20014_Missing_Lambda()
        // 'object' would not take a lambda.
        => TestMissingAsync("""
            class C
            {
                static infer F() => [|x => x|];
            }
            """, s_sharper);

    [Fact]
    public Task CS20024_OffersEachTypeTheBaseClassAndObject()
        => TestExactActionSetOfferedAsync("""
            class Animal { }
            class Dog : Animal { }
            class Cat : Animal { }
            class C
            {
                static [|infer|] Pick(bool dog) { if (dog) return new Dog(); return new Cat(); }
            }
            """, [SharperTestOptions.Title("Dog"), SharperTestOptions.Title("Cat"), SharperTestOptions.Title("Animal"), SharperTestOptions.Title("object")], s_sharper);

    [Fact]
    public Task CS20024_WritesTheBaseClass()
        => TestAsync("""
            class Animal { }
            class Dog : Animal { }
            class Cat : Animal { }
            class C
            {
                static [|infer|] Pick(bool dog) { if (dog) return new Dog(); return new Cat(); }
            }
            """, """
            class Animal { }
            class Dog : Animal { }
            class Cat : Animal { }
            class C
            {
                static Animal Pick(bool dog) { if (dog) return new Dog(); return new Cat(); }
            }
            """, s_sharper.WithIndex(2));

    [Fact]
    public Task CS20024_NoBaseClass()
        => TestExactActionSetOfferedAsync("""
            class C
            {
                static [|infer|] F(bool b) { if (b) return 1; return ""; }
            }
            """, [SharperTestOptions.Title("int"), SharperTestOptions.Title("string"), SharperTestOptions.Title("object")], s_sharper);

    [Fact]
    public Task CS20024_WithNull_Nullable()
        => TestExactActionSetOfferedAsync("""
            #nullable enable
            class C
            {
                static [|infer|] F(int i) { if (i == 0) return 1; if (i == 1) return ""; return null; }
            }
            """, [SharperTestOptions.Title("int?"), SharperTestOptions.Title("string?"), SharperTestOptions.Title("object?")], s_sharper);

    [Fact]
    public Task CS20024_IgnoresNestedFunctions()
        => TestExactActionSetOfferedAsync("""
            using System;
            class C
            {
                static [|infer|] F(bool b)
                {
                    Func<long> f = () => { return 1L; };
                    if (b) return 1;
                    return "";
                }
            }
            """, [SharperTestOptions.Title("int"), SharperTestOptions.Title("string"), SharperTestOptions.Title("object")], s_sharper);

    [Fact]
    public Task CS20024_Iterator()
        => TestExactActionSetOfferedAsync("""
            using System.Collections.Generic;
            class C
            {
                static [|infer|] F() { yield return 1; yield return ""; }
            }
            """, [SharperTestOptions.Title("IEnumerable<int>"), SharperTestOptions.Title("IEnumerable<string>"), SharperTestOptions.Title("IEnumerable<object>")], s_sharper);

    [Fact]
    public Task CS20026_Null()
        => TestAsync("""
            class C
            {
                void F(infer x = [|null|]) { }
            }
            """, """
            class C
            {
                void F(object x = null) { }
            }
            """, s_sharper);

    [Fact]
    public Task CS20026_Default_NullableEnabled()
        => TestAsync("""
            #nullable enable
            class C
            {
                void F(infer x = [|default|]) { }
            }
            """, """
            #nullable enable
            class C
            {
                void F(object? x = default) { }
            }
            """, s_sharper);

    [Fact]
    public Task CxDocument()
        => TestAsync(SharperTestOptions.InCxDocument("""
            class C
            {
                void F(infer x = [|null|]) { }
            }
            """), SharperTestOptions.CrLf("""
            class C
            {
                void F(object x = null) { }
            }
            """), TestParameters.Default);

    [Fact]
    public Task Missing_PlainCSharp()
        => TestMissingAsync("""
            class C
            {
                void F(infer x = [|null|]) { }
            }
            """);
}
