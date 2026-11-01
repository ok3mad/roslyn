// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class LocalRewriter
    {
        /// <summary>
        /// Lowers <c>x |&gt; stage</c> to <c>it = x;</c> before the enclosing statement and the stage in its place, where a
        /// bare stage <c>F</c> is the call <c>F(it)</c>. So the left side is evaluated once, before anything in the stage,
        /// including the receiver of a bare stage (<c>x |&gt; o.M</c>). <c>it</c> and the stage's variables are user
        /// variables, which codegen keeps in statements, not in an expression's sequence, so the pipe is a spill sequence,
        /// as a switch expression is: the spiller moves it to the enclosing statement list, and earlier operands of the
        /// enclosing expression to temps, which keeps the order of evaluation.
        /// </summary>
        public override BoundNode VisitSharperPipe(BoundSharperPipe node)
        {
            Debug.Assert(node.Type is not null);
            // PROTOTYPE(sharper): no sequence points in a stage yet, so the debugger steps over a pipe as one expression (#158).
            BoundExpression left = VisitExpression(node.Left);
            BoundExpression stage = VisitExpression(node.Stage);
            var local = new BoundLocal(node.Syntax, node.Local, constantValueOpt: null, node.Local.Type) { WasCompilerGenerated = true };
            var assignment = new BoundAssignmentOperator(node.Syntax, local, left, isRef: false, local.Type) { WasCompilerGenerated = true };
            this._needsSpilling = true;
            return new BoundSpillSequence(
                node.Syntax,
                node.Locals.Insert(0, node.Local),
                ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(node.Syntax, assignment) { WasCompilerGenerated = true }),
                stage,
                node.Type);
        }
    }
}
