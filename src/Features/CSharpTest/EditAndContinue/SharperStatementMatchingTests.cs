// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Differencing;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.EditAndContinue.UnitTests;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.EditAndContinue.UnitTests;

/// <summary>
/// Statement matching for Sharper syntax (docs/sharper).
/// </summary>
[UseExportProvider]
public sealed class SharperStatementMatchingTests : EditingTestBase
{
    private static readonly CSharpParseOptions s_sharperParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithFeatures([new("sharper", "true")]);

    private static Match<SyntaxNode> GetIteratorMatch(string body1, string body2, CSharpParseOptions parseOptions, bool isAsync = false)
    {
        var oldBody = GetIteratorBody(body1, parseOptions, isAsync);
        var newBody = GetIteratorBody(body2, parseOptions, isAsync);

        var match = oldBody.ComputeSingleRootMatch(newBody, knownMatches: null);
        Contract.ThrowIfNull(match);
        return match;
    }

    private static MemberBody GetIteratorBody(string body, CSharpParseOptions parseOptions, bool isAsync)
    {
        var source = isAsync
            ? "class C { async System.Collections.Generic.IAsyncEnumerable<int> F() {" + body.NormalizeLineEndings() + "} }"
            : WrapMethodBodyWithClass(body.NormalizeLineEndings(), MethodKind.Iterator);
        var tree = SyntaxFactory.ParseSyntaxTree(source, parseOptions);
        tree.GetDiagnostics().Verify();

        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var memberBody = SyntaxUtilities.TryGetDeclarationBody(declaration, symbol: null);
        Contract.ThrowIfNull(memberBody);
        return memberBody;
    }

    [Fact]
    public void YieldReturnSpread_MatchesSpread()
    {
        var src1 = """
            yield return ..xs;
            yield return 1;
            """;
        var src2 = """
            yield return 0;
            yield return ..ys;
            yield return 1;
            """;

        var actual = ToMatchingPairs(GetIteratorMatch(src1, src2, s_sharperParseOptions));

        var expected = new MatchingPairs
        {
            { "yield return ..xs;", "yield return ..ys;" },
            { "yield return 1;", "yield return 1;" }
        };

        expected.AssertEqual(actual);
    }

    [Fact]
    public void YieldReturnSpread_DoesNotMatchYieldReturn()
    {
        var src1 = """
            yield return ..xs;
            yield return x;
            """;
        var src2 = """
            yield return xs;
            yield return ..x;
            """;

        var actual = ToMatchingPairs(GetIteratorMatch(src1, src2, s_sharperParseOptions));

        // A spread has its own state machine states and hoisted enumerator, so it matches only a spread, never a yield
        // return of a single value (as yield return does not match yield break), even one that is textually closer.
        var expected = new MatchingPairs
        {
            { "yield return ..xs;", "yield return ..x;" },
            { "yield return x;", "yield return xs;" }
        };

        expected.AssertEqual(actual);
    }

    [Fact]
    public void YieldReturnSpread_ReplacesYieldReturn()
    {
        var src1 = """
            A();
            yield return x;
            """;
        var src2 = """
            A();
            yield return ..x;
            """;

        var actual = ToMatchingPairs(GetIteratorMatch(src1, src2, s_sharperParseOptions));

        // The yield return is deleted and the spread inserted, so their states are not mapped to each other.
        var expected = new MatchingPairs
        {
            { "A();", "A();" }
        };

        expected.AssertEqual(actual);
    }

    [Fact]
    public void YieldReturnSpread_AsyncIterator_MatchesSpread()
    {
        var src1 = """
            await A();
            yield return ..xs;
            yield return x;
            """;
        var src2 = """
            await A();
            yield return xs;
            yield return ..ys;
            yield return x;
            """;

        var actual = ToMatchingPairs(GetIteratorMatch(src1, src2, s_sharperParseOptions, isAsync: true));

        // As in a synchronous iterator, a spread over a synchronous source matches only a spread.
        var expected = new MatchingPairs
        {
            { "await A();", "await A();" },
            { "await A()", "await A()" },
            { "yield return ..xs;", "yield return ..ys;" },
            { "yield return x;", "yield return x;" }
        };

        expected.AssertEqual(actual);
    }

    [Fact]
    public void YieldReturnSpread_SharperDisabled_MatchesYieldReturn()
    {
        var src1 = """
            yield return ..x;
            """;
        var src2 = """
            yield return x;
            """;

        // Without Sharper, `..x` is a range, and the statement is an ordinary yield return.
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var actual = ToMatchingPairs(GetIteratorMatch(src1, src2, parseOptions));

        var expected = new MatchingPairs
        {
            { "yield return ..x;", "yield return x;" }
        };

        expected.AssertEqual(actual);
    }
}
