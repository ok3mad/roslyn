// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Inferred refinements (Feature 11 of the implementation plan, step 3): the refinement of a value, of a <c>val</c>
    /// local and of the return of an <c>infer</c> method or local function. Nothing is written: the refinement is the
    /// union of what the value can be.
    /// </summary>
    internal sealed partial class SharperRefinement
    {
        /// <summary>
        /// The most constants and refinements a union keeps. A larger union gives no refinement: its text would not be
        /// readable, and every caller would pay for its value set.
        /// </summary>
        internal const int MaxInferredAlternatives = 64;

        /// <summary>
        /// The attribute text of an inferred return that could have a refinement and has none: the discard pattern,
        /// which every value matches. It is written so that the attribute is there whatever the body returns: Edit and
        /// Continue cannot add or remove the metadata row of a return when a body is edited, only rewrite its
        /// attribute. <see cref="FromMetadata"/> reads it as no refinement.
        /// </summary>
        internal const string AnyValueText = "_";

        /// <summary>
        /// True if the return of <paramref name="method"/>, inferred as <paramref name="returnType"/>, can have a
        /// refinement. A refinement is not part of the signature, so a member whose callers do not know which body runs
        /// has none (virtual and interface members; overrides and implementations copy their type and infer nothing).
        /// An iterator and an <c>async</c> method return a sequence or a task, which has no value set; a refinement of
        /// the element or of the awaited value would need a slot there, which no slice has yet.
        /// </summary>
        /// <summary>
        /// True if the refinement was inferred (a union, or the result of a call), false if it is written on a parameter,
        /// possibly reached through a <c>val</c> local. An argument that cannot be proven is an error when its refinement is
        /// inferred, so that a widened return breaks its callers, and a warning when it is written, since slices 2 and 3
        /// narrow such values after tests (Feature 11, step 4).
        /// </summary>
        internal bool IsInferred { get; }

        private SharperRefinement? _lazyInferred;

        /// <summary>This refinement, marked as inferred: the same pattern, values and text.</summary>
        internal SharperRefinement AsInferred()
        {
            if (IsInferred)
            {
                return this;
            }

            if (_lazyInferred is null)
            {
                var inferred = new SharperRefinement(Pattern, Values, MatchesNull, isInferred: true) { _lazyText = _lazyText };
                Interlocked.CompareExchange(ref _lazyInferred, inferred, null);
            }

            return _lazyInferred;
        }

        /// <summary>
        /// The refinement of a refined parameter or a <c>val</c> local where it is read: the one in scope if
        /// <paramref name="scope"/> is given (the hook an <c>assume</c> statement feeds), else its own.
        /// </summary>
        internal static SharperRefinement? OfVariable(Symbol variable, Binder? scope)
            => scope is not null
                ? scope.GetSharperRefinementInScope(variable)
                : variable switch
                {
                    ParameterSymbol parameter => parameter.SharperRefinement,
                    LocalSymbol local => local.SharperRefinement,
                    _ => null,
                };

        internal static bool CanInferForReturn(MethodSymbol method, TypeWithAnnotations returnType)
            => returnType.HasType && !returnType.Type.IsErrorType() && !returnType.Type.IsNullableType() && ValueSetFactory.ForType(returnType.Type) is not null &&
               !method.IsAsync && !method.IsIterator && method.RefKind == RefKind.None &&
               !method.IsVirtual && !method.IsAbstract && !method.IsOverride &&
               (method.MethodKind == MethodKind.LocalFunction || !method.ContainingType.IsInterface);

        /// <summary>
        /// The refinement of the value of <paramref name="expression"/>, or null if it has none: a constant, a refined
        /// parameter, a <c>val</c> local with a refinement, a call of a method whose return has one, and a switch
        /// expression or conditional expression whose arms all yield such values.
        /// </summary>
        /// <param name="syntax">The syntax of the patterns of a new refinement.</param>
        /// <param name="scope">
        /// The binder of the expression, which gives the refinement of a variable in scope
        /// (<see cref="Binder.GetSharperRefinementInScope"/>); null for the variable's own refinement.
        /// </param>
        internal static SharperRefinement? OfExpression(BoundExpression expression, SyntaxNode syntax, Binder? scope = null)
        {
            if (expression.Type is not { } type || expression.HasAnyErrors)
            {
                return null;
            }

            var values = ArrayBuilder<BoundExpression>.GetInstance();
            values.Add(expression);
            SharperRefinement? result = OfValues(values, type, syntax, scope);
            values.Free();
            return result;
        }

        /// <summary>
        /// The refinement of a value that is one of <paramref name="values"/> (the returned values of a body), as a value
        /// of <paramref name="type"/>: the union of their refinements, or null if one of them has none, if the type has
        /// no value set, or if the union allows every value.
        /// </summary>
        internal static SharperRefinement? OfValues(ArrayBuilder<BoundExpression> values, TypeSymbol type, SyntaxNode syntax, Binder? scope = null)
        {
            if (values.Count == 0 || type.IsErrorType() || type.IsNullableType() || ValueSetFactory.ForType(type) is not { } factory)
            {
                return null;
            }

            var alternatives = ArrayBuilder<BoundPattern>.GetInstance();
            SharperRefinement? single = null;
            int sources = 0;
            bool fromCall = false;
            bool known = true;
            foreach (var value in values)
            {
                if (!addValue(value, type, scope, alternatives, ref single, ref sources, ref fromCall))
                {
                    known = false;
                    break;
                }
            }

            SharperRefinement? result = null;
            if (known && sources == 1 && single is not null)
            {
                // A refined value passed on unchanged keeps its refinement as it is written. The result of a call is
                // inferred, even where the method returns a refined parameter unchanged: its body decides it.
                result = fromCall ? single.AsInferred() : single;
            }
            else if (known && alternatives.Count != 0)
            {
                result = Normalize(alternatives, type, factory, syntax);
            }

            alternatives.Free();
            return result;

            static bool addValue(BoundExpression value, TypeSymbol type, Binder? scope, ArrayBuilder<BoundPattern> alternatives, ref SharperRefinement? single, ref int sources, ref bool fromCall)
            {
                while (value is BoundConversion { Conversion.IsIdentity: true, ConstantValueOpt: null } identity)
                {
                    value = identity.Operand;
                }

                if (value is BoundThrowExpression or BoundConversion { Conversion.IsThrow: true })
                {
                    // An arm that throws yields no value.
                    return true;
                }

                if (value.HasAnyErrors)
                {
                    return false;
                }

                if (value.ConstantValueOpt is { IsBad: false } constant)
                {
                    if (constant.IsNull ? !type.IsReferenceType : value.Type?.Equals(type, TypeCompareKind.AllIgnoreOptions) != true)
                    {
                        return false;
                    }

                    sources++;
                    alternatives.Add(new BoundConstantPattern(
                        value.Syntax, new BoundLiteral(value.Syntax, constant, type) { WasCompilerGenerated = true }, constant,
                        UnionMatchingMode.None, inputType: type, narrowedType: type));
                    return true;
                }

                switch (value)
                {
                    case BoundSwitchExpression switchExpression:
                        foreach (var arm in switchExpression.SwitchArms)
                        {
                            if (!addValue(arm.Value, type, scope, alternatives, ref single, ref sources, ref fromCall))
                            {
                                return false;
                            }
                        }

                        return true;

                    case BoundConditionalOperator { IsRef: false } conditional:
                        return addValue(conditional.Consequence, type, scope, alternatives, ref single, ref sources, ref fromCall) &&
                            addValue(conditional.Alternative, type, scope, alternatives, ref single, ref sources, ref fromCall);

                    case BoundUnconvertedConditionalOperator conditional:
                        return addValue(conditional.Consequence, type, scope, alternatives, ref single, ref sources, ref fromCall) &&
                            addValue(conditional.Alternative, type, scope, alternatives, ref single, ref sources, ref fromCall);
                }

                if (value.Type?.Equals(type, TypeCompareKind.AllIgnoreOptions) != true)
                {
                    return false;
                }

                SharperRefinement? refinement = value switch
                {
                    // A refined parameter and a 'val' local are readonly, so the fact holds wherever they are read.
                    BoundParameter { ParameterSymbol: { RefKind: RefKind.None } parameter } => OfVariable(parameter, scope),
                    BoundLocal { LocalSymbol: { RefKind: RefKind.None } local } => OfVariable(local, scope),
                    BoundCall { Method: { RefKind: RefKind.None } method } => method.SharperReturnRefinement,
                    _ => null,
                };

                fromCall |= value is BoundCall;

                if (refinement is not { HasErrors: false })
                {
                    return false;
                }

                sources++;
                single = refinement;
                addAlternatives(refinement.Pattern, alternatives);
                return true;
            }

            static void addAlternatives(BoundPattern pattern, ArrayBuilder<BoundPattern> alternatives)
            {
                if (pattern is BoundBinaryPattern { Disjunction: true } binary)
                {
                    addAlternatives(binary.Left, alternatives);
                    addAlternatives(binary.Right, alternatives);
                }
                else
                {
                    alternatives.Add(pattern);
                }
            }
        }

        /// <summary>
        /// The union of <paramref name="alternatives"/> in its normal form: <c>null</c>, then the constants in ascending
        /// order, each once, then the other patterns in the order they were found, each once; a constant that another
        /// pattern allows is dropped. Null if there are more than <see cref="MaxInferredAlternatives"/>, or if the union
        /// allows every value of the type, which says nothing.
        /// </summary>
        private static SharperRefinement? Normalize(ArrayBuilder<BoundPattern> alternatives, TypeSymbol type, IConstantValueSetFactory factory, SyntaxNode syntax)
        {
            var constants = ArrayBuilder<ConstantValue>.GetInstance();
            var others = ArrayBuilder<BoundPattern>.GetInstance();
            var otherTexts = PooledHashSet<string>.GetInstance();
            bool hasNull = false;

            foreach (var alternative in alternatives)
            {
                if (alternative is BoundConstantPattern constant)
                {
                    if (constant.ConstantValue.IsNull)
                    {
                        hasNull = true;
                    }
                    else if (!constants.Contains(constant.ConstantValue))
                    {
                        constants.Add(constant.ConstantValue);
                    }
                }
                else if (otherTexts.Add(new SharperRefinement(alternative, values: null, matchesNull: false).FormatPattern()))
                {
                    others.Add(alternative);
                }
            }

            otherTexts.Free();

            for (int i = constants.Count - 1; i >= 0; i--)
            {
                foreach (var other in others)
                {
                    if (Matches(other, factory, constants[i]))
                    {
                        constants.RemoveAt(i);
                        break;
                    }
                }
            }

            if (hasNull && others.Any(static (other, factory) => Matches(other, factory, ConstantValue.Null), factory))
            {
                hasNull = false;
            }

            SharperRefinement? result = null;
            if (constants.Count + others.Count + (hasNull ? 1 : 0) <= MaxInferredAlternatives)
            {
                constants.Sort(new ConstantComparer(factory));

                BoundPattern? pattern = null;
                if (hasNull)
                {
                    add(ref pattern, constantPattern(ConstantValue.Null, type, syntax), type, syntax);
                }

                foreach (var constant in constants)
                {
                    add(ref pattern, constantPattern(constant, type, syntax), type, syntax);
                }

                foreach (var other in others)
                {
                    add(ref pattern, other, type, syntax);
                }

                Debug.Assert(pattern is not null);
                pattern = Rebuild(pattern, type, syntax);
                var values = ComputeValues(pattern, factory);
                bool matchesNull = Matches(pattern, factory, ConstantValue.Null);
                if (!(((IConstantValueSet)values.Complement()).IsEmpty && (matchesNull || !type.IsReferenceType)))
                {
                    result = new SharperRefinement(pattern, values, matchesNull, isInferred: true);
                }
            }

            constants.Free();
            others.Free();
            return result;

            static BoundPattern constantPattern(ConstantValue value, TypeSymbol type, SyntaxNode syntax)
                => new BoundConstantPattern(
                    syntax, new BoundLiteral(syntax, value, type) { WasCompilerGenerated = true }, value,
                    UnionMatchingMode.None, inputType: type, narrowedType: type);

            static void add(ref BoundPattern? pattern, BoundPattern alternative, TypeSymbol type, SyntaxNode syntax)
            {
                pattern = pattern is null
                    ? alternative
                    : new BoundBinaryPattern(syntax, disjunction: true, pattern, alternative, inputType: type, narrowedType: type);
            }
        }

        /// <summary>Orders the constants of one type: numbers by value with NaN last, strings by ordinal, false before true.</summary>
        private sealed class ConstantComparer : IComparer<ConstantValue>
        {
            private readonly IConstantValueSetFactory _factory;

            internal ConstantComparer(IConstantValueSetFactory factory)
            {
                _factory = factory;
            }

            public int Compare(ConstantValue? x, ConstantValue? y)
            {
                Debug.Assert(x is not null && y is not null);
                if (x.IsString)
                {
                    return string.CompareOrdinal(x.StringValue, y.StringValue);
                }

                if (x.IsBoolean)
                {
                    return x.BooleanValue.CompareTo(y.BooleanValue);
                }

                bool xNaN = x.IsFloating && double.IsNaN(x.DoubleValue);
                bool yNaN = y.IsFloating && double.IsNaN(y.DoubleValue);
                if (xNaN || yNaN)
                {
                    return xNaN.CompareTo(yNaN);
                }

                return _factory.Related(BinaryOperatorKind.LessThan, x, y) ? -1
                    : _factory.Related(BinaryOperatorKind.LessThan, y, x) ? 1
                    : 0;
            }
        }
    }
}
