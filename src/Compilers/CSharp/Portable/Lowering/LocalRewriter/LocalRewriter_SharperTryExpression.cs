// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class LocalRewriter
    {
        public override BoundNode VisitConvertedTryExpression(BoundConvertedTryExpression node)
        {
            // The try expression lowers to a try statement, so it is represented by a BoundSpillSequence, and the spiller
            // moves it to the enclosing statement list (earlier operands of the enclosing expression go to temps).
            this._needsSpilling = true;
            return SharperTryExpressionLocalRewriter.Rewrite(this, node);
        }

        /// <summary>
        /// Lowers <c>try body catch { p1 when c1 => v1, p2 => v2 }</c> to
        /// <code>
        /// try
        /// {
        ///     result = body;
        /// }
        /// catch (Exception e) when ({ arm = 0; [the decision dag over e sets arm to 1 or 2 and the pattern variables] } arm != 0)
        /// {
        ///     switch (arm) { case 1: result = v1; break; default: result = v2; break; }
        /// }
        /// </code>
        /// The arms run in the filter, so an exception that no arm matches is never caught: it keeps its stack trace, and
        /// inner finally blocks run after the filter decides, as for any unhandled exception.
        /// </summary>
        private sealed class SharperTryExpressionLocalRewriter : BaseSwitchLocalRewriter
        {
            private SharperTryExpressionLocalRewriter(BoundConvertedTryExpression node, LocalRewriter localRewriter)
                // PROTOTYPE(sharper): no instrumentation (sequence points, code coverage) for the arms yet (#145).
                : base(node.Syntax, localRewriter, node.Arms.SelectAsArray(arm => arm.Syntax), generateInstrumentation: false)
            {
            }

            public static BoundExpression Rewrite(LocalRewriter localRewriter, BoundConvertedTryExpression node)
            {
                var rewriter = new SharperTryExpressionLocalRewriter(node, localRewriter);
                BoundExpression result = rewriter.LowerTryExpression(node);
                rewriter.Free();
                return result;
            }

            private BoundExpression LowerTryExpression(BoundConvertedTryExpression node)
            {
                _factory.Syntax = node.Syntax;
                var outerVariables = ArrayBuilder<LocalSymbol>.GetInstance();
                LocalSymbol resultTemp = _factory.SynthesizedLocal(node.Type, node.Syntax, kind: SynthesizedLocalKind.LoweringTemp);
                LocalSymbol armTemp = _factory.SynthesizedLocal(_factory.SpecialType(SpecialType.System_Int32), node.Syntax, kind: SynthesizedLocalKind.LoweringTemp);
                LocalSymbol exceptionLocal = _factory.SynthesizedLocal(node.ExceptionPlaceholder.Type!, node.Syntax, kind: SynthesizedLocalKind.LoweringTemp);

                // try { result = body; }
                BoundBlock tryBlock = _factory.Block(_factory.Assignment(_factory.Local(resultTemp), _localRewriter.VisitExpression(node.Body)));

                // The filter prologue: the decision dag over the caught exception, which sets the arm index.
                var prologue = ArrayBuilder<BoundStatement>.GetInstance();
                prologue.Add(_factory.Assignment(_factory.Local(armTemp), _factory.Literal(0)));
                BoundDecisionDag decisionDag = ShareTempsIfPossibleAndEvaluateInput(
                    GetDecisionDagForLowering(node, out LabelSymbol? defaultLabel),
                    _factory.Local(exceptionLocal), prologue, out _);
                (ImmutableArray<BoundStatement> loweredDag, ImmutableDictionary<SyntaxNode, ImmutableArray<BoundStatement>> armSections) =
                    LowerDecisionDag(decisionDag);
                prologue.Add(_factory.Block(loweredDag));

                if (_whenNodeIdentifierLocal is not null)
                {
                    outerVariables.Add(_whenNodeIdentifierLocal);
                }

                // An await in an arm's value is an await in a catch block, which AsyncExceptionHandlerRewriter moves out of the
                // handler, as for a try statement (VisitTryStatement).
                bool origSawAwait = _localRewriter._sawAwait;
                _localRewriter._sawAwait = false;

                // Each arm label sets the arm index; the catch block dispatches on it.
                LabelSymbol afterDecision = _factory.GenerateLabel("afterTryExpressionFilter");
                LabelSymbol afterArms = _factory.GenerateLabel("afterTryExpressionArms");
                var cases = ArrayBuilder<(ConstantValue value, LabelSymbol label)>.GetInstance(node.Arms.Length);
                var catchBody = ArrayBuilder<BoundStatement>.GetInstance();
                for (int i = 0; i < node.Arms.Length; i++)
                {
                    BoundSwitchExpressionArm arm = node.Arms[i];
                    _factory.Syntax = arm.Syntax;
                    prologue.AddRange(armSections[arm.Syntax]);
                    prologue.Add(_factory.Label(arm.Label));
                    prologue.Add(_factory.Assignment(_factory.Local(armTemp), _factory.Literal(i + 1)));
                    prologue.Add(_factory.Goto(afterDecision));

                    // Pattern variables are set in the filter and read in the catch block, as in `catch (E e) when (e is X x)`.
                    outerVariables.AddRange(arm.Locals);

                    var valueLabel = _factory.GenerateLabel("tryExpressionArm");
                    cases.Add((ConstantValue.Create(i + 1), valueLabel));
                    catchBody.Add(_factory.Label(valueLabel));
                    catchBody.Add(_factory.Assignment(_factory.Local(resultTemp), _localRewriter.VisitExpression(arm.Value)));
                    catchBody.Add(_factory.Goto(afterArms));
                }

                _localRewriter._sawAwaitInExceptionHandler |= _localRewriter._sawAwait;
                _localRewriter._sawAwait |= origSawAwait;

                _factory.Syntax = node.Syntax;
                if (defaultLabel is not null)
                {
                    // No arm matches: the arm index stays 0 and the filter is false.
                    prologue.Add(_factory.Label(defaultLabel));
                }

                prologue.Add(_factory.Label(afterDecision));

                LabelSymbol unreachable = _factory.GenerateLabel("tryExpressionNoArm");
                catchBody.Insert(0, new BoundSwitchDispatch(node.Syntax, _factory.Local(armTemp), cases.ToImmutableAndFree(), unreachable, lengthBasedStringSwitchDataOpt: null) { WasCompilerGenerated = true });
                // The filter only lets an exception through when an arm matched.
                catchBody.Add(_factory.Label(unreachable));
                catchBody.Add(_factory.Label(afterArms));

                var catchBlock = new BoundCatchBlock(
                    node.Syntax,
                    ImmutableArray.Create(exceptionLocal),
                    _factory.Local(exceptionLocal),
                    exceptionLocal.Type,
                    exceptionFilterPrologueOpt: new BoundStatementList(node.Syntax, prologue.ToImmutableAndFree()) { WasCompilerGenerated = true },
                    exceptionFilterOpt: _factory.IntNotEqual(_factory.Local(armTemp), _factory.Literal(0)),
                    body: _factory.Block(catchBody.ToImmutableAndFree()),
                    isSynthesizedAsyncCatchAll: false);

                var tryStatement = new BoundTryStatement(node.Syntax, tryBlock, ImmutableArray.Create(catchBlock), finallyBlockOpt: null) { WasCompilerGenerated = true };

                outerVariables.Add(resultTemp);
                outerVariables.Add(armTemp);
                outerVariables.AddRange(_tempAllocator.AllTemps());
                return _factory.SpillSequence(outerVariables.ToImmutableAndFree(), ImmutableArray.Create<BoundStatement>(tryStatement), _factory.Local(resultTemp));
            }

            private BoundDecisionDag GetDecisionDagForLowering(BoundConvertedTryExpression node, out LabelSymbol? defaultLabel)
            {
                defaultLabel = node.DefaultLabel;
                BoundDecisionDag decisionDag = node.ReachabilityDecisionDag;
                if (!decisionDag.SuitableForLowering)
                {
                    decisionDag = DecisionDagBuilder.CreateDecisionDagForSwitchExpression(
                        _factory.Compilation,
                        node.Syntax,
                        node.ExceptionPlaceholder,
                        node.Arms,
                        defaultLabel ??= new GeneratedLabelSymbol("unmatched"),
                        BindingDiagnosticBag.Discarded,
                        forLowering: true);
                    Debug.Assert(decisionDag.SuitableForLowering);
                }

                return decisionDag;
            }
        }
    }
}
