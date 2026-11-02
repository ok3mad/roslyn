// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal static partial class SharperInferredParameterType
    {
        /// <summary>
        /// The types that a method copies from the member it overrides or implements, for its <c>infer</c> parameters
        /// without a default value and its <c>infer</c> return type (source 1, step P2):
        /// <c>public infer Load(infer userId) => result;</c> implementing <c>Task&lt;ProfileResult&gt; IProfiles.Load(int)</c>
        /// is a <c>Task&lt;ProfileResult&gt; Load(int userId)</c>. Nothing is inferred from the body.
        /// </summary>
        /// <remarks>
        /// The member is found before the method's signature exists, by name and parameter count, and the types the method
        /// writes (or infers from default values) narrow the candidates. It is searched for outside of the signature lock,
        /// as default values are (<see cref="InferBeforeSignature"/>), since it reads the signatures of other members; the
        /// result is kept per method syntax by the compilation and published once. A candidate whose signature depends on
        /// the method itself (through a default value, or through another method that copies its types) is a cycle on the
        /// same stack as default values.
        /// </remarks>
        internal sealed class OverriddenSignature
        {
            /// <summary>The method has no <c>infer</c> that this source could fill, or is no override or implementation.</summary>
            internal static readonly OverriddenSignature NotApplicable = new OverriddenSignature(isApplicable: false, member: null, default, default, default, default, ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty);

            internal readonly bool IsApplicable;

            /// <summary>The member the types are copied from; null when there is none, or more than one.</summary>
            internal readonly MethodSymbol? Member;

            private readonly ImmutableArray<TypeWithAnnotations> _parameterTypes;
            private readonly ImmutableArray<RefKind> _refKinds;
            private readonly ImmutableArray<bool> _isParams;
            private readonly TypeWithAnnotations _returnType;

            /// <summary>The errors of the search, reported once, with the method's signature.</summary>
            internal readonly ReadOnlyBindingDiagnostic<AssemblySymbol> Diagnostics;

            private OverriddenSignature(
                bool isApplicable,
                MethodSymbol? member,
                ImmutableArray<TypeWithAnnotations> parameterTypes,
                ImmutableArray<RefKind> refKinds,
                ImmutableArray<bool> isParams,
                TypeWithAnnotations returnType,
                ReadOnlyBindingDiagnostic<AssemblySymbol> diagnostics)
            {
                IsApplicable = isApplicable;
                Member = member;
                _parameterTypes = parameterTypes;
                _refKinds = refKinds;
                _isParams = isParams;
                _returnType = returnType;
                Diagnostics = diagnostics;
            }

            internal static OverriddenSignature CreateError(ReadOnlyBindingDiagnostic<AssemblySymbol> diagnostics)
                => new OverriddenSignature(isApplicable: true, member: null, default, default, default, default, diagnostics);

            internal static OverriddenSignature CreateCycle(MethodDeclarationSyntax method)
            {
                var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
                diagnostics.Add(ErrorCode.ERR_SharperInferOverriddenMemberCycle, method.Identifier.GetLocation(), method.Identifier.ValueText);
                return CreateError(diagnostics.ToReadOnlyAndFree());
            }

            internal static OverriddenSignature Create(MethodSymbol member, ImmutableArray<TypeWithAnnotations> parameterTypes, ImmutableArray<bool> isParams, TypeWithAnnotations returnType)
                => new OverriddenSignature(
                    isApplicable: true,
                    member,
                    parameterTypes,
                    member.Parameters.SelectAsArray(p => p.RefKind),
                    isParams,
                    returnType,
                    ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty);

            /// <summary>The type of the parameter at <paramref name="ordinal"/>, or an error type when there is no member.</summary>
            internal TypeWithAnnotations GetParameterType(int ordinal, CSharpCompilation compilation)
            {
                Debug.Assert(IsApplicable);
                return _parameterTypes.IsDefault ? CreateErrorType(compilation) : _parameterTypes[ordinal];
            }

            /// <summary>The ref kind of the parameter at <paramref name="ordinal"/>, for a parameter that writes none.</summary>
            internal RefKind GetRefKind(int ordinal) => _refKinds.IsDefault ? RefKind.None : _refKinds[ordinal];

            /// <summary>Whether the parameter at <paramref name="ordinal"/>, an <c>infer</c> one, copies <c>params</c>.</summary>
            internal bool IsParams(int ordinal) => !_isParams.IsDefault && _isParams[ordinal];

            /// <summary>The return type, or an error type when there is no member.</summary>
            internal TypeWithAnnotations GetReturnType(CSharpCompilation compilation)
            {
                Debug.Assert(IsApplicable);
                return _returnType.HasType ? _returnType : CreateErrorType(compilation);
            }
        }

        /// <summary>
        /// The types that <paramref name="owner"/> copies from the member it overrides or implements, or
        /// <see cref="OverriddenSignature.NotApplicable"/>. Only methods take types from there: an <c>override</c> from its
        /// base classes, an explicit implementation from the interface it names, and a public method from the interfaces
        /// of its type, when one of them has a member with its name and parameter count.
        /// </summary>
        internal static OverriddenSignature GetOverriddenSignature(Symbol owner)
        {
            if (owner is not SourceOrdinaryMethodSymbol method ||
                method.SyntaxNode is not MethodDeclarationSyntax syntax ||
                !MayCopyFromOverriddenMember(method, syntax))
            {
                return OverriddenSignature.NotApplicable;
            }

            var results = method.DeclaringCompilation.SharperOverriddenSignatures;
            if (results.TryGetValue(syntax, out OverriddenSignature? result))
            {
                return result;
            }

            var inProgress = s_inProgress ??= new List<SyntaxNode>();
            int cycleStart = inProgress.IndexOf(syntax);
            if (cycleStart >= 0)
            {
                PublishCycle(method.DeclaringCompilation, inProgress, cycleStart);
                return results.GetValue(syntax, _ => throw ExceptionUtilities.Unreachable());
            }

            inProgress.Add(syntax);
            try
            {
                result = FindOverriddenSignature(method, syntax);
            }
            finally
            {
                Debug.Assert(inProgress[inProgress.Count - 1] == syntax);
                inProgress.RemoveAt(inProgress.Count - 1);
            }

            // A method found to be in a cycle while its candidates were read already has its result.
            return Publish(results, method.DeclaringCompilation, syntax, result);
        }

        /// <summary>
        /// A check on syntax and modifiers only, which every method goes through: whether the method may override or
        /// implement a member, and has an <c>infer</c> that the member could fill.
        /// </summary>
        private static bool MayCopyFromOverriddenMember(SourceOrdinaryMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            if (!method.IsOverride &&
                method.MethodKind != MethodKind.ExplicitInterfaceImplementation &&
                (method.DeclaredAccessibility != Accessibility.Public || method.ContainingType.IsInterface))
            {
                return false;
            }

            if (syntax.ReturnType.SkipScoped(out _).SkipRef().IsInfer)
            {
                return true;
            }

            foreach (var parameter in syntax.ParameterList.Parameters)
            {
                if (parameter is { Type.IsInfer: true, Default: null })
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the member that <paramref name="method"/> overrides or implements, in four steps: read the signature the
        /// method writes, collect the members it could override or implement, copy each one's signature and keep those
        /// that fit what is written, then pick the one match or report why there is none.
        /// </summary>
        private static OverriddenSignature FindOverriddenSignature(SourceOrdinaryMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            if (WrittenSignature.Read(method, syntax) is not { } written)
            {
                return OverriddenSignature.NotApplicable;
            }

            ArrayBuilder<MethodSymbol>? candidates = GetCandidates(method, syntax);
            if (candidates is null)
            {
                return OverriddenSignature.NotApplicable;
            }

            var matches = ArrayBuilder<CopiedSignature>.GetInstance();
            foreach (MethodSymbol candidate in candidates)
            {
                var copied = CopiedSignature.Create(candidate, method);
                if (written.Accepts(copied) && !matches.Any(static (match, copied) => match.HasSameSignature(copied), copied))
                {
                    matches.Add(copied);
                }
            }

            candidates.Free();
            OverriddenSignature result = Decide(syntax, written, matches);
            matches.Free();
            return result;
        }

        /// <summary>
        /// The members <paramref name="method"/> could override or implement, with its name, arity and parameter count,
        /// or null when it is a public method that implements nothing. Members of base classes come nearest first, so
        /// that of a virtual member and its overrides, which have the same signature, the most derived is kept.
        /// </summary>
        private static ArrayBuilder<MethodSymbol>? GetCandidates(SourceOrdinaryMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            string name = syntax.Identifier.ValueText;
            var candidates = ArrayBuilder<MethodSymbol>.GetInstance();

            if (method.IsOverride)
            {
                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                for (NamedTypeSymbol? type = method.ContainingType.BaseTypeNoUseSiteDiagnostics; type is not null; type = type.BaseTypeNoUseSiteDiagnostics)
                {
                    foreach (Symbol member in type.GetMembers(name))
                    {
                        if (isCandidate(member, out MethodSymbol? candidate) &&
                            !candidate.IsStatic &&
                            (candidate.IsVirtual || candidate.IsAbstract || candidate.IsOverride) &&
                            AccessCheck.IsSymbolAccessible(candidate, method.ContainingType, ref useSiteInfo))
                        {
                            candidates.Add(candidate);
                        }
                    }
                }
            }
            else if (method.MethodKind == MethodKind.ExplicitInterfaceImplementation)
            {
                if (method.SharperExplicitInterfaceType is NamedTypeSymbol { IsInterface: true } explicitInterface)
                {
                    addInterfaceMembers(explicitInterface);
                }
            }
            else
            {
                foreach (NamedTypeSymbol @interface in method.ContainingType.AllInterfacesNoUseSiteDiagnostics)
                {
                    addInterfaceMembers(@interface);
                }

                // A public method that overrides nothing implements an interface member only if one has its name and
                // parameter count; otherwise its 'infer' has no source here.
                if (candidates.Count == 0)
                {
                    candidates.Free();
                    return null;
                }
            }

            return candidates;

            void addInterfaceMembers(NamedTypeSymbol @interface)
            {
                foreach (Symbol member in @interface.GetMembers(name))
                {
                    if (isCandidate(member, out MethodSymbol? candidate) &&
                        candidate.IsImplementableInterfaceMember() &&
                        candidate.IsStatic == method.IsStatic)
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            bool isCandidate(Symbol member, [NotNullWhen(true)] out MethodSymbol? candidate)
            {
                candidate = member as MethodSymbol;
                return candidate is { MethodKind: MethodKind.Ordinary, IsVararg: false } &&
                    candidate.Arity == syntax.Arity &&
                    candidate.ParameterCount == syntax.ParameterList.Parameters.Count;
            }
        }

        /// <summary>The single match, or CS20043 for none and CS20044 for several.</summary>
        private static OverriddenSignature Decide(MethodDeclarationSyntax syntax, WrittenSignature written, ArrayBuilder<CopiedSignature> matches)
        {
            string name = syntax.Identifier.ValueText;
            if (matches.Count == 1)
            {
                CopiedSignature match = matches[0];
                var isParams = match.Member.Parameters.SelectAsArray(static (p, i, written) => p.IsParams && written.IsOpen(i), written);
                return OverriddenSignature.Create(match.Member, match.ParameterTypes, isParams, match.ReturnType);
            }

            var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
            if (matches.Count == 0)
            {
                diagnostics.Add(ErrorCode.ERR_SharperInferNoOverriddenMember, syntax.Identifier.GetLocation(), name, syntax.ParameterList.Parameters.Count);
            }
            else
            {
                string list = string.Join(", ", matches.Select(m => "'" + m.Member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "'"));
                diagnostics.Add(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, syntax.Identifier.GetLocation(), name, list);
            }

            return OverriddenSignature.CreateError(diagnostics.ToReadOnlyAndFree());
        }

        /// <summary>
        /// The signature a method writes: per parameter its type and ref kind, and its return type. An <c>infer</c> that
        /// the overridden or implemented member fills is open; an <c>infer</c> with a default value counts as written,
        /// with the type of the default value (P1).
        /// </summary>
        private sealed class WrittenSignature
        {
            /// <summary>The written types; a default (no type) where the parameter is open.</summary>
            private readonly ImmutableArray<TypeWithAnnotations> _parameterTypes;

            /// <summary>The written ref kinds; null for an open parameter that writes none, which copies the member's.</summary>
            private readonly ImmutableArray<RefKind?> _refKinds;

            /// <summary>The written return type; a default (no type) where it is open.</summary>
            private readonly TypeWithAnnotations _returnType;

            private WrittenSignature(ImmutableArray<TypeWithAnnotations> parameterTypes, ImmutableArray<RefKind?> refKinds, TypeWithAnnotations returnType)
            {
                _parameterTypes = parameterTypes;
                _refKinds = refKinds;
                _returnType = returnType;
            }

            internal bool IsOpen(int ordinal) => !_parameterTypes[ordinal].HasType;

            /// <summary>
            /// Reads the signature of <paramref name="method"/>, or returns null when nothing in it is open (a type named
            /// <c>infer</c> is in scope) or it has an <c>__arglist</c>.
            /// </summary>
            internal static WrittenSignature? Read(SourceOrdinaryMethodSymbol method, MethodDeclarationSyntax syntax)
            {
                Binder binder = method.DeclaringCompilation.GetBinderFactory(syntax.SyntaxTree).GetBinder(syntax.ReturnType, syntax, method)
                    .WithAdditionalFlagsAndContainingMemberOrLambda(BinderFlags.SuppressConstraintChecks, method);

                var parameterTypes = ArrayBuilder<TypeWithAnnotations>.GetInstance();
                var refKinds = ArrayBuilder<RefKind?>.GetInstance();
                bool anyOpen = false;
                foreach (var parameter in syntax.ParameterList.Parameters)
                {
                    if (parameter.IsArgList || parameter.Type is null)
                    {
                        parameterTypes.Free();
                        refKinds.Free();
                        return null;
                    }

                    RefKind refKind = ParameterHelpers.GetModifiers(parameter.Modifiers, ignoreParams: false, out SyntaxToken refnessKeyword, out _, out _, out _);
                    TypeWithAnnotations type = binder.BindTypeOrSharperInferKeyword(parameter.Type, BindingDiagnosticBag.Discarded, out bool isInfer);
                    bool isOpen = isInfer && parameter.Default is null;
                    if (isInfer && !isOpen)
                    {
                        type = GetOrInfer(method, parameter, GetDefaultValueBinder(method, parameter)).Type;
                    }

                    anyOpen |= isOpen;
                    parameterTypes.Add(isOpen ? default : type);
                    refKinds.Add(isOpen && refnessKeyword.Kind() == SyntaxKind.None ? null : refKind);
                }

                TypeWithAnnotations returnType = binder.BindTypeOrSharperInferKeyword(syntax.ReturnType.SkipScoped(out _).SkipRef(), BindingDiagnosticBag.Discarded, out bool returnIsInfer);
                anyOpen |= returnIsInfer;

                var result = new WrittenSignature(parameterTypes.ToImmutableAndFree(), refKinds.ToImmutableAndFree(), returnIsInfer ? default : returnType);
                return anyOpen ? result : null;
            }

            /// <summary>
            /// Whether the member's signature fits what is written: the same ref kinds where they are written, and the
            /// same types (ignoring nullability, tuple names and <c>dynamic</c>) where they are written. A written type
            /// with errors of its own fits anything; its errors are reported with the signature.
            /// </summary>
            internal bool Accepts(CopiedSignature copied)
            {
                for (int i = 0; i < _parameterTypes.Length; i++)
                {
                    if (_refKinds[i] is { } refKind && refKind != copied.Member.Parameters[i].RefKind)
                    {
                        return false;
                    }

                    if (!fits(_parameterTypes[i], copied.ParameterTypes[i]))
                    {
                        return false;
                    }
                }

                return fits(_returnType, copied.ReturnType);

                static bool fits(TypeWithAnnotations written, TypeWithAnnotations copied)
                    => !written.HasType || written.Type.IsErrorType() || written.Type.Equals(copied.Type, TypeCompareKind.AllIgnoreOptions);
            }
        }

        /// <summary>The signature of a candidate member, as the method would copy it.</summary>
        private readonly struct CopiedSignature
        {
            internal readonly MethodSymbol Member;
            internal readonly ImmutableArray<TypeWithAnnotations> ParameterTypes;
            internal readonly TypeWithAnnotations ReturnType;

            private CopiedSignature(MethodSymbol member, ImmutableArray<TypeWithAnnotations> parameterTypes, TypeWithAnnotations returnType)
            {
                Member = member;
                ParameterTypes = parameterTypes;
                ReturnType = returnType;
            }

            /// <summary>
            /// Copies the types of <paramref name="member"/> for <paramref name="method"/>: the member's method type
            /// parameters become the method's own (alpha-renamed, since a member of a constructed base type has
            /// substituted type parameters), and custom modifiers are dropped. The method checks copy those from an
            /// overridden or explicitly implemented member later, as for written types.
            /// </summary>
            internal static CopiedSignature Create(MethodSymbol member, SourceOrdinaryMethodSymbol method)
            {
                TypeMap? typeMap = member.Arity == 0 ? null : new TypeMap(member.TypeParameters, method.TypeParameters, allowAlpha: true);
                return new CopiedSignature(
                    member,
                    member.Parameters.SelectAsArray(static (p, typeMap) => copy(p.TypeWithAnnotations, typeMap), typeMap),
                    copy(member.ReturnTypeWithAnnotations, typeMap));

                static TypeWithAnnotations copy(TypeWithAnnotations type, TypeMap? typeMap)
                    => (typeMap is null ? type : typeMap.SubstituteType(type)).WithModifiers(ImmutableArray<CustomModifier>.Empty);
            }

            /// <summary>
            /// Whether both have the same ref kinds and types, ignoring nullability, tuple names and <c>dynamic</c>: a
            /// virtual member and its override, or the same member declared by two interfaces.
            /// </summary>
            internal bool HasSameSignature(CopiedSignature other)
            {
                for (int i = 0; i < ParameterTypes.Length; i++)
                {
                    if (Member.Parameters[i].RefKind != other.Member.Parameters[i].RefKind ||
                        !ParameterTypes[i].Type.Equals(other.ParameterTypes[i].Type, TypeCompareKind.AllIgnoreOptions))
                    {
                        return false;
                    }
                }

                return ReturnType.Type.Equals(other.ReturnType.Type, TypeCompareKind.AllIgnoreOptions);
            }
        }
    }
}
