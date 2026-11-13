// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        /// <summary>
        /// A lambda or method group converted to a Sharper single-method interface lowers to
        /// <c>new &lt;IProfiles&gt;Lambda(delegate)</c>, the generated class nested in the type being compiled
        /// (<see cref="SharperInterfaceAdapterTypeSymbol"/>). The delegate creation lowers as usual, so a lambda that
        /// captures nothing keeps its cached delegate.
        /// </summary>
        public override BoundNode VisitSharperFunctionToInterface(BoundSharperFunctionToInterface node)
        {
            var function = VisitExpression(node.Function);
            var adapter = SharperInterfaceAdapterTypeSymbol.GetOrCreate(_factory.CompilationState, node, _diagnostics);

            var interfaceType = (NamedTypeSymbol)node.Type;
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            NamedTypeSymbol constructedAdapter = adapter.Arity == 0
                ? adapter
                : adapter.Construct(interfaceType.GetAllTypeArguments(ref discardedUseSiteInfo));

            var syntax = _factory.Syntax;
            _factory.Syntax = node.Syntax;
            var creation = _factory.New(adapter.Constructor.AsMember(constructedAdapter), function);
            var result = _factory.Convert(interfaceType, creation, Conversion.ImplicitReference);
            _factory.Syntax = syntax;
            return result;
        }

        /// <summary>
        /// A Sharper interface initializer lowers to <c>new &lt;IProfiles&gt;Stub(value0, value1, ...)</c>, the generated
        /// class nested in the type being compiled (<see cref="SharperInterfaceStubTypeSymbol"/>), with the values in source
        /// order.
        /// </summary>
        public override BoundNode VisitSharperInterfaceCreation(BoundSharperInterfaceCreation node)
        {
            var values = VisitList(node.Values);
            var stub = SharperInterfaceStubTypeSymbol.GetOrCreate(_factory.CompilationState, node, _diagnostics);

            var interfaceType = (NamedTypeSymbol)node.Type;
            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            NamedTypeSymbol constructedStub = stub.Arity == 0
                ? stub
                : stub.Construct(interfaceType.GetAllTypeArguments(ref discardedUseSiteInfo));

            var syntax = _factory.Syntax;
            _factory.Syntax = node.Syntax;
            var creation = _factory.New(stub.Constructor.AsMember(constructedStub), values);
            var result = _factory.Convert(interfaceType, creation, Conversion.ImplicitReference);
            _factory.Syntax = syntax;
            return result;
        }
    }
}
