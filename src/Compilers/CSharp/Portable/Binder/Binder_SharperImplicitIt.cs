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
        internal const string SharperItName = "it";

        /// <summary>
        /// Whether the node is the identifier <c>it</c>. <c>@it</c> is not: it keeps the C# meaning.
        /// </summary>
        internal static bool IsSharperItIdentifier(SyntaxNode node)
            => node is IdentifierNameSyntax { Identifier.Text: SharperItName };

        private bool HasSharperImplicitItArgument(BaseArgumentListSyntax argumentList)
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
                IsSharperItIdentifier(expression))
            {
                return null;
            }

            return FindSharperFreeIt(expression);
        }

        /// <summary>
        /// Finds the first free <c>it</c> of an expression (see the argument overload), outside the stages of pipes
        /// (<c>x |&gt; stage</c>), which own every <c>it</c> they contain.
        /// </summary>
        private IdentifierNameSyntax? FindSharperFreeIt(ExpressionSyntax expression)
        {
            if (!expression.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return null;
            }

            IdentifierNameSyntax? first = null;
            foreach (var node in expression.DescendantNodesAndSelf(descendIntoChildren: n => n is not QueryExpressionSyntax && !declaresItParameter(n) && (n == expression || !isPipeStage(n))))
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

            static bool isPipeStage(SyntaxNode node)
            {
                return node.Parent is PipeExpressionSyntax pipe && pipe.Stage == node;
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
            return symbol is null || IsSharperImplicitItSymbol(symbol);
        }

        /// <summary>
        /// Whether the symbol is an <c>it</c> that Sharper declared: the parameter of an implicit lambda, or the value of a
        /// pipe stage.
        /// </summary>
        private static bool IsSharperImplicitItSymbol(Symbol? symbol)
            => symbol is LambdaParameterSymbol { IsSharperImplicitIt: true } or SharperPipeItLocalSymbol;

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
        /// Binds an invocation that has an argument with a free <c>it</c>, in the two trials of
        /// <see cref="BindSharperImplicitItArguments"/>; the candidates are the method group or the delegate.
        /// </summary>
        private BoundExpression BindSharperImplicitItInvocation(
            InvocationExpressionSyntax node,
            BoundExpression boundExpression,
            string? methodName,
            AnalyzedArguments analyzedArguments,
            BindingDiagnosticBag diagnostics)
        {
            return BindSharperImplicitItArguments(
                node.ArgumentList,
                methodName ?? node.Expression.ToString(),
                argument => SharperCandidateTakesFunction(boundExpression, methodName, node.ArgumentList, argument),
                (implicitItArguments, trialDiagnostics) =>
                {
                    analyzedArguments.Clear();
                    BindArgumentsAndNames(node.ArgumentList, trialDiagnostics, analyzedArguments, allowArglist: true, sharperImplicitItArguments: implicitItArguments);
                    return BindInvocationExpression(node, node.Expression, methodName, boundExpression, analyzedArguments, trialDiagnostics, acceptOnlyMethods: false);
                },
                diagnostics);
        }

        /// <summary>
        /// Binds <c>new C(...)</c> when an argument has a free <c>it</c>; the candidates are the type's instance constructors.
        /// </summary>
        private BoundExpression BindSharperImplicitItClassCreation(ObjectCreationExpressionSyntax node, NamedTypeSymbol type, string typeName, TypeSymbol? initializerType, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node.ArgumentList is not null);
            return BindSharperImplicitItObjectCreation(
                node.ArgumentList,
                type,
                (analyzedArguments, trialDiagnostics) => BindClassCreationExpressionWithArguments(node, type, typeName, analyzedArguments, trialDiagnostics, initializerType),
                diagnostics);
        }

        /// <summary>
        /// Binds an object creation (<c>new C(...)</c>, or a target-typed <c>new(...)</c> once its type is known) that has an
        /// argument with a free <c>it</c>; the candidates are the type's instance constructors.
        /// </summary>
        private BoundExpression BindSharperImplicitItObjectCreation(
            ArgumentListSyntax argumentList,
            NamedTypeSymbol type,
            Func<AnalyzedArguments, BindingDiagnosticBag, BoundExpression> bind,
            BindingDiagnosticBag diagnostics)
        {
            return BindSharperImplicitItArguments(
                argumentList,
                type.Name,
                argument => SharperCandidatesTakeFunction(type.InstanceConstructors, argumentList, argument),
                (implicitItArguments, trialDiagnostics) =>
                {
                    var analyzedArguments = AnalyzedArguments.GetInstance();
                    BindArgumentsAndNames(argumentList, trialDiagnostics, analyzedArguments, allowArglist: true, sharperImplicitItArguments: implicitItArguments);
                    var result = bind(analyzedArguments, trialDiagnostics);
                    analyzedArguments.Free();
                    return result;
                },
                diagnostics);
        }

        /// <summary>
        /// The arguments of a target-typed <c>new(...)</c> that are implicit lambdas until its type is known: those with a
        /// free <c>it</c>, unless the arguments declare a variable. Null when there are none.
        /// </summary>
        private ArrayBuilder<ArgumentSyntax>? GetSharperImplicitItTargetTypedArguments(ImplicitObjectCreationExpressionSyntax node)
        {
            if (!HasSharperImplicitItArgument(node.ArgumentList) || FindSharperDeclaration(node.ArgumentList) is not null)
            {
                return null;
            }

            var arguments = ArrayBuilder<ArgumentSyntax>.GetInstance();
            foreach (var argument in node.ArgumentList.Arguments)
            {
                if (FindSharperFreeIt(argument) is not null)
                {
                    arguments.Add(argument);
                }
            }

            return arguments;
        }

        /// <summary>
        /// Whether a target-typed <c>new(...)</c> holds implicit lambdas (see
        /// <see cref="GetSharperImplicitItTargetTypedArguments"/>), which the constructor turns into lambdas or values once
        /// the type is known (<see cref="BindSharperImplicitItTargetTypedCreation"/>).
        /// </summary>
        internal static bool HasSharperImplicitItTargetTypedArguments(BoundUnconvertedObjectCreationExpression node)
        {
            foreach (var argument in node.Arguments)
            {
                if (argument is UnboundLambda { Data.IsSharperImplicitIt: true })
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Binds a target-typed <c>new(...)</c> that holds implicit lambdas once its type is known: in the two trials for a
        /// class, struct or enum, and with the lambdas bound as values otherwise. The other arguments keep the values bound
        /// with the expression, whose diagnostics are already reported.
        /// </summary>
        internal BoundExpression BindSharperImplicitItTargetTypedCreation(
            BoundUnconvertedObjectCreationExpression node,
            TypeSymbol type,
            Func<AnalyzedArguments, BindingDiagnosticBag, BoundExpression> bind,
            BindingDiagnosticBag diagnostics)
        {
            var argumentList = ((ImplicitObjectCreationExpressionSyntax)node.Syntax).ArgumentList;
            if (type is NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Enum, IsAnonymousType: false } namedType)
            {
                return BindSharperImplicitItArguments(
                    argumentList,
                    namedType.Name,
                    argument => SharperCandidatesTakeFunction(namedType.InstanceConstructors, argumentList, argument),
                    bindWithArguments,
                    diagnostics);
            }

            return bindWithArguments(null, diagnostics);

            BoundExpression bindWithArguments(ArrayBuilder<ArgumentSyntax>? implicitItArguments, BindingDiagnosticBag trialDiagnostics)
            {
                var analyzedArguments = AnalyzedArguments.GetInstance(node.Arguments, node.ArgumentRefKindsOpt, node.ArgumentNamesOpt);
                for (int i = 0; i < analyzedArguments.Arguments.Count; i++)
                {
                    var argument = argumentList.Arguments[i];
                    if (analyzedArguments.Arguments[i] is UnboundLambda { Data.IsSharperImplicitIt: true } && implicitItArguments?.Contains(argument) != true)
                    {
                        analyzedArguments.Arguments[i] = BindArgumentValue(trialDiagnostics, argument, allowArglist: true, RefKind.None);
                    }
                }

                var result = bind(analyzedArguments, trialDiagnostics);
                analyzedArguments.Free();
                return result;
            }
        }

        /// <summary>
        /// A target-typed <c>new(...)</c> with no type, whose implicit lambdas become values.
        /// </summary>
        internal BoundExpression BindSharperImplicitItTargetTypedCreationForErrorRecovery(BoundUnconvertedObjectCreationExpression node, BindingDiagnosticBag diagnostics)
        {
            return BindSharperImplicitItTargetTypedCreation(
                node,
                CreateErrorType(),
                (analyzedArguments, trialDiagnostics) => MakeBadExpressionForObjectCreation(node.Syntax, CreateErrorType(), analyzedArguments, node.InitializerOpt, typeSyntax: node.Syntax, trialDiagnostics),
                diagnostics);
        }

        /// <summary>
        /// Binds an element access that has an argument with a free <c>it</c>; the candidates are the indexers of the
        /// receiver.
        /// </summary>
        private BoundExpression BindSharperImplicitItElementAccess(
            ExpressionSyntax node,
            BoundExpression receiver,
            BracketedArgumentListSyntax argumentList,
            bool allowInlineArrayElementAccess,
            BindingDiagnosticBag diagnostics)
        {
            ImmutableArray<PropertySymbol> indexers = default;
            return BindSharperImplicitItArguments(
                argumentList,
                "this[]",
                argument =>
                {
                    switch (receiver)
                    {
                        case BoundPropertyGroup propertyGroup:
                            return SharperCandidatesTakeFunction(propertyGroup.Properties, argumentList, argument);
                        case { Type: { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.TypeParameter } type }:
                            if (indexers.IsDefault)
                            {
                                var lookupResult = LookupResult.GetInstance();
                                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                                LookupMembersWithFallback(lookupResult, type, WellKnownMemberNames.Indexer, arity: 0, ref useSiteInfo);
                                var builder = ArrayBuilder<PropertySymbol>.GetInstance();
                                foreach (var symbol in lookupResult.Symbols)
                                {
                                    if (symbol is PropertySymbol { IsIndexer: true } indexer)
                                    {
                                        builder.Add(indexer);
                                    }
                                }

                                lookupResult.Free();
                                indexers = builder.ToImmutableAndFree();
                            }

                            return SharperCandidatesTakeFunction(indexers, argumentList, argument);
                        case { Type: { TypeKind: TypeKind.Array or TypeKind.Pointer or TypeKind.FunctionPointer } }:
                            return false;
                        default:
                            // 'dynamic', an error, or something else that binding reports on.
                            return true;
                    }
                },
                (implicitItArguments, trialDiagnostics) =>
                {
                    var analyzedArguments = AnalyzedArguments.GetInstance();
                    BindArgumentsAndNames(argumentList, trialDiagnostics, analyzedArguments, sharperImplicitItArguments: implicitItArguments);
                    var result = BindElementAccessWithArguments(node, receiver, analyzedArguments, allowInlineArrayElementAccess, trialDiagnostics);
                    analyzedArguments.Free();
                    return result;
                },
                diagnostics);
        }

        /// <summary>
        /// Binds a constructor initializer (<c>: base(...)</c>, <c>: this(...)</c> or a primary constructor's base
        /// arguments) that has an argument with a free <c>it</c>; the candidates are the constructors it can call.
        /// </summary>
        private BoundExpression BindSharperImplicitItConstructorInitializer(
            ArgumentListSyntax argumentList,
            MethodSymbol constructor,
            BindingDiagnosticBag diagnostics)
        {
            bool isThis = argumentList.Parent.IsKind(SyntaxKind.ThisConstructorInitializer);
            NamedTypeSymbol? initializerType = isThis ? constructor.ContainingType : constructor.ContainingType.BaseTypeNoUseSiteDiagnostics;
            return BindSharperImplicitItArguments(
                argumentList,
                isThis ? "this" : "base",
                argument => initializerType is not null && SharperCandidatesTakeFunction(initializerType.InstanceConstructors, argumentList, argument),
                (implicitItArguments, trialDiagnostics) => BindConstructorInitializerCore(argumentList, constructor, trialDiagnostics, implicitItArguments),
                diagnostics);
        }

        /// <summary>
        /// Binds an argument list that has an argument with a free <c>it</c>, in two trials: the arguments as implicit
        /// lambdas, then as values. The lambda trial wins when it binds without errors and each lambda uses its
        /// <c>it</c>. With an outer <c>it</c> in scope both trials can bind, when the callee takes a value or a function
        /// at that argument; that is an error, so that an overload added later never moves <c>it</c>.
        /// </summary>
        /// <param name="calleeName">The name in CS20021.</param>
        /// <param name="takesFunction">Whether some candidate may take the argument as a function with one parameter.</param>
        /// <param name="bind">Binds the arguments, the given ones as implicit lambdas (none when null), and the expression.</param>
        private BoundExpression BindSharperImplicitItArguments(
            BaseArgumentListSyntax argumentList,
            string calleeName,
            Func<ArgumentSyntax, bool> takesFunction,
            Func<ArrayBuilder<ArgumentSyntax>?, BindingDiagnosticBag, BoundExpression> bind,
            BindingDiagnosticBag diagnostics)
        {
            if (FindSharperDeclaration(argumentList) is { } declaration)
            {
                // A variable declared in the arguments gets its type while the call binds, so the two trials would
                // both set it. Bind as values, and report a declaration inside an argument that would be a lambda.
                // PROTOTYPE(sharper): 'F(Make(it), out var v)' cannot be an implicit lambda yet (#125).
                if (declaration.argument is not null)
                {
                    diagnostics.Add(ErrorCode.ERR_SharperItDeclaration, declaration.designation.Location, declaration.designation.Identifier.ValueText);
                }

                return bind(null, diagnostics);
            }

            // Only an argument that some candidate takes as a function can be a lambda. Without this check every call
            // with an 'it' in its arguments would bind twice, and a failed call binds its lambdas again for error
            // recovery, which multiplies with the nesting depth.
            var implicitItArguments = ArrayBuilder<ArgumentSyntax>.GetInstance();
            foreach (var argument in argumentList.Arguments)
            {
                if (FindSharperFreeIt(argument) is not null && takesFunction(argument))
                {
                    implicitItArguments.Add(argument);
                }
            }

            if (implicitItArguments.Count == 0)
            {
                implicitItArguments.Free();
                return bind(null, diagnostics);
            }

            var lambdaDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression lambdaResult = bind(implicitItArguments, lambdaDiagnostics);
            bool lambdasUseIt = AllSharperImplicitItLambdasUseIt(lambdaResult, argumentList, implicitItArguments.Count, out BoundLambda? lambdaWithoutIt);
            bool lambdaOk = lambdasUseIt && !lambdaDiagnostics.HasAnyErrors();
            bool lambdaFailedOnlyForIt = !lambdasUseIt && !lambdaDiagnostics.HasAnyErrors();
            implicitItArguments.Free();

            if (lambdaOk && !IsSharperOuterItInScope(argumentList))
            {
                diagnostics.AddRangeAndFree(lambdaDiagnostics);
                return lambdaResult;
            }

            var valueDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression valueResult = bind(null, valueDiagnostics);
            bool valueOk = !valueDiagnostics.HasAnyErrors();

            if (lambdaOk && valueOk)
            {
                // Keep the value, where 'it' is the outer one, so that the outer lambda reports no second error.
                lambdaDiagnostics.Free();
                diagnostics.AddRangeAndFree(valueDiagnostics);
                var firstIt = FindSharperFirstFreeIt(argumentList);
                Debug.Assert(firstIt is not null);
                diagnostics.Add(ErrorCode.ERR_SharperItValueOrFunction, firstIt.Location, calleeName);
                return valueResult.WithHasErrors();
            }

            // When neither binds, report the lambda trial if its lambdas use 'it' (the callee takes a function there,
            // and the lambda has errors), and the value trial otherwise.
            if (lambdaOk || (!valueOk && lambdasUseIt))
            {
                valueDiagnostics.Free();
                diagnostics.AddRangeAndFree(lambdaDiagnostics);
                return lambdaResult;
            }

            lambdaDiagnostics.Free();
            if (!valueOk && lambdaFailedOnlyForIt && lambdaWithoutIt is not null)
            {
                ReportSharperItClaimedByInnerCall(lambdaWithoutIt, diagnostics);
            }

            diagnostics.AddRangeAndFree(valueDiagnostics);
            return valueResult;
        }

        /// <summary>
        /// The value trial failed, and the lambda trial bound but its lambda used no <c>it</c> of its own: every <c>it</c>
        /// in it went to an inner call, as in <c>xs.Select(F(xs.Count(it &gt; 1)))</c>. Name the inner call at the outer
        /// argument, next to the C# errors of the value trial. When the lambda trial had errors of its own (such as an
        /// inner call that reported this error already), they are the mistake to fix, and nothing is added.
        /// </summary>
        private static void ReportSharperItClaimedByInnerCall(BoundLambda lambda, BindingDiagnosticBag diagnostics)
        {
            var (_, innerLambda) = SharperItUseFinder.Find(lambda.Body, lambda.Symbol.Parameters[0]);
            if (innerLambda?.Syntax.Parent is ArgumentSyntax { Parent: BaseArgumentListSyntax { Parent: { } innerCall } })
            {
                // The names of CS20021.
                string? name = innerCall switch
                {
                    InvocationExpressionSyntax invocation => getCallName(invocation.Expression),
                    ObjectCreationExpressionSyntax creation => getCallName(creation.Type),
                    ImplicitObjectCreationExpressionSyntax => "new",
                    ElementAccessExpressionSyntax or ElementBindingExpressionSyntax or ImplicitElementAccessSyntax => "this[]",
                    ConstructorInitializerSyntax initializer => initializer.ThisOrBaseKeyword.ValueText,
                    PrimaryConstructorBaseTypeSyntax => "base",
                    _ => null,
                };

                if (name is not null)
                {
                    diagnostics.Add(ErrorCode.ERR_SharperItClaimedByInnerCall, lambda.Syntax.Location, name);
                }
            }

            static string getCallName(ExpressionSyntax expression)
            {
                return expression switch
                {
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
                    QualifiedNameSyntax qualifiedName => qualifiedName.Right.Identifier.ValueText,
                    SimpleNameSyntax name => name.Identifier.ValueText,
                    _ => expression.ToString(),
                };
            }
        }

        /// <summary>
        /// Finds a variable declared in the arguments, outside explicit lambdas and pipe stages (which get new locals each
        /// time they bind), preferring one inside an argument with a free <c>it</c>.
        /// </summary>
        private (SingleVariableDesignationSyntax designation, ArgumentSyntax? argument)? FindSharperDeclaration(BaseArgumentListSyntax argumentList)
        {
            (SingleVariableDesignationSyntax designation, ArgumentSyntax? argument)? result = null;
            foreach (var argument in argumentList.Arguments)
            {
                foreach (var node in argument.DescendantNodes(descendIntoChildren: static n => n is not AnonymousFunctionExpressionSyntax and not QueryExpressionSyntax && !(n.Parent is PipeExpressionSyntax pipe && pipe.Stage == n)))
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
                => SharperParameterTakesFunction(method.Parameters, position, name);
        }

        /// <summary>
        /// Whether one of the constructors or indexers may take the argument as a function with one parameter (see
        /// <see cref="SharperCandidateTakesFunction"/>).
        /// </summary>
        private static bool SharperCandidatesTakeFunction<TSymbol>(ImmutableArray<TSymbol> candidates, BaseArgumentListSyntax argumentList, ArgumentSyntax argument)
            where TSymbol : Symbol
        {
            int position = argumentList.Arguments.IndexOf(argument);
            string? name = argument.NameColon?.Name.Identifier.ValueText;
            foreach (var candidate in candidates)
            {
                if (SharperParameterTakesFunction(candidate.GetParameters(), position, name))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SharperParameterTakesFunction(ImmutableArray<ParameterSymbol> parameters, int position, string? name)
        {
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

        private IdentifierNameSyntax? FindSharperFirstFreeIt(BaseArgumentListSyntax argumentList)
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

        private bool IsSharperOuterItInScope(BaseArgumentListSyntax argumentList)
        {
            return FindSharperFirstFreeIt(argumentList) is { } it &&
                IsSharperImplicitItSymbol(LookupSharperIt(it));
        }

        /// <summary>
        /// Whether every argument bound as an implicit <c>it</c> lambda became a lambda that uses its <c>it</c>. A
        /// lambda whose <c>it</c> is used only by inner implicit lambdas is a value: <c>Make(ys.First(it.X))</c>; the
        /// first such lambda is returned for the error if the value does not bind either.
        /// </summary>
        private static bool AllSharperImplicitItLambdasUseIt(BoundExpression result, BaseArgumentListSyntax argumentList, int expected, out BoundLambda? lambdaWithoutIt)
        {
            var finder = new SharperImplicitItLambdaFinder(argumentList, expected);
            finder.Visit(result);
            lambdaWithoutIt = finder.FirstWithoutIt;
            return finder.AllUseIt && finder.Found == finder.Expected;
        }

        private sealed class SharperImplicitItLambdaFinder : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly BaseArgumentListSyntax _argumentList;

            public int Expected { get; }
            public int Found { get; private set; }
            public bool AllUseIt { get; private set; } = true;
            public BoundLambda? FirstWithoutIt { get; private set; }

            public SharperImplicitItLambdaFinder(BaseArgumentListSyntax argumentList, int expected)
            {
                _argumentList = argumentList;
                Expected = expected;
            }

            public override BoundNode? VisitLambda(BoundLambda node)
            {
                if (node.Symbol is LambdaSymbol { IsSharperImplicitIt: true } && node.Syntax.Parent is ArgumentSyntax argument && argument.Parent == _argumentList)
                {
                    Found++;
                    if (!SharperItUseFinder.Uses(node.Body, node.Symbol.Parameters[0]))
                    {
                        AllUseIt = false;
                        FirstWithoutIt ??= node;
                    }
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
        /// Finds whether a bound body reads an <c>it</c> (an implicit lambda's parameter or a pipe's value), and the first
        /// implicit <c>it</c> lambda in it, through explicit lambdas too. An inner pipe stage's <c>it</c> is not another
        /// meaning to report: a stage owns every <c>it</c> it contains, which the syntax shows without any signature.
        /// </summary>
        private sealed class SharperItUseFinder : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly Symbol _it;
            private bool _uses;
            private BoundLambda? _firstImplicitLambda;

            private SharperItUseFinder(Symbol it)
            {
                _it = it;
            }

            public static bool Uses(BoundNode body, Symbol it)
            {
                var finder = new SharperItUseFinder(it);
                finder.Visit(body);
                return finder._uses;
            }

            public static (bool uses, BoundLambda? firstImplicitLambda) Find(BoundNode body, Symbol it)
            {
                var finder = new SharperItUseFinder(it);
                finder.Visit(body);
                return (finder._uses, finder._firstImplicitLambda);
            }

            public override BoundNode? VisitParameter(BoundParameter node)
            {
                if (Symbol.Equals(node.ParameterSymbol, _it, TypeCompareKind.ConsiderEverything))
                {
                    _uses = true;
                }

                return null;
            }

            public override BoundNode? VisitLocal(BoundLocal node)
            {
                if (Symbol.Equals(node.LocalSymbol, _it, TypeCompareKind.ConsiderEverything))
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
                    // lambda depends on types; decide whether declarations can be allowed (#125).
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
