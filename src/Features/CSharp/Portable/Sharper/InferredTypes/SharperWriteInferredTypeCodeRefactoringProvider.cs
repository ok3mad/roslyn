// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;

/// <summary>
/// Sharper: on an <c>infer</c> return, parameter or member type, writes out the type the compiler inferred, as "Use
/// explicit type" does for <c>var</c>. Offered in Sharper documents only, where the type is known.
/// </summary>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = SharperProviderNames.WriteInferredType), Shared]
[method: ImportingConstructor]
[method: SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
internal sealed class SharperWriteInferredTypeCodeRefactoringProvider() : CodeRefactoringProvider
{
    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var (document, span, cancellationToken) = context;
        if (document.Project.Language != LanguageNames.CSharp)
            return;

        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (!SharperFacts.IsSharperEnabled(root.SyntaxTree))
            return;

        var inferType = await context.TryGetRelevantNodeAsync<IdentifierNameSyntax>().ConfigureAwait(false);
        if (!SharperFacts.IsInferPosition(inferType))
            return;

        var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        // An anonymous type has no name to write.
        var type = SharperFacts.GetInferredType(inferType, semanticModel, cancellationToken);
        if (type is null || type.ContainsAnonymousType())
            return;

        context.RegisterRefactoring(
            SharperInferredTypeHelpers.CreateWriteTypeAction(document, inferType, type, semanticModel),
            inferType.Span);
    }
}
