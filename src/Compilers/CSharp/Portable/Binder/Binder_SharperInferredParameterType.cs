// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper's inferred parameter type, <c>void F(infer retries = 3)</c>: the parameter type is the type that
    /// <c>var x = e;</c> would give <c>x</c>, where <c>e</c> is the default value. The caching and the cycles are in
    /// <see cref="SharperInferredParameterType"/>.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Infers the type of <paramref name="parameter"/> from its default value. This binder is the binder of the
        /// default value, as <see cref="SourceComplexParameterSymbolBase"/> gets it. The default value is bound again,
        /// against the inferred type, when the parameter's default value is computed; that binding reports the errors of
        /// the default value (such as CS1736 for a value that is not a constant), so they are dropped here.
        /// </summary>
        internal TypeWithAnnotations BindSharperInferredParameterType(ParameterSyntax parameter, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(parameter.Type is { IsInfer: true } && parameter.Default is not null);

            ExpressionSyntax valueSyntax = parameter.Default.Value;
            var valueDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression value = BindValue(valueSyntax, valueDiagnostics, BindValueKind.RValue);

            // A lambda or method group gets its natural function type, as for 'var'.
            var naturalTypeDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression expression = value.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup ?
                BindToInferredDelegateType(value, naturalTypeDiagnostics) :
                BindToNaturalType(value, naturalTypeDiagnostics);

            TypeWithAnnotations result;
            if (valueDiagnostics.HasAnyErrors())
            {
                // The default value reports its own errors when it is bound against the parameter type.
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else if (naturalTypeDiagnostics.HasAnyErrors() || !expression.HasExpressionType() || expression.Type.IsVoidType())
            {
                // null, default, new() and collection expressions have no type of their own, so there is nothing to
                // infer from.
                diagnostics.Add(ErrorCode.ERR_SharperInferParameterNoType, valueSyntax.Location, parameter.Identifier.ValueText, value.Display);
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else
            {
                diagnostics.AddDependencies(valueDiagnostics);
                diagnostics.AddDependencies(naturalTypeDiagnostics);
                result = TypeWithAnnotations.Create(expression.Type, GetSharperInferredParameterAnnotation(parameter, expression));
            }

            valueDiagnostics.Free();
            naturalTypeDiagnostics.Free();
            return result;
        }

        /// <summary>
        /// The top-level nullable annotation of an inferred parameter type: annotated when the default value is null
        /// (<c>infer s = default(string)</c> is a <c>string?</c>), not annotated otherwise. A default value is a
        /// constant, so its null state needs no flow analysis. Where nullable annotations are disabled at
        /// <c>infer</c>, the type is oblivious, as a written type would be.
        /// </summary>
        private NullableAnnotation GetSharperInferredParameterAnnotation(ParameterSyntax parameter, BoundExpression value)
        {
            Debug.Assert(value.Type is not null);

            if (!AreNullableAnnotationsEnabled(((IdentifierNameSyntax)parameter.Type!).Identifier))
            {
                return NullableAnnotation.Oblivious;
            }

            bool isNull = value.ConstantValueOpt is { IsNull: true } || value is BoundDefaultExpression;
            return isNull && !value.Type.IsValueType ? NullableAnnotation.Annotated : NullableAnnotation.NotAnnotated;
        }
    }
}
