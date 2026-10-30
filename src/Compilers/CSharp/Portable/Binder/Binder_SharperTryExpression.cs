// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper's try expression, <c>try body catch { arms }</c>. The arms are switch expression arms over the caught
    /// exception: patterns, designations and <c>when</c> clauses as in a switch expression, with no exhaustiveness check,
    /// because an exception that no arm matches propagates. Like a switch expression, the try expression converts to a
    /// target type (the body and each arm convert to it, which reaches a C# 15 union), and otherwise has the best common
    /// type of the body and the arms.
    /// </summary>
    internal partial class Binder
    {
        private BoundExpression BindSharperTryExpression(TryExpressionSyntax node, BindingDiagnosticBag diagnostics)
        {
            // The parser makes a try expression only with Sharper on, but a tree can be built with SyntaxFactory.
            CheckFeatureAvailability(node.TryKeyword, MessageID.IDS_FeatureSharper, diagnostics);

            // The try expression lowers to a try block, which a catch filter cannot contain. A lambda in a filter is a method
            // of its own, so its binder has no filter flag.
            bool hasErrors = false;
            if (this.Flags.Includes(BinderFlags.InCatchFilter))
            {
                diagnostics.Add(ErrorCode.ERR_SharperTryExpressionInCatchFilter, node.TryKeyword.GetLocation());
                hasErrors = true;
            }

            TypeSymbol exceptionType = GetSharperTryExpressionInputType(node, diagnostics);
            BoundExpression body = BindValue(node.Expression, diagnostics, BindValueKind.RValue);
            ReportSharperTryExpressionAwaitableBody(node, body, diagnostics);

            var armsBuilder = ArrayBuilder<BoundSwitchExpressionArm>.GetInstance(node.Arms.Count);
            foreach (var arm in node.Arms)
            {
                armsBuilder.Add(GetRequiredBinder(arm).BindSwitchExpressionArm(arm, exceptionType, diagnostics));
            }

            ImmutableArray<BoundSwitchExpressionArm> arms = armsBuilder.ToImmutableAndFree();
            TypeSymbol? naturalType = InferSharperTryExpressionType(node, body, arms, diagnostics);

            // The decision dag matches the arms against the caught exception, which the placeholder stands for.
            var exceptionPlaceholder = new BoundValuePlaceholder(node, exceptionType) { WasCompilerGenerated = true };
            LabelSymbol? defaultLabel = new GeneratedLabelSymbol("unmatched");
            BoundDecisionDag decisionDag = DecisionDagBuilder.CreateDecisionDagForSwitchExpression(
                this.Compilation, node, exceptionPlaceholder, arms, defaultLabel, diagnostics);

            // An arm that can never match is an error, as in a switch expression. There is no exhaustiveness check.
            var reachableLabels = decisionDag.ReachableLabels;
            bool reportedSubsumed = false;
            foreach (BoundSwitchExpressionArm arm in arms)
            {
                if (arm.HasErrors)
                {
                    reportedSubsumed = true;
                    break;
                }

                if (!reachableLabels.Contains(arm.Label))
                {
                    diagnostics.Add(ErrorCode.ERR_SwitchArmSubsumed, arm.Pattern.Syntax.Location);
                    reportedSubsumed = true;
                }
            }

            if (!reachableLabels.Contains(defaultLabel))
            {
                // An arm matches every exception.
                defaultLabel = null;
            }

            if (!reportedSubsumed && diagnostics.AccumulatesDiagnostics && DecisionDagBuilder.EnableRedundantPatternsCheck(this.Compilation))
            {
                DecisionDagBuilder.CheckRedundantPatternsForSwitchExpression(this.Compilation, node, exceptionPlaceholder, arms, diagnostics);
            }

            return new BoundUnconvertedTryExpression(
                node, body, arms, exceptionPlaceholder, decisionDag, defaultLabel, naturalType, hasErrors);
        }

        /// <summary>
        /// The type the arms of a try expression match: <see cref="System.Exception"/>.
        /// </summary>
        internal TypeSymbol GetSharperTryExpressionInputType(SyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            return GetWellKnownType(WellKnownType.System_Exception, diagnostics, node);
        }

        /// <summary>
        /// A body whose value is a task only throws the exceptions raised before the task is returned; the others surface
        /// when it is awaited, outside the try. So <c>try F() catch { ... }</c> with an asynchronous <c>F</c> must be written
        /// <c>try await F() catch { ... }</c>.
        /// </summary>
        private void ReportSharperTryExpressionAwaitableBody(TryExpressionSyntax node, BoundExpression body, BindingDiagnosticBag diagnostics)
        {
            // PROTOTYPE(sharper): lift a task body instead (#136), after the lifting design (#32). Only task-like types are
            // checked here, not every type with a GetAwaiter method.
            if (body.Type is { } bodyType &&
                (bodyType.IsNonGenericTaskType(Compilation) || bodyType.IsGenericTaskType(Compilation)))
            {
                diagnostics.Add(ErrorCode.WRN_SharperTryExpressionAwaitableBody, node.Expression.Location, bodyType);
            }
        }

        /// <summary>
        /// The natural type of a try expression: the best common type of its body and arms, to which each of them converts.
        /// </summary>
        private TypeSymbol? InferSharperTryExpressionType(
            TryExpressionSyntax node,
            BoundExpression body,
            ImmutableArray<BoundSwitchExpressionArm> arms,
            BindingDiagnosticBag diagnostics)
        {
            var seenTypes = Symbols.SpecializedSymbolCollections.GetPooledSymbolHashSetInstance<TypeSymbol>();
            var typesInOrder = ArrayBuilder<TypeSymbol>.GetInstance();
            addType(body);
            foreach (var arm in arms)
            {
                addType(arm.Value);
            }

            seenTypes.Free();
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            var commonType = BestTypeInferrer.GetBestType(typesInOrder, Conversions, ref useSiteInfo);
            typesInOrder.Free();

            // Every value, including those without a type, must convert to the candidate.
            if (commonType is not null)
            {
                bool allConvert = Conversions.ClassifyImplicitConversionFromExpression(body, commonType, ref useSiteInfo).Exists;
                foreach (var arm in arms)
                {
                    allConvert = allConvert && Conversions.ClassifyImplicitConversionFromExpression(arm.Value, commonType, ref useSiteInfo).Exists;
                }

                if (!allConvert)
                {
                    commonType = null;
                }
            }

            diagnostics.Add(node, useSiteInfo);
            return commonType;

            void addType(BoundExpression value)
            {
                if (value.Type is { } type && seenTypes.Add(type))
                {
                    typesInOrder.Add(type);
                }
            }
        }

        private void ReportSharperTryExpressionNoType(BoundUnconvertedTryExpression node, BindingDiagnosticBag diagnostics)
        {
            var types = ArrayBuilder<string>.GetInstance();
            addDisplay(node.Body);
            foreach (var arm in node.Arms)
            {
                addDisplay(arm.Value);
            }

            diagnostics.Add(ErrorCode.ERR_SharperTryExpressionNoType, ((TryExpressionSyntax)node.Syntax).TryKeyword.GetLocation(), string.Join(", ", types));
            types.Free();

            void addDisplay(BoundExpression value)
            {
                string display = value.Display.ToString() ?? "";
                if (!types.Contains(display))
                {
                    types.Add(display);
                }
            }
        }

        private BoundExpression ConvertSharperTryExpression(BoundUnconvertedTryExpression source, TypeSymbol destination, Conversion? conversionIfTargetTyped, BindingDiagnosticBag diagnostics, bool hasErrors = false)
        {
            bool targetTyped = conversionIfTargetTyped is { };
            Conversion conversion = conversionIfTargetTyped ?? Conversion.Identity;
            Debug.Assert(targetTyped || destination.IsErrorType() || destination.Equals(source.Type, TypeCompareKind.ConsiderEverything));
            ImmutableArray<Conversion> underlyingConversions = conversion.UnderlyingConversions;

            // The conversions are those of the body, then of each arm.
            BoundExpression body = convert(source.Body, 0);
            var builder = ArrayBuilder<BoundSwitchExpressionArm>.GetInstance(source.Arms.Length);
            for (int i = 0, n = source.Arms.Length; i < n; i++)
            {
                var oldArm = source.Arms[i];
                var newValue = convert(oldArm.Value, i + 1);
                builder.Add(oldArm.Value == newValue ? oldArm :
                    new BoundSwitchExpressionArm(oldArm.Syntax, oldArm.Locals, oldArm.Pattern, oldArm.HasUnionMatching, oldArm.WhenClause, newValue, oldArm.Label, oldArm.HasErrors));
            }

            conversion.MarkUnderlyingConversionsChecked();

            return new BoundConvertedTryExpression(
                source.Syntax, source.Type, targetTyped, body, builder.ToImmutableAndFree(), source.ExceptionPlaceholder,
                source.ReachabilityDecisionDag, source.DefaultLabel, destination, hasErrors || source.HasErrors).WithSuppression(source.IsSuppressed);

            BoundExpression convert(BoundExpression value, int index)
                => targetTyped
                    ? CreateConversion(value.Syntax, value, underlyingConversions[index], isCast: false, conversionGroupOpt: null, InConversionGroupFlags.Unspecified, destination, diagnostics)
                    : GenerateConversionForAssignment(destination, value, diagnostics);
        }
    }
}
