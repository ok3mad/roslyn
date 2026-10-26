// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitYieldBreakStatement(BoundYieldBreakStatement node)
        {
            var result = (BoundStatement)base.VisitYieldBreakStatement(node)!;

            // We also add sequence points for the implicit "yield break" statement at the end of the method body
            // (added by FlowAnalysisPass.AppendImplicitReturn). Implicitly added "yield break" for async method
            // does not need sequence points added here since it would be done later (presumably during Async rewrite),
            // except in runtime async where the method body is emitted directly.
            // This will need additional testing when async iterators are emitted with runtime async. https://github.com/dotnet/roslyn/issues/75960
            var currentFunction = _factory.CurrentFunction;
            var isRuntimeAsync = currentFunction is not null && _compilation.IsRuntimeAsyncEnabledIn(currentFunction);
            if (this.Instrument &&
                (!node.WasCompilerGenerated || (node.Syntax.Kind() == SyntaxKind.Block && (currentFunction?.IsAsync == false || isRuntimeAsync))))
            {
                result = Instrumenter.InstrumentYieldBreakStatement(node, result);
            }

            return result;
        }

        public override BoundNode VisitYieldReturnStatement(BoundYieldReturnStatement node)
        {
            var result = (BoundStatement)base.VisitYieldReturnStatement(node)!;
            if (this.Instrument && !node.WasCompilerGenerated)
            {
                result = Instrumenter.InstrumentYieldReturnStatement(node, result);
            }

            return result;
        }

        /// <summary>
        /// Lower `yield return ..xs` to a foreach over xs whose body yields each element:
        /// <![CDATA[
        /// foreach (T e in xs)
        ///     yield return e;
        /// ]]>
        /// The yield returns survive until the iterator state machine is built.
        /// </summary>
        public override BoundNode VisitYieldReturnSpreadStatement(BoundYieldReturnSpreadStatement node)
        {
            // No point in performing this lowering if the node won't be emitted.
            if (node.HasErrors)
            {
                return node;
            }

            var enumeratorInfo = node.EnumeratorInfoOpt;
            var convertedCollection = (BoundConversion)node.Expression;
            Debug.Assert(enumeratorInfo is { });
            Debug.Assert(node.ElementConversion is { Type: { } });

            // The iteration variable already has the iterator element type, so the yield return needs no conversion.
            var iterationVariable = _factory.SynthesizedLocal(node.ElementConversion.Type, node.Syntax);
            var iterationVariables = ImmutableArray.Create(iterationVariable);

            // Not compiler-generated, so it gets a sequence point on the `yield return ..xs;` statement for each element.
            // The generated try/finally (disposing the enumerator) and the yield return both use the `yield return ..xs`
            // syntax, so both of their iterator states belong to that syntax node. Edit and Continue tells them apart by
            // their relative ordinal (IteratorMethodToStateMachineRewriter.PushFrame).
            var body = (BoundStatement)VisitYieldReturnStatement(new BoundYieldReturnStatement(node.Syntax, _factory.Local(iterationVariable)));
            var breakLabel = new GeneratedLabelSymbol("break");
            var continueLabel = new GeneratedLabelSymbol("continue");

            if (convertedCollection.Operand.Type is ArrayTypeSymbol arrayType)
            {
                return arrayType.IsSZArray
                    ? RewriteSingleDimensionalArrayForEachEnumerator(node, convertedCollection.Operand, node.ElementPlaceholder, node.ElementConversion, iterationVariables, deconstruction: null, breakLabel, continueLabel, body)
                    : RewriteMultiDimensionalArrayForEachEnumerator(node, convertedCollection.Operand, node.ElementPlaceholder, node.ElementConversion, iterationVariables, deconstruction: null, breakLabel, continueLabel, body);
            }

            return RewriteForEachEnumerator(node, convertedCollection, enumeratorInfo, node.ElementPlaceholder, node.ElementConversion, iterationVariables, deconstruction: null, breakLabel, continueLabel, body);
        }
    }
}
