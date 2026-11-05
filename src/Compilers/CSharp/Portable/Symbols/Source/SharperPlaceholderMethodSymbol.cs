// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
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
        private readonly ImmutableArray<bool> _isBeingInferred;
        private readonly object _methodChecksLock = new object();

        /// <summary>
        /// Per argument of a call in the body that is a parameter being inferred, the candidates of each overload
        /// resolution of that call that are applicable with the placeholder in place (step P5), in the order of the
        /// resolutions: instance methods, then the extension method scopes from the innermost. The key is the argument's
        /// syntax and the lambda, local function or placeholder that the binding of the call is in: each binding of a
        /// lambda for a delegate type has its own lambda symbol, and bound nodes can be cloned, so their identity is no key.
        /// </summary>
        private readonly Dictionary<SyntaxNode, ArrayBuilder<(Symbol? Owner, ImmutableArray<SharperPlaceholderCandidate> Candidates)>> _candidates = new();

        /// <param name="returnType">The written return type, or default when it is <c>infer</c>.</param>
        /// <param name="isBeingInferred">Per parameter, true if its type is the placeholder type, being inferred.</param>
        internal SharperPlaceholderMethodSymbol(
            SourceMemberMethodSymbol original,
            ImmutableArray<(TypeWithAnnotations Type, RefKind RefKind, string Name)> parameters,
            TypeWithAnnotations returnType,
            ImmutableArray<bool> isBeingInferred)
            : base(original.ContainingType, original.SyntaxRef, original.GetFirstLocation(), original.IsIterator, GetModifiersAndFlags(original))
        {
            _original = original;
            _returnType = returnType;
            _isBeingInferred = isBeingInferred;
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

        /// <summary>True if <paramref name="parameter"/> is a parameter of this placeholder whose type is being inferred.</summary>
        internal bool IsBeingInferred(ParameterSymbol parameter)
            => (object)parameter.ContainingSymbol == this && _isBeingInferred[parameter.Ordinal];

        /// <summary>
        /// Records the candidates of one overload resolution that are applicable with the placeholder in place, for an
        /// argument that is a parameter being inferred (<see cref="OverloadResolution"/>, step P5).
        /// </summary>
        /// <param name="owner">The lambda, local function or placeholder whose binding the call is in.</param>
        internal void RecordCandidates(SyntaxNode argument, Symbol? owner, ImmutableArray<SharperPlaceholderCandidate> candidates)
        {
            lock (_candidates)
            {
                if (!_candidates.TryGetValue(argument, out var resolutions))
                {
                    resolutions = ArrayBuilder<(Symbol?, ImmutableArray<SharperPlaceholderCandidate>)>.GetInstance();
                    _candidates.Add(argument, resolutions);
                }

                resolutions.Add((owner, candidates));
            }
        }

        /// <summary>
        /// The candidates applicable with the placeholder in place for <paramref name="argument"/> in the binding of
        /// <paramref name="owner"/>: those of the first overload resolution of its call that has any, as C# stops at the
        /// first set of candidates with an applicable one; empty if none has any; default if the call recorded none.
        /// </summary>
        internal ImmutableArray<SharperPlaceholderCandidate> GetCandidates(SyntaxNode argument, Symbol? owner)
        {
            lock (_candidates)
            {
                if (!_candidates.TryGetValue(argument, out var resolutions))
                {
                    return default;
                }

                ImmutableArray<SharperPlaceholderCandidate> result = default;
                foreach (var (resolutionOwner, candidates) in resolutions)
                {
                    if (!ReferenceEquals(resolutionOwner, owner))
                    {
                        continue;
                    }

                    if (!candidates.IsEmpty)
                    {
                        return candidates;
                    }

                    result = candidates;
                }

                return result;
            }
        }

        /// <summary>Frees what the binding recorded.</summary>
        internal void FreeCandidates()
        {
            lock (_candidates)
            {
                foreach (var (_, resolutions) in _candidates)
                {
                    resolutions.Free();
                }

                _candidates.Clear();
            }
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

    /// <summary>
    /// A candidate of a call in a body bound with placeholders that is applicable with the placeholder in place, and the
    /// type it takes for one argument that is a parameter being inferred (step P5).
    /// </summary>
    internal readonly struct SharperPlaceholderCandidate
    {
        /// <summary>The candidate, constructed with the type arguments inferred from the other arguments.</summary>
        internal readonly MethodSymbol Candidate;

        /// <summary>The parameter the argument goes to.</summary>
        internal readonly ParameterSymbol Parameter;

        /// <summary>The type the candidate takes for the argument: the parameter's, or its element type in the expanded form.</summary>
        internal readonly TypeWithAnnotations Type;

        /// <summary>True for the expanded form of a <c>params</c> candidate.</summary>
        internal readonly bool IsExpanded;

        internal SharperPlaceholderCandidate(MethodSymbol candidate, ParameterSymbol parameter, TypeWithAnnotations type, bool isExpanded)
        {
            Candidate = candidate;
            Parameter = parameter;
            Type = type;
            IsExpanded = isExpanded;
        }
    }
}
