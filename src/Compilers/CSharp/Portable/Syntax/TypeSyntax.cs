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

        /// <summary>
        /// True if this is the identifier <c>infer</c> and Sharper is enabled, so that it may stand for the Sharper
        /// <c>infer</c> keyword: a return type inferred from the body. Like <see cref="IsVar"/>, it still binds to a
        /// type named <c>infer</c> when one is in scope.
        /// </summary>
        internal bool IsInfer => ((InternalSyntax.TypeSyntax)this.Green).IsInfer && this.IsFeatureEnabled(MessageID.IDS_FeatureSharper);

        /// <summary>
        /// True if this is the identifier <c>val</c> as the type of a parameter that has an <c>infer</c> form: a method,
        /// local function, constructor, operator, indexer or delegate parameter, where <c>val x</c> is
        /// <c>readonly infer x</c> (#178). Lambdas have their own <c>(val x)</c> form, and anonymous methods and
        /// extension block receivers keep <c>val</c> a type name. Syntax only: like <see cref="IsInfer"/>, it still binds
        /// to a type named <c>val</c> when one is in scope.
        /// </summary>
        internal bool IsSharperValParameterType
            => IsVal &&
                Parent is ParameterSyntax { Parent: BaseParameterListSyntax { Parent: not (ExtensionBlockDeclarationSyntax or ParenthesizedLambdaExpressionSyntax or AnonymousMethodExpressionSyntax) } } parameter &&
                parameter.Type == this;

        /// <summary>
        /// True if this may stand for an inferred parameter type: <see cref="IsInfer"/>, or <c>val</c> where it is
        /// <c>readonly infer</c> (<see cref="IsSharperValParameterType"/>).
        /// </summary>
        internal bool IsInferOrValParameterType => IsInfer || IsSharperValParameterType;

        public bool IsUnmanaged => ((InternalSyntax.TypeSyntax)this.Green).IsUnmanaged;

        public bool IsNotNull => ((InternalSyntax.TypeSyntax)this.Green).IsNotNull;

        public bool IsNint => ((InternalSyntax.TypeSyntax)this.Green).IsNint;

        public bool IsNuint => ((InternalSyntax.TypeSyntax)this.Green).IsNuint;
    }
}
