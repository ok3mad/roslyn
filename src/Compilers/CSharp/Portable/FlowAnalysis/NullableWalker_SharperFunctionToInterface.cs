// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class NullableWalker
    {
        /// <summary>
        /// A lambda or method group converted to a Sharper single-method interface: the delegate creation is analyzed as
        /// for its delegate type (the lambda's body against the interface method's signature), and the result is a new
        /// instance of the generated class, never null.
        /// </summary>
        public override BoundNode? VisitSharperFunctionToInterface(BoundSharperFunctionToInterface node)
        {
            VisitRvalue(node.Function);
            SetResultType(node, TypeWithState.Create(node.Type, NullableFlowState.NotNull));
            return null;
        }
    }
}
