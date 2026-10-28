// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper implicit <c>it</c> (#24): an argument that uses <c>it</c> where C# finds no <c>it</c> in scope is the
    /// lambda <c>it => argument</c>, if the parameter it goes to takes a function with one parameter. The lambda is the
    /// innermost such argument around <c>it</c>, decided in two trials at each call: first every argument with a free
    /// <c>it</c> is bound as an implicit lambda, then, if that fails, as a value, so that its <c>it</c> belongs to a
    /// lambda further out. Converting a lambda to a parameter that is not a delegate fails without binding its body, so
    /// the second trial costs one more overload resolution.
    /// </summary>
    internal partial class Binder
    {
        private const string SharperItName = "it";

        /// <summary>
        /// Whether the node is the identifier <c>it</c>. <c>@it</c> is not: it keeps the C# meaning.
        /// </summary>
        internal static bool IsSharperItIdentifier(SyntaxNode node)
            => node is IdentifierNameSyntax { Identifier.Text: SharperItName };

        private bool HasSharperImplicitItArgument(ArgumentListSyntax argumentList)
        {
            if (argumentList.Arguments.Count == 0 || !argumentList.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return false;
            }

            foreach (var argument in argumentList.Arguments)
            {
                if (FindSharperFreeIt(argument) is not null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the first <c>it</c> of an argument that can become an implicit <c>it</c> lambda: one that C# does not
        /// find in scope, or finds only as the parameter of an outer implicit lambda. A bare <c>it</c> is never a
        /// lambda by itself, and an <c>it</c> declared inside the argument (a lambda parameter, a pattern or <c>out</c>
        /// variable) keeps the C# meaning.
        /// </summary>
        private IdentifierNameSyntax? FindSharperFreeIt(ArgumentSyntax argument)
        {
            var expression = argument.Expression;
            if (argument.RefKindKeyword.Kind() != SyntaxKind.None ||
                expression is DeclarationExpressionSyntax ||
                IsSharperItIdentifier(expression) ||
                !expression.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return null;
            }

            IdentifierNameSyntax? first = null;
            foreach (var node in expression.DescendantNodesAndSelf(descendIntoChildren: static n => n is not QueryExpressionSyntax && !declaresItParameter(n)))
            {
                if (node is SingleVariableDesignationSyntax { Identifier.ValueText: SharperItName })
                {
                    return null;
                }

                if (first is null && IsSharperItIdentifier(node) && !isMemberName(node))
                {
                    first = (IdentifierNameSyntax)node;
                }
            }

            return first is not null && IsSharperItFree(first) ? first : null;

            static bool declaresItParameter(SyntaxNode node)
            {
                return node switch
                {
                    SimpleLambdaExpressionSyntax lambda => lambda.Parameter.Identifier.ValueText == SharperItName,
                    ParenthesizedLambdaExpressionSyntax lambda => hasItParameter(lambda.ParameterList),
                    AnonymousMethodExpressionSyntax { ParameterList: { } parameterList } => hasItParameter(parameterList),
                    _ => false,
                };
            }

            static bool hasItParameter(ParameterListSyntax parameterList)
            {
                foreach (var parameter in parameterList.Parameters)
                {
                    if (parameter.Identifier.ValueText == SharperItName)
                    {
                        return true;
                    }
                }

                return false;
            }

            // The member name in 'x.it', 'x?.it', 'new C { it = e }', 'new { it = e }' and 'F(it: e)'.
            static bool isMemberName(SyntaxNode node)
            {
                return node.Parent switch
                {
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Name == node,
                    MemberBindingExpressionSyntax => true,
                    QualifiedNameSyntax qualifiedName => qualifiedName.Right == node,
                    NameColonSyntax or NameEqualsSyntax => true,
                    AssignmentExpressionSyntax assignment => assignment.Left == node && assignment.Parent is InitializerExpressionSyntax,
                    _ => false,
                };
            }
        }

        private bool IsSharperItFree(IdentifierNameSyntax it)
        {
            var symbol = LookupSharperIt(it);
            return symbol is null or LambdaParameterSymbol { IsSharperImplicitIt: true };
        }

        /// <summary>
        /// Looks up <c>it</c> as C# would: null when nothing named <c>it</c> is in scope.
        /// </summary>
        private Symbol? LookupSharperIt(IdentifierNameSyntax it)
        {
            var lookupResult = LookupResult.GetInstance();
            var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            LookupIdentifier(lookupResult, it, invoked: false, ref useSiteInfo);
            Symbol? symbol = lookupResult.Kind == LookupResultKind.Empty
                ? null
                : lookupResult.Symbols.Count > 0 ? lookupResult.Symbols[0] : ErrorTypeSymbol.UnknownResultType;
            lookupResult.Free();
            return symbol;
        }

        private UnboundLambda? TryMakeSharperImplicitItLambda(ArgumentSyntax argument, BindingDiagnosticBag diagnostics)
        {
            if (FindSharperFreeIt(argument) is not { } firstIt)
            {
                return null;
            }

            var state = new SharperImplicitItUnboundLambdaState(this, argument.Expression, firstIt, includeCache: true);
            var lambda = new UnboundLambda(argument.Expression, state, functionType: null, diagnostics.AccumulatesDependencies, hasErrors: false) { WasCompilerGenerated = true };
            state.SetUnboundLambda(lambda);
            return lambda;
        }

        /// <summary>
        /// Binds an invocation that has an argument with a free <c>it</c>, in two trials: the arguments as implicit
        /// lambdas, then as values. The lambda trial wins when it binds without errors and each lambda uses its
        /// <c>it</c>. With an outer <c>it</c> in scope both trials can bind, when the method takes a value or a
        /// function at that argument; that is an error, so that an overload added later never moves <c>it</c>.
        /// </summary>
        private BoundExpression BindSharperImplicitItInvocation(
            InvocationExpressionSyntax node,
            BoundExpression boundExpression,
            string? methodName,
            AnalyzedArguments analyzedArguments,
            BindingDiagnosticBag diagnostics)
        {
            if (FindSharperDeclaration(node.ArgumentList) is { } declaration)
            {
                // A variable declared in the arguments gets its type while the call binds, so the two trials would
                // both set it. Bind as values, and report a declaration inside an argument that would be a lambda.
                // PROTOTYPE(sharper): 'F(Make(it), out var v)' cannot be an implicit lambda yet (#24).
                if (declaration.argument is not null)
                {
                    diagnostics.Add(ErrorCode.ERR_SharperItDeclaration, declaration.designation.Location, declaration.designation.Identifier.ValueText);
                }

                BindArgumentsAndNames(node.ArgumentList, diagnostics, analyzedArguments, allowArglist: true);
                return BindInvocationExpression(node, node.Expression, methodName, boundExpression, analyzedArguments, diagnostics, acceptOnlyMethods: false);
            }

            // Only an argument that some candidate takes as a function can be a lambda. Without this check every call
            // with an 'it' in its arguments would bind twice, and a failed call binds its lambdas again for error
            // recovery, which multiplies with the nesting depth.
            var implicitItArguments = ArrayBuilder<ArgumentSyntax>.GetInstance();
            foreach (var argument in node.ArgumentList.Arguments)
            {
                if (FindSharperFreeIt(argument) is not null && SharperCandidateTakesFunction(boundExpression, methodName, node.ArgumentList, argument))
                {
                    implicitItArguments.Add(argument);
                }
            }

            if (implicitItArguments.Count == 0)
            {
                implicitItArguments.Free();
                BindArgumentsAndNames(node.ArgumentList, diagnostics, analyzedArguments, allowArglist: true);
                return BindInvocationExpression(node, node.Expression, methodName, boundExpression, analyzedArguments, diagnostics, acceptOnlyMethods: false);
            }

            var lambdaDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BindArgumentsAndNames(node.ArgumentList, lambdaDiagnostics, analyzedArguments, allowArglist: true, sharperImplicitItArguments: implicitItArguments);
            BoundExpression lambdaResult = BindInvocationExpression(node, node.Expression, methodName, boundExpression, analyzedArguments, lambdaDiagnostics, acceptOnlyMethods: false);
            bool lambdasUseIt = AllSharperImplicitItLambdasUseIt(lambdaResult, node.ArgumentList, implicitItArguments.Count);
            bool lambdaOk = lambdasUseIt && !lambdaDiagnostics.HasAnyErrors();
            implicitItArguments.Free();

            if (lambdaOk && !IsSharperOuterItInScope(node))
            {
                diagnostics.AddRangeAndFree(lambdaDiagnostics);
                return lambdaResult;
            }

            analyzedArguments.Clear();
            var valueDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BindArgumentsAndNames(node.ArgumentList, valueDiagnostics, analyzedArguments, allowArglist: true);
            BoundExpression valueResult = BindInvocationExpression(node, node.Expression, methodName, boundExpression, analyzedArguments, valueDiagnostics, acceptOnlyMethods: false);
            bool valueOk = !valueDiagnostics.HasAnyErrors();

            if (lambdaOk && valueOk)
            {
                // Keep the value, where 'it' is the outer one, so that the outer lambda reports no second error.
                lambdaDiagnostics.Free();
                diagnostics.AddRangeAndFree(valueDiagnostics);
                var firstIt = FindSharperFirstFreeIt(node.ArgumentList);
                Debug.Assert(firstIt is not null);
                diagnostics.Add(ErrorCode.ERR_SharperItValueOrFunction, firstIt.Location, methodName ?? node.Expression.ToString());
                return valueResult.WithHasErrors();
            }

            // When neither binds, report the lambda trial if its lambdas use 'it' (the method takes a function there,
            // and the lambda has errors), and the value trial otherwise.
            if (lambdaOk || (!valueOk && lambdasUseIt))
            {
                valueDiagnostics.Free();
                diagnostics.AddRangeAndFree(lambdaDiagnostics);
                return lambdaResult;
            }

            lambdaDiagnostics.Free();
            diagnostics.AddRangeAndFree(valueDiagnostics);
            return valueResult;
        }

        /// <summary>
        /// Finds a variable declared in the arguments, outside explicit lambdas (which get new locals each time they
        /// bind), preferring one inside an argument with a free <c>it</c>.
        /// </summary>
        private (SingleVariableDesignationSyntax designation, ArgumentSyntax? argument)? FindSharperDeclaration(ArgumentListSyntax argumentList)
        {
            (SingleVariableDesignationSyntax designation, ArgumentSyntax? argument)? result = null;
            foreach (var argument in argumentList.Arguments)
            {
                foreach (var node in argument.DescendantNodes(descendIntoChildren: static n => n is not AnonymousFunctionExpressionSyntax and not QueryExpressionSyntax))
                {
                    if (node is SingleVariableDesignationSyntax designation)
                    {
                        if (FindSharperFreeIt(argument) is not null)
                        {
                            return (designation, argument);
                        }

                        result ??= (designation, null);
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Whether a candidate of the call may take the argument as a function with one parameter: a delegate or an
        /// expression tree of one. It only filters, so it errs towards yes: overload resolution decides.
        /// </summary>
        private bool SharperCandidateTakesFunction(BoundExpression boundExpression, string? methodName, ArgumentListSyntax argumentList, ArgumentSyntax argument)
        {
            int position = argumentList.Arguments.IndexOf(argument);
            string? name = argument.NameColon?.Name.Identifier.ValueText;

            switch (boundExpression)
            {
                case BoundMethodGroup methodGroup:
                    foreach (var method in methodGroup.Methods)
                    {
                        if (takesFunction(method, position, name))
                        {
                            return true;
                        }
                    }

                    if (methodGroup.SearchExtensions && methodName is not null)
                    {
                        var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                        var lookupResults = ArrayBuilder<SingleLookupResult>.GetInstance();
                        int arity = methodGroup.TypeArgumentsOpt.IsDefaultOrEmpty ? 0 : methodGroup.TypeArgumentsOpt.Length;
                        LookupOptions options = arity == 0 ? LookupOptions.AllMethodsOnArityZero : LookupOptions.Default;
                        foreach (var scope in new ExtensionScopes(this))
                        {
                            lookupResults.Clear();
                            scope.Binder.EnumerateAllExtensionMembersInSingleBinder(lookupResults, methodName, arity, options, originalBinder: this, ref discardedUseSiteInfo, ref discardedUseSiteInfo);
                            foreach (var lookupResult in lookupResults)
                            {
                                if (lookupResult.Symbol is MethodSymbol method && takesFunction(method, method.IsExtensionMethod ? position + 1 : position, name))
                                {
                                    lookupResults.Free();
                                    return true;
                                }
                            }
                        }

                        lookupResults.Free();
                    }

                    return false;

                case { Type: NamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } }:
                    return takesFunction(invoke, position, name);

                default:
                    // 'dynamic', an error, or something else that overload resolution reports on.
                    return true;
            }

            static bool takesFunction(MethodSymbol method, int position, string? name)
            {
                var parameters = method.Parameters;
                ParameterSymbol? parameter = null;
                if (name is not null)
                {
                    foreach (var p in parameters)
                    {
                        if (p.Name == name)
                        {
                            parameter = p;
                            break;
                        }
                    }
                }
                else if (position < parameters.Length)
                {
                    parameter = parameters[position];
                }
                else if (parameters is [.., { IsParams: true } last])
                {
                    parameter = last;
                }

                if (parameter is null)
                {
                    return false;
                }

                var type = parameter.Type;
                if (parameter.IsParams && type is ArrayTypeSymbol array)
                {
                    type = array.ElementType;
                }

                if (type.IsExpressionTree())
                {
                    type = ((NamedTypeSymbol)type).TypeArgumentsWithAnnotationsNoUseSiteDiagnostics[0].Type;
                }

                return type is NamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod.ParameterCount: 1 };
            }
        }

        private IdentifierNameSyntax? FindSharperFirstFreeIt(ArgumentListSyntax argumentList)
        {
            foreach (var argument in argumentList.Arguments)
            {
                if (FindSharperFreeIt(argument) is { } it)
                {
                    return it;
                }
            }

            return null;
        }

        private bool IsSharperOuterItInScope(InvocationExpressionSyntax node)
        {
            return FindSharperFirstFreeIt(node.ArgumentList) is { } it &&
                LookupSharperIt(it) is LambdaParameterSymbol { IsSharperImplicitIt: true };
        }

        /// <summary>
        /// Whether every argument bound as an implicit <c>it</c> lambda became a lambda that uses its <c>it</c>. A
        /// lambda whose <c>it</c> is used only by inner implicit lambdas is a value: <c>Make(ys.First(it.X))</c>.
        /// </summary>
        private static bool AllSharperImplicitItLambdasUseIt(BoundExpression result, ArgumentListSyntax argumentList, int expected)
        {
            var finder = new SharperImplicitItLambdaFinder(argumentList, expected);
            finder.Visit(result);
            return finder.AllUseIt && finder.Found == finder.Expected;
        }

        private sealed class SharperImplicitItLambdaFinder : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly ArgumentListSyntax _argumentList;

            public int Expected { get; }
            public int Found { get; private set; }
            public bool AllUseIt { get; private set; } = true;

            public SharperImplicitItLambdaFinder(ArgumentListSyntax argumentList, int expected)
            {
                _argumentList = argumentList;
                Expected = expected;
            }

            public override BoundNode? VisitLambda(BoundLambda node)
            {
                if (node.Symbol is LambdaSymbol { IsSharperImplicitIt: true } && node.Syntax.Parent is ArgumentSyntax argument && argument.Parent == _argumentList)
                {
                    Found++;
                    AllUseIt &= SharperItUseFinder.Uses(node.Body, node.Symbol.Parameters[0]);
                }

                // Lambdas in the body belong to other calls.
                return null;
            }

            public override BoundNode? VisitUnboundLambda(UnboundLambda node)
            {
                if (node.Data.IsSharperImplicitIt && node.Syntax.Parent is ArgumentSyntax argument && argument.Parent == _argumentList)
                {
                    // Not converted: the call failed, and the lambda never got a delegate type.
                    AllUseIt = false;
                }

                return null;
            }
        }

        /// <summary>
        /// Finds whether a bound body reads a parameter, and the first implicit <c>it</c> lambda in it, through
        /// explicit lambdas too.
        /// </summary>
        private sealed class SharperItUseFinder : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly ParameterSymbol _parameter;
            private bool _uses;
            private BoundLambda? _firstImplicitLambda;

            private SharperItUseFinder(ParameterSymbol parameter)
            {
                _parameter = parameter;
            }

            public static bool Uses(BoundNode body, ParameterSymbol parameter)
            {
                var finder = new SharperItUseFinder(parameter);
                finder.Visit(body);
                return finder._uses;
            }

            public static (bool uses, BoundLambda? firstImplicitLambda) Find(BoundNode body, ParameterSymbol parameter)
            {
                var finder = new SharperItUseFinder(parameter);
                finder.Visit(body);
                return (finder._uses, finder._firstImplicitLambda);
            }

            public override BoundNode? VisitParameter(BoundParameter node)
            {
                if (Symbol.Equals(node.ParameterSymbol, _parameter, TypeCompareKind.ConsiderEverything))
                {
                    _uses = true;
                }

                return null;
            }

            public override BoundNode? VisitLambda(BoundLambda node)
            {
                if (node.Symbol is LambdaSymbol { IsSharperImplicitIt: true })
                {
                    _firstImplicitLambda ??= node;
                }

                return base.VisitLambda(node);
            }
        }

        private sealed class SharperImplicitItUnboundLambdaState : UnboundLambdaState
        {
            private readonly ExpressionSyntax _body;
            private readonly IdentifierNameSyntax _firstIt;

            public SharperImplicitItUnboundLambdaState(Binder binder, ExpressionSyntax body, IdentifierNameSyntax firstIt, bool includeCache)
                : base(binder, includeCache)
            {
                _body = body;
                _firstIt = firstIt;
            }

            public override bool IsSharperImplicitIt => true;
            public override string ParameterName(int index) => SharperItName;
            public override bool ParameterIsDiscard(int index) => false;
            public override bool ParameterIsSharperVal(int index) => true;
            public override SyntaxList<AttributeListSyntax> ParameterAttributes(int index) => default;
            public override bool HasSignature => true;

            public override bool HasExplicitReturnType(out RefKind refKind, out ImmutableArray<CustomModifier> refCustomModifiers, out TypeWithAnnotations returnType)
            {
                refKind = default;
                refCustomModifiers = [];
                returnType = default;
                return false;
            }

            public override bool HasExplicitlyTypedParameterList => false;
            public override int ParameterCount => 1;
            public override bool IsAsync => false;
            public override bool IsStatic => false;
            public override RefKind RefKind(int index) => Microsoft.CodeAnalysis.RefKind.None;
            public override ScopedKind DeclaredScope(int index) => ScopedKind.None;
            public override MessageID MessageID => MessageID.IDS_Lambda;
            public override Location ParameterLocation(int index) => _firstIt.Location;
            public override ParameterSyntax? ParameterSyntax(int index) => null;
            public override TypeWithAnnotations ParameterTypeWithAnnotations(int index) => throw new ArgumentException(); // implicitly typed

            protected override UnboundLambdaState WithCachingCore(bool includeCache)
                => new SharperImplicitItUnboundLambdaState(Binder, _body, _firstIt, includeCache);

            protected override BoundExpression? GetLambdaExpressionBody(BoundBlock body) => null;

            protected override BoundBlock CreateBlockFromLambdaExpressionBody(Binder lambdaBodyBinder, BoundExpression expression, BindingDiagnosticBag diagnostics)
                => throw ExceptionUtilities.Unreachable();

            protected override BoundBlock BindLambdaBodyCore(LambdaSymbol lambdaSymbol, Binder lambdaBodyBinder, BindingDiagnosticBag diagnostics)
            {
                var bodyBinder = lambdaBodyBinder.GetRequiredBinder(_body);
                BoundExpression expression = bodyBinder.BindValue(_body, diagnostics, BindValueKind.RValue);

                var locals = bodyBinder.GetDeclaredLocalsForScope(_body);
                if (!locals.IsEmpty)
                {
                    // PROTOTYPE(sharper): the binder map scopes these to the lambda, but whether an argument is a
                    // lambda depends on types; decide whether declarations can be allowed (#24).
                    diagnostics.Add(ErrorCode.ERR_SharperItDeclaration, locals[0].GetFirstLocation(), locals[0]);
                }

                var (usesIt, innerLambda) = SharperItUseFinder.Find(expression, lambdaSymbol.Parameters[0]);
                if (usesIt && innerLambda is not null)
                {
                    // One meaning of 'it' per lambda: 'orders.Where(it.Lines.Any(it.Qty > 5))'.
                    diagnostics.Add(ErrorCode.ERR_SharperItTwoMeanings, innerLambda.Symbol.Parameters[0].GetFirstLocation());
                }

                return bodyBinder.CreateBlockFromExpression(_body, locals, Microsoft.CodeAnalysis.RefKind.None, expression, _body, diagnostics);
            }
        }
    }
}
