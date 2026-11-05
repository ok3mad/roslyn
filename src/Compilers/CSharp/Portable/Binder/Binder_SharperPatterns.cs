// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        /// <summary>
        /// Whether a pattern operand that is not a constant is a Sharper opaque pattern (#34) rather than C#'s CS9135 or
        /// CS0150. Only in Sharper files, and only where the comparison is one a constant of the input's type would make:
        /// the operand converts to the input type (or its underlying type for a nullable input), the type has a built-in
        /// comparison, and the input is not a type parameter and not matched as a union. Equality excludes
        /// <c>float</c> and <c>double</c>, whose constant patterns match <c>NaN</c> with <c>NaN</c>; relational patterns
        /// exclude <c>string</c> and <c>bool</c>, as C# does.
        /// </summary>
        private static bool IsSharperOpaquePatternOperand(
            BoundExpression convertedExpression,
            NamedTypeSymbol? unionType,
            TypeSymbol inputType,
            ExpressionSyntax patternExpression,
            bool isRelational)
        {
            if (!patternExpression.IsFeatureEnabled(MessageID.IDS_FeatureSharper) ||
                unionType is not null ||
                inputType.ContainsTypeParameter() ||
                convertedExpression.Type is not { } type ||
                !type.Equals(inputType.StrippedType(), TypeCompareKind.AllIgnoreOptions))
            {
                return false;
            }

            return SharperOpaqueOperatorType(type, isRelational) != BinaryOperatorKind.Error;
        }

        private static BinaryOperatorKind SharperOpaqueOperatorType(TypeSymbol type, bool isRelational)
        {
            BinaryOperatorKind operatorType = RelationalOperatorType(type.EnumUnderlyingTypeOrSelf());
            return (operatorType, isRelational) switch
            {
                (BinaryOperatorKind.String or BinaryOperatorKind.Bool, true) => BinaryOperatorKind.Error,
                (BinaryOperatorKind.Float or BinaryOperatorKind.Double, false) => BinaryOperatorKind.Error,
                _ => operatorType,
            };
        }

        /// <summary>
        /// The pattern for an operand that <see cref="IsSharperOpaquePatternOperand"/> accepted: <paramref name="operation"/>
        /// is the operator without its operand type (<see cref="BinaryOperatorKind.Equal"/> for a constant pattern).
        /// </summary>
        private static BoundSharperOpaquePattern MakeSharperOpaquePattern(
            SyntaxNode node,
            BinaryOperatorKind operation,
            BoundExpression value,
            TypeSymbol inputType,
            bool hasErrors)
        {
            TypeSymbol type = value.Type!;
            BinaryOperatorKind operatorType = SharperOpaqueOperatorType(type, isRelational: operation != BinaryOperatorKind.Equal);
            Debug.Assert(hasErrors || operatorType != BinaryOperatorKind.Error);
            return new BoundSharperOpaquePattern(node, operation | operatorType, value, inputType, narrowedType: type, hasErrors || operatorType == BinaryOperatorKind.Error);
        }
    }
}
