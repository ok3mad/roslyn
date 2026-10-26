// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    public abstract partial class TypeSyntax
    {
        public bool IsVar => ((InternalSyntax.TypeSyntax)this.Green).IsVar;

        /// <summary>
        /// True if this is the identifier <c>val</c> and Sharper is enabled, so that it may stand for the Sharper
        /// <c>val</c> keyword: an implicitly typed local that cannot be reassigned. Like <see cref="IsVar"/>, it
        /// still binds to a type named <c>val</c> when one is in scope.
        /// </summary>
        internal bool IsVal => ((InternalSyntax.TypeSyntax)this.Green).IsVal && this.IsFeatureEnabled(MessageID.IDS_FeatureSharper);

        /// <summary>
        /// True if this may stand for an implicitly typed local: <see cref="IsVar"/> or <see cref="IsVal"/>.
        /// </summary>
        internal bool IsVarOrVal => IsVar || IsVal;

        public bool IsUnmanaged => ((InternalSyntax.TypeSyntax)this.Green).IsUnmanaged;

        public bool IsNotNull => ((InternalSyntax.TypeSyntax)this.Green).IsNotNull;

        public bool IsNint => ((InternalSyntax.TypeSyntax)this.Green).IsNint;

        public bool IsNuint => ((InternalSyntax.TypeSyntax)this.Green).IsNuint;
    }
}
