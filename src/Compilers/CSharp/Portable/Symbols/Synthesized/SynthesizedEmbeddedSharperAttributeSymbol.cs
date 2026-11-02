// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Sharper: one of the immutability attributes (<c>[Immutable]</c>, <c>[Phantom]</c>, ...), synthesized for a
    /// compilation that uses it without declaring it, and embedded into the assembly like <c>IsReadOnlyAttribute</c>
    /// (see <see cref="SharperImmutability"/>).
    /// </summary>
    internal sealed class SynthesizedEmbeddedSharperAttributeSymbol : SynthesizedEmbeddedAttributeSymbolBase
    {
        private readonly ImmutableArray<MethodSymbol> _constructors;
        private readonly AttributeTargets _targets;

        public SynthesizedEmbeddedSharperAttributeSymbol(
            string name,
            AttributeTargets targets,
            NamespaceSymbol containingNamespace,
            ModuleSymbol containingModule,
            NamedTypeSymbol systemAttributeType)
            : base(name, containingNamespace, containingModule, baseType: systemAttributeType)
        {
            _targets = targets;
            _constructors = ImmutableArray.Create<MethodSymbol>(
                new SynthesizedEmbeddedAttributeConstructorSymbol(this, m => ImmutableArray<ParameterSymbol>.Empty));
        }

        public override ImmutableArray<MethodSymbol> Constructors => _constructors;

        internal override AttributeUsageInfo GetAttributeUsageInfo() =>
            new AttributeUsageInfo(_targets, allowMultiple: false, inherited: false);
    }
}
