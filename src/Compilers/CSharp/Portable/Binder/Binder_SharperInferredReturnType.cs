// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper's inferred return type, <c>infer F(...) =&gt; e</c>: the return type is the type that <c>var r = e;</c>
    /// would give <c>r</c>, or <c>void</c> when <c>e</c> is a call that returns nothing. The lazy computation, its
    /// thread-safety and the use of other inferred methods are in <see cref="SharperInferredReturnType"/>.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Binds the return type syntax of a method or local function, taking into account that it may be the Sharper
        /// <c>infer</c> keyword. Like <c>var</c>, <c>infer</c> is a keyword only where no type named <c>infer</c> is in
        /// scope, and with Sharper off it is an ordinary type name (CS0246 when there is none).
        /// </summary>
        /// <returns>The bound type, or default when the syntax is the <c>infer</c> keyword.</returns>
        internal TypeWithAnnotations BindTypeOrSharperInferKeyword(TypeSyntax syntax, BindingDiagnosticBag diagnostics, out bool isInfer)
        {
            if (syntax.IsInfer)
            {
                var symbol = BindTypeOrAliasOrKeyword((IdentifierNameSyntax)syntax, diagnostics, out isInfer);
                return isInfer ? default : UnwrapAlias(symbol, diagnostics, syntax).TypeWithAnnotations;
            }

            isInfer = false;
            return BindType(syntax, diagnostics);
        }

        /// <summary>
        /// Binds a type in a position where Sharper does not support <c>infer</c> yet, but will: the <c>infer</c>
        /// keyword reports that, rather than a missing type named <c>infer</c>.
        /// </summary>
        internal TypeWithAnnotations BindTypeReportingSharperInferNotSupportedYet(TypeSyntax syntax, MessageID position, BindingDiagnosticBag diagnostics)
        {
            TypeWithAnnotations type = BindTypeOrSharperInferKeyword(syntax, diagnostics, out bool isInfer);
            if (isInfer)
            {
                diagnostics.Add(ErrorCode.ERR_SharperInferNotSupportedYet, syntax.Location, position.Localize());
                type = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }

            return type;
        }

        /// <summary>
        /// Infers the return type of <paramref name="method"/> from its expression body. This binder is the binder of
        /// the body, as <see cref="BindExpressionBodyAsBlock"/> gets it. The body is bound again, and the result thrown
        /// away, when the method itself is compiled; that binding reports the errors of the body, so they are dropped
        /// here.
        /// </summary>
        internal TypeWithAnnotations BindSharperInferredReturnType(
            MethodSymbol method,
            ArrowExpressionClauseSyntax body,
            TypeSyntax inferSyntax,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(inferSyntax.IsInfer);

            var bodyDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            ExpressionSyntax expressionSyntax = body.Expression.CheckAndUnwrapRefExpression(bodyDiagnostics, out RefKind refKind);
            BoundExpression value = BindValue(expressionSyntax, bodyDiagnostics, BindValueKind.RValue);
            bool bodyHasErrors = bodyDiagnostics.HasAnyErrors() || refKind != RefKind.None;

            // A lambda or method group gets its natural function type, as for 'var'.
            var naturalTypeDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression expression = value.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup ?
                BindToInferredDelegateType(value, naturalTypeDiagnostics) :
                BindToNaturalType(value, naturalTypeDiagnostics);

            TypeWithAnnotations result;
            if (bodyHasErrors)
            {
                // The body reports its own errors when it is compiled.
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else if (naturalTypeDiagnostics.HasAnyErrors() || !expression.HasExpressionType())
            {
                // Certain expressions (null, default, a method group or lambda without a natural type, a throw
                // expression) have no type of their own, so there is nothing to infer from.
                diagnostics.Add(ErrorCode.ERR_SharperInferNoType, expressionSyntax.Location, method, value.Display);
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else
            {
                diagnostics.AddDependencies(bodyDiagnostics);
                diagnostics.AddDependencies(naturalTypeDiagnostics);
                result = InferSharperReturnTypeNullability(method, body, expressionSyntax, expression, inferSyntax);
            }

            bodyDiagnostics.Free();
            naturalTypeDiagnostics.Free();
            return result;
        }

        /// <summary>
        /// The top-level nullable annotation of an inferred return type comes from the flow state of the body: a body
        /// that may be null gives an annotated type (<c>string?</c>), as for the type of a lambda. Where nullable
        /// annotations are disabled at <c>infer</c>, the type is oblivious, as a written type would be.
        /// </summary>
        private TypeWithAnnotations InferSharperReturnTypeNullability(
            MethodSymbol method,
            ArrowExpressionClauseSyntax body,
            ExpressionSyntax expressionSyntax,
            BoundExpression expression,
            TypeSyntax inferSyntax)
        {
            Debug.Assert(expression.Type is not null);

            if (expression.Type.IsVoidType() || !AreNullableAnnotationsEnabled(((IdentifierNameSyntax)inferSyntax).Identifier))
            {
                return TypeWithAnnotations.Create(expression.Type, NullableAnnotation.Oblivious);
            }

            var block = new BoundBlock(
                body,
                GetDeclaredLocalsForScope(body),
                [new BoundReturnStatement(expressionSyntax, RefKind.None, expression, @checked: CheckOverflowAtRuntime) { WasCompilerGenerated = true }])
            { WasCompilerGenerated = true };

            // PROTOTYPE(sharper): a local function's body is analyzed on its own, so a captured variable has the state of
            // its declared type (maybe null for a 'var' local), not its state where the local function is used.
            TypeWithAnnotations? analyzed = NullableWalker.AnalyzeSharperInferredReturnType(Compilation, method, block, this);
            return analyzed ?? TypeWithAnnotations.Create(expression.Type);
        }
    }
}
