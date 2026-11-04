// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal static partial class SharperInferredParameterType
    {
        /// <summary>
        /// The types that a member copies from the member it overrides or implements, for its <c>infer</c> parameters
        /// without a default value and its <c>infer</c> return, property or event type (source 1, step P2):
        /// <c>public infer Load(infer userId) => result;</c> implementing <c>Task&lt;ProfileResult&gt; IProfiles.Load(int)</c>
        /// is a <c>Task&lt;ProfileResult&gt; Load(int userId)</c>. Nothing is inferred from the body. Methods, properties,
        /// indexers, operators, conversion operators and events copy alike (<see cref="MemberDeclaration"/>).
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
            internal readonly Symbol? Member;

            private readonly ImmutableArray<TypeWithAnnotations> _parameterTypes;
            private readonly ImmutableArray<RefKind> _refKinds;
            private readonly ImmutableArray<bool> _isParams;
            private readonly TypeWithAnnotations _returnType;

            /// <summary>The errors of the search, reported once, with the method's signature.</summary>
            internal readonly ReadOnlyBindingDiagnostic<AssemblySymbol> Diagnostics;

            private OverriddenSignature(
                bool isApplicable,
                Symbol? member,
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

            internal static OverriddenSignature CreateCycle(CSharpSyntaxNode member)
            {
                var (location, name) = MemberDeclaration.GetNameForDiagnostics(member);
                var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
                diagnostics.Add(ErrorCode.ERR_SharperInferOverriddenMemberCycle, location, name);
                return CreateError(diagnostics.ToReadOnlyAndFree());
            }

            internal static OverriddenSignature Create(Symbol member, ImmutableArray<TypeWithAnnotations> parameterTypes, ImmutableArray<bool> isParams, TypeWithAnnotations returnType)
                => new OverriddenSignature(
                    isApplicable: true,
                    member,
                    parameterTypes,
                    member.GetParameters().SelectAsArray(p => p.RefKind),
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

            /// <summary>The return type (of a property or indexer: its type; of an event: its delegate type), or an error type when there is no member.</summary>
            internal TypeWithAnnotations GetReturnType(CSharpCompilation compilation)
            {
                Debug.Assert(IsApplicable);
                return _returnType.HasType ? _returnType : CreateErrorType(compilation);
            }
        }

        /// <summary>
        /// The types that <paramref name="owner"/> copies from the member it overrides or implements, or
        /// <see cref="OverriddenSignature.NotApplicable"/>. A method, property, indexer, operator, conversion operator or
        /// event takes types from there when it is an <c>override</c> (from its base classes), an explicit implementation
        /// (from the interface it names), or public (from the interfaces of its type, when one of them has a member of its
        /// kind with its name and parameter count).
        /// </summary>
        internal static OverriddenSignature GetOverriddenSignature(Symbol owner)
        {
            if (MemberDeclaration.Create(owner) is not { } declaration ||
                !MayCopyFromOverriddenMember(declaration))
            {
                return OverriddenSignature.NotApplicable;
            }

            CSharpSyntaxNode syntax = declaration.Syntax;
            CSharpCompilation compilation = owner.DeclaringCompilation;
            var results = compilation.SharperOverriddenSignatures;
            if (results.TryGetValue(syntax, out OverriddenSignature? result))
            {
                return result;
            }

            var inProgress = s_inProgress ??= new List<SyntaxNode>();
            int cycleStart = inProgress.IndexOf(syntax);
            if (cycleStart >= 0)
            {
                PublishCycle(compilation, inProgress, cycleStart);
                return results.GetValue(syntax, _ => throw ExceptionUtilities.Unreachable());
            }

            inProgress.Add(syntax);
            try
            {
                result = FindOverriddenSignature(declaration);
            }
            finally
            {
                Debug.Assert(inProgress[inProgress.Count - 1] == syntax);
                inProgress.RemoveAt(inProgress.Count - 1);
            }

            // A member found to be in a cycle while its candidates were read already has its result.
            return Publish(results, compilation, syntax, result);
        }

        /// <summary>
        /// A check on syntax and modifiers only, which every member goes through: whether the member may override or
        /// implement a member, and has an <c>infer</c> that the member could fill.
        /// </summary>
        private static bool MayCopyFromOverriddenMember(MemberDeclaration declaration)
        {
            Symbol owner = declaration.Owner;
            if (!owner.IsOverride &&
                !declaration.IsExplicitImplementation &&
                (owner.DeclaredAccessibility != Accessibility.Public || owner.ContainingType.IsInterface))
            {
                return false;
            }

            if (declaration.TypeSyntax.IsInfer)
            {
                return true;
            }

            foreach (var parameter in declaration.Parameters)
            {
                if (parameter is { Type.IsInfer: true, Default: null })
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the member that <paramref name="declaration"/> overrides or implements, in four steps: read the signature
        /// the member writes, collect the members it could override or implement, copy each one's signature and keep those
        /// that fit what is written, then pick the one match or report why there is none.
        /// </summary>
        private static OverriddenSignature FindOverriddenSignature(MemberDeclaration declaration)
        {
            if (WrittenSignature.Read(declaration) is not { } written)
            {
                return OverriddenSignature.NotApplicable;
            }

            ArrayBuilder<Symbol>? candidates = GetCandidates(declaration);
            if (candidates is null)
            {
                return OverriddenSignature.NotApplicable;
            }

            var matches = ArrayBuilder<CopiedSignature>.GetInstance();
            var ruledOut = ArrayBuilder<(CopiedSignature Copied, int Position)>.GetInstance();
            foreach (Symbol candidate in candidates)
            {
                var copied = CopiedSignature.Create(candidate, declaration.Owner);
                if (written.GetRuledOutPosition(copied) is int position)
                {
                    if (!ruledOut.Any(static (other, copied) => other.Copied.HasSameSignature(copied), copied))
                    {
                        ruledOut.Add((copied, position));
                    }
                }
                else if (!matches.Any(static (match, copied) => match.HasSameSignature(copied), copied))
                {
                    matches.Add(copied);
                }
            }

            candidates.Free();
            OverriddenSignature result = Decide(declaration, written, matches, ruledOut);
            matches.Free();
            ruledOut.Free();
            return result;
        }

        /// <summary>
        /// The members <paramref name="declaration"/> could override or implement, of its kind, with its name, arity and
        /// parameter count, or null when it is a public member that implements nothing. Members of base classes come
        /// nearest first, so that of a virtual member and its overrides, which have the same signature, the most derived
        /// is kept.
        /// </summary>
        private static ArrayBuilder<Symbol>? GetCandidates(MemberDeclaration declaration)
        {
            Symbol owner = declaration.Owner;
            var candidates = ArrayBuilder<Symbol>.GetInstance();

            if (owner.IsOverride)
            {
                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                for (NamedTypeSymbol? type = owner.ContainingType.BaseTypeNoUseSiteDiagnostics; type is not null; type = type.BaseTypeNoUseSiteDiagnostics)
                {
                    foreach (Symbol member in type.GetMembers(declaration.Name))
                    {
                        if (declaration.IsCandidate(member) &&
                            !member.IsStatic &&
                            (member.IsVirtual || member.IsAbstract || member.IsOverride) &&
                            AccessCheck.IsSymbolAccessible(member, owner.ContainingType, ref useSiteInfo))
                        {
                            candidates.Add(member);
                        }
                    }
                }
            }
            else if (declaration.IsExplicitImplementation)
            {
                if (declaration.ExplicitInterfaceType is NamedTypeSymbol { IsInterface: true } explicitInterface)
                {
                    addInterfaceMembers(explicitInterface);
                }
            }
            else
            {
                foreach (NamedTypeSymbol @interface in owner.ContainingType.AllInterfacesNoUseSiteDiagnostics)
                {
                    addInterfaceMembers(@interface);
                }

                // A public member that overrides nothing implements an interface member only if one has its kind, name and
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
                foreach (Symbol member in @interface.GetMembers(declaration.Name))
                {
                    if (declaration.IsCandidate(member) &&
                        member.IsImplementableInterfaceMember() &&
                        member.IsStatic == owner.IsStatic)
                    {
                        candidates.Add(member);
                    }
                }
            }
        }

        /// <summary>
        /// What the search reads from a member's declaration, for each kind of member that can copy its types: the cache
        /// key, the name its candidates have, its parameters and its type (the return type without <c>ref</c>, or the
        /// type of a property, indexer or event). The member symbol may not be complete yet: an event binds its type in
        /// its constructor, a property and an indexer before their signature, a method and an operator before their
        /// method checks.
        /// </summary>
        private readonly struct MemberDeclaration
        {
            internal readonly Symbol Owner;

            /// <summary>The syntax the result is kept by: the member declaration, or the declarator of a field-like event.</summary>
            internal readonly CSharpSyntaxNode Syntax;

            /// <summary>The name of the members it can override or implement (<c>op_Addition</c>, <c>this[]</c>).</summary>
            internal readonly string Name;

            internal readonly SeparatedSyntaxList<ParameterSyntax> Parameters;

            /// <summary>The return, property or event type, without <c>scoped</c> and <c>ref</c>.</summary>
            internal readonly TypeSyntax TypeSyntax;

            /// <summary>The type as written, with <c>ref</c>: where a written return type that rules out a member is.</summary>
            internal readonly TypeSyntax WrittenTypeSyntax;
            internal readonly bool IsExplicitImplementation;
            internal readonly TypeSymbol? ExplicitInterfaceType;
            private readonly int _arity;

            private MemberDeclaration(Symbol owner, CSharpSyntaxNode syntax, string name, SeparatedSyntaxList<ParameterSyntax> parameters, TypeSyntax typeSyntax, ExplicitInterfaceSpecifierSyntax? explicitInterfaceSpecifier, TypeSymbol? explicitInterfaceType, int arity = 0)
            {
                Owner = owner;
                Syntax = syntax;
                Name = name;
                Parameters = parameters;
                TypeSyntax = typeSyntax.SkipScoped(out _).SkipRef();
                WrittenTypeSyntax = typeSyntax;
                IsExplicitImplementation = explicitInterfaceSpecifier is not null;
                ExplicitInterfaceType = explicitInterfaceType;
                _arity = arity;
            }

            internal static MemberDeclaration? Create(Symbol owner)
            {
                switch (owner)
                {
                    case SourceOrdinaryMethodSymbol { SyntaxNode: MethodDeclarationSyntax method } ordinary:
                        return new MemberDeclaration(owner, method, method.Identifier.ValueText, method.ParameterList.Parameters, method.ReturnType,
                            method.ExplicitInterfaceSpecifier, ordinary.SharperExplicitInterfaceType, method.Arity);

                    case SourceUserDefinedOperatorSymbolBase { SyntaxNode: OperatorDeclarationSyntax @operator } userDefined:
                        return new MemberDeclaration(owner, @operator, OperatorFacts.OperatorNameFromDeclaration(@operator), @operator.ParameterList.Parameters, @operator.ReturnType,
                            @operator.ExplicitInterfaceSpecifier, userDefined.SharperExplicitInterfaceType);

                    case SourceUserDefinedOperatorSymbolBase { SyntaxNode: ConversionOperatorDeclarationSyntax conversion } userDefined:
                        return new MemberDeclaration(owner, conversion, OperatorFacts.OperatorNameFromDeclaration(conversion), conversion.ParameterList.Parameters, conversion.Type,
                            conversion.ExplicitInterfaceSpecifier, userDefined.SharperExplicitInterfaceType);

                    case SourcePropertySymbol property when property.CSharpSyntaxNode is PropertyDeclarationSyntax syntax:
                        return new MemberDeclaration(owner, syntax, syntax.Identifier.ValueText, default, syntax.Type,
                            syntax.ExplicitInterfaceSpecifier, property.SharperExplicitInterfaceType);

                    case SourcePropertySymbol indexer when indexer.CSharpSyntaxNode is IndexerDeclarationSyntax syntax:
                        return new MemberDeclaration(owner, syntax, WellKnownMemberNames.Indexer, syntax.ParameterList.Parameters, syntax.Type,
                            syntax.ExplicitInterfaceSpecifier, indexer.SharperExplicitInterfaceType);

                    case SourceCustomEventSymbol @event when @event.CSharpSyntaxNode is EventDeclarationSyntax syntax:
                        return new MemberDeclaration(owner, syntax, syntax.Identifier.ValueText, default, syntax.Type,
                            syntax.ExplicitInterfaceSpecifier, @event.SharperExplicitInterfaceType);

                    case SourceFieldLikeEventSymbol @event when @event.CSharpSyntaxNode is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator:
                        return new MemberDeclaration(owner, declarator, declarator.Identifier.ValueText, default, declaration.Type,
                            explicitInterfaceSpecifier: null, explicitInterfaceType: null);

                    default:
                        return null;
                }
            }

            /// <summary>Whether <paramref name="member"/> is of this declaration's kind, arity and parameter count.</summary>
            internal bool IsCandidate(Symbol member)
            {
                bool isOfKind = (Syntax, member) switch
                {
                    (MethodDeclarationSyntax, MethodSymbol { MethodKind: MethodKind.Ordinary, IsVararg: false } method) => method.Arity == _arity,
                    (OperatorDeclarationSyntax, MethodSymbol { MethodKind: MethodKind.UserDefinedOperator }) => true,
                    (ConversionOperatorDeclarationSyntax, MethodSymbol { MethodKind: MethodKind.Conversion }) => true,
                    (PropertyDeclarationSyntax, PropertySymbol { IsIndexer: false }) => true,
                    (IndexerDeclarationSyntax, PropertySymbol { IsIndexer: true }) => true,
                    (EventDeclarationSyntax or VariableDeclaratorSyntax, EventSymbol) => true,
                    _ => false,
                };

                return isOfKind && member.GetParameters().Length == Parameters.Count;
            }

            /// <summary>
            /// The binder the written types are bound with. Only methods and operators are looked up by the binder factory
            /// (for their type parameters and their parameters' scope); a property, an indexer and an event bind their
            /// types in the scope of their type, and an event is not a member of its type yet when it binds its type.
            /// </summary>
            internal Binder GetSignatureBinder()
            {
                var factory = Owner.DeclaringCompilation.GetBinderFactory(Syntax.SyntaxTree);
                Binder binder = Owner is MethodSymbol
                    ? factory.GetBinder(TypeSyntax, Syntax, Owner)
                    : factory.GetBinder(TypeSyntax);
                return binder.WithAdditionalFlagsAndContainingMemberOrLambda(BinderFlags.SuppressConstraintChecks, Owner);
            }

            /// <summary>Where the search's errors are reported, and the name they give the member.</summary>
            internal static (Location Location, string Name) GetNameForDiagnostics(CSharpSyntaxNode syntax)
            {
                return syntax switch
                {
                    MethodDeclarationSyntax method => (method.Identifier.GetLocation(), method.Identifier.ValueText),
                    OperatorDeclarationSyntax @operator => (@operator.OperatorToken.GetLocation(), SyntaxFacts.GetText(SyntaxKind.OperatorKeyword) + " " + @operator.OperatorToken.ValueText),
                    ConversionOperatorDeclarationSyntax conversion => (conversion.OperatorKeyword.GetLocation(), conversion.ImplicitOrExplicitKeyword.ValueText + " " + SyntaxFacts.GetText(SyntaxKind.OperatorKeyword)),
                    PropertyDeclarationSyntax property => (property.Identifier.GetLocation(), property.Identifier.ValueText),
                    IndexerDeclarationSyntax indexer => (indexer.ThisKeyword.GetLocation(), indexer.ThisKeyword.ValueText),
                    EventDeclarationSyntax @event => (@event.Identifier.GetLocation(), @event.Identifier.ValueText),
                    VariableDeclaratorSyntax declarator => (declarator.Identifier.GetLocation(), declarator.Identifier.ValueText),
                    _ => throw ExceptionUtilities.UnexpectedValue(syntax),
                };
            }
        }

        /// <summary>
        /// The single match, or CS20043 for none and CS20044 for several. CS20043 names each candidate that the written
        /// signature ruled out, with the written part that ruled it out, which is also an additional location.
        /// </summary>
        private static OverriddenSignature Decide(
            MemberDeclaration declaration,
            WrittenSignature written,
            ArrayBuilder<CopiedSignature> matches,
            ArrayBuilder<(CopiedSignature Copied, int Position)> ruledOut)
        {
            var (location, name) = MemberDeclaration.GetNameForDiagnostics(declaration.Syntax);
            if (matches.Count == 1)
            {
                CopiedSignature match = matches[0];
                var isParams = match.Member.GetParameters().SelectAsArray(static (p, i, written) => p.IsParams && written.IsOpen(i), written);
                return OverriddenSignature.Create(match.Member, match.ParameterTypes, isParams, match.ReturnType);
            }

            var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
            if (matches.Count == 0)
            {
                if (ruledOut.Count == 0)
                {
                    var reason = new MessageFragment(MessageID.IDS_SharperInferNoMemberWithParameterCount, [name]);
                    diagnostics.Add(ErrorCode.ERR_SharperInferNoOverriddenMember, location, name, reason);
                }
                else
                {
                    var members = ruledOut.SelectAsArray(static (r, declaration) => describeRuledOut(r.Copied, r.Position, declaration), declaration);
                    var reason = new MessageFragment(MessageID.IDS_SharperInferAllMembersRuledOut, [name, new MessageFragmentList(members)]);
                    var info = new CSDiagnosticInfo(
                        ErrorCode.ERR_SharperInferNoOverriddenMember,
                        args: [name, reason],
                        symbols: ruledOut.SelectAsArray(static r => (Symbol)r.Copied.Member),
                        additionalLocations: ruledOut.Select(r => getWrittenLocation(r.Position, declaration)).Distinct().ToImmutableArray());
                    diagnostics.Add(info, location);
                }
            }
            else
            {
                string list = string.Join(", ", matches.Select(m => "'" + m.Member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "'"));
                diagnostics.Add(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, location, name, list);
            }

            return OverriddenSignature.CreateError(diagnostics.ToReadOnlyAndFree());

            // "'B.M(long, string)' (it takes 'long' for 'a')" or "'B.M()' (it returns 'int')".
            static MessageFragment describeRuledOut(CopiedSignature copied, int position, MemberDeclaration declaration)
            {
                string member = copied.Member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                if (position == copied.ParameterTypes.Length)
                {
                    return new MessageFragment(MessageID.IDS_SharperInferRuledOutByReturnType, [member, copied.ReturnType.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)]);
                }

                RefKind refKind = copied.Member.GetParameters()[position].RefKind;
                string type = copied.ParameterTypes[position].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                return new MessageFragment(
                    MessageID.IDS_SharperInferRuledOutByParameter,
                    [member, refKind == RefKind.None ? type : refKind.ToParameterDisplayString() + " " + type, declaration.Parameters[position].Identifier.ValueText]);
            }

            static Location getWrittenLocation(int position, MemberDeclaration declaration)
                => position == declaration.Parameters.Count
                    ? declaration.WrittenTypeSyntax.GetLocation()
                    : declaration.Parameters[position].GetLocation();
        }

        /// <summary>A part of a message, from a resource and formatted in the culture of the message.</summary>
        private sealed class MessageFragment(MessageID id, object[] arguments) : IFormattable
        {
            public override string ToString() => ToString(null, null);

            public string ToString(string? format, IFormatProvider? formatProvider)
                => string.Format(formatProvider, ErrorFacts.GetMessage(id, formatProvider as CultureInfo), arguments);
        }

        /// <summary>Message fragments separated by commas.</summary>
        private sealed class MessageFragmentList(ImmutableArray<MessageFragment> fragments) : IFormattable
        {
            public override string ToString() => ToString(null, null);

            public string ToString(string? format, IFormatProvider? formatProvider)
                => string.Join(", ", fragments.Select(f => f.ToString(format, formatProvider)));
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
            /// Reads the signature of <paramref name="declaration"/>, or returns null when nothing in it is open (a type
            /// named <c>infer</c> is in scope) or it has an <c>__arglist</c>.
            /// </summary>
            internal static WrittenSignature? Read(MemberDeclaration declaration)
            {
                Symbol owner = declaration.Owner;
                Binder binder = declaration.GetSignatureBinder();

                var parameterTypes = ArrayBuilder<TypeWithAnnotations>.GetInstance();
                var refKinds = ArrayBuilder<RefKind?>.GetInstance();
                bool anyOpen = false;
                foreach (var parameter in declaration.Parameters)
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
                        type = GetOrInfer(owner, parameter, GetDefaultValueBinder(owner, parameter)).Type;
                    }

                    anyOpen |= isOpen;
                    parameterTypes.Add(isOpen ? default : type);
                    refKinds.Add(isOpen && refnessKeyword.Kind() == SyntaxKind.None ? null : refKind);
                }

                TypeWithAnnotations returnType = binder.BindTypeOrSharperInferKeyword(declaration.TypeSyntax, BindingDiagnosticBag.Discarded, out bool returnIsInfer);
                anyOpen |= returnIsInfer;

                var result = new WrittenSignature(parameterTypes.ToImmutableAndFree(), refKinds.ToImmutableAndFree(), returnIsInfer ? default : returnType);
                return anyOpen ? result : null;
            }

            /// <summary>
            /// Null when the member's signature fits what is written: the same ref kinds where they are written, and the
            /// same types (ignoring nullability, tuple names and <c>dynamic</c>) where they are written. A written type
            /// with errors of its own fits anything; its errors are reported with the signature. Otherwise the first
            /// parameter that does not fit, or the parameter count for the return type.
            /// </summary>
            internal int? GetRuledOutPosition(CopiedSignature copied)
            {
                for (int i = 0; i < _parameterTypes.Length; i++)
                {
                    if (_refKinds[i] is { } refKind && refKind != copied.Member.GetParameters()[i].RefKind)
                    {
                        return i;
                    }

                    if (!fits(_parameterTypes[i], copied.ParameterTypes[i]))
                    {
                        return i;
                    }
                }

                return fits(_returnType, copied.ReturnType) ? null : _parameterTypes.Length;

                static bool fits(TypeWithAnnotations written, TypeWithAnnotations copied)
                    => !written.HasType || written.Type.IsErrorType() || written.Type.Equals(copied.Type, TypeCompareKind.AllIgnoreOptions);
            }
        }

        /// <summary>The signature of a candidate member, as the method would copy it.</summary>
        private readonly struct CopiedSignature
        {
            internal readonly Symbol Member;
            internal readonly ImmutableArray<TypeWithAnnotations> ParameterTypes;
            internal readonly TypeWithAnnotations ReturnType;

            private CopiedSignature(Symbol member, ImmutableArray<TypeWithAnnotations> parameterTypes, TypeWithAnnotations returnType)
            {
                Member = member;
                ParameterTypes = parameterTypes;
                ReturnType = returnType;
            }

            /// <summary>
            /// Copies the types of <paramref name="member"/> for <paramref name="owner"/>: the member's method type
            /// parameters become the method's own (alpha-renamed, since a member of a constructed base type has
            /// substituted type parameters), and custom modifiers are dropped. The method checks (and the property and
            /// event constructors) copy those from an overridden or explicitly implemented member later, as for written
            /// types.
            /// </summary>
            internal static CopiedSignature Create(Symbol member, Symbol owner)
            {
                TypeMap? typeMap = member is MethodSymbol { Arity: > 0 } method && owner is MethodSymbol ownerMethod
                    ? new TypeMap(method.TypeParameters, ownerMethod.TypeParameters, allowAlpha: true)
                    : null;
                return new CopiedSignature(
                    member,
                    member.GetParameters().SelectAsArray(static (p, typeMap) => copy(p.TypeWithAnnotations, typeMap), typeMap),
                    copy(member.GetTypeOrReturnType(), typeMap));

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
                    if (Member.GetParameters()[i].RefKind != other.Member.GetParameters()[i].RefKind ||
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
