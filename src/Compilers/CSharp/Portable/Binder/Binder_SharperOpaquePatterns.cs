// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper: opaque patterns, slice 1 of Feature 13 (#34). A relational or constant pattern whose operand is not a
    /// constant, such as <c>&gt; 0 and &lt;= max</c>, is an error in C# (CS9135 or CS0150). In a Sharper file it binds to a
    /// <see cref="BoundOpaquePattern"/> when its operand is pure: the decision DAG tests it with a
    /// <see cref="BoundDagOpaqueTest"/>, whose outcome it knows nothing about except that an identical test has the same one.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Called where C# reports that the operand of a constant or relational pattern is not a constant. Returns true
        /// if the pattern is opaque instead, with <paramref name="convertedExpression"/> the operand as it is compared: of
        /// the input type, or of its own type when the input is <c>object</c>, <c>System.ValueType</c> or a type
        /// parameter (the match then tests for that type first, as it does for a constant).
        /// </summary>
        private bool TryConvertSharperOpaqueOperand(
            ExpressionSyntax patternExpression,
            BinaryOperatorKind opaqueOperator,
            NamedTypeSymbol? unionType,
            TypeSymbol inputType,
            Conversion patternExpressionConversion,
            ref BoundExpression convertedExpression)
        {
            // Union matching is left for later: it rewrites the pattern before the DAG is built.
            if (unionType is not null || !patternExpression.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return false;
            }

            BoundExpression value = convertedExpression;
            TypeSymbol strippedInputType = inputType.StrippedType();
            if (strippedInputType.Kind == SymbolKind.TypeParameter ||
                strippedInputType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType)
            {
                // Compare with the operand's own type, not with a boxed copy of it.
                if (value is BoundConversion { ConversionKind: ConversionKind.Boxing or ConversionKind.ImplicitReference, ExplicitCastInCode: false } conversion)
                {
                    value = conversion.Operand;
                }

                if (value.Type is not { } operandType || ShouldBlockINumberBaseConversion(patternExpressionConversion, inputType))
                {
                    return false;
                }

                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                ConstantValue match = ExpressionOfTypeMatchesPatternType(Conversions, inputType, operandType, ref useSiteInfo, out _, operandConstantValue: null);
                if (match == ConstantValue.False || match == ConstantValue.Bad)
                {
                    return false;
                }
            }
            else if (value.Type is null || !value.Type.Equals(strippedInputType, TypeCompareKind.AllIgnoreOptions))
            {
                // A ReadOnlySpan<char> input with a string operand, for one.
                return false;
            }

            BinaryOperatorKind operandKind = RelationalOperatorType(value.Type.EnumUnderlyingTypeOrSelf());
            bool supported = operandKind switch
            {
                BinaryOperatorKind.Error => false,
                BinaryOperatorKind.String or BinaryOperatorKind.Bool => opaqueOperator == BinaryOperatorKind.Equal,
                _ => true,
            };

            if (!supported)
            {
                return false;
            }

            convertedExpression = value;
            return true;
        }

        /// <summary>
        /// Makes the opaque pattern for an operand that <see cref="TryConvertSharperOpaqueOperand"/> accepted, and checks
        /// that the operand is pure (CS20062) by the rules of a <c>[Pure]</c> body.
        /// </summary>
        private BoundOpaquePattern MakeSharperOpaquePattern(
            SyntaxNode node,
            BinaryOperatorKind relation,
            bool isConstantPattern,
            BoundExpression value,
            TypeSymbol inputType,
            bool hasErrors,
            BindingDiagnosticBag diagnostics)
        {
            TypeSymbol? type = value.Type;
            Debug.Assert(type is not null);
            Debug.Assert(relation.OperandTypes() == 0);
            relation |= RelationalOperatorType(type.EnumUnderlyingTypeOrSelf());

            // A refinement rejects every opaque pattern as not closed (CS20058), so its operand is not checked there.
            if (!hasErrors && !value.HasErrors &&
                node.FirstAncestorOrSelf<RefinementClauseSyntax>() is null &&
                !SharperImmutability.CheckOpaquePatternOperand(Compilation, ContainingMemberOrLambda as MethodSymbol, value, diagnostics))
            {
                hasErrors = true;
            }

            return new BoundOpaquePattern(node, relation, value, isConstantPattern, inputType: inputType, narrowedType: type, hasErrors);
        }
    }
}
