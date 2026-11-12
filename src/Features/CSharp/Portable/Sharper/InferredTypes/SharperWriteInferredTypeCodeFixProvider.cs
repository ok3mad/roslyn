// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;

/// <summary>
/// Sharper: the fix for an <c>infer</c> parameter whose type nothing pins. It writes the parameter type: each type
/// the error lists, or <c>object</c> when it lists none.
/// <list type="bullet">
/// <item>CS20027 (nothing pins the parameter). Where a call with several candidates pinned nothing because they
/// disagree, the error lists the type each candidate takes; the fix offers each.</item>
/// <item>CS20057 (uses that need different types): the fix offers the type of each use.</item>
/// </list>
/// The fix reads the types from the error's additional locations (the use that pinned nothing, or the disagreeing
/// arguments) and the candidates of the call there. Where the candidates disagree, CS20027 also lists the types of the
/// candidates the compiler kept in its <see cref="Diagnostic.Properties"/> (<see cref="CandidateTypesPropertyName"/>),
/// and the fix offers exactly those, not the types of candidates the other arguments rule out.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = SharperProviderNames.WriteInferredType), Shared]
[method: ImportingConstructor]
[method: SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
internal sealed class SharperWriteInferredTypeCodeFixProvider() : CodeFixProvider
{
    /// <summary>Cannot infer the type of parameter '{0}', because it has no default value, and {1}.</summary>
    private const string CS20027 = nameof(CS20027);

    /// <summary>Cannot infer the type of parameter '{0}', because its uses need different types.</summary>
    private const string CS20057 = nameof(CS20057);

    /// <summary>
    /// The property of CS20027 that lists the types of the candidates that disagree, as documentation comment reference
    /// IDs, one per line. The compiler's <c>SharperInferredParameterType.CandidateTypesPropertyName</c>.
    /// </summary>
    internal const string CandidateTypesPropertyName = "SharperInferCandidateTypes";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [CS20027, CS20057];

    public override FixAllProvider GetFixAllProvider()
        => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var document = context.Document;
        var cancellationToken = context.CancellationToken;
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (!SharperFacts.IsSharperEnabled(root.SyntaxTree))
            return;

        var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not IdentifierNameSyntax inferType ||
                !SharperFacts.IsInferKeyword(inferType, semanticModel, cancellationToken) ||
                inferType.Parent is not ParameterSyntax)
            {
                continue;
            }

            foreach (var type in GetTypesToWrite(diagnostic, root, semanticModel, inferType, cancellationToken))
                context.RegisterCodeFix(SharperInferredTypeHelpers.CreateWriteTypeAction(document, inferType, type, semanticModel), diagnostic);
        }
    }

    /// <summary>The types the error lists, in order, or <c>object</c>.</summary>
    private static ImmutableArray<ITypeSymbol> GetTypesToWrite(
        Diagnostic diagnostic, SyntaxNode root, SemanticModel semanticModel, TypeSyntax inferType, CancellationToken cancellationToken)
    {
        using var _ = ArrayBuilder<ITypeSymbol>.GetInstance(out var types);
        foreach (var location in diagnostic.AdditionalLocations)
        {
            if (location.IsInSource && location.SourceTree == root.SyntaxTree)
            {
                var use = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
                if (!TryAddTypeFromConversion(use, semanticModel, inferType, types, cancellationToken))
                    AddTypesFromUse(use, semanticModel, types, cancellationToken);
            }
        }

        if (diagnostic.Properties.TryGetValue(CandidateTypesPropertyName, out var ids) && ids is not null)
        {
            // The compiler's list is exact. A type the candidates' parameters spell keeps their nullable annotations and
            // tuple names, which a reference ID does not carry.
            var rebuilt = types.ToImmutable();
            types.Clear();
            foreach (var id in ids.Split('\n'))
            {
                if (DocumentationCommentId.GetFirstSymbolForReferenceId(id, semanticModel.Compilation) is ITypeSymbol listed)
                    types.Add(rebuilt.FirstOrDefault(type => type.Equals(listed, SymbolEqualityComparer.Default)) ?? listed);
            }
        }

        var result = types
            .Select(type => SharperInferredTypeHelpers.AdjustNullability(type, semanticModel, inferType.SpanStart))
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default)
            .ToImmutableArray();

        return result.IsEmpty ? [semanticModel.Compilation.GetSpecialType(SpecialType.System_Object)] : result;
    }

    /// <summary>
    /// A use of CS20057 that converts the local function declaring <paramref name="inferType"/> to a delegate type (step
    /// P7): the delegate's parameter type at the parameter's position, when the conversion has a delegate type. Returns
    /// false for any other use.
    /// </summary>
    private static bool TryAddTypeFromConversion(
        SyntaxNode use, SemanticModel semanticModel, TypeSyntax inferType, ArrayBuilder<ITypeSymbol> types, CancellationToken cancellationToken)
    {
        if (inferType.Parent is not ParameterSyntax { Parent: ParameterListSyntax { Parent: LocalFunctionStatementSyntax localFunction } parameterList } parameter ||
            use is not ExpressionSyntax expression ||
            semanticModel.GetDeclaredSymbol(localFunction, cancellationToken) is not { } declared)
        {
            return false;
        }

        var symbolInfo = semanticModel.GetSymbolInfo(expression, cancellationToken);
        IEnumerable<ISymbol> methods = symbolInfo.Symbol is { } symbol ? [symbol] : symbolInfo.CandidateSymbols;
        if (!methods.Any(s => s.OriginalDefinition.Equals(declared)))
            return false;

        var ordinal = parameterList.Parameters.IndexOf(parameter);
        if (semanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType is INamedTypeSymbol { DelegateInvokeMethod.Parameters: var delegateParameters } &&
            ordinal < delegateParameters.Length &&
            !delegateParameters[ordinal].Type.IsErrorType())
        {
            types.Add(delegateParameters[ordinal].Type);
        }

        return true;
    }

    /// <summary>
    /// The types the candidates of the call around <paramref name="use"/> take at its argument: every candidate for
    /// CS20027's "candidates disagree", the one candidate for each use of CS20057. A <c>params</c> parameter takes the
    /// array in its normal form, and the element in its expanded form.
    /// </summary>
    private static void AddTypesFromUse(SyntaxNode use, SemanticModel semanticModel, ArrayBuilder<ITypeSymbol> types, CancellationToken cancellationToken)
    {
        if (use.FirstAncestorOrSelf<ArgumentSyntax>() is not { Parent: BaseArgumentListSyntax { Parent: { } call } argumentList } argument ||
            !argument.Expression.Span.Contains(use.Span))
        {
            return;
        }

        var symbolInfo = semanticModel.GetSymbolInfo(call, cancellationToken);
        IEnumerable<ISymbol> candidates = symbolInfo.Symbol is { } symbol ? [symbol] : symbolInfo.CandidateSymbols;
        if (!candidates.Any() && call is InvocationExpressionSyntax invocation)
            candidates = semanticModel.GetMemberGroup(invocation.Expression, cancellationToken);

        var index = argumentList.Arguments.IndexOf(argument);
        foreach (var candidate in candidates)
        {
            var parameters = candidate switch
            {
                IMethodSymbol method => method.Parameters,
                IPropertySymbol property => property.Parameters,
                _ => [],
            };

            var parameter = argument.NameColon is { } nameColon
                ? parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.ValueText)
                : index < parameters.Length ? parameters[index] : parameters.LastOrDefault(p => p.IsParams);
            if (parameter is null)
                continue;

            if (!parameter.IsParams || index < parameters.Length - 1)
            {
                AddType(parameter.Type, candidate);
                continue;
            }

            // The normal form takes the collection, the expanded form an element.
            if (index == parameters.Length - 1)
                AddType(parameter.Type, candidate);

            if (parameter.Type is IArrayTypeSymbol { ElementType: var elementType })
                AddType(elementType, candidate);
        }

        void AddType(ITypeSymbol type, ISymbol candidate)
        {
            // A type parameter of the candidate is no type to write: its type argument comes from the call.
            if (type.IsErrorType() ||
                type.GetReferencedTypeParameters().Any(t => t.DeclaringMethod?.OriginalDefinition.Equals(candidate.OriginalDefinition) == true))
            {
                return;
            }

            types.Add(type);
        }
    }
}
