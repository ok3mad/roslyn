// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class NullableWalker
    {
        /// <summary>
        /// A Sharper pipe's <c>it</c> is inferred like a <c>var</c> local initialized with the left side: its type is
        /// annotated, and its state is the left side's. The pipe's result is the stage's. A target-typed stage (#187) is
        /// converted when the target type is known, as a switch expression's arms are.
        /// </summary>
        public override BoundNode? VisitSharperPipe(BoundSharperPipe node)
        {
            VisitSharperPipeCore(node, targetTyped: node.WasTargetTyped);
            return null;
        }

        public override BoundNode? VisitUnconvertedSharperPipe(BoundUnconvertedSharperPipe node)
        {
            // Only reached in the inference of a lambda's return type, as for a switch expression.
            VisitSharperPipeCore(node, targetTyped: false);
            return null;
        }

        private void VisitSharperPipeCore(BoundSharperPipeBase node, bool targetTyped)
        {
            TypeWithState leftType = VisitRvalueWithState(node.Left);
            var local = node.Local;
            TypeWithAnnotations localType = local.TypeWithAnnotations;
            if (leftType.HasNullType)
            {
                leftType = localType.ToTypeWithState();
            }
            else
            {
                localType = leftType.ToAnnotatedTypeWithAnnotations(compilation);
            }

            _variables.SetType(local, localType);
            int slot = GetOrCreateSlot(local);
            TrackNullableStateForAssignment(node.Left, localType, slot, leftType, MakeSlot(node.Left));

            if (!targetTyped)
            {
                TypeWithState stageType = VisitRvalueWithState(node.Stage);
                SetResultType(node, stageType);
                return;
            }

            // The stage's conversion to the target type happens when the target type, with its annotations, is known.
            BoundExpression stage = node.Stage;
            (BoundExpression expression, Conversion conversion) = RemoveConversion(stage, includeExplicitConversions: false);
            SnapshotWalkerThroughConversionGroup(stage, expression);
            TypeWithState valueType = VisitRvalueWithState(expression);
            LocalState state = this.State.Clone();
            TargetTypedAnalysisCompletion[node] =
                (TypeWithAnnotations targetType) =>
                {
                    TypeWithState convertedType = ConvertConditionalOperandOrSwitchExpressionArmResult(stage, expression, conversion, targetType, valueType, state, state.Reachable);
                    var resultType = TypeWithState.Create(targetType.Type, convertedType.State);
                    SetAnalyzedNullability(node, resultType);
                    return resultType;
                };

            var stateType = TypeWithState.Create(node.Type, valueType.State);
            SetResult(node, stateType, stateType.ToTypeWithAnnotations(compilation), updateAnalyzedNullability: false);
        }
    }
}
