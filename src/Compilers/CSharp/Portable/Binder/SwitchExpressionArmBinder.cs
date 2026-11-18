// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Binder for one of the arms of a switch expression. For example, in the one-armed switch expression
    /// "e switch { p when c => v }", this could be the binder for the arm "p when c => v".
    /// It also binds the arms of a Sharper try expression, which have no switch expression binder: their input is the
    /// caught exception, their pattern and `when` clause run in a catch filter, and their value in the catch block.
    /// </summary>
    internal sealed class SwitchExpressionArmBinder : Binder
    {
        private readonly SwitchExpressionArmSyntax _arm;
        private readonly ExpressionVariableBinder _armScopeBinder;
        private readonly SwitchExpressionBinder? _switchExpressionBinder;

        public SwitchExpressionArmBinder(SwitchExpressionArmSyntax arm, ExpressionVariableBinder armScopeBinder, SwitchExpressionBinder? switchExpressionBinder) : base(armScopeBinder)
        {
            this._arm = arm;
            this._armScopeBinder = armScopeBinder;
            this._switchExpressionBinder = switchExpressionBinder;
        }

        internal BoundSwitchExpressionArm BindSwitchExpressionArm(SwitchExpressionArmSyntax node, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node == _arm);
            TypeSymbol inputType = _switchExpressionBinder?.GetInputType() ?? GetSharperTryExpressionInputType(node, BindingDiagnosticBag.Discarded);
            return BindSwitchExpressionArm(node, inputType, diagnostics);
        }

        /// <summary>
        /// Sharper: the variable that is the switch input and its refinement inside this arm (refined types, slice 2),
        /// set when the arm binds with a <see cref="SharperSwitchNarrowing"/>. Binding is deterministic, so binding the
        /// arm again sets an equal one.
        /// </summary>
        private (Symbol Variable, SharperRefinement Refinement)? _sharperNarrowing;

        internal override SharperRefinement? GetSharperRefinementInScope(Symbol variable)
            => _sharperNarrowing is { } narrowing && narrowing.Variable.Equals(variable)
                ? narrowing.Refinement
                : base.GetSharperRefinementInScope(variable);

        internal override BoundSwitchExpressionArm BindSwitchExpressionArm(SwitchExpressionArmSyntax node, TypeSymbol switchGoverningType, BindingDiagnosticBag diagnostics)
            => BindSwitchExpressionArm(node, switchGoverningType, sharperNarrowing: null, diagnostics);

        internal BoundSwitchExpressionArm BindSwitchExpressionArm(SwitchExpressionArmSyntax node, TypeSymbol switchGoverningType, SharperSwitchNarrowing? sharperNarrowing, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(node == _arm);
            Binder armBinder = this.GetRequiredBinder(node);
            bool hasErrors = switchGoverningType.IsErrorType();
            ImmutableArray<LocalSymbol> locals = _armScopeBinder.Locals;
            NamedTypeSymbol? unionType = null;
            Binder patternBinder = armBinder;
            Binder valueBinder = armBinder;
            if (_switchExpressionBinder is null)
            {
                patternBinder = armBinder.WithAdditionalFlags(BinderFlags.InCatchFilter);
                valueBinder = armBinder.WithAdditionalFlags(BinderFlags.InCatchBlock);
            }

            BoundPattern pattern = patternBinder.BindPattern(node.Pattern, ref unionType, switchGoverningType, permitDesignations: true, hasErrors, diagnostics, out bool hasUnionMatching, underIsPattern: false);
            if (sharperNarrowing?.NarrowArm(pattern, node.WhenClause != null, node.Pattern) is { } narrowed)
            {
                _sharperNarrowing = (sharperNarrowing.Variable, narrowed);
            }

            BoundExpression? whenClause = node.WhenClause != null
                ? patternBinder.BindBooleanExpression(node.WhenClause.Condition, diagnostics)
                : null;
            BoundExpression armResult = valueBinder.BindValue(node.Expression, diagnostics, BindValueKind.RValue);
            var label = new GeneratedLabelSymbol("arm");
            return new BoundSwitchExpressionArm(node, locals, pattern, hasUnionMatching, whenClause, armResult, label, hasErrors | pattern.HasErrors);
        }
    }
}
