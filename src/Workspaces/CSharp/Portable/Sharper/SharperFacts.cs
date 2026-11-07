// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Sharper;

/// <summary>
/// What the IDE needs to know about Sharper, the C# dialect of this fork, from syntax and public API only (the compiler's
/// own helpers are internal). Every Sharper IDE feature asks <see cref="IsSharperEnabled"/> first, so that plain C#
/// documents do not change.
/// </summary>
internal static class SharperFacts
{
    /// <summary>The parse options feature that turns Sharper on (<c>/features:sharper</c>, or a <c>.cx</c> document).</summary>
    public const string FeatureName = "sharper";

    /// <summary>The Sharper <c>infer</c> keyword, which stands for an inferred return, parameter or member type.</summary>
    public const string InferKeyword = "infer";

    /// <summary>
    /// Whether <paramref name="tree"/> is parsed with the Sharper feature: a <c>.cx</c> document (the workspace gives its
    /// tree the feature) or any document of a project with <c>&lt;Features&gt;sharper&lt;/Features&gt;</c>.
    /// </summary>
    public static bool IsSharperEnabled([NotNullWhen(true)] SyntaxTree? tree)
        => tree?.Options is CSharpParseOptions options && options.Features.ContainsKey(FeatureName);

    /// <summary>
    /// Whether <paramref name="type"/> is an <c>infer</c> written where the compiler infers a type (see
    /// <see cref="IsInferablePosition"/>), in a Sharper tree. This is syntax only: like <c>var</c>, the <c>infer</c> is
    /// still a type name when a type named <c>infer</c> is in scope, which <see cref="IsInferKeyword(TypeSyntax?,
    /// SemanticModel, CancellationToken)"/> also checks.
    /// </summary>
    public static bool IsInferPosition([NotNullWhen(true)] TypeSyntax? type)
        // '@infer' is always the type name.
        => type is IdentifierNameSyntax { Identifier.Text: InferKeyword } && IsSharperEnabled(type.SyntaxTree) && IsInferablePosition(type);

    /// <summary>
    /// Whether <paramref name="type"/>, whatever it names, is in a position where Sharper can infer the type from
    /// <c>infer</c>: the return type of a method, local function, operator or conversion operator, or the type of a
    /// parameter, a property, an indexer or an event.
    /// </summary>
    public static bool IsInferablePosition(TypeSyntax type)
        => type.Parent switch
        {
            MethodDeclarationSyntax method => method.ReturnType == type,
            LocalFunctionStatementSyntax localFunction => localFunction.ReturnType == type,
            OperatorDeclarationSyntax @operator => @operator.ReturnType == type,
            ConversionOperatorDeclarationSyntax conversion => conversion.Type == type,
            ParameterSyntax parameter => parameter.Type == type,
            BasePropertyDeclarationSyntax property => property.Type == type,
            VariableDeclarationSyntax { Parent: EventFieldDeclarationSyntax } declaration => declaration.Type == type,
            _ => false,
        };

    /// <summary>
    /// The type of the member or parameter declared with <paramref name="type"/> in an inferable position (see
    /// <see cref="IsInferablePosition"/>): the return type of the method, or the type of the parameter, property or
    /// event, with its nullable annotation. Null elsewhere.
    /// </summary>
    public static ITypeSymbol? GetDeclaredType(TypeSyntax type, SemanticModel semanticModel, CancellationToken cancellationToken)
        => type.Parent switch
        {
            MethodDeclarationSyntax or LocalFunctionStatementSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax
                when IsInferablePosition(type) => (semanticModel.GetDeclaredSymbol(type.Parent, cancellationToken) as IMethodSymbol)?.ReturnType,
            ParameterSyntax parameter when parameter.Type == type => semanticModel.GetDeclaredSymbol(parameter, cancellationToken)?.Type,
            BasePropertyDeclarationSyntax property when property.Type == type => semanticModel.GetDeclaredSymbol(property, cancellationToken) switch
            {
                IPropertySymbol propertySymbol => propertySymbol.Type,
                IEventSymbol eventSymbol => eventSymbol.Type,
                _ => null,
            },
            VariableDeclarationSyntax { Parent: EventFieldDeclarationSyntax, Variables: [var first, ..] } declaration when declaration.Type == type
                => (semanticModel.GetDeclaredSymbol(first, cancellationToken) as IEventSymbol)?.Type,
            _ => null,
        };

    /// <summary>
    /// Whether <paramref name="type"/> is the Sharper <c>infer</c> keyword: an <c>infer</c> in a position where the
    /// compiler infers a type (<see cref="IsInferPosition"/>), with no type named <c>infer</c> in scope. Where the type
    /// cannot be inferred (CS20027 and the like), the <c>infer</c> is still the keyword, and binds to an error type.
    /// </summary>
    public static bool IsInferKeyword([NotNullWhen(true)] TypeSyntax? type, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (!IsInferPosition(type))
            return false;

        // Like 'var', the semantic model gives the type that 'infer' stands for.
        var symbolInfo = semanticModel.GetSymbolInfo(type, cancellationToken);
        return IsInferKeyword(type, symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null));
    }

    /// <summary>
    /// Whether <paramref name="type"/>, which binds to <paramref name="boundSymbol"/>, is the Sharper <c>infer</c> keyword
    /// (see <see cref="IsInferKeyword(TypeSyntax?, SemanticModel, CancellationToken)"/>).
    /// </summary>
    public static bool IsInferKeyword([NotNullWhen(true)] TypeSyntax? type, ISymbol? boundSymbol)
        // A type named 'infer' that is in scope wins over the keyword, as for 'var'.
        => IsInferPosition(type) && boundSymbol is not ITypeSymbol { Name: InferKeyword, TypeKind: not TypeKind.Error };

    /// <summary>
    /// The type that the <c>infer</c> keyword <paramref name="type"/> stands for, or null where it is no
    /// <c>infer</c> keyword, or the type could not be inferred.
    /// </summary>
    public static ITypeSymbol? GetInferredType(TypeSyntax? type, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (!IsInferKeyword(type, semanticModel, cancellationToken))
            return null;

        // The declared symbol's type carries the inferred nullable annotation.
        var inferred = GetDeclaredType(type, semanticModel, cancellationToken);
        return inferred is null or IErrorTypeSymbol ? null : inferred;
    }
}
