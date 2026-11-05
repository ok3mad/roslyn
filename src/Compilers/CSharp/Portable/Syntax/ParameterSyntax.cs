// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    public partial class ParameterSyntax
    {
        internal bool IsArgList
        {
            get
            {
                return this.Type == null && this.Identifier.ContextualKind() == SyntaxKind.ArgListKeyword;
            }
        }

        /// <summary>
        /// Sharper: the overload without <see cref="Refinement"/>, kept because it is shipped public API. It keeps the
        /// refinement of this node.
        /// </summary>
        public ParameterSyntax Update(SyntaxList<AttributeListSyntax> attributeLists, SyntaxTokenList modifiers, TypeSyntax? type, SyntaxToken identifier, EqualsValueClauseSyntax? @default)
            => this.Update(attributeLists, modifiers, type, identifier, this.Refinement, @default);

        private partial void Validate()
        {
            if (Type is null && Identifier.IsKind(SyntaxKind.None))
            {
                throw new System.ArgumentException(CSharpResources.ParameterRequiresTypeOrIdentifier);
            }
        }
    }
}
