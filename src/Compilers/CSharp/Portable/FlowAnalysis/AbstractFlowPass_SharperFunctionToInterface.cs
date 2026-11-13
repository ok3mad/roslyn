// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

namespace Microsoft.CodeAnalysis.CSharp
{
    internal abstract partial class AbstractFlowPass<TLocalState, TLocalFunctionState>
    {
        /// <summary>
        /// A lambda or method group converted to a Sharper single-method interface is its delegate creation.
        /// </summary>
        public override BoundNode VisitSharperFunctionToInterface(BoundSharperFunctionToInterface node)
        {
            VisitRvalue(node.Function);
            return null;
        }

        /// <summary>
        /// A Sharper interface initializer evaluates its values in order (#30).
        /// </summary>
        public override BoundNode VisitSharperInterfaceCreation(BoundSharperInterfaceCreation node)
        {
            foreach (var value in node.Values)
            {
                VisitRvalue(value);
            }

            return null;
        }
    }
}
