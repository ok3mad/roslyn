// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Sharper.InferredTypes;

/// <summary>
/// Sharper: the fixes that write a type in place of an <c>infer</c> that had nothing to infer from.
/// <list type="bullet">
/// <item>CS20014 (the returned value has no type: <c>null</c>, <c>default</c>, <c>new()</c>, a <c>throw</c>
/// expression) writes <c>object</c>, <c>object?</c> for <c>null</c> and <c>default</c> where nullable annotations are
/// enabled. A lambda or method group without a natural type gets no fix: <c>object</c> would not take it.</item>
/// <item>CS20024 (the returned values have no best common type) offers each type the error lists, then the most
/// derived base class they all convert to (not <c>object</c>), then <c>object</c>. Where a returned value has no type
/// (<c>null</c>), each type is nullable (<c>int?</c>, <c>string?</c>).</item>
/// <item>CS20026 (the default value has no type: <c>infer x = null</c>) writes <c>object</c>, as CS20014 does.</item>
/// </list>
/// The return type of an async method is <c>Task&lt;T&gt;</c>, and of an iterator <c>IEnumerable&lt;T&gt;</c>, as the
/// compiler would have inferred.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = SharperProviderNames.WriteTypeForNothingToInfer), Shared]
[method: ImportingConstructor]
[method: SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
internal sealed class SharperWriteTypeForNothingToInferCodeFixProvider() : CodeFixProvider
{
    /// <summary>Cannot infer the return type of '{0}', because the value it returns ({1}) has no type.</summary>
    private const string CS20014 = nameof(CS20014);

    /// <summary>Cannot infer the return type of '{0}', because the values it returns ({1}) have no best common type.</summary>
    private const string CS20024 = nameof(CS20024);

    /// <summary>Cannot infer the type of parameter '{0}', because its default value ({1}) has no type.</summary>
    private const string CS20026 = nameof(CS20026);

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [CS20014, CS20024, CS20026];

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
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var (inferType, types, method) = diagnostic.Id switch
            {
                CS20014 => ForReturnedValue(node, semanticModel, cancellationToken),
                CS20024 => ForReturnedValues(node, semanticModel, cancellationToken),
                CS20026 => ForDefaultValue(node, semanticModel, cancellationToken),
                _ => default,
            };

            if (inferType is null || types.IsDefaultOrEmpty)
                continue;

            foreach (var type in types
                .Select(type => SharperInferredTypeHelpers.AdjustNullability(type, semanticModel, inferType.SpanStart))
                .Select(type => method is null ? type : WrapReturnType(type, method, semanticModel.Compilation))
                .Distinct<ITypeSymbol>(SymbolEqualityComparer.IncludeNullability))
            {
                context.RegisterCodeFix(SharperInferredTypeHelpers.CreateWriteTypeAction(document, inferType, type, semanticModel), diagnostic);
            }
        }
    }

    /// <summary>CS20014: the error is on the value, in an expression body or a <c>return</c> or <c>yield return</c>.</summary>
    private static (TypeSyntax? InferType, ImmutableArray<ITypeSymbol> Types, IMethodSymbol? Method) ForReturnedValue(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (node is not ExpressionSyntax value)
            return default;

        value = value.WalkUpParentheses();
        var function = value.Parent switch
        {
            ArrowExpressionClauseSyntax arrow => arrow.Parent,
            ReturnStatementSyntax or YieldStatementSyntax => value.Parent.FirstAncestorOrSelf<SyntaxNode>(IsFunction),
            _ => null,
        };

        if (GetInferReturnType(function, semanticModel, cancellationToken) is not var (inferType, method) ||
            GetTypeOfValueWithNoType(value, semanticModel) is not { } type)
        {
            return default;
        }

        return (inferType, [type], method);
    }

    /// <summary>CS20024: the error is on <c>infer</c>; the types are those of the returned values.</summary>
    private static (TypeSyntax? InferType, ImmutableArray<ITypeSymbol> Types, IMethodSymbol? Method) ForReturnedValues(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (node is not TypeSyntax { Parent: { } function } ||
            GetInferReturnType(function, semanticModel, cancellationToken) is not var (inferType, method) ||
            inferType != node)
        {
            return default;
        }

        // The returned values of this function, not of the lambdas and local functions in it.
        SyntaxNode? body = function switch
        {
            BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
            _ => null,
        };
        if (body is null)
            return default;

        var values = body.DescendantNodesAndSelf(n => n == body || !IsFunction(n))
            .Select(n => n switch
            {
                ReturnStatementSyntax r => r.Expression,
                YieldStatementSyntax(SyntaxKind.YieldReturnStatement) y => y.Expression,
                _ => null,
            })
            .WhereNotNull()
            .ToImmutableArray();

        using var _ = ArrayBuilder<ITypeSymbol>.GetInstance(out var listed);
        var anyWithNoType = false;
        foreach (var value in values)
        {
            if (semanticModel.GetTypeInfo(value, cancellationToken).Type is { } type and not IErrorTypeSymbol)
            {
                if (!listed.Any(t => t.Equals(type, SymbolEqualityComparer.Default)))
                    listed.Add(type);
            }
            else
            {
                anyWithNoType = true;
            }
        }

        if (listed.IsEmpty)
            return default;

        var compilation = semanticModel.Compilation;
        using var _1 = ArrayBuilder<ITypeSymbol>.GetInstance(out var types);
        types.AddRange(listed);
        if (GetCommonBaseClass(listed, compilation) is { } baseClass)
            types.Add(baseClass);

        types.Add(compilation.GetSpecialType(SpecialType.System_Object));
        return (inferType, types.SelectAsArray(type => anyWithNoType ? MakeNullable(type, compilation) : type), method);
    }

    /// <summary>CS20026: the error is on the default value.</summary>
    private static (TypeSyntax? InferType, ImmutableArray<ITypeSymbol> Types, IMethodSymbol? Method) ForDefaultValue(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (node is not ExpressionSyntax value ||
            value.WalkUpParentheses() is not { Parent: EqualsValueClauseSyntax { Parent: ParameterSyntax { Type: { } inferType } } } ||
            !SharperFacts.IsInferKeyword(inferType, semanticModel, cancellationToken) ||
            GetTypeOfValueWithNoType(value, semanticModel) is not { } type)
        {
            return default;
        }

        return (inferType, [type], null);
    }

    /// <summary>The <c>infer</c> return type of an ordinary method, local function or operator, and the method.</summary>
    private static (TypeSyntax InferType, IMethodSymbol Method)? GetInferReturnType(SyntaxNode? function, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var returnType = function switch
        {
            MethodDeclarationSyntax m => m.ReturnType,
            LocalFunctionStatementSyntax l => l.ReturnType,
            OperatorDeclarationSyntax o => o.ReturnType,
            ConversionOperatorDeclarationSyntax c => c.Type,
            _ => null,
        };

        return SharperFacts.IsInferKeyword(returnType, semanticModel, cancellationToken) &&
            semanticModel.GetDeclaredSymbol(function!, cancellationToken) is IMethodSymbol method
            ? (returnType, method)
            : null;
    }

    private static bool IsFunction(SyntaxNode node)
        => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax;

    /// <summary>
    /// <c>object</c> for a value that has no type and converts to it: <c>object?</c> for <c>null</c> and
    /// <c>default</c>, <c>object</c> for <c>new()</c> and a <c>throw</c> expression. Null for any other value, such as
    /// a lambda or a collection expression.
    /// </summary>
    private static ITypeSymbol? GetTypeOfValueWithNoType(ExpressionSyntax value, SemanticModel semanticModel)
    {
        var @object = semanticModel.Compilation.GetSpecialType(SpecialType.System_Object);
        return value.WalkDownParentheses() switch
        {
            LiteralExpressionSyntax(SyntaxKind.NullLiteralExpression or SyntaxKind.DefaultLiteralExpression) => @object.WithNullableAnnotation(NullableAnnotation.Annotated),
            ImplicitObjectCreationExpressionSyntax or ThrowExpressionSyntax => @object,
            _ => null,
        };
    }

    /// <summary>
    /// The most derived base class, other than <c>object</c>, that each of <paramref name="types"/> converts to by
    /// reference, or null.
    /// </summary>
    private static ITypeSymbol? GetCommonBaseClass(ArrayBuilder<ITypeSymbol> types, Compilation compilation)
    {
        for (var baseClass = types[0].BaseType; baseClass is not null && baseClass.SpecialType != SpecialType.System_Object; baseClass = baseClass.BaseType)
        {
            if (types.All(type => compilation.ClassifyCommonConversion(type, baseClass) is { IsImplicit: true } conversion && (conversion.IsIdentity || conversion.IsReference)))
                return baseClass;
        }

        return null;
    }

    private static ITypeSymbol MakeNullable(ITypeSymbol type, Compilation compilation)
        => type.IsValueType
            ? type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? type : compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(type)
            : type.WithNullableAnnotation(NullableAnnotation.Annotated);

    /// <summary>The return type for a returned value of <paramref name="type"/>: in a task for async, in a sequence for an iterator.</summary>
    private static ITypeSymbol WrapReturnType(ITypeSymbol type, IMethodSymbol method, Compilation compilation)
    {
        var wrapper = method.IsIterator ? compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T)
            : method.IsAsync ? compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1")
            : null;
        return wrapper is null ? type : wrapper.Construct([type], [type.NullableAnnotation]);
    }
}
