// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Sharper;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Simplification;

namespace Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;

/// <summary>Names of the Sharper code fix and refactoring providers, for ordering.</summary>
internal static class SharperProviderNames
{
    public const string WriteInferredType = "Sharper Write Inferred Type";
    public const string UseInfer = "Sharper Use Infer";
    public const string MakeGeneric = "Sharper Make Generic";
    public const string WriteTypeForNothingToInfer = "Sharper Write Type For Nothing To Infer";
}

/// <summary>What the Sharper fixers and refactorings for <c>infer</c> share.</summary>
internal static class SharperInferredTypeHelpers
{
    /// <summary>
    /// The code action that replaces the <c>infer</c> keyword <paramref name="inferType"/> with <paramref name="type"/>,
    /// written as the shortest name that binds at that position.
    /// </summary>
    public static CodeAction CreateWriteTypeAction(Document document, TypeSyntax inferType, ITypeSymbol type, SemanticModel semanticModel)
    {
        var display = type.ToMinimalDisplayString(semanticModel, inferType.SpanStart);
        return CodeAction.Create(
            string.Format(CSharpFeaturesResources.Write_the_type_0, display),
            cancellationToken => WriteTypeAsync(document, inferType, type, cancellationToken),
            equivalenceKey: SharperProviderNames.WriteInferredType + ":" + display);
    }

    private static async Task<Document> WriteTypeAsync(Document document, TypeSyntax inferType, ITypeSymbol type, CancellationToken cancellationToken)
    {
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var typeSyntax = type.GenerateTypeSyntax(allowVar: false)
            .WithTriviaFrom(inferType)
            .WithAdditionalAnnotations(Simplifier.Annotation);
        return document.WithSyntaxRoot(root.ReplaceNode(inferType, typeSyntax));
    }

    /// <summary>
    /// The type to write for <paramref name="type"/> at <paramref name="position"/>: where nullable annotations are
    /// disabled, a written '?' on a reference type would warn, so it is left out.
    /// </summary>
    public static ITypeSymbol AdjustNullability(ITypeSymbol type, SemanticModel semanticModel, int position)
        => semanticModel.GetNullableContext(position).AnnotationsEnabled() || type.IsValueType
            ? type
            : type.WithNullableAnnotation(NullableAnnotation.None);

    /// <summary>
    /// The written type in an inferable position (<see cref="SharperFacts.IsInferablePosition"/>) that contains
    /// <paramref name="token"/>: the whole type, not the part of it under the caret.
    /// </summary>
    public static TypeSyntax? FindTypeInInferablePosition(SyntaxToken token)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (node is TypeSyntax type && SharperFacts.IsInferablePosition(type))
                return type;

            // Stop at the first node that cannot be part of a type.
            if (node is not (TypeSyntax or TypeArgumentListSyntax or TupleElementSyntax or ArrayRankSpecifierSyntax or
                FunctionPointerParameterListSyntax or FunctionPointerParameterSyntax or FunctionPointerCallingConventionSyntax))
            {
                return null;
            }
        }

        return null;
    }
}
