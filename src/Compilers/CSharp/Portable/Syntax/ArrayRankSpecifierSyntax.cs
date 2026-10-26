// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    public partial class ArrayRankSpecifierSyntax
    {
        public int Rank
        {
            get
            {
                return this.Sizes.Count;
            }
        }

        /// <summary>
        /// True if this is the <c>[..]</c> of a Sharper sequence type <c>T[..]</c>, which stands for
        /// <c>IEnumerable&lt;T&gt;</c>, and Sharper is enabled: a single size that is a range with no operands. The
        /// <c>..</c> is then part of the type, not an expression. An array creation (<c>new T[..]</c>, <c>stackalloc</c>)
        /// keeps its C# meaning.
        /// </summary>
        // PROTOTYPE(sharper): decide whether array creation should accept sequence elements (`new int[3][..]`).
        internal bool IsSharperSequence =>
            this.Sizes is [RangeExpressionSyntax { LeftOperand: null, RightOperand: null }] &&
            !this.CloseBracketToken.IsMissing &&
            this.Parent is ArrayTypeSyntax { Parent: not (ArrayCreationExpressionSyntax or StackAllocArrayCreationExpressionSyntax) } &&
            this.IsFeatureEnabled(MessageID.IDS_FeatureSharper);
    }
}
