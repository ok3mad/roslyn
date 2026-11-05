// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class BoundSharperOpaquePattern
    {
        private partial void Validate()
        {
            Debug.Assert(HasErrors || NarrowedType.Equals(Value.Type, TypeCompareKind.AllIgnoreOptions));
            Debug.Assert(Value.ConstantValueOpt is null);
        }
    }
}
