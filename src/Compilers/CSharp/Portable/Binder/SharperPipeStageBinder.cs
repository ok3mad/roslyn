// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// The scope of a Sharper pipe stage (<c>x |&gt; stage</c>, #25): it declares the stage's <c>it</c>, and maps the
    /// binders of the scopes inside the stage, which the enclosing member's map leaves out (<see
    /// cref="LocalBinderFactory"/> and <see cref="ExpressionVariableFinder"/> do not enter stages). The local's type is
    /// the type of the piped value, known only when the pipe binds, so each binding of a pipe makes its own stage binder,
    /// as each binding of a lambda makes its own body binder. Unlike an <see cref="ExecutableCodeBinder"/>, it keeps the
    /// enclosing flags, such as <see cref="BinderFlags.InCatchFilter"/>.
    /// </summary>
    internal sealed class SharperPipeStageBinder : LocalScopeBinder
    {
        private readonly ExpressionSyntax _stage;
        private readonly ImmutableArray<LocalSymbol> _locals;
        private SmallDictionary<SyntaxNode, Binder>? _lazyBinderMap;

        /// <param name="stage">The stage, or the body of a lambda stage (<c>x |&gt; (y =&gt; body)</c>, #186).</param>
        /// <param name="locals">The stage's <c>it</c> or the lambda stage's named value, if the stage declares one.</param>
        internal SharperPipeStageBinder(Binder next, ExpressionSyntax stage, ImmutableArray<LocalSymbol> locals)
            : base(next)
        {
            _stage = stage;
            _locals = locals;
        }

        protected override ImmutableArray<LocalSymbol> BuildLocals() => _locals;

        internal override Binder? GetBinder(SyntaxNode node)
        {
            if (_lazyBinderMap is null)
            {
                Interlocked.CompareExchange(ref _lazyBinderMap, LocalBinderFactory.BuildMap(ContainingMemberOrLambda, _stage, this), null);
            }

            return _lazyBinderMap.TryGetValue(node, out var binder) ? binder : Next!.GetBinder(node);
        }
    }
}
