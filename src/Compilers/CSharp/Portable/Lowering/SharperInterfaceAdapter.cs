// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// The class generated for a Sharper lambda or method group converted to a single-method interface (#29): a private
    /// sealed class nested in the type being compiled, generic over the interface definition's type parameters (its own and
    /// its containing types'), that stores a delegate and implements the interface's method by invoking it.
    /// <code>
    /// private sealed class &lt;IProfiles&gt;Lambda : IProfiles
    /// {
    ///     private readonly Func&lt;int, Profile&gt; function;
    ///     public &lt;IProfiles&gt;Lambda(Func&lt;int, Profile&gt; function) { this.function = function; }
    ///     Profile IProfiles.Load(int id) => function(id);   // emitted with the name 'Load', for stack traces
    /// }
    /// </code>
    /// One class per interface definition and type being compiled; every lambda of that interface in the type shares it.
    /// Nesting it gives it access to private and protected interfaces and to the types in their signatures, which a
    /// top-level class would not have.
    /// </summary>
    internal sealed class SharperInterfaceAdapterTypeSymbol : SynthesizedContainer
    {
        private readonly NamedTypeSymbol _containingType;
        private readonly ImmutableArray<NamedTypeSymbol> _interfaces;
        private readonly SynthesizedFieldSymbol _function;
        private readonly SharperInterfaceAdapterConstructor _constructor;
        private readonly SharperInterfaceAdapterMethod _method;

        private SharperInterfaceAdapterTypeSymbol(
            NamedTypeSymbol containingType,
            string name,
            NamedTypeSymbol interfaceDefinition,
            MethodSymbol methodOverDefinition,
            NamedTypeSymbol delegateTypeOverDefinition)
            : base(name, interfaceDefinition.GetAllTypeParameters())
        {
            _containingType = containingType;
            _interfaces = ImmutableArray.Create(TypeMap.SubstituteNamedType(interfaceDefinition));
            _function = new SynthesizedFieldSymbol(this, TypeMap.SubstituteNamedType(delegateTypeOverDefinition), "function", isReadOnly: true);
            _constructor = new SharperInterfaceAdapterConstructor(this, _function.Type);

            var interfaceMethod = methodOverDefinition.OriginalDefinition.AsMember(TypeMap.SubstituteNamedType(methodOverDefinition.ContainingType));
            _method = new SharperInterfaceAdapterMethod(interfaceMethod, this);
        }

        public override TypeKind TypeKind => TypeKind.Class;

        public override Symbol ContainingSymbol => _containingType;

        internal override MethodSymbol Constructor => _constructor;

        public override bool AreLocalsZeroed => true;

        internal override bool IsRecord => false;

        internal override bool IsRecordStruct => false;

        internal override bool HasPossibleWellKnownCloneMethod() => false;

        internal override ImmutableArray<NamedTypeSymbol> InterfacesNoUseSiteDiagnostics(ConsList<TypeSymbol>? basesBeingResolved) => _interfaces;

        public override ImmutableArray<Symbol> GetMembers()
            => ImmutableArray.Create<Symbol>(_function, _constructor, _method);

        public override ImmutableArray<Symbol> GetMembers(string name)
            => GetMembers().WhereAsArray(static (m, name) => m.Name == name, name);

        public override IEnumerable<string> MemberNames => GetMembers().Select(static m => m.Name);

        /// <summary>
        /// The generated class for the interface in the type being compiled, made on first use with its constructor's and
        /// method's bodies.
        /// </summary>
        internal static SharperInterfaceAdapterTypeSymbol GetOrCreate(
            TypeCompilationState compilationState,
            BoundSharperFunctionToInterface node,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(compilationState.ModuleBuilderOpt is not null);

            var interfaceDefinition = ((NamedTypeSymbol)node.Type).OriginalDefinition;
            var adapters = compilationState.SharperInterfaceAdapters ??= new Dictionary<NamedTypeSymbol, SharperInterfaceAdapterTypeSymbol>();
            if (adapters.TryGetValue(interfaceDefinition, out var adapter))
            {
                return adapter;
            }

            var methodOverDefinition = Binder.GetSharperSingleAbstractMethod(interfaceDefinition);
            Debug.Assert(methodOverDefinition is not null);

            // '<IProfiles>Lambda', with an ordinal when two interfaces of the same name are used in one type.
            // PROTOTYPE(sharper): Edit and Continue does not map these classes between generations yet.
            int sameName = adapters.Keys.Count(k => k.Name == interfaceDefinition.Name);
            string name = "<" + interfaceDefinition.Name + ">Lambda" + (sameName == 0 ? "" : "_" + sameName);

            var containingType = compilationState.Type;
            adapter = new SharperInterfaceAdapterTypeSymbol(containingType, name, interfaceDefinition, methodOverDefinition, (NamedTypeSymbol)node.DelegateTypeOverDefinition);
            adapters.Add(interfaceDefinition, adapter);
            compilationState.ModuleBuilderOpt.AddSynthesizedDefinition(containingType, adapter.GetCciAdapter());

            var syntax = node.Syntax;
            compilationState.AddSynthesizedMethod(adapter._constructor, adapter.MakeConstructorBody(syntax, compilationState, diagnostics));
            compilationState.AddSynthesizedMethod(adapter._method, adapter.MakeMethodBody(syntax, compilationState, diagnostics));
            return adapter;
        }

        private BoundStatement MakeConstructorBody(SyntaxNode syntax, TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            var f = new SyntheticBoundNodeFactory(_constructor, syntax, compilationState, diagnostics);
            f.CurrentFunction = _constructor;
            var objectConstructor = BaseTypeNoUseSiteDiagnostics.InstanceConstructors.Single(c => c.ParameterCount == 0);

            // object..ctor(); this.function = function; return;
            return f.Block(
                f.ExpressionStatement(f.Call(f.This(), objectConstructor)),
                f.Assignment(f.Field(f.This(), _function), f.Parameter(_constructor.Parameters[0])),
                f.Return());
        }

        private BoundStatement MakeMethodBody(SyntaxNode syntax, TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            var f = new SyntheticBoundNodeFactory(_method, syntax, compilationState, diagnostics);
            f.CurrentFunction = _method;
            var invoke = ((NamedTypeSymbol)_function.Type).DelegateInvokeMethod;
            Debug.Assert(invoke is not null && invoke.ParameterCount == _method.ParameterCount);

            // this.function.Invoke(p0, ref p1, ...), with each parameter passed as the delegate takes it.
            var arguments = _method.Parameters.SelectAsArray(static (p, f) => (BoundExpression)f.Parameter(p), f);
            var refKinds = invoke.Parameters.SelectAsArray(static p => SyntheticBoundNodeFactory.ArgumentRefKindFromParameterRefKind(p.RefKind, useStrictArgumentRefKinds: false));
            BoundExpression call = f.Call(f.Field(f.This(), _function), invoke, refKinds.Any(static r => r != RefKind.None) ? refKinds : default, arguments);

            return _method.ReturnsVoid
                ? f.Block(f.ExpressionStatement(call), f.Return())
                : f.Block(new BoundReturnStatement(syntax, _method.RefKind, call, @checked: false) { WasCompilerGenerated = true });
        }
    }

    /// <summary>
    /// The constructor of a <see cref="SharperInterfaceAdapterTypeSymbol"/>, which takes the delegate.
    /// </summary>
    internal sealed class SharperInterfaceAdapterConstructor : SynthesizedInstanceConstructor
    {
        internal SharperInterfaceAdapterConstructor(SharperInterfaceAdapterTypeSymbol containingType, TypeSymbol delegateType)
            : base(containingType)
        {
            Parameters = ImmutableArray.Create<ParameterSymbol>(
                SynthesizedParameterSymbol.Create(this, TypeWithAnnotations.Create(delegateType), ordinal: 0, RefKind.None, "function"));
        }

        public override ImmutableArray<ParameterSymbol> Parameters { get; }

        public override Accessibility DeclaredAccessibility => Accessibility.Public;
    }

    /// <summary>
    /// The interface method of a <see cref="SharperInterfaceAdapterTypeSymbol"/>: an explicit implementation emitted with
    /// the method's own name, so that a stack trace reads <c>&lt;IProfiles&gt;Lambda.Load(Int32 id)</c>.
    /// </summary>
    internal sealed class SharperInterfaceAdapterMethod : SynthesizedImplementationMethod
    {
        internal SharperInterfaceAdapterMethod(MethodSymbol interfaceMethod, SynthesizedContainer containingType)
            : base(interfaceMethod, containingType, name: interfaceMethod.Name, generateDebugInfo: false)
        {
        }
    }
}
