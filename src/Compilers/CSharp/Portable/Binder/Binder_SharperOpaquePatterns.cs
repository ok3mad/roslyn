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

            return new BoundOpaquePattern(node, relation, value, isConstantPattern, predicateArgument: null, inputType: inputType, narrowedType: type, hasErrors);
        }

        /// <summary>
        /// Whether <paramref name="syntax"/> is the method of a predicate pattern, which is also the syntax of its call:
        /// the expression of a constant pattern or a <c>case</c> label, or the right side of <c>is</c>, in parentheses or not.
        /// </summary>
        internal static bool IsSharperPredicatePatternName(SyntaxNode syntax)
        {
            if (syntax is not (SimpleNameSyntax or MemberAccessExpressionSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax))
            {
                return false;
            }

            SyntaxNode node = syntax;
            while (node.Parent is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
            {
                node = node.Parent;
            }

            return node.Parent switch
            {
                ConstantPatternSyntax or CaseSwitchLabelSyntax => true,
                BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } isExpression => isExpression.Right == node,
                _ => false,
            } && syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper);
        }

        /// <summary>
        /// Sharper: the predicate form of patterns on pure functions (Feature 13, slice 2, #35). A constant pattern that
        /// binds to a method group, <c>x is IsEven</c> or <c>x is Checks.IsEven</c>, is an error in C#: a method group is
        /// neither a constant nor a type. In a Sharper file it is the call <c>IsEven(x)</c>, bound as a call with the input
        /// as its only argument would be, if that call is applicable and finds a method with one parameter that returns
        /// <c>bool</c>; the pattern matches when the call returns true. Returns null where C#'s error stays: no applicable
        /// method, another shape, union matching or a <c>dynamic</c> input. The callee must be pure (CS20069).
        /// </summary>
        private BoundOpaquePattern? TryBindSharperPredicatePattern(
            SyntaxNode node,
            ExpressionSyntax expression,
            BoundMethodGroup methodGroup,
            NamedTypeSymbol? unionType,
            TypeSymbol? inputType,
            bool hasErrors,
            BindingDiagnosticBag diagnostics)
        {
            if (unionType is not null ||
                inputType is null ||
                !expression.IsFeatureEnabled(MessageID.IDS_FeatureSharper) ||
                inputType.IsErrorType() ||
                inputType.IsDynamic() ||
                inputType.IsVoidType() ||
                inputType.StrippedType() is NamedTypeSymbol { IsUnionType: true })
            {
                return null;
            }

            // The input is the argument, never a receiver: 'x is IsEven' is 'IsEven(x)', not 'x.IsEven()'. The call's
            // syntax is the method's name, so the semantic model finds the method there; the argument is compiler generated.
            var argument = new BoundValuePlaceholder(expression, inputType) { WasCompilerGenerated = true };
            var analyzedArguments = AnalyzedArguments.GetInstance();
            analyzedArguments.Arguments.Add(argument);
            var trial = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression bound = BindMethodGroupInvocation(
                expression, expression, GetName(expression), methodGroup, analyzedArguments, trial, queryClause: null,
                ignoreNormalFormIfHasValidParamsParameter: false, out bool anyApplicableCandidates,
                disallowExpandedNonArrayParams: false, acceptOnlyMethods: true);
            analyzedArguments.Free();

            if (bound is not BoundCall call ||
                (call.HasAnyErrors ? !anyApplicableCandidates : !isPredicate(call)))
            {
                // Nothing applies, or the method found is not a predicate: C#'s error stays.
                trial.Free();
                return null;
            }

            // An applicable call with an error, such as an ambiguity, reports it.
            hasErrors |= call.HasAnyErrors;
            diagnostics.AddRangeAndFree(trial);

            // A refinement rejects every opaque pattern as not closed (CS20058), so its call is not checked there.
            if (!hasErrors &&
                node.FirstAncestorOrSelf<RefinementClauseSyntax>() is null &&
                !SharperImmutability.CheckPredicatePatternCall(Compilation, ContainingMemberOrLambda as MethodSymbol, call, diagnostics))
            {
                hasErrors = true;
            }

            return new BoundOpaquePattern(
                node, BinaryOperatorKind.BoolEqual, call, isConstantPattern: true, predicateArgument: argument,
                inputType: inputType, narrowedType: inputType, hasErrors);

            // 'bool M(T)': the input is the only argument, with no 'params' expansion and no default values. An extension
            // method called on a receiver ('x is checker.IsValid') takes the receiver as its first parameter.
            static bool isPredicate(BoundCall call)
            {
                MethodSymbol method = call.Method;
                if (method.ReturnType.SpecialType != SpecialType.System_Boolean || method.RefKind != RefKind.None || call.Expanded)
                {
                    return false;
                }

                if (!call.DefaultArguments.IsNull)
                {
                    for (int i = 0; i < call.Arguments.Length; i++)
                    {
                        if (call.DefaultArguments[i])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }
    }
}
