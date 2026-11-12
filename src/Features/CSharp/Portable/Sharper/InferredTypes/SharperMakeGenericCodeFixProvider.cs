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
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;

/// <summary>
/// Sharper: the other fix for CS20027 (nothing pins an <c>infer</c> parameter) that step P8 plans: "Make 'F' generic"
/// adds a type parameter to the method or local function and uses it as the parameter type, <c>void F&lt;T&gt;(T x)</c>.
/// </summary>
/// <remarks>
/// Offered on an ordinary method or a local function with a body, when:
/// <list type="bullet">
/// <item>the method overrides, implements or can be overridden by nothing (not <c>override</c>, <c>virtual</c>,
/// <c>abstract</c>, an explicit or implicit interface implementation, or <c>partial</c>): a type parameter would no
/// longer match the other declaration;</item>
/// <item>the name of the type parameter, <c>T</c>, or else <c>T</c> and the parameter name (<c>TValue</c> for
/// <c>value</c>), clashes with nothing: no symbol of that name is in scope at the parameter, and no identifier in the
/// method is spelled so;</item>
/// <item>the document with the type parameter in place has no error it did not have before, but for the CS20027 that
/// goes away. So it is not offered where the body needs a specific type (<c>x.Length</c>, or a call whose
/// candidates disagree), or where a call in the document gives no type argument (<c>F(null)</c>). Calls in other
/// documents are not checked, as for any change of signature that the user asks for; a call that passes an argument
/// still infers the type argument.</item>
/// </list>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = SharperProviderNames.MakeGeneric), Shared]
[ExtensionOrder(After = SharperProviderNames.WriteInferredType)]
[method: ImportingConstructor]
[method: SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
internal sealed class SharperMakeGenericCodeFixProvider() : CodeFixProvider
{
    /// <summary>Cannot infer the type of parameter '{0}', because it has no default value, and {1}.</summary>
    private const string CS20027 = nameof(CS20027);

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [CS20027];

    /// <summary>Each fix adds its own type parameter, so there is nothing to fix all at once.</summary>
    public override FixAllProvider? GetFixAllProvider()
        => null;

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
                inferType.Parent is not ParameterSyntax { Parent: ParameterListSyntax { Parent: { } function } } parameter ||
                function is not (MethodDeclarationSyntax { Body: not null } or MethodDeclarationSyntax { ExpressionBody: not null } or LocalFunctionStatementSyntax) ||
                !SharperFacts.IsInferKeyword(inferType, semanticModel, cancellationToken) ||
                semanticModel.GetDeclaredSymbol(function, cancellationToken) is not IMethodSymbol method ||
                !CanAddTypeParameter(function, method) ||
                GetTypeParameterName(function, parameter, semanticModel, cancellationToken) is not { } name)
            {
                continue;
            }

            var newDocument = MakeGeneric(document, root, function, inferType, name);
            if (!await AddsNoErrorAsync(document, semanticModel, newDocument, cancellationToken).ConfigureAwait(false))
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    string.Format(CSharpFeaturesResources.Make_0_generic, method.Name),
                    _ => Task.FromResult(newDocument),
                    equivalenceKey: SharperProviderNames.MakeGeneric + ":" + method.Name + ":" + parameter.Identifier.ValueText),
                diagnostic);
        }
    }

    /// <summary>A method that overrides, implements or is overridden by nothing, and has one declaration.</summary>
    private static bool CanAddTypeParameter(SyntaxNode function, IMethodSymbol method)
    {
        if (method.IsOverride || method.IsVirtual || method.IsAbstract || method.IsExtern ||
            method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null ||
            !method.ExplicitInterfaceImplementations.IsEmpty ||
            function is MethodDeclarationSyntax { Modifiers: var modifiers } && modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return false;
        }

        // An implicit implementation of an interface member.
        var containingType = method.ContainingType;
        return method.MethodKind == MethodKind.LocalFunction || containingType is null ||
            !containingType.AllInterfaces.SelectMany(i => i.GetMembers(method.Name)).Any(
                member => method.Equals(containingType.FindImplementationForInterfaceMember(member), SymbolEqualityComparer.Default));
    }

    /// <summary><c>T</c>, or else <c>T</c> and the parameter name, when that name clashes with nothing.</summary>
    private static string? GetTypeParameterName(SyntaxNode function, ParameterSyntax parameter, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var parameterName = parameter.Identifier.ValueText;
        IEnumerable<string> names = parameterName.Length == 0
            ? ["T"]
            : ["T", "T" + char.ToUpperInvariant(parameterName[0]) + parameterName[1..]];

        var identifiers = function.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToImmutableHashSet();
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!identifiers.Contains(name) && semanticModel.LookupSymbols(parameter.SpanStart, name: name).IsEmpty)
                return name;
        }

        return null;
    }

    private static Document MakeGeneric(Document document, SyntaxNode root, SyntaxNode function, IdentifierNameSyntax inferType, string name)
    {
        var newFunction = function.ReplaceNode(inferType, SyntaxFactory.IdentifierName(name).WithTriviaFrom(inferType));
        var typeParameter = SyntaxFactory.TypeParameter(name);
        newFunction = newFunction switch
        {
            MethodDeclarationSyntax m => m.TypeParameterList is null
                ? m.WithIdentifier(m.Identifier.WithoutTrivia()).WithTypeParameterList(SyntaxFactory.TypeParameterList([typeParameter]).WithTrailingTrivia(m.Identifier.TrailingTrivia))
                : m.AddTypeParameterListParameters(typeParameter),
            LocalFunctionStatementSyntax l => l.TypeParameterList is null
                ? l.WithIdentifier(l.Identifier.WithoutTrivia()).WithTypeParameterList(SyntaxFactory.TypeParameterList([typeParameter]).WithTrailingTrivia(l.Identifier.TrailingTrivia))
                : l.AddTypeParameterListParameters(typeParameter),
            _ => newFunction,
        };

        return document.WithSyntaxRoot(root.ReplaceNode(function, newFunction));
    }

    /// <summary>
    /// Whether <paramref name="newDocument"/> has no error that <paramref name="document"/> did not have, counted per
    /// error code, and one error fewer: the CS20027 that the fix removes.
    /// </summary>
    private static async Task<bool> AddsNoErrorAsync(Document document, SemanticModel semanticModel, Document newDocument, CancellationToken cancellationToken)
    {
        var newSemanticModel = await newDocument.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var before = CountErrors(semanticModel, cancellationToken);
        var after = CountErrors(newSemanticModel, cancellationToken);
        return after.Values.Sum() < before.Values.Sum() &&
            after.All(pair => before.TryGetValue(pair.Key, out var count) && pair.Value <= count);

        static Dictionary<string, int> CountErrors(SemanticModel semanticModel, CancellationToken cancellationToken)
            => semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .GroupBy(d => d.Id)
                .ToDictionary(g => g.Key, g => g.Count());
    }
}
