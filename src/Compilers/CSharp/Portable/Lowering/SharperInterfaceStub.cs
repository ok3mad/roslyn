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
    /// The class generated for a Sharper interface initializer (#30), <c>new IProfiles { Load = id =&gt; ..., Plan = "Pro",
    /// _ = Unexpected }</c>: a private sealed class nested in the type being compiled, generic over the interface
    /// definition's type parameters, with a field per assigned member, in source order, filled by its constructor.
    /// <code>
    /// private sealed class &lt;IProfiles&gt;Stub : IProfiles
    /// {
    ///     private readonly Func&lt;int, Profile&gt; Load;
    ///     private readonly string Plan;
    ///     public &lt;IProfiles&gt;Stub(Func&lt;int, Profile&gt; Load, string Plan) { ... }
    ///     Profile IProfiles.Load(int id) => Load(id);
    ///     string IProfiles.get_Plan() => Plan;
    ///     void IProfiles.Save(Profile p) => throw new InvalidOperationException("'IProfiles.Save(Profile)' is not assigned ...");
    /// }
    /// </code>
    /// A method is implemented by invoking its delegate, a property's getter by returning its value. With
    /// <c>_ = Unexpected</c>, each unassigned member without a default implementation throws; the members with a default
    /// keep it. One class per interface definition, list of assigned members and <c>_ = Unexpected</c> in the type.
    /// </summary>
    internal sealed class SharperInterfaceStubTypeSymbol : SynthesizedContainer
    {
        private readonly NamedTypeSymbol _containingType;
        private readonly NamedTypeSymbol _interfaceDefinition;
        private readonly ImmutableArray<Symbol> _assignedOverDefinition;
        private readonly bool _throwsForUnassigned;
        private readonly ImmutableArray<NamedTypeSymbol> _interfaces;
        private readonly ImmutableArray<SynthesizedFieldSymbol> _fields;
        private readonly SharperInterfaceStubConstructor _constructor;

        /// <summary>The implementations, each with the index of the field it uses, or -1 when it throws.</summary>
        private readonly ImmutableArray<(SharperInterfaceAdapterMethod Method, int Field, Symbol MemberOverDefinition)> _methods;

        private SharperInterfaceStubTypeSymbol(
            NamedTypeSymbol containingType,
            string name,
            NamedTypeSymbol interfaceDefinition,
            ImmutableArray<Symbol> assignedOverDefinition,
            ImmutableArray<TypeSymbol> fieldTypesOverDefinition,
            bool throwsForUnassigned)
            : base(name, interfaceDefinition.GetAllTypeParameters())
        {
            _containingType = containingType;
            _interfaceDefinition = interfaceDefinition;
            _assignedOverDefinition = assignedOverDefinition;
            _throwsForUnassigned = throwsForUnassigned;
            _interfaces = ImmutableArray.Create(TypeMap.SubstituteNamedType(interfaceDefinition));
            _fields = assignedOverDefinition.ZipAsArray(
                fieldTypesOverDefinition,
                (member, type) => new SynthesizedFieldSymbol(this, TypeMap.SubstituteType(type).Type, member.Name, isReadOnly: true));
            _constructor = new SharperInterfaceStubConstructor(this, _fields);

            var methods = ArrayBuilder<(SharperInterfaceAdapterMethod, int, Symbol)>.GetInstance();
            var implementable = ArrayBuilder<(Symbol Member, bool IsAbstract)>.GetInstance();
            Binder.GetSharperImplementableMembers(interfaceDefinition, implementable);
            foreach (var (memberOverDefinition, isAbstract) in implementable)
            {
                int field = assignedOverDefinition.IndexOf(memberOverDefinition, ReferenceEqualityComparer.Instance);
                if (field < 0 && !isAbstract)
                {
                    // The interface's default implementation stays.
                    continue;
                }

                Debug.Assert(field >= 0 || throwsForUnassigned);
                var member = memberOverDefinition.OriginalDefinition.SymbolAsMember(TypeMap.SubstituteNamedType(memberOverDefinition.ContainingType));
                switch (member)
                {
                    case MethodSymbol method:
                        methods.Add((new SharperInterfaceAdapterMethod(method, this), field, memberOverDefinition));
                        break;

                    case PropertySymbol property:
                        if (property.GetMethod is { } getter)
                        {
                            methods.Add((new SharperInterfaceAdapterMethod(getter, this), field, memberOverDefinition));
                        }

                        if (property.SetMethod is { } setter)
                        {
                            Debug.Assert(field < 0);
                            methods.Add((new SharperInterfaceAdapterMethod(setter, this), -1, memberOverDefinition));
                        }
                        break;

                    case EventSymbol @event:
                        Debug.Assert(field < 0);
                        foreach (var accessor in new[] { @event.AddMethod, @event.RemoveMethod })
                        {
                            if (accessor is not null)
                            {
                                methods.Add((new SharperInterfaceAdapterMethod(accessor, this), -1, memberOverDefinition));
                            }
                        }
                        break;

                    default:
                        throw ExceptionUtilities.UnexpectedValue(member);
                }
            }

            implementable.Free();
            _methods = methods.ToImmutableAndFree();
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
        {
            var builder = ArrayBuilder<Symbol>.GetInstance();
            builder.AddRange(_fields);
            builder.Add(_constructor);
            foreach (var (method, _, _) in _methods)
            {
                builder.Add(method);
            }

            return builder.ToImmutableAndFree();
        }

        public override ImmutableArray<Symbol> GetMembers(string name)
            => GetMembers().WhereAsArray(static (m, name) => m.Name == name, name);

        public override IEnumerable<string> MemberNames => GetMembers().Select(static m => m.Name);

        /// <summary>
        /// The generated class for the initializer in the type being compiled, made on first use with its bodies.
        /// </summary>
        internal static SharperInterfaceStubTypeSymbol GetOrCreate(
            TypeCompilationState compilationState,
            BoundSharperInterfaceCreation node,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(compilationState.ModuleBuilderOpt is not null);

            var interfaceDefinition = ((NamedTypeSymbol)node.Type).OriginalDefinition;
            var assignedOverDefinition = node.Members.SelectAsArray(static (m, d) => Binder.GetSharperMemberOverDefinition(d, m), interfaceDefinition);

            var stubs = compilationState.SharperInterfaceStubs ??= new List<SharperInterfaceStubTypeSymbol>();
            foreach (var existing in stubs)
            {
                if (ReferenceEquals(existing._interfaceDefinition, interfaceDefinition) &&
                    existing._throwsForUnassigned == node.ThrowsForUnassigned &&
                    sameMembers(existing._assignedOverDefinition, assignedOverDefinition))
                {
                    return existing;
                }
            }

            // '<IProfiles>Stub', with an ordinal for the other classes of interfaces of the same name in the type.
            // PROTOTYPE(sharper): Edit and Continue does not map these classes between generations yet.
            int sameName = stubs.Count(s => s._interfaceDefinition.Name == interfaceDefinition.Name);
            string name = "<" + interfaceDefinition.Name + ">Stub" + (sameName == 0 ? "" : "_" + sameName);

            var containingType = compilationState.Type;
            var stub = new SharperInterfaceStubTypeSymbol(containingType, name, interfaceDefinition, assignedOverDefinition, node.FieldTypesOverDefinition, node.ThrowsForUnassigned);
            stubs.Add(stub);
            compilationState.ModuleBuilderOpt.AddSynthesizedDefinition(containingType, stub.GetCciAdapter());

            var syntax = node.Syntax;
            compilationState.AddSynthesizedMethod(stub._constructor, stub.MakeConstructorBody(syntax, compilationState, diagnostics));
            foreach (var (method, field, memberOverDefinition) in stub._methods)
            {
                compilationState.AddSynthesizedMethod(method, stub.MakeMethodBody(method, field, memberOverDefinition, syntax, compilationState, diagnostics));
            }

            return stub;

            static bool sameMembers(ImmutableArray<Symbol> left, ImmutableArray<Symbol> right)
            {
                if (left.Length != right.Length)
                {
                    return false;
                }

                for (int i = 0; i < left.Length; i++)
                {
                    if (!ReferenceEquals(left[i], right[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private BoundStatement MakeConstructorBody(SyntaxNode syntax, TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            var f = new SyntheticBoundNodeFactory(_constructor, syntax, compilationState, diagnostics);
            f.CurrentFunction = _constructor;
            var objectConstructor = BaseTypeNoUseSiteDiagnostics.InstanceConstructors.Single(c => c.ParameterCount == 0);

            // object..ctor(); this.Load = Load; ...; return;
            var statements = ArrayBuilder<BoundStatement>.GetInstance();
            statements.Add(f.ExpressionStatement(f.Call(f.This(), objectConstructor)));
            for (int i = 0; i < _fields.Length; i++)
            {
                statements.Add(f.Assignment(f.Field(f.This(), _fields[i]), f.Parameter(_constructor.Parameters[i])));
            }

            statements.Add(f.Return());
            return f.Block(statements.ToImmutableAndFree());
        }

        private BoundStatement MakeMethodBody(SharperInterfaceAdapterMethod method, int field, Symbol memberOverDefinition, SyntaxNode syntax, TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            var f = new SyntheticBoundNodeFactory(method, syntax, compilationState, diagnostics);
            f.CurrentFunction = method;

            if (field < 0)
            {
                // throw new InvalidOperationException("'IProfiles.Save(Profile)' is not assigned in the interface initializer ...");
                string message = string.Format(
                    "'{0}' is not assigned in the interface initializer that created this object ('_ = Unexpected').",
                    memberOverDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                var exceptionType = f.WellKnownType(WellKnownType.System_InvalidOperationException);
                var constructor = exceptionType.InstanceConstructors.FirstOrDefault(static c => c is { ParameterCount: 1 } && c.Parameters[0].Type.SpecialType == SpecialType.System_String);
                BoundExpression exception = constructor is not null
                    ? f.New(constructor, f.StringLiteral(message))
                    : f.New(f.WellKnownMethod(WellKnownMember.System_InvalidOperationException__ctor));
                return f.Block(f.Throw(exception));
            }

            var fieldAccess = f.Field(f.This(), _fields[field]);
            if (memberOverDefinition is PropertySymbol)
            {
                // return this.Plan;
                return f.Block(f.Return(fieldAccess));
            }

            // return this.Load.Invoke(p0, ref p1, ...);
            var invoke = ((NamedTypeSymbol)_fields[field].Type).DelegateInvokeMethod;
            Debug.Assert(invoke is not null && invoke.ParameterCount == method.ParameterCount);
            var arguments = method.Parameters.SelectAsArray(static (p, f) => (BoundExpression)f.Parameter(p), f);
            var refKinds = invoke.Parameters.SelectAsArray(static p => SyntheticBoundNodeFactory.ArgumentRefKindFromParameterRefKind(p.RefKind, useStrictArgumentRefKinds: false));
            BoundExpression call = f.Call(fieldAccess, invoke, refKinds.Any(static r => r != RefKind.None) ? refKinds : default, arguments);

            return method.ReturnsVoid
                ? f.Block(f.ExpressionStatement(call), f.Return())
                : f.Block(new BoundReturnStatement(syntax, method.RefKind, call, @checked: false) { WasCompilerGenerated = true });
        }
    }

    /// <summary>
    /// The constructor of a <see cref="SharperInterfaceStubTypeSymbol"/>, which takes the assigned values in source order.
    /// </summary>
    internal sealed class SharperInterfaceStubConstructor : SynthesizedInstanceConstructor
    {
        internal SharperInterfaceStubConstructor(SharperInterfaceStubTypeSymbol containingType, ImmutableArray<SynthesizedFieldSymbol> fields)
            : base(containingType)
        {
            Parameters = fields.SelectAsArray(
                static (field, ordinal, constructor) => (ParameterSymbol)SynthesizedParameterSymbol.Create(constructor, TypeWithAnnotations.Create(field.Type), ordinal, RefKind.None, field.Name),
                this);
        }

        public override ImmutableArray<ParameterSymbol> Parameters { get; }

        public override Accessibility DeclaredAccessibility => Accessibility.Public;
    }
}
