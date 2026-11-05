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
            var local = new SharperPipeItLocalSymbol(ContainingMemberOrLambda!, TypeWithAnnotations.Create(type), (SyntaxNode?)firstIt ?? node.Stage);
            var stageScopeBinder = new SharperPipeStageBinder(this, node.Stage, firstIt is null ? null : local);
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

            stage = CheckSharperPipeStageItUses(source.Local, stage, diagnostics);
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
                // 'x |> y.Length' instead of 'x |> it.Length'.
                Error(diagnostics, ErrorCode.ERR_SharperPipeStageNotFunction, stageSyntax, function.Display);
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
