// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;

/// <summary>
/// Sharper: replaces a written return, parameter or member type with <c>infer</c>, when the compiler infers the same
/// type, nullable annotations included. The reverse of <see cref="SharperWriteInferredTypeCodeRefactoringProvider"/>.
/// Offered in Sharper documents only.
/// </summary>
/// <remarks>
/// Speculative binding does not reach member signatures, so the check binds the document with <c>infer</c> in place,
/// then compares the declared type and the errors in the member.
/// </remarks>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = SharperProviderNames.UseInfer), Shared]
[method: ImportingConstructor]
[method: SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
internal sealed class SharperUseInferCodeRefactoringProvider() : CodeRefactoringProvider
{
    private static readonly SyntaxAnnotation s_inferAnnotation = new();

    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var (document, span, cancellationToken) = context;
        if (document.Project.Language != LanguageNames.CSharp)
            return;

        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (!SharperFacts.IsSharperEnabled(root.SyntaxTree))
            return;

        // The type under the caret, or right before it, as in 'string$$ F()'.
        var type = SharperInferredTypeHelpers.FindTypeInInferablePosition(root.FindToken(span.Start));
        if (type is null && span.IsEmpty && span.Start > 0)
            type = SharperInferredTypeHelpers.FindTypeInInferablePosition(root.FindToken(span.Start - 1));

        if (type is null || !type.Span.Contains(span) ||
            SharperFacts.IsInferPosition(type) ||
            type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword } ||
            type.IsVar)
        {
            return;
        }

        var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var writtenType = SharperFacts.GetDeclaredType(type, semanticModel, cancellationToken);
        if (writtenType is null or IErrorTypeSymbol)
            return;

        var newDocument = await TryUseInferAsync(document, root, semanticModel, type, writtenType, cancellationToken).ConfigureAwait(false);
        if (newDocument is null)
            return;

        context.RegisterRefactoring(
            CodeAction.Create(
                CSharpFeaturesResources.Use_infer,
                _ => Task.FromResult(newDocument),
                equivalenceKey: SharperProviderNames.UseInfer),
            type.Span);
    }

    /// <summary>
    /// The document with <c>infer</c> in place of <paramref name="type"/>, or null when that changes the type, or adds
    /// errors to the member.
    /// </summary>
    private static async Task<Document?> TryUseInferAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        TypeSyntax type,
        ITypeSymbol writtenType,
        CancellationToken cancellationToken)
    {
        var member = GetMember(type);
        var inferType = SyntaxFactory.IdentifierName(SharperFacts.InferKeyword).WithTriviaFrom(type).WithAdditionalAnnotations(s_inferAnnotation);
        var newDocument = document.WithSyntaxRoot(root.ReplaceNode(type, inferType));
        var newRoot = await newDocument.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var newInferType = (TypeSyntax)newRoot.GetAnnotatedNodes(s_inferAnnotation).Single();
        var newSemanticModel = await newDocument.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        // 'infer' must be the keyword there (no type named 'infer' in scope), and stand for the same type.
        if (!SharperFacts.IsInferKeyword(newInferType, newSemanticModel, cancellationToken) ||
            SharperFacts.GetDeclaredType(newInferType, newSemanticModel, cancellationToken) is not { } inferredType ||
            !inferredType.Equals(writtenType, SymbolEqualityComparer.IncludeNullability))
        {
            return null;
        }

        // A body that now uses itself, or nothing to infer from, is an error in the member.
        var newMember = GetMember(newInferType);
        if (CountErrors(newSemanticModel, newMember.Span, cancellationToken) > CountErrors(semanticModel, member.Span, cancellationToken))
            return null;

        return newDocument;
    }

    /// <summary>The member, local function or lambda whose signature has <paramref name="type"/>.</summary>
    private static SyntaxNode GetMember(TypeSyntax type)
        => type.Parent is ParameterSyntax parameter
            ? parameter.Parent?.Parent ?? parameter
            : type.Parent is VariableDeclarationSyntax declaration ? declaration.Parent! : type.Parent!;

    private static int CountErrors(SemanticModel semanticModel, TextSpan span, CancellationToken cancellationToken)
        => semanticModel.GetDiagnostics(span, cancellationToken).Count(d => d.Severity == DiagnosticSeverity.Error);
}
