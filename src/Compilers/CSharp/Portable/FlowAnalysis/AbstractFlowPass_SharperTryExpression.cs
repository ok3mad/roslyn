// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

namespace Microsoft.CodeAnalysis.CSharp
{
    internal abstract partial class AbstractFlowPass<TLocalState, TLocalFunctionState>
    {
        public override BoundNode VisitUnconvertedTryExpression(BoundUnconvertedTryExpression node)
        {
            return this.VisitSharperTryExpression(node);
        }

        public override BoundNode VisitConvertedTryExpression(BoundConvertedTryExpression node)
        {
            return this.VisitSharperTryExpression(node);
        }

        /// <summary>
        /// A Sharper try expression flows like a try statement with one catch block, whose filter matches the arms as a
        /// switch expression does. Each arm starts from the state before the body (joined, when the transfer function is
        /// non-monotonic, with every state the body passes through), and the end state joins the body's and the arms'.
        /// </summary>
        private BoundNode VisitSharperTryExpression(BoundTryExpression node)
        {
            var catchState = this.State.Clone();
            var oldTryState = BeginSharperTryRegion();
            VisitRvalue(node.Body);
            EndSharperTryRegion(oldTryState, ref catchState);
            var endState = this.State;

            var reachableLabels = node.ReachabilityDecisionDag.ReachableLabels;
            foreach (var arm in node.Arms)
            {
                SetState(catchState.Clone());
                oldTryState = BeginSharperTryRegion();
                VisitPattern(arm.Pattern);
                SetState(StateWhenTrue);
                if (!reachableLabels.Contains(arm.Label) || arm.Pattern.HasErrors)
                {
                    SetUnreachable();
                }

                if (arm.WhenClause != null)
                {
                    VisitCondition(arm.WhenClause);
                    SetState(StateWhenTrue);
                }

                VisitRvalue(arm.Value);
                var armRegionState = _nonMonotonicTransfer ? ReachableBottomState() : default;
                EndSharperTryRegion(oldTryState, ref armRegionState);
                Join(ref endState, ref this.State);
            }

            SetState(endState);
            return node;
        }

        /// <summary>
        /// Starts the body or an arm of a try expression. With a non-monotonic transfer function, it collects the states
        /// that part passes through, as the try statement does for its blocks (<see cref="VisitTryBlockWithAnyTransferFunction"/>).
        /// </summary>
        protected Optional<TLocalState> BeginSharperTryRegion()
        {
            Optional<TLocalState> oldTryState = NonMonotonicState;
            if (_nonMonotonicTransfer)
            {
                NonMonotonicState = ReachableBottomState();
            }

            return oldTryState;
        }

        /// <summary>
        /// Ends a part started by <see cref="BeginSharperTryRegion"/>: joins the states it passed through into
        /// <paramref name="regionState"/> and into the enclosing try region, if any.
        /// </summary>
        protected void EndSharperTryRegion(Optional<TLocalState> oldTryState, ref TLocalState regionState)
        {
            if (!_nonMonotonicTransfer)
            {
                return;
            }

            var tempTryStateValue = NonMonotonicState.Value;
            Join(ref regionState, ref tempTryStateValue);
            if (oldTryState.HasValue)
            {
                var oldTryStateValue = oldTryState.Value;
                JoinTryBlockState(ref oldTryStateValue, ref tempTryStateValue);
                oldTryState = oldTryStateValue;
            }

            NonMonotonicState = oldTryState;
        }
    }
}
