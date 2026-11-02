// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class NullableWalker
    {
        public override BoundNode? VisitConvertedTryExpression(BoundConvertedTryExpression node)
        {
            VisitSharperTryExpressionCore(node, inferType: !node.WasTargetTyped);
            return null;
        }

        public override BoundNode? VisitUnconvertedTryExpression(BoundUnconvertedTryExpression node)
        {
            // This method is only involved in method inference with unbound lambdas.
            VisitSharperTryExpressionCore(node, inferType: true);
            return null;
        }

        /// <summary>
        /// The nullable analysis of a Sharper try expression, modelled on <see cref="VisitSwitchExpressionCore"/> for the
        /// arms and the result type, and on the try statement for the states: an arm starts from the state before the body,
        /// joined with every state the body passes through. The body's value is one more value to convert, before the arms'.
        /// </summary>
        private void VisitSharperTryExpressionCore(BoundTryExpression node, bool inferType)
        {
            int numValues = node.Arms.Length + 1;
            var conversions = ArrayBuilder<Conversion>.GetInstance(numValues);
            var resultTypes = ArrayBuilder<TypeWithState>.GetInstance(numValues);
            var expressions = ArrayBuilder<BoundExpression>.GetInstance(numValues);
            var states = ArrayBuilder<LocalState>.GetInstance(numValues);
            var placeholderBuilder = ArrayBuilder<BoundExpression>.GetInstance(numValues);

            var catchState = this.State.Clone();
            var oldTryState = BeginSharperTryRegion();
            visitValue(node.Body);
            EndSharperTryRegion(oldTryState, ref catchState);
            var endState = this.State.Clone();

            // The caught exception is never null. The decision dag's input stands for it; it is not a placeholder here,
            // because the walker requires placeholders to be replaced by the expression they stand for.
            SetState(catchState);
            TypeSymbol exceptionType = node.ExceptionPlaceholder.Type!;
            var exceptionInput = new BoundExpressionWithNullability(node.Syntax, node.ExceptionPlaceholder, NullableAnnotation.NotAnnotated, exceptionType);
            var labelStateMap = LearnFromDecisionDag(
                node.Syntax, node.ReachabilityDecisionDag, exceptionInput, TypeWithState.Create(exceptionType, NullableFlowState.NotNull), stateWhenNotNullOpt: null, reachabilityInfo: null);

            foreach (var arm in node.Arms)
            {
                SetState(!arm.Pattern.HasErrors && labelStateMap.TryGetValue(arm.Label, out var labelState) ? labelState.state : UnreachableState());
                TakeIncrementalSnapshot(arm);
                oldTryState = BeginSharperTryRegion();
                VisitForRewriting(arm.Pattern);
                if (!State.Reachable && arm.WhenClause != null)
                {
                    // Unreachable when clauses are not visited in `LearnFromDecisionDag`.
                    VisitForRewriting(arm.WhenClause);
                }

                visitValue(arm.Value);
                var unusedState = ReachableBottomState();
                EndSharperTryRegion(oldTryState, ref unusedState);
                Join(ref endState, ref this.State);
            }

            labelStateMap.Free();
            SetState(endState);

            var placeholders = placeholderBuilder.ToImmutableAndFree();
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            TypeSymbol? inferredType =
                (inferType ? BestTypeInferrer.InferBestType(placeholders, _conversions, ref discardedUseSiteInfo, out _) : null)
                    ?? node.Type?.SetUnknownNullabilityForReferenceTypes();
            var inferredTypeWithAnnotations = TypeWithAnnotations.Create(inferredType);

            if (inferType && inferredType is null)
            {
                // As for a switch expression: only when inferring the return type of a lambda, or visiting without diagnostics.
                Debug.Assert(node is BoundUnconvertedTryExpression && (_returnTypesOpt is not null || _disableDiagnostics));
                freeValues();
                SetResult(node, TypeWithState.Create(inferredType, default), inferredTypeWithAnnotations);
                return;
            }

            if (inferType)
            {
                SetResult(node, convertValues(inferredTypeWithAnnotations), inferredTypeWithAnnotations, updateAnalyzedNullability: false);
            }
            else
            {
                // The conversion of each value happens when the target type is known.
                TargetTypedAnalysisCompletion[node] = convertValues;
                SetResult(node, TypeWithState.Create(inferredType, BestTypeInferrer.GetNullableState(resultTypes)), inferredTypeWithAnnotations, updateAnalyzedNullability: false);
            }

            void visitValue(BoundExpression value)
            {
                (BoundExpression expression, Conversion conversion) = RemoveConversion(value, includeExplicitConversions: false);
                SnapshotWalkerThroughConversionGroup(value, expression);
                expressions.Add(expression);
                conversions.Add(conversion);
                var valueType = VisitRvalueWithState(expression);
                resultTypes.Add(valueType);
                states.Add(this.State.Clone());
                if (!IsTargetTypedExpression(expression))
                {
                    // Build placeholders for inference in order to preserve annotations.
                    placeholderBuilder.Add(CreatePlaceholderIfNecessary(expression, valueType.ToTypeWithAnnotations(compilation)));
                }
            }

            TypeWithState convertValues(TypeWithAnnotations targetType)
            {
                for (int i = 0; i < numValues; i++)
                {
                    BoundExpression value = i == 0 ? node.Body : node.Arms[i - 1].Value;
                    resultTypes[i] = ConvertConditionalOperandOrSwitchExpressionArmResult(value, expressions[i], conversions[i], targetType, resultTypes[i], states[i], states[i].Reachable);
                }

                var resultType = TypeWithState.Create(targetType.Type, BestTypeInferrer.GetNullableState(resultTypes));
                freeValues();
                SetAnalyzedNullability(node, resultType);
                return resultType;
            }

            void freeValues()
            {
                conversions.Free();
                resultTypes.Free();
                expressions.Free();
                states.Free();
            }
        }
    }
}
