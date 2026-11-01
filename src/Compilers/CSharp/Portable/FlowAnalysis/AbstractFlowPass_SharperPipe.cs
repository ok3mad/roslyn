// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

namespace Microsoft.CodeAnalysis.CSharp
{
    internal abstract partial class AbstractFlowPass<TLocalState, TLocalFunctionState>
    {
        /// <summary>
        /// A Sharper pipe evaluates its left side, assigns it to the stage's <c>it</c>, then evaluates the stage.
        /// </summary>
        public override BoundNode VisitSharperPipe(BoundSharperPipe node)
        {
            VisitRvalue(node.Left);
            VisitSharperPipeLocal(node);
            VisitRvalue(node.Stage);
            return null;
        }

        /// <summary>
        /// Called after the left side of a pipe is visited, when its value is assigned to the stage's <c>it</c>.
        /// </summary>
        protected virtual void VisitSharperPipeLocal(BoundSharperPipe node)
        {
        }
    }
}
