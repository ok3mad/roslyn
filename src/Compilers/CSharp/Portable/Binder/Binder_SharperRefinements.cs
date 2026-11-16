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
}
