// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
            return results.GetValue(syntax, _ => result);
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

        private static OverriddenSignature FindOverriddenSignature(SourceOrdinaryMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            CSharpCompilation compilation = method.DeclaringCompilation;
            Binder binder = compilation.GetBinderFactory(syntax.SyntaxTree).GetBinder(syntax.ReturnType, syntax, method)
                .WithAdditionalFlagsAndContainingMemberOrLambda(BinderFlags.SuppressConstraintChecks, method);

            // The written types, and the types inferred from default values, narrow the candidates. An open position
            // (default type) is an 'infer' this source fills; an open ref kind (null) is a parameter that writes none.
            var parameterSyntaxes = syntax.ParameterList.Parameters;
            var writtenTypes = ArrayBuilder<TypeWithAnnotations>.GetInstance(parameterSyntaxes.Count);
            var writtenRefKinds = ArrayBuilder<RefKind?>.GetInstance(parameterSyntaxes.Count);
            var openParameters = ArrayBuilder<bool>.GetInstance(parameterSyntaxes.Count);
            try
            {
                bool anyOpen = false;
                foreach (var parameter in parameterSyntaxes)
                {
                    if (parameter.IsArgList || parameter.Type is null)
                    {
                        return OverriddenSignature.NotApplicable;
                    }

                    RefKind refKind = ParameterHelpers.GetModifiers(parameter.Modifiers, ignoreParams: false, out SyntaxToken refnessKeyword, out _, out _, out _);
                    TypeWithAnnotations type = binder.BindTypeOrSharperInferKeyword(parameter.Type, BindingDiagnosticBag.Discarded, out bool isInfer);
                    if (isInfer && parameter.Default is not null)
                    {
                        type = GetOrInfer(method, parameter, GetDefaultValueBinder(method, parameter)).Type;
                    }

                    bool isOpen = isInfer && parameter.Default is null;
                    anyOpen |= isOpen;
                    openParameters.Add(isOpen);
                    writtenTypes.Add(isOpen ? default : type);
                    writtenRefKinds.Add(isOpen && refnessKeyword.Kind() == SyntaxKind.None ? null : refKind);
                }

                TypeWithAnnotations writtenReturnType = binder.BindTypeOrSharperInferKeyword(syntax.ReturnType.SkipScoped(out _).SkipRef(), BindingDiagnosticBag.Discarded, out bool returnIsInfer);
                anyOpen |= returnIsInfer;
                if (!anyOpen)
                {
                    // A type named 'infer' is in scope.
                    return OverriddenSignature.NotApplicable;
                }

                string name = syntax.Identifier.ValueText;
                int arity = syntax.Arity;
                var matches = ArrayBuilder<(MethodSymbol Member, ImmutableArray<TypeWithAnnotations> ParameterTypes, TypeWithAnnotations ReturnType)>.GetInstance();
                try
                {
                    if (method.IsOverride)
                    {
                        // The nearest base class comes first, so of members with the same signature (a virtual method and
                        // its overrides) the most derived is kept.
                        var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                        for (NamedTypeSymbol? type = method.ContainingType.BaseTypeNoUseSiteDiagnostics; type is not null; type = type.BaseTypeNoUseSiteDiagnostics)
                        {
                            foreach (Symbol member in type.GetMembers(name))
                            {
                                if (member is MethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } candidate &&
                                    (candidate.IsVirtual || candidate.IsAbstract || candidate.IsOverride) &&
                                    candidate.Arity == arity &&
                                    AccessCheck.IsSymbolAccessible(candidate, method.ContainingType, ref useSiteInfo))
                                {
                                    consider(candidate);
                                }
                            }
                        }
                    }
                    else if (method.MethodKind == MethodKind.ExplicitInterfaceImplementation)
                    {
                        if (method.SharperExplicitInterfaceType is NamedTypeSymbol { IsInterface: true } explicitInterface)
                        {
                            foreach (Symbol member in explicitInterface.GetMembers(name))
                            {
                                if (member is MethodSymbol { MethodKind: MethodKind.Ordinary } candidate &&
                                    candidate.IsImplementableInterfaceMember() &&
                                    candidate.IsStatic == method.IsStatic &&
                                    candidate.Arity == arity)
                                {
                                    consider(candidate);
                                }
                            }
                        }
                    }
                    else
                    {
                        // A public method that overrides nothing is an implicit implementation only if an interface of its
                        // type has a member with its name and parameter count; otherwise its 'infer' has no source here.
                        bool anyByNameAndCount = false;
                        foreach (NamedTypeSymbol @interface in method.ContainingType.AllInterfacesNoUseSiteDiagnostics)
                        {
                            foreach (Symbol member in @interface.GetMembers(name))
                            {
                                if (member is MethodSymbol { MethodKind: MethodKind.Ordinary } candidate &&
                                    candidate.IsImplementableInterfaceMember() &&
                                    candidate.IsStatic == method.IsStatic &&
                                    candidate.Arity == arity &&
                                    candidate.ParameterCount == parameterSyntaxes.Count)
                                {
                                    anyByNameAndCount = true;
                                    consider(candidate);
                                }
                            }
                        }

                        if (!anyByNameAndCount)
                        {
                            return OverriddenSignature.NotApplicable;
                        }
                    }

                    var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
                    switch (matches.Count)
                    {
                        case 0:
                            diagnostics.Add(ErrorCode.ERR_SharperInferNoOverriddenMember, syntax.Identifier.GetLocation(), name, parameterSyntaxes.Count);
                            return OverriddenSignature.CreateError(diagnostics.ToReadOnlyAndFree());

                        case 1:
                            diagnostics.Free();
                            MethodSymbol member = matches[0].Member;
                            var isParams = member.Parameters.SelectAsArray((p, i, open) => p.IsParams && open[i], openParameters);
                            return OverriddenSignature.Create(member, matches[0].ParameterTypes, isParams, matches[0].ReturnType);

                        default:
                            string candidates = string.Join(", ", matches.Select(m => "'" + m.Member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "'"));
                            diagnostics.Add(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, syntax.Identifier.GetLocation(), name, candidates);
                            return OverriddenSignature.CreateError(diagnostics.ToReadOnlyAndFree());
                    }
                }
                finally
                {
                    matches.Free();
                }

                void consider(MethodSymbol candidate)
                {
                    if (candidate.IsVararg || candidate.ParameterCount != parameterSyntaxes.Count)
                    {
                        return;
                    }

                    // The candidate's method type parameters become the method's own. A candidate of a constructed base
                    // type has substituted type parameters, hence the alpha-renaming.
                    TypeMap? typeMap = arity == 0 ? null : new TypeMap(candidate.TypeParameters, method.TypeParameters, allowAlpha: true);
                    var parameterTypes = candidate.Parameters.SelectAsArray(p => copy(p.TypeWithAnnotations));
                    TypeWithAnnotations returnType = copy(candidate.ReturnTypeWithAnnotations);

                    for (int i = 0; i < parameterTypes.Length; i++)
                    {
                        if (writtenRefKinds[i] is { } refKind && refKind != candidate.Parameters[i].RefKind)
                        {
                            return;
                        }

                        // A written type with errors of its own narrows nothing; its errors are reported with the signature.
                        if (writtenTypes[i].HasType && !writtenTypes[i].Type.IsErrorType() &&
                            !writtenTypes[i].Type.Equals(parameterTypes[i].Type, TypeCompareKind.AllIgnoreOptions))
                        {
                            return;
                        }
                    }

                    if (!returnIsInfer && !writtenReturnType.Type.IsErrorType() &&
                        !writtenReturnType.Type.Equals(returnType.Type, TypeCompareKind.AllIgnoreOptions))
                    {
                        return;
                    }

                    foreach (var match in matches)
                    {
                        if (haveSameSignature(match.Member, match.ParameterTypes, match.ReturnType, candidate, parameterTypes, returnType))
                        {
                            return;
                        }
                    }

                    matches.Add((candidate, parameterTypes, returnType));

                    TypeWithAnnotations copy(TypeWithAnnotations type)
                    {
                        if (typeMap is not null)
                        {
                            type = typeMap.SubstituteType(type);
                        }

                        // Custom modifiers are copied from an overridden or explicitly implemented member later, by the
                        // method checks, as for written types.
                        return type.WithModifiers(ImmutableArray<CustomModifier>.Empty);
                    }
                }

                static bool haveSameSignature(
                    MethodSymbol x, ImmutableArray<TypeWithAnnotations> xParameterTypes, TypeWithAnnotations xReturnType,
                    MethodSymbol y, ImmutableArray<TypeWithAnnotations> yParameterTypes, TypeWithAnnotations yReturnType)
                {
                    for (int i = 0; i < xParameterTypes.Length; i++)
                    {
                        if (x.Parameters[i].RefKind != y.Parameters[i].RefKind ||
                            !xParameterTypes[i].Type.Equals(yParameterTypes[i].Type, TypeCompareKind.AllIgnoreOptions))
                        {
                            return false;
                        }
                    }

                    return xReturnType.Type.Equals(yReturnType.Type, TypeCompareKind.AllIgnoreOptions);
                }
            }
            finally
            {
                writtenTypes.Free();
                writtenRefKinds.Free();
                openParameters.Free();
            }
        }
    }
}
