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
        /// annotated, and its state is the left side's. The pipe's result is the stage's.
        /// </summary>
        public override BoundNode? VisitSharperPipe(BoundSharperPipe node)
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

            TypeWithState stageType = VisitRvalueWithState(node.Stage);
            SetResultType(node, stageType);
            return null;
        }
    }
}
