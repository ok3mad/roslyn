// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class DefiniteAssignmentPass
    {
        protected override BoundNode VisitSharperPipeBase(BoundSharperPipeBase node)
        {
            DeclareVariable(node.Local);
            DeclareVariables(node.Locals);
            var result = base.VisitSharperPipeBase(node);
            ReportUnusedVariables(node.Locals);
            return result;
        }

        protected override void VisitSharperPipeLocal(BoundSharperPipeBase node)
        {
            // The stage's 'it' is assigned once, from the left side, and is always considered used.
            int slot = GetOrCreateSlot(node.Local);
            if (slot > 0)
            {
                SetSlotAssigned(slot);
            }

            NoteWrite(node.Local, value: null, read: true, isRef: false);
        }
    }
}
