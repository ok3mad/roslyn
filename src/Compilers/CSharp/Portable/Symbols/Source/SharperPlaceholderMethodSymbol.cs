// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// A stand-in for a method, constructor or operator whose <c>infer</c> parameter types are inferred from the uses in
    /// its body (Sharper step P4, <see cref="SharperInferredParameterType.GetBodyInference"/>). The body is bound once
    /// with this symbol as its containing member, before the method's signature exists: its parameters have the types
    /// the signature will have where those are known, and a placeholder type for the parameters being inferred.
    /// </summary>
    /// <remarks>
    /// Like the <see cref="LambdaSymbol"/> of each delegate type a lambda is bound for, the symbol is made for one binding
    /// and dropped with it. It is not a member of its type. Its return type is the written one, or, for <c>infer</c>,
    /// being inferred, as a lambda's is (<see cref="LambdaSymbol.ReturnTypeIsBeingInferred"/>,
    /// <see cref="Binder.IsSharperReturnTypeBeingInferred"/>), so the binding of <c>return</c>, <c>yield</c> and
    /// <c>async</c> never reads the method's own signature.
    /// </remarks>
    internal sealed class SharperPlaceholderMethodSymbol : SourceMemberMethodSymbol
    {
        private readonly SourceMemberMethodSymbol _original;
        private readonly ImmutableArray<ParameterSymbol> _parameters;
        private readonly TypeWithAnnotations _returnType;
        private readonly object _methodChecksLock = new object();

        /// <param name="returnType">The written return type, or default when it is <c>infer</c>.</param>
        internal SharperPlaceholderMethodSymbol(
            SourceMemberMethodSymbol original,
            ImmutableArray<(TypeWithAnnotations Type, RefKind RefKind, string Name)> parameters,
            TypeWithAnnotations returnType)
            : base(original.ContainingType, original.SyntaxRef, original.GetFirstLocation(), original.IsIterator, GetModifiersAndFlags(original))
        {
            _original = original;
            _returnType = returnType;
            var builder = ArrayBuilder<ParameterSymbol>.GetInstance(parameters.Length);
            for (int i = 0; i < parameters.Length; i++)
            {
                builder.Add(SynthesizedParameterSymbol.Create(this, parameters[i].Type, i, parameters[i].RefKind, parameters[i].Name));
            }

            _parameters = builder.ToImmutableAndFree();
        }

        /// <summary>The method this symbol stands for.</summary>
        internal SourceMemberMethodSymbol Original => _original;

        /// <summary>
        /// Replaces the original method among the candidates of a call in the body with this placeholder. A recursive call,
        /// or a call to an overload of the method, then reads the placeholder's parameters instead of the signature that
        /// is being inferred, which would be a cycle. The placeholder's parameter types pin nothing.
        /// </summary>
        internal void ReplaceOriginal(ArrayBuilder<MethodSymbol> candidates)
        {
            int index = candidates.IndexOf(_original);
            if (index >= 0)
            {
                candidates[index] = this;
            }
        }

        /// <inheritdoc cref="ReplaceOriginal(ArrayBuilder{MethodSymbol})"/>
        internal ImmutableArray<MethodSymbol> ReplaceOriginal(ImmutableArray<MethodSymbol> candidates)
        {
            int index = candidates.IndexOf(_original);
            return index >= 0 ? candidates.SetItem(index, this) : candidates;
        }

        public override string Name => _original.Name;

        public override ImmutableArray<ParameterSymbol> Parameters => _parameters;

        internal override int ParameterCount => _parameters.Length;

        /// <summary>
        /// True when the method's return type is <c>infer</c>: the body is then bound as a lambda's whose return type is
        /// being inferred (<see cref="Binder.IsSharperReturnTypeBeingInferred"/>).
        /// </summary>
        internal bool IsReturnTypeInferred => !_returnType.HasType;

        public override TypeWithAnnotations ReturnTypeWithAnnotations => _returnType.HasType ? _returnType : TypeWithAnnotations.Create(LambdaSymbol.ReturnTypeIsBeingInferred);

        public override bool ReturnsVoid => _returnType.HasType && _returnType.IsVoidType();

        public override ImmutableArray<CustomModifier> RefCustomModifiers => ImmutableArray<CustomModifier>.Empty;

        public override FlowAnalysisAnnotations ReturnTypeFlowAnalysisAnnotations => FlowAnalysisAnnotations.None;

        public override ImmutableHashSet<string> ReturnNotNullIfParameterNotNull => ImmutableHashSet<string>.Empty;

        public override FlowAnalysisAnnotations FlowAnalysisAnnotations => FlowAnalysisAnnotations.None;

        public override ImmutableArray<TypeParameterSymbol> TypeParameters => _original.TypeParameters;

        public override ImmutableArray<ImmutableArray<TypeWithAnnotations>> GetTypeParameterConstraintTypes() => _original.GetTypeParameterConstraintTypes();

        public override ImmutableArray<TypeParameterConstraintKind> GetTypeParameterConstraintKinds() => _original.GetTypeParameterConstraintKinds();

        internal override bool HasUnsafeModifier => _original.HasUnsafeModifier;

        internal override bool HasSafeModifier => _original.HasSafeModifier;

        internal override bool CanRequireUnsafe => _original.CanRequireUnsafe;

        public override bool IsImplicitlyDeclared => false;

        internal override bool GenerateDebugInfo => false;

        protected override object MethodChecksLockObject => _methodChecksLock;

        protected override void MethodChecks(BindingDiagnosticBag diagnostics)
        {
        }

        /// <summary>
        /// The placeholder reports nothing: it is not a member of its type, and whatever its binding finds, such as its
        /// attributes' errors, the original reports.
        /// </summary>
        internal override void AddDeclarationDiagnostics(BindingDiagnosticBag diagnostics)
        {
        }

        internal override int CalculateLocalSyntaxOffset(int localPosition, SyntaxTree localTree) => throw ExceptionUtilities.Unreachable();

        internal override int TryGetOverloadResolutionPriority() => 0;

        /// <summary>
        /// The binder of the body: the original method's body binder chain, with this symbol in place of the original in
        /// its <see cref="InMethodBinder"/>, so that names bind to the placeholder parameters.
        /// </summary>
        internal override ExecutableCodeBinder? TryGetBodyBinder(BinderFactory? binderFactoryOpt = null, bool ignoreAccessibility = false)
        {
            if (_original.TryGetBodyBinder(binderFactoryOpt, ignoreAccessibility) is not { } originalBodyBinder)
            {
                return null;
            }

            Binder? inMethod = originalBodyBinder.Next;
            while (inMethod is not null and not InMethodBinder)
            {
                inMethod = inMethod.Next;
            }

            if (inMethod?.Next is not { } outer)
            {
                return null;
            }

            Debug.Assert(((InMethodBinder)inMethod).ContainingMemberOrLambda == (object)_original);
            Binder result = new InMethodBinder(this, outer);

            // The flags the binders above the original's InMethodBinder add (an unsafe region, ignored accessibility).
            BinderFlags addedFlags = originalBodyBinder.Next!.Flags & ~outer.Flags;
            result = result.WithAdditionalFlags(addedFlags);
            return new ExecutableCodeBinder(SyntaxNode, this, result);
        }
    }
}
