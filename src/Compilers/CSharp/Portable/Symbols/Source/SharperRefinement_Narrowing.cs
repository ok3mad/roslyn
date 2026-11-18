// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Refined types, slice 2 (Feature 11 of the implementation plan): refinements computed from value sets, the
    /// refinement of <c>+</c>, <c>-</c> and unary <c>-</c> over refined integers (interval arithmetic), and the values a
    /// switch expression arm narrows its input to (<see cref="SharperSwitchNarrowing"/>).
    /// </summary>
    internal sealed partial class SharperRefinement
    {
        /// <summary>
        /// The refinement whose values are <paramref name="values"/> of <paramref name="type"/>, and null if
        /// <paramref name="matchesNull"/>, in the normal form of inferred refinements; null if the set allows every value,
        /// is empty, or cannot be written as a pattern (<c>nint</c>, <c>nuint</c>).
        /// </summary>
        internal static SharperRefinement? FromValues(IConstantValueSet values, bool matchesNull, TypeSymbol type, SyntaxNode syntax, bool isInferred)
        {
            if (type.IsNullableType() || ValueSetFactory.ForType(type) is not { } factory)
            {
                return null;
            }

            matchesNull &= type.CanContainNull();
            var ranges = ArrayBuilder<(ConstantValue? Low, ConstantValue? High)>.GetInstance();
            var alternatives = ArrayBuilder<BoundPattern>.GetInstance();
            SharperRefinement? result = null;
            if (ValueSetFactory.TryGetSharperRanges(values, ranges, out bool excluded))
            {
                if (excluded)
                {
                    // Every value but a few: 'not (...)', which matches null unless null is excluded too.
                    BoundPattern? negated = matchesNull ? null : constant(ConstantValue.Null, type, syntax);
                    foreach (var (low, _) in ranges)
                    {
                        or(ref negated, constant(low!, type, syntax), type, syntax);
                    }

                    alternatives.Add(negated is null
                        ? new BoundDiscardPattern(syntax, inputType: type, narrowedType: type)
                        : new BoundNegatedPattern(syntax, negated, inputType: type, narrowedType: type));
                }
                else
                {
                    if (matchesNull)
                    {
                        alternatives.Add(constant(ConstantValue.Null, type, syntax));
                    }

                    foreach (var (low, high) in ranges)
                    {
                        if (low is not null && high is not null && TryGetIntegralValue(low, out decimal first) && TryGetIntegralValue(high, out decimal last) &&
                            last - first < SmallRange)
                        {
                            // A few values read better as constants: '0 or 1', not '>= 0 and <= 1'.
                            for (decimal value = first; value <= last; value++)
                            {
                                alternatives.Add(constant(IntegralConstant(value, low.Discriminator), type, syntax));
                            }

                            continue;
                        }

                        alternatives.Add(range(low, high, type, syntax));
                    }
                }

                if (alternatives is not [BoundDiscardPattern] && alternatives.Count != 0)
                {
                    result = Normalize(alternatives, type, factory, syntax, isInferred);
                }
            }

            ranges.Free();
            alternatives.Free();
            return result;

            static BoundPattern constant(ConstantValue value, TypeSymbol type, SyntaxNode syntax)
                => new BoundConstantPattern(
                    syntax, new BoundLiteral(syntax, value, type) { WasCompilerGenerated = true }, value,
                    UnionMatchingMode.None, inputType: type, narrowedType: type);

            static BoundPattern relational(BinaryOperatorKind relation, ConstantValue value, TypeSymbol type, SyntaxNode syntax)
                => new BoundRelationalPattern(
                    syntax, relation | Binder.RelationalOperatorType(type.EnumUnderlyingTypeOrSelf()),
                    new BoundLiteral(syntax, value, type) { WasCompilerGenerated = true }, value,
                    isUnionMatching: false, inputType: type, narrowedType: type);

            static BoundPattern range(ConstantValue? low, ConstantValue? high, TypeSymbol type, SyntaxNode syntax)
            {
                Debug.Assert(low is not null || high is not null);
                if (low is not null && high is not null && low.Equals(high))
                {
                    return constant(low, type, syntax);
                }

                if (low is null)
                {
                    return relational(BinaryOperatorKind.LessThanOrEqual, high!, type, syntax);
                }

                var from = relational(BinaryOperatorKind.GreaterThanOrEqual, low, type, syntax);
                return high is null
                    ? from
                    : new BoundBinaryPattern(syntax, disjunction: false, from, relational(BinaryOperatorKind.LessThanOrEqual, high, type, syntax), inputType: type, narrowedType: type);
            }

            static void or(ref BoundPattern? pattern, BoundPattern alternative, TypeSymbol type, SyntaxNode syntax)
            {
                pattern = pattern is null
                    ? alternative
                    : new BoundBinaryPattern(syntax, disjunction: true, pattern, alternative, inputType: type, narrowedType: type);
            }
        }

        /// <summary>A range of integral values shorter than this is written as constants.</summary>
        private const int SmallRange = 4;

        private static bool TryGetIntegralValue(ConstantValue value, out decimal result)
        {
            (bool known, result) = value.Discriminator switch
            {
                ConstantValueTypeDiscriminator.SByte => (true, value.SByteValue),
                ConstantValueTypeDiscriminator.Byte => (true, value.ByteValue),
                ConstantValueTypeDiscriminator.Int16 => (true, value.Int16Value),
                ConstantValueTypeDiscriminator.UInt16 => (true, value.UInt16Value),
                ConstantValueTypeDiscriminator.Int32 => (true, value.Int32Value),
                ConstantValueTypeDiscriminator.UInt32 => (true, value.UInt32Value),
                ConstantValueTypeDiscriminator.Int64 => (true, value.Int64Value),
                ConstantValueTypeDiscriminator.UInt64 => (true, value.UInt64Value),
                _ => (false, 0m),
            };
            return known;
        }

        private static ConstantValue IntegralConstant(decimal value, ConstantValueTypeDiscriminator discriminator)
            => discriminator switch
            {
                ConstantValueTypeDiscriminator.SByte => ConstantValue.Create((sbyte)value),
                ConstantValueTypeDiscriminator.Byte => ConstantValue.Create((byte)value),
                ConstantValueTypeDiscriminator.Int16 => ConstantValue.Create((short)value),
                ConstantValueTypeDiscriminator.UInt16 => ConstantValue.Create((ushort)value),
                ConstantValueTypeDiscriminator.Int32 => ConstantValue.Create((int)value),
                ConstantValueTypeDiscriminator.UInt32 => ConstantValue.Create((uint)value),
                ConstantValueTypeDiscriminator.Int64 => ConstantValue.Create((long)value),
                ConstantValueTypeDiscriminator.UInt64 => ConstantValue.Create((ulong)value),
                _ => throw ExceptionUtilities.UnexpectedValue(discriminator),
            };

        #region Arithmetic

        /// <summary>
        /// The refinement of <c>a + b</c>, <c>a - b</c> or <c>-a</c> over <c>int</c>, <c>uint</c>, <c>long</c> or
        /// <c>ulong</c> operands that have refinements, by interval arithmetic. Unchecked arithmetic that can overflow
        /// wraps around, so it gives no refinement; checked arithmetic drops the values that overflow, since they throw.
        /// The result is never inferred (<see cref="IsInferred"/>).
        /// </summary>
        private static SharperRefinement? OfArithmetic(BoundExpression value, TypeSymbol type, Binder? scope, SyntaxNode syntax)
        {
            if (ValueSetFactory.ForType(type) is not { } factory || !TryGetIntegerRange(type, out decimal min, out decimal max))
            {
                return null;
            }

            BoundExpression left;
            BoundExpression? right = null;
            BinaryOperatorKind operation;
            bool isChecked;
            switch (value)
            {
                case BoundBinaryOperator binary when binary.OperatorKind.Operator() is BinaryOperatorKind.Addition or BinaryOperatorKind.Subtraction &&
                                                     binary.OperatorKind.OperandTypes() is BinaryOperatorKind.Int or BinaryOperatorKind.UInt or BinaryOperatorKind.Long or BinaryOperatorKind.ULong &&
                                                     !binary.OperatorKind.IsLifted():
                    (left, right, operation, isChecked) = (binary.Left, binary.Right, binary.OperatorKind.Operator(), binary.OperatorKind.IsChecked());
                    break;
                case BoundUnaryOperator unary when (unary.OperatorKind & ~UnaryOperatorKind.Checked) is UnaryOperatorKind.IntUnaryMinus or UnaryOperatorKind.LongUnaryMinus:
                    (left, operation, isChecked) = (unary.Operand, BinaryOperatorKind.Subtraction, unary.OperatorKind.IsChecked());
                    break;
                default:
                    return null;
            }

            var leftRanges = ArrayBuilder<(decimal Low, decimal High)>.GetInstance();
            var rightRanges = ArrayBuilder<(decimal Low, decimal High)>.GetInstance();
            IConstantValueSet? result = null;
            if (operandRanges(left, type, scope, syntax, min, max, leftRanges) &&
                (right is null ? rightRanges.Count == 0 : operandRanges(right, type, scope, syntax, min, max, rightRanges)) &&
                leftRanges.Count * System.Math.Max(rightRanges.Count, 1) <= MaxInferredAlternatives)
            {
                result = factory.NoValues;
                foreach (var (a, b) in leftRanges)
                {
                    if (right is null)
                    {
                        // -[a, b] is [-b, -a].
                        if (!add(-b, -a, ref result))
                        {
                            break;
                        }

                        continue;
                    }

                    foreach (var (c, d) in rightRanges)
                    {
                        // [a, b] + [c, d] is [a + c, b + d]; [a, b] - [c, d] is [a - d, b - c].
                        bool added = operation == BinaryOperatorKind.Addition ? add(a + c, b + d, ref result) : add(a - d, b - c, ref result);
                        if (!added)
                        {
                            break;
                        }
                    }

                    if (result is null)
                    {
                        break;
                    }
                }
            }

            leftRanges.Free();
            rightRanges.Free();
            return result is null ? null : FromValues(result, matchesNull: false, type, syntax, isInferred: false);

            // Adds [low, high] to the result; false and a null result if unchecked arithmetic can wrap around.
            bool add(decimal low, decimal high, ref IConstantValueSet? result)
            {
                if (low < min || high > max)
                {
                    if (!isChecked)
                    {
                        result = null;
                        return false;
                    }

                    (low, high) = (System.Math.Max(low, min), System.Math.Min(high, max));
                    if (low > high)
                    {
                        return true;
                    }
                }

                var interval = (IConstantValueSet)factory.Related(BinaryOperatorKind.GreaterThanOrEqual, IntegerConstant(low, type))
                    .Intersect(factory.Related(BinaryOperatorKind.LessThanOrEqual, IntegerConstant(high, type)));
                result = (IConstantValueSet)result!.Union(interval);
                return true;
            }

            static bool operandRanges(BoundExpression operand, TypeSymbol type, Binder? scope, SyntaxNode syntax, decimal min, decimal max, ArrayBuilder<(decimal Low, decimal High)> ranges)
            {
                if (OfExpression(operand, syntax, scope) is not { HasErrors: false, Values: { } values } refinement ||
                    operand.Type?.Equals(type, TypeCompareKind.AllIgnoreOptions) != true)
                {
                    return false;
                }

                Debug.Assert(!refinement.MatchesNull);
                var parts = ArrayBuilder<(ConstantValue? Low, ConstantValue? High)>.GetInstance();
                bool known = ValueSetFactory.TryGetSharperRanges(values, parts, out bool excluded) && !excluded;
                if (known)
                {
                    foreach (var (low, high) in parts)
                    {
                        ranges.Add((low is null ? min : IntegerValue(low), high is null ? max : IntegerValue(high)));
                    }
                }

                parts.Free();
                return known && ranges.Count != 0;
            }
        }

        private static bool TryGetIntegerRange(TypeSymbol type, out decimal min, out decimal max)
        {
            (bool known, min, max) = type.SpecialType switch
            {
                SpecialType.System_Int32 => (true, (decimal)int.MinValue, (decimal)int.MaxValue),
                SpecialType.System_UInt32 => (true, uint.MinValue, uint.MaxValue),
                SpecialType.System_Int64 => (true, long.MinValue, long.MaxValue),
                SpecialType.System_UInt64 => (true, ulong.MinValue, ulong.MaxValue),
                _ => (false, 0m, 0m),
            };
            return known;
        }

        private static decimal IntegerValue(ConstantValue value)
            => TryGetIntegralValue(value, out decimal result) ? result : throw ExceptionUtilities.UnexpectedValue(value.Discriminator);

        private static ConstantValue IntegerConstant(decimal value, TypeSymbol type)
            => IntegralConstant(value, ConstantValue.GetDiscriminator(type.SpecialType));

        #endregion
    }
}
