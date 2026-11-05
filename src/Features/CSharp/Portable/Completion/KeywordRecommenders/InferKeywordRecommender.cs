// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Sharper;
using Microsoft.CodeAnalysis.CSharp.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;

/// <summary>
/// Sharper: <c>infer</c> where the compiler can infer a type, in Sharper documents only: the return type of a member
/// or a local function, the type of a parameter, and the type of a conversion operator.
/// </summary>
internal sealed class InferKeywordRecommender : IKeywordRecommender<CSharpSyntaxContext>
{
    private static bool IsValidContext(CSharpSyntaxContext context, CancellationToken cancellationToken)
    {
        if (!SharperFacts.IsSharperEnabled(context.SyntaxTree) || context.IsTaskLikeTypeContext)
            return false;

        return
            context.IsParameterTypeContext ||
            context.IsPossibleLambdaOrAnonymousMethodParameterTypeContext ||
            context.IsLocalFunctionDeclarationContext ||
            context.IsStatementContext ||
            context.IsGlobalStatementContext ||
            context.IsImplicitOrExplicitOperatorTypeContext ||
            context.IsMemberDeclarationContext(
                validModifiers: SyntaxKindSet.AllMemberModifiers,
                validTypeDeclarations: SyntaxKindSet.NonEnumTypeDeclarations,
                canBePartial: true,
                cancellationToken);
    }

    public ImmutableArray<RecommendedKeyword> RecommendKeywords(int position, CSharpSyntaxContext context, CancellationToken cancellationToken)
        => IsValidContext(context, cancellationToken)
            ? [new RecommendedKeyword(SharperFacts.InferKeyword)]
            : [];
}
