// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Recommendations;

/// <summary>Sharper step R5: <c>infer</c> is offered in type positions where it can be inferred, in Sharper only.</summary>
[Trait(Traits.Feature, Traits.Features.KeywordRecommending)]
public sealed class InferKeywordRecommenderTests : RecommenderTests
{
    private static readonly CSharpParseOptions s_sharper = CSharpImplicitParseOptions.WithFeatures([new("sharper", "true")]);
    private static readonly CSharpParseOptions s_sharperScript = CSharpImplicitScriptParseOptions.WithFeatures([new("sharper", "true")]);

    protected override string KeywordText => "infer";

    private readonly InferKeywordRecommender _recommender = new();

    public InferKeywordRecommenderTests()
    {
        this.RecommendKeywordsAsync = async (position, context) => _recommender.RecommendKeywords(position, context, CancellationToken.None);
    }

    private Task VerifySharperAsync(string text)
        => VerifyKeywordAsync(text, s_sharper, s_sharperScript);

    private Task VerifySharperAbsenceAsync(string text)
        => VerifyAbsenceAsync(text, s_sharper, s_sharperScript);

    [Fact]
    public Task TestMemberReturnType()
        => VerifySharperAsync("""
            class C
            {
                public static $$
            }
            """);

    [Fact]
    public Task TestMemberReturnType_NoModifiers()
        => VerifySharperAsync("""
            class C
            {
                $$
            }
            """);

    [Fact]
    public Task TestParameterType()
        => VerifySharperAsync("""
            class C
            {
                void Fetch(string url, $$
            }
            """);

    [Fact]
    public Task TestLambdaParameterType()
        => VerifySharperAsync(AddInsideMethod("""
            System.Func<int, int> f = ($$
            """));

    [Fact]
    public Task TestLocalFunction()
        => VerifySharperAsync(AddInsideMethod("""
            static $$
            """));

    [Fact]
    public Task TestStatement()
        => VerifySharperAsync(AddInsideMethod("""
            $$
            """));

    [Fact]
    public Task TestConversionOperator()
        => VerifySharperAsync("""
            class C
            {
                public static implicit operator $$
            }
            """);

    [Fact]
    public Task TestNotInTypeArgument()
        => VerifySharperAbsenceAsync(AddInsideMethod("""
            var x = new System.Collections.Generic.List<$$
            """));

    [Fact]
    public Task TestNotInEnum()
        => VerifySharperAbsenceAsync("""
            enum E
            {
                $$
            }
            """);

    [Fact]
    public Task TestNotAfterDot()
        => VerifySharperAbsenceAsync(AddInsideMethod("""
            System.$$
            """));

    [Fact]
    public Task TestNotInPlainCSharp_Member()
        => VerifyAbsenceAsync("""
            class C
            {
                public static $$
            }
            """);

    [Fact]
    public Task TestNotInPlainCSharp_Parameter()
        => VerifyAbsenceAsync("""
            class C
            {
                void Fetch(string url, $$
            }
            """);

    [Fact]
    public Task TestNotInPlainCSharp_Statement()
        => VerifyAbsenceAsync(AddInsideMethod("""
            $$
            """));
}
