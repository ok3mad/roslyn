// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class BoundDagOpaqueTest
    {
        public override BoundDagTest UpdateTestImpl(BoundDagTemp input) => Update(input);
        public new BoundDagOpaqueTest Update(BoundDagTemp input)
        {
            return Update(OperatorKind, Value, PredicateArgument, input);
        }

        /// <summary>
        /// Whether two opaque tests make the same comparison: the same operator and the same operand, written the same
        /// way. Operands are pure, so the same reads give the same value while a match runs. Two predicate tests are the
        /// same when they make the same call with the input as the argument. The input is compared by the caller.
        /// </summary>
        internal bool IsSameComparison(BoundDagOpaqueTest other)
            => OperatorKind == other.OperatorKind &&
                (PredicateArgument is null) == (other.PredicateArgument is null) &&
                IsSameValue(Value, other.Value, PredicateArgument, other.PredicateArgument);

        private static bool IsSameValue(BoundExpression? x, BoundExpression? y, BoundValuePlaceholder? xArgument, BoundValuePlaceholder? yArgument)
        {
            if (x == (object?)y)
            {
                return true;
            }

            if (x is null || y is null || x.Kind != y.Kind || !TypeSymbol.Equals(x.Type, y.Type, TypeCompareKind.AllIgnoreOptions))
            {
                return false;
            }

            if (x.ConstantValueOpt is { } constant)
            {
                return constant.Equals(y.ConstantValueOpt);
            }

            return (x, y) switch
            {
                (BoundLocal a, BoundLocal b) => a.LocalSymbol.Equals(b.LocalSymbol),
                (BoundParameter a, BoundParameter b) => a.ParameterSymbol.Equals(b.ParameterSymbol),
                (BoundThisReference, BoundThisReference) => true,
                (BoundTypeExpression, BoundTypeExpression) => true, // The receiver of a static call; the types are compared above.
                (BoundValuePlaceholder a, BoundValuePlaceholder b) => a == (object?)xArgument && b == (object?)yArgument,
                (BoundFieldAccess a, BoundFieldAccess b) => a.FieldSymbol.Equals(b.FieldSymbol) && IsSameValue(a.ReceiverOpt, b.ReceiverOpt, xArgument, yArgument),
                (BoundPropertyAccess a, BoundPropertyAccess b) => a.PropertySymbol.Equals(b.PropertySymbol) && IsSameValue(a.ReceiverOpt, b.ReceiverOpt, xArgument, yArgument),
                (BoundConversion a, BoundConversion b) => a.Conversion == b.Conversion && a.Checked == b.Checked && a.ExplicitCastInCode == b.ExplicitCastInCode && IsSameValue(a.Operand, b.Operand, xArgument, yArgument),
                (BoundUnaryOperator a, BoundUnaryOperator b) => a.OperatorKind == b.OperatorKind && Equals(a.MethodOpt, b.MethodOpt) && IsSameValue(a.Operand, b.Operand, xArgument, yArgument),
                (BoundBinaryOperator a, BoundBinaryOperator b) => a.OperatorKind == b.OperatorKind && Equals(a.BinaryOperatorMethod, b.BinaryOperatorMethod) && IsSameValue(a.Left, b.Left, xArgument, yArgument) && IsSameValue(a.Right, b.Right, xArgument, yArgument),
                (BoundCall a, BoundCall b) => a.Method.Equals(b.Method) && a.ArgumentRefKindsOpt.IsDefaultOrEmpty && b.ArgumentRefKindsOpt.IsDefaultOrEmpty &&
                    IsSameValue(a.ReceiverOpt, b.ReceiverOpt, xArgument, yArgument) && AreSameValues(a.Arguments, b.Arguments, xArgument, yArgument),
                _ => false,
            };
        }

        private static bool AreSameValues(ImmutableArray<BoundExpression> x, ImmutableArray<BoundExpression> y, BoundValuePlaceholder? xArgument, BoundValuePlaceholder? yArgument)
        {
            if (x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (!IsSameValue(x[i], y[i], xArgument, yArgument))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
