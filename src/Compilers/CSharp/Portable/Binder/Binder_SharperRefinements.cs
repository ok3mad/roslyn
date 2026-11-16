// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Refined types at call sites and in switches (Feature 11 of the implementation plan, step 4). An argument for a
    /// refined parameter is proven (silent), disproven (an error) or not proven: an error when its value has an inferred
    /// refinement, so that a widened return breaks its callers, and a warning when it has a written one or none. A switch
    /// on a refined value seeds its decision dag with the refinement's values. Only Sharper files are checked; C# callers
    /// meet the guard (<see cref="SharperRefinementGuard"/>).
    /// </summary>
    /// <remarks>
    /// Not checked, and covered by the guard: method group conversions, dynamic calls, and the operands of user-defined
    /// operators and conversions, which do not bind through <see cref="CheckAndCoerceArguments"/>.
    /// </remarks>
    internal partial class Binder
    {
        // PROTOTYPE(sharper): callers now depend on inferred return refinements, so an Edit and Continue body edit that
        // changes only a refinement must be a rude edit; the IDE cannot see refinements yet (#237).
        // PROTOTYPE(sharper): check the operands of user-defined operators and conversions with refined parameters.

        /// <summary>
        /// The refinement of a refined parameter or a <c>val</c> local where this binder reads it: the variable's own
        /// refinement, unless a binder of an enclosing scope knows better. This is the hook an <c>assume</c> statement
        /// (#39) feeds: its scope binder overrides this method for the following statements of its block.
        /// </summary>
        internal virtual SharperRefinement? GetSharperRefinementInScope(Symbol variable)
        {
            Debug.Assert(variable is ParameterSymbol or LocalSymbol);
            return Next is { } next
                ? next.GetSharperRefinementInScope(variable)
                : SharperRefinement.OfVariable(variable, scope: null);
        }

        /// <summary>
        /// Checks a converted argument against the refinement of its parameter. Every call the binder resolves comes
        /// here (<see cref="CheckAndCoerceArguments"/>): methods, constructors and constructor initializers, indexers,
        /// attributes, local functions, extension methods and extension block members, with named and optional
        /// arguments. Default values were checked at the declaration (CS20060), and a <c>params</c> parameter cannot be
        /// refined (its type has no value set).
        /// </summary>
        private void CheckSharperRefinedArgument(SyntaxNode node, BoundExpression argument, ParameterSymbol parameter, BindingDiagnosticBag diagnostics)
        {
            if (parameter.SharperRefinement is not { HasErrors: false } refinement ||
                !diagnostics.AccumulatesDiagnostics ||
                argument.HasAnyErrors ||
                !node.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return;
            }

            Debug.Assert(parameter.RefKind == RefKind.None);
            SyntaxNode argumentSyntax = argument.Syntax;
            SharperRefinement? argumentRefinement = SharperRefinement.OfExpression(argument, argumentSyntax, scope: this);
            if (argumentRefinement is not { HasErrors: false })
            {
                diagnostics.Add(ErrorCode.WRN_SharperRefinementArgumentUnknown, argumentSyntax.Location, argumentSyntax, parameter.Name, refinement.Text);
                return;
            }

            switch (argumentRefinement.Prove(refinement))
            {
                case SharperRefinementProof.Proven:
                    return;
                case SharperRefinementProof.Disproven:
                    diagnostics.Add(ErrorCode.ERR_SharperRefinementArgumentDisproven, argumentSyntax.Location, argumentSyntax, parameter.Name, refinement.Text, argumentRefinement.Text);
                    return;
                default:
                    diagnostics.Add(
                        argumentRefinement.IsInferred ? ErrorCode.ERR_SharperRefinementArgumentNotProven : ErrorCode.WRN_SharperRefinementArgumentNotProven,
                        argumentSyntax.Location, argumentSyntax, parameter.Name, refinement.Text, argumentRefinement.Text, argumentRefinement.SampleOutside(refinement));
                    return;
            }
        }

        /// <summary>
        /// The refinement of the input of a switch in a Sharper file, whose values seed its decision dag, or null if
        /// it has none. A refinement that allows null (<c>string s is "a" or null</c>) does not seed the dag: value sets
        /// do not hold null, and the dag builder takes an empty set of values to mean that no value is left.
        /// </summary>
        internal SharperRefinement? GetSharperSwitchInputRefinement(BoundExpression input)
        {
            if (input.HasAnyErrors || !input.Syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper) ||
                SharperRefinement.OfExpression(input, input.Syntax, scope: this) is not { Values: { } values, MatchesNull: false } refinement ||
                ValueSetFactory.ForInput(BoundDagTemp.ForOriginalInput(input)) is not { } factory ||
                factory.AllValues.GetType() != values.GetType())
            {
                return null;
            }

            return refinement;
        }
    }

    /// <summary>
    /// Refined types, slice 2: the values a switch expression arm narrows its input to, when the input is a variable that
    /// cannot change (a refined or <c>readonly</c> parameter, a <c>val</c> or <c>readonly</c> local). Arms are bound in
    /// order; each one's values are what the earlier arms without a <c>when</c> clause leave, that its pattern can match,
    /// and the arm binder (<see cref="SwitchExpressionArmBinder"/>) gives them to the variable inside its <c>when</c>
    /// clause and value, through <see cref="Binder.GetSharperRefinementInScope"/>. Patterns that are not closed over
    /// constants count as matching every value for the arm, and as matching none for the arms after it, so the
    /// narrowed values always include the real ones.
    /// </summary>
    internal sealed class SharperSwitchNarrowing
    {
        private readonly IConstantValueSetFactory _factory;
        private IConstantValueSet _remaining;
        private bool _remainingNull;

        private SharperSwitchNarrowing(Symbol variable, TypeSymbol type, IConstantValueSetFactory factory, IConstantValueSet remaining, bool remainingNull, bool isInferred)
        {
            Variable = variable;
            Type = type;
            _factory = factory;
            _remaining = remaining;
            _remainingNull = remainingNull;
            IsInferred = isInferred;
        }

        internal Symbol Variable { get; }

        internal TypeSymbol Type { get; }

        /// <summary>True if the variable's refinement is inferred; a narrowed one keeps that.</summary>
        internal bool IsInferred { get; }

        /// <summary>The narrowing for a switch expression on <paramref name="input"/> in a Sharper file, or null.</summary>
        internal static SharperSwitchNarrowing? Create(Binder binder, BoundExpression input)
        {
            if (input.HasAnyErrors || !input.Syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return null;
            }

            while (input is BoundConversion { Conversion.IsIdentity: true, ConstantValueOpt: null } identity)
            {
                input = identity.Operand;
            }

            Symbol? variable = input switch
            {
                BoundLocal { LocalSymbol: SourceLocalSymbol { IsValOrSharperReadOnly: true, RefKind: RefKind.None } local } => local,
                BoundParameter { ParameterSymbol: SourceComplexParameterSymbolBase { IsSharperReadOnly: true, RefKind: RefKind.None } parameter } => parameter,
                BoundParameter { ParameterSymbol: LambdaParameterSymbol { IsSharperVal: true, RefKind: RefKind.None } parameter } => parameter,
                _ => null,
            };

            if (variable is null || input.Type is not { } type || type.IsNullableType() || ValueSetFactory.ForType(type) is not { } factory)
            {
                return null;
            }

            SharperRefinement? refinement = SharperRefinement.OfVariable(variable, binder);
            if (refinement is { HasErrors: true })
            {
                return null;
            }

            return new SharperSwitchNarrowing(
                variable, type, factory,
                refinement?.Values ?? factory.AllValues,
                refinement?.MatchesNull ?? type.CanContainNull(),
                refinement?.IsInferred ?? false);
        }

        /// <summary>
        /// The refinement of the variable inside the next arm, whose pattern is <paramref name="pattern"/>, or null if it
        /// narrows nothing that can be written; then takes the arm's values out of those the later arms can see.
        /// </summary>
        internal SharperRefinement? NarrowArm(BoundPattern pattern, bool hasWhenClause, SyntaxNode syntax)
        {
            var armValues = (IConstantValueSet)_remaining.Intersect(matched(pattern, superset: true));
            bool armNull = _remainingNull && matchesNull(pattern, superset: true);
            if (!hasWhenClause)
            {
                _remaining = (IConstantValueSet)_remaining.Intersect(matched(pattern, superset: false).Complement());
                _remainingNull &= !matchesNull(pattern, superset: false);
            }

            return SharperRefinement.FromValues(armValues, armNull, Type, syntax, IsInferred);

            // The values the pattern matches, or a superset (a subset) of them where they are not known.
            IConstantValueSet matched(BoundPattern pattern, bool superset)
            {
                switch (pattern)
                {
                    case BoundConstantPattern { ConstantValue: { IsBad: false } value }:
                        return value.IsNull ? _factory.NoValues : _factory.Related(BinaryOperatorKind.Equal, value);
                    case BoundRelationalPattern { ConstantValue: { IsBad: false, IsNull: false } value } relational:
                        return _factory.Related(relational.Relation.Operator(), value);
                    case BoundBinaryPattern { Disjunction: true } binary:
                        return (IConstantValueSet)matched(binary.Left, superset).Union(matched(binary.Right, superset));
                    case BoundBinaryPattern binary:
                        return (IConstantValueSet)matched(binary.Left, superset).Intersect(matched(binary.Right, superset));
                    case BoundNegatedPattern negated:
                        return (IConstantValueSet)matched(negated.Negated, !superset).Complement();
                    case BoundDiscardPattern:
                        return _factory.AllValues;
                    case BoundDeclarationPattern or BoundTypePattern or BoundRecursivePattern { Deconstruction.IsDefaultOrEmpty: true, Properties.IsDefaultOrEmpty: true } when
                            pattern.NarrowedType.Equals(Type, TypeCompareKind.AllIgnoreOptions):
                        return _factory.AllValues;
                    default:
                        return superset ? _factory.AllValues : _factory.NoValues;
                }
            }

            // Whether the pattern matches null; true (false) where that is not known.
            static bool matchesNull(BoundPattern pattern, bool superset)
            {
                switch (pattern)
                {
                    case BoundConstantPattern { ConstantValue: { IsBad: false } value }:
                        return value.IsNull;
                    case BoundRelationalPattern:
                        return false;
                    case BoundBinaryPattern { Disjunction: true } binary:
                        return matchesNull(binary.Left, superset) || matchesNull(binary.Right, superset);
                    case BoundBinaryPattern binary:
                        return matchesNull(binary.Left, superset) && matchesNull(binary.Right, superset);
                    case BoundNegatedPattern negated:
                        return !matchesNull(negated.Negated, !superset);
                    case BoundDiscardPattern or BoundDeclarationPattern { IsVar: true }:
                        return true;
                    case BoundDeclarationPattern or BoundTypePattern or BoundRecursivePattern:
                        return false;
                    default:
                        return superset;
                }
            }
        }
    }
}
