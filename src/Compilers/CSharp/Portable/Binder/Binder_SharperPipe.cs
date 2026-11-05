// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper's pipe without lifting (#25): <c>x |&gt; stage</c>. The left side is evaluated once, before the stage,
    /// into a read-only local. A stage with a free <c>it</c> is an expression whose <c>it</c> is that local:
    /// <c>x |&gt; F(it, y)</c> is <c>F(x, y)</c>. Any other stage is a function applied to it: <c>x |&gt; F</c> is
    /// <c>F(x)</c>, with the overload resolution of that call. A stage is its own scope and the outermost slot for
    /// <c>it</c>; an inner implicit lambda claims an <c>it</c> as it would anywhere else (innermost wins).
    /// </summary>
    internal partial class Binder
    {
        private BoundExpression BindSharperPipe(PipeExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // The parser makes a pipe only with Sharper on.
            bool hasErrors = false;
            BoundExpression left = BindRValueWithoutTargetType(node.Left, diagnostics);
            TypeSymbol? type = left.Type;
            if (type is null || type.IsVoidType())
            {
                if (!left.HasAnyErrors)
                {
                    Error(diagnostics, ErrorCode.ERR_SharperPipeNoValue, node.Left, left.Display);
                }

                type = CreateErrorType();
                hasErrors = true;
            }

            // Like an implicit lambda's 'it', the stage's 'it' is one that C# does not find, or finds only as the 'it' of an
            // outer implicit lambda or stage.
            IdentifierNameSyntax? firstIt = FindSharperFreeIt(node.Stage);

            // A lambda stage names the value (#186): 'x |> (y => y + 1)' is 'x |> it + 1'. A lambda that uses the stage's
            // 'it' is the stage's value instead, target-typed as any stage with 'it' (#187): 'x |> (y => y + it)'.
            if (GetSharperPipeLambdaStage(node.Stage) is var (parameter, body) &&
                (firstIt is null || !SharperPipeLambdaStageUsesIt(parameter, body, type, firstIt)))
            {
                return BindSharperPipeLambdaStage(node, parameter, body, left, type, hasErrors, diagnostics);
            }

            var local = new SharperPipeItLocalSymbol(ContainingMemberOrLambda!, TypeWithAnnotations.Create(type), (SyntaxNode?)firstIt ?? node.Stage);
            var stageScopeBinder = new SharperPipeStageBinder(this, node.Stage, firstIt is null ? [] : [local]);
            Binder stageBinder = stageScopeBinder.GetBinder(node.Stage)!;

            if (firstIt is null)
            {
                BoundExpression call = stageBinder.BindSharperPipeBareStage(node, local, diagnostics);
                return new BoundSharperPipe(
                    node, isBareStage: true, naturalTypeOpt: call.Type, wasTargetTyped: false, local, left, stageBinder.GetDeclaredLocalsForScope(node.Stage), call, call.Type, hasErrors);
            }

            // A stage with 'it' is target-typed, as a switch expression's arms are (#187): the pipe stays unconverted, with
            // the stage's natural type if it has one, until its target type is known (ConvertSharperPipe).
            BoundExpression stage = stageBinder.BindValue(node.Stage, diagnostics, BindValueKind.RValue);
            return new BoundUnconvertedSharperPipe(node, local, left, stageBinder.GetDeclaredLocalsForScope(node.Stage), stage, stage.Type, hasErrors);
        }

        /// <summary>
        /// The parameter and body of a stage that is a lambda that names the value (#186): one parameter, an expression
        /// body, and no <c>async</c>, <c>static</c>, attributes, explicit return type, parameter modifiers or default value.
        /// Parentheses around the lambda do not count, since a lambda stage needs them: <c>x |&gt; (y =&gt; y + 1)</c>.
        /// </summary>
        internal static (ParameterSyntax Parameter, ExpressionSyntax Body)? GetSharperPipeLambdaStage(ExpressionSyntax stage)
        {
            while (stage is ParenthesizedExpressionSyntax parenthesized)
            {
                stage = parenthesized.Expression;
            }

            (ParameterSyntax Parameter, ExpressionSyntax Body)? result = stage switch
            {
                SimpleLambdaExpressionSyntax { ExpressionBody: { } body, Modifiers.Count: 0, AttributeLists.Count: 0 } lambda
                    => (lambda.Parameter, body),
                ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } body, Modifiers.Count: 0, AttributeLists.Count: 0, ReturnType: null, ParameterList.Parameters: [var parameter] }
                    => (parameter, body),
                _ => null,
            };

            return result is { Parameter: { Modifiers.Count: 0, AttributeLists.Count: 0, Default: null, Type: null or { IsInfer: false } } } ? result : null;
        }

        /// <summary>
        /// The parameter of a lambda stage that names the value, given the parameter's syntax, or null. Whether the lambda
        /// is a lambda stage or the value of a stage with <c>it</c> is decided when the pipe binds.
        /// </summary>
        internal static PipeExpressionSyntax? GetSharperPipeOfLambdaStageParameter(ParameterSyntax parameter)
        {
            SyntaxNode? node = parameter.Parent is ParameterListSyntax list ? list.Parent : parameter.Parent;
            if (node is not LambdaExpressionSyntax)
            {
                return null;
            }

            while (node.Parent is ParenthesizedExpressionSyntax)
            {
                node = node.Parent;
            }

            return node.Parent is PipeExpressionSyntax pipe && pipe.Stage == node && GetSharperPipeLambdaStage(pipe.Stage)?.Parameter == parameter
                ? pipe
                : null;
        }

        /// <summary>
        /// Whether a lambda stage with a free <c>it</c> uses the stage's <c>it</c> (<c>x |&gt; (y =&gt; y + it)</c>), and is
        /// then the value of a stage with <c>it</c>, rather than leaving it to an inner implicit lambda
        /// (<c>x |&gt; (v =&gt; xs.Where(it &gt; v))</c>). Only binding tells, as for any stage: the body is bound once on
        /// trial, with both the named value and the stage's <c>it</c> in scope.
        /// </summary>
        private bool SharperPipeLambdaStageUsesIt(ParameterSyntax parameter, ExpressionSyntax body, TypeSymbol type, IdentifierNameSyntax firstIt)
        {
            var trialDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: false, withDependencies: false);
            var it = new SharperPipeItLocalSymbol(ContainingMemberOrLambda!, TypeWithAnnotations.Create(type), firstIt);
            TypeWithAnnotations parameterType = BindSharperPipeLambdaStageParameterType(parameter, type, trialDiagnostics, out bool isExplicitlyTyped);
            var named = new SharperPipeItLocalSymbol(ContainingMemberOrLambda!, parameterType, parameter, isExplicitlyTyped);
            ImmutableArray<LocalSymbol> locals = parameter.Identifier.ValueText == "_" ? [it] : [named, it];
            Binder stageBinder = new SharperPipeStageBinder(this, body, locals).GetBinder(body)!;
            BoundExpression stage = stageBinder.BindValue(body, trialDiagnostics, BindValueKind.RValue);

            // A lambda in the body binds only when it converts: give it its natural type, as a stage without a target.
            stage = stage.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup
                ? stageBinder.BindToInferredDelegateType(stage, trialDiagnostics)
                : stageBinder.BindToNaturalType(stage, trialDiagnostics);
            trialDiagnostics.Free();
            return SharperItUseFinder.Uses(stage, it);
        }

        private TypeWithAnnotations BindSharperPipeLambdaStageParameterType(ParameterSyntax parameter, TypeSymbol valueType, BindingDiagnosticBag diagnostics, out bool isExplicitlyTyped)
        {
            isExplicitlyTyped = false;
            if (parameter.Type is not { } typeSyntax)
            {
                return TypeWithAnnotations.Create(valueType);
            }

            if (typeSyntax.IsVal)
            {
                // '(val y) => ...', as for a lambda: implicitly typed unless a type named 'val' is in scope. Lookup only; if
                // 'val' binds to a type, binding the type reports its diagnostics.
                BindTypeOrAliasOrKeyword((IdentifierNameSyntax)typeSyntax, BindingDiagnosticBag.Discarded, out bool isKeyword);
                if (isKeyword)
                {
                    return TypeWithAnnotations.Create(valueType);
                }
            }

            isExplicitlyTyped = true;
            return BindType(typeSyntax, diagnostics);
        }

        /// <summary>
        /// Binds a lambda stage that names the value (#186): <c>x |&gt; (y =&gt; body)</c> is <c>x |&gt; body</c> with
        /// <c>y</c> for <c>it</c>. No delegate is made: the body is a stage, target-typed as any stage with <c>it</c>, and
        /// <c>y</c> is the read-only local that holds the value. An explicit parameter type converts the left side
        /// implicitly, and the parameter <c>_</c> is a discard.
        /// </summary>
        private BoundExpression BindSharperPipeLambdaStage(
            PipeExpressionSyntax node, ParameterSyntax parameter, ExpressionSyntax body, BoundExpression left, TypeSymbol type, bool hasErrors, BindingDiagnosticBag diagnostics)
        {
            TypeWithAnnotations parameterType = BindSharperPipeLambdaStageParameterType(parameter, type, diagnostics, out bool isExplicitlyTyped);
            if (isExplicitlyTyped && !hasErrors)
            {
                // 'x |> ((long y) => y * 2)' converts as 'long y = x;' would (trap 4: report and flag together).
                var conversionDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
                left = GenerateConversionForAssignment(parameterType.Type, left, conversionDiagnostics);
                hasErrors = conversionDiagnostics.HasAnyErrors();
                diagnostics.AddRangeAndFree(conversionDiagnostics);
            }

            var local = new SharperPipeItLocalSymbol(ContainingMemberOrLambda!, parameterType, parameter, isExplicitlyTyped);
            bool isDiscard = parameter.Identifier.ValueText == "_";
            if (!isDiscard && !Compilation.IsFeatureEnabled(MessageID.IDS_FeatureNameShadowingInNestedFunctions))
            {
                // The name follows lambda parameter rules.
                ValidateLambdaParameterNameConflictsInScope(parameter.Identifier.GetLocation(), local.Name, diagnostics);
            }

            Binder stageBinder = new SharperPipeStageBinder(this, body, isDiscard ? [] : [local]).GetBinder(body)!;
            BoundExpression stage = stageBinder.BindValue(body, diagnostics, BindValueKind.RValue);
            return new BoundUnconvertedSharperPipe(node, local, left, stageBinder.GetDeclaredLocalsForScope(body), stage, stage.Type, hasErrors);
        }

        /// <summary>
        /// Converts the stage of a pipe that uses <c>it</c>: with <paramref name="conversionIfTargetTyped"/>, to
        /// <paramref name="destination"/> (the pipe conversion, #187); with no <paramref name="destination"/>, to its natural
        /// type, reporting why it has none; otherwise to the error type <paramref name="destination"/>, after a conversion
        /// error was reported.
        /// </summary>
        private BoundExpression ConvertSharperPipe(BoundUnconvertedSharperPipe source, TypeSymbol? destination, Conversion? conversionIfTargetTyped, BindingDiagnosticBag diagnostics, bool hasErrors = false)
        {
            BoundExpression stage = source.Stage;
            if (conversionIfTargetTyped is { } conversion)
            {
                Debug.Assert(destination is not null);
                stage = CreateConversion(stage.Syntax, stage, conversion.UnderlyingConversions[0], isCast: false, conversionGroupOpt: null, InConversionGroupFlags.Unspecified, destination, diagnostics);
                conversion.MarkUnderlyingConversionsChecked();
            }
            else if (destination is null)
            {
                // 'var r = x |> (it > 0 ? 1 : null)' reports what a 'var' initializer with the stage would, and a lambda
                // stage has its function type, as it would there.
                stage = stage.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup
                    ? BindToInferredDelegateType(stage, diagnostics)
                    : BindToNaturalType(stage, diagnostics);
            }
            else
            {
                Debug.Assert(destination.IsErrorType());
                stage = GenerateConversionForAssignment(destination, stage, diagnostics);
            }

            if (source.Local is not SharperPipeItLocalSymbol { LambdaParameter: not null })
            {
                // A lambda stage names its value, so its 'it', if any, belongs to an inner implicit lambda.
                stage = CheckSharperPipeStageItUses(source.Local, stage, diagnostics);
            }

            return new BoundSharperPipe(
                source.Syntax, isBareStage: false, naturalTypeOpt: source.Type, wasTargetTyped: conversionIfTargetTyped is not null,
                source.Local, source.Left, source.Locals, stage, stage.Type, hasErrors || source.HasErrors).WithSuppression(source.IsSuppressed);
        }

        /// <summary>
        /// The <c>it</c> rules of a stage, checked once the stage is converted: its lambdas are bound only then.
        /// </summary>
        private static BoundExpression CheckSharperPipeStageItUses(LocalSymbol local, BoundExpression stage, BindingDiagnosticBag diagnostics)
        {
            var (usesIt, innerLambda) = SharperItUseFinder.Find(stage, local);
            if (!usesIt && innerLambda is not null && !stage.HasAnyErrors)
            {
                // 'x |> xs.Where(it > 0)': the innermost function argument claims 'it', as anywhere else.
                Error(diagnostics, ErrorCode.ERR_SharperPipeStageItNotUsed, local.GetDeclaratorSyntax(), sharperImplicitLambdaCallName(innerLambda));
                stage = stage.WithHasErrors();
            }
            else if (usesIt && innerLambda is not null)
            {
                // One meaning of 'it' per stage: 'x |> Render(it, ys.Where(it.Active))'. An inner stage is not another
                // meaning: 'x |> F(it, y |> G(it))' shows by its syntax which 'it' is which.
                Error(diagnostics, ErrorCode.ERR_SharperItTwoMeanings, innerLambda.Symbol.Parameters[0].GetFirstLocation());
                stage = stage.WithHasErrors();
            }

            return stage;

            static string sharperImplicitLambdaCallName(BoundLambda lambda)
            {
                return lambda.Syntax.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } }
                    ? invocation.Expression.ToString()
                    : lambda.Syntax.ToString();
            }
        }

        private BoundExpression BindSharperPipeBareStage(PipeExpressionSyntax node, LocalSymbol local, BindingDiagnosticBag diagnostics)
        {
            ExpressionSyntax stageSyntax = node.Stage;
            // Not 'invoked': the stage is not the expression of an invocation, and lookup decides the mode from syntax
            // (SyntaxFacts.IsInvoked, which primary constructor captures rely on), as for a method group conversion.
            BoundExpression function = BindMethodGroup(stageSyntax, invoked: false, indexed: false, diagnostics);
            function = CheckValue(function, BindValueKind.RValueOrMethodGroup, diagnostics);

            if (!function.HasAnyErrors &&
                function.Kind != BoundKind.MethodGroup &&
                function.Type is not { TypeKind: TypeKind.Dynamic or TypeKind.Delegate or TypeKind.FunctionPointer })
            {
                // 'x |> y.Length' instead of 'x |> it.Length'. A lambda that cannot name the value gets a hint (#186).
                object display = stageSyntax.SkipParens() is AnonymousFunctionExpressionSyntax
                    ? MessageID.IDS_SharperPipeLambdaStage.Localize()
                    : function.Display;
                Error(diagnostics, ErrorCode.ERR_SharperPipeStageNotFunction, stageSyntax, display);
                return BadExpression(stageSyntax, LookupResultKind.NotInvocable, function);
            }

            // The call's syntax is the stage, so the semantic model finds the method on it. The argument is compiler
            // generated, so the semantic model does not map the left side to it.
            string? name = function.Kind == BoundKind.MethodGroup ? GetName(stageSyntax) : null;
            var analyzedArguments = AnalyzedArguments.GetInstance();
            analyzedArguments.Arguments.Add(new BoundLocal(node.Left, local, constantValueOpt: null, local.Type) { WasCompilerGenerated = true });
            BoundExpression result = BindInvocationExpression(stageSyntax, stageSyntax, name, function, analyzedArguments, diagnostics, acceptOnlyMethods: false);
            analyzedArguments.Free();
            return result;
        }
    }
}
