// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper interface initializer (#30): <c>new IProfiles { Load = id =&gt; ..., Plan = "Pro", _ = Unexpected }</c>
    /// creates an instance of a generated class that implements the interface, where C# reports CS0144. A method is
    /// assigned a function (a lambda, a method group or a delegate), converted to the delegate of the method as in #29; a
    /// property with a getter and no setter is assigned its value. Every member without a default implementation must be
    /// assigned, unless <c>_ = Unexpected</c> makes the unassigned ones throw.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Whether <c>new I { ... }</c> or <c>new() { ... }</c> of an interface is a Sharper interface initializer: an object
        /// initializer, no arguments, in Sharper code. Without an initializer C#'s CS0144 stays.
        /// </summary>
        private static bool IsSharperInterfaceInitializer(SyntaxNode node, AnalyzedArguments analyzedArguments, InitializerExpressionSyntax? initializerOpt)
            => initializerOpt is { RawKind: (int)SyntaxKind.ObjectInitializerExpression } &&
                analyzedArguments.Arguments.Count == 0 &&
                node.IsFeatureEnabled(MessageID.IDS_FeatureSharper);

        private BoundExpression BindSharperInterfaceInitializer(SyntaxNode node, NamedTypeSymbol interfaceType, InitializerExpressionSyntax initializer, BindingDiagnosticBag diagnostics)
        {
            bool hasErrors = false;
            var implementable = ArrayBuilder<(Symbol Member, bool IsAbstract)>.GetInstance();
            if (GetSharperImplementableMembers(interfaceType, implementable) is { } staticAbstract)
            {
                Error(diagnostics, ErrorCode.ERR_SharperInterfaceInitializerStaticAbstract, node, interfaceType, staticAbstract);
                hasErrors = true;
            }

            var members = ArrayBuilder<Symbol>.GetInstance();
            var values = ArrayBuilder<BoundExpression>.GetInstance();
            var fieldTypes = ArrayBuilder<TypeSymbol>.GetInstance();
            var recovery = ArrayBuilder<BoundExpression>.GetInstance();
            bool throwsForUnassigned = false;

            foreach (var expression in initializer.Expressions)
            {
                if (expression is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: IdentifierNameSyntax name } assignment ||
                    assignment.Right is InitializerExpressionSyntax)
                {
                    Error(diagnostics, ErrorCode.ERR_InvalidInitializerElementInitializer, expression);
                    hasErrors = true;
                    continue;
                }

                string memberName = name.Identifier.ValueText;
                if (memberName == "_" &&
                    assignment.Right is IdentifierNameSyntax { Identifier.ValueText: "Unexpected" } &&
                    !implementable.Any(static (m, memberName) => m.Member.Name == memberName, memberName))
                {
                    // The strict mode of mock libraries: the members left unassigned throw when called.
#if DEBUG
                    // 'Unexpected' is never looked up, but the method compiler expects every identifier outside an
                    // initializer's member names to be bound.
                    AdjustIdentifierMapIfAny((IdentifierNameSyntax)assignment.Right, invoked: false);
#endif
                    if (throwsForUnassigned)
                    {
                        Error(diagnostics, ErrorCode.ERR_MemberAlreadyInitialized, name, memberName);
                        hasErrors = true;
                    }

                    throwsForUnassigned = true;
                    continue;
                }

                Symbol? member = null;
                int count = 0;
                foreach (var (candidate, _) in implementable)
                {
                    if (candidate.Name == memberName)
                    {
                        member ??= candidate;
                        count++;
                    }
                }

                if (member is null)
                {
                    Error(diagnostics, ErrorCode.ERR_NoSuchMember, name, interfaceType, memberName);
                }
                else if (count > 1 || !TryGetSharperInterfaceInitializerValueType(interfaceType, member, name, out var valueType, out var valueTypeOverDefinition))
                {
                    Error(diagnostics, ErrorCode.ERR_SharperInterfaceInitializerBadMember, name, member);
                }
                else if (members.Contains(member))
                {
                    Error(diagnostics, ErrorCode.ERR_MemberAlreadyInitialized, name, memberName);
                }
                else
                {
                    var value = BindValue(assignment.Right, diagnostics, BindValueKind.RValue);
                    members.Add(member);
                    values.Add(GenerateConversionForAssignment(valueType, value, diagnostics));
                    fieldTypes.Add(valueTypeOverDefinition);
                    continue;
                }

                hasErrors = true;
                recovery.Add(BindToNaturalType(BindValue(assignment.Right, diagnostics, BindValueKind.RValue), BindingDiagnosticBag.Discarded));
            }

            if (!throwsForUnassigned)
            {
                var missing = ArrayBuilder<string>.GetInstance();
                foreach (var (member, isAbstract) in implementable)
                {
                    if (isAbstract && !members.Contains(member))
                    {
                        missing.Add("'" + member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "'");
                    }
                }

                string list = string.Join(", ", missing);
                bool anyMissing = missing.Count > 0;
                missing.Free();
                if (anyMissing)
                {
                    Error(diagnostics, ErrorCode.ERR_SharperInterfaceInitializerMissingMembers, node, interfaceType, list);
                    hasErrors = true;
                }
            }

            implementable.Free();

            if (hasErrors)
            {
                members.Free();
                fieldTypes.Free();
                recovery.AddRange(values);
                values.Free();
                return BadExpression(node, LookupResultKind.NotCreatable, ImmutableArray.Create<Symbol>(interfaceType), recovery.ToImmutableAndFree());
            }

            recovery.Free();

            // PROTOTYPE(sharper): the semantic model finds no symbol for the member names on the left ('Load' in 'Load = ...').
            return new BoundSharperInterfaceCreation(node, members.ToImmutableAndFree(), values.ToImmutableAndFree(), fieldTypes.ToImmutableAndFree(), throwsForUnassigned, interfaceType);
        }

        /// <summary>
        /// The members a class implementing the interface implements: the instance methods (not accessors), properties,
        /// indexers and events of the interface and its base interfaces, each with whether it needs an implementation
        /// (it is abstract and no interface in between implements it with a default). Returns a static abstract member, which
        /// no generated class can implement, if there is one.
        /// </summary>
        internal static Symbol? GetSharperImplementableMembers(NamedTypeSymbol interfaceType, ArrayBuilder<(Symbol Member, bool IsAbstract)> builder)
        {
            Symbol? staticAbstract = add(interfaceType, interfaceType, builder);
            foreach (var baseInterface in interfaceType.AllInterfacesNoUseSiteDiagnostics)
            {
                staticAbstract ??= add(interfaceType, baseInterface, builder);
            }

            return staticAbstract;

            static Symbol? add(NamedTypeSymbol interfaceType, NamedTypeSymbol declaringInterface, ArrayBuilder<(Symbol Member, bool IsAbstract)> builder)
            {
                Symbol? staticAbstract = null;
                foreach (var member in declaringInterface.GetMembersUnordered())
                {
                    if (member.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event) ||
                        member is MethodSymbol { MethodKind: not MethodKind.Ordinary })
                    {
                        // Accessors are implemented with their property or event, and explicit implementations are defaults.
                        continue;
                    }

                    if (member.IsStatic)
                    {
                        if (member.IsAbstract)
                        {
                            staticAbstract ??= member;
                        }

                        continue;
                    }

                    if (!member.IsAbstract && !member.IsVirtual)
                    {
                        // A sealed or private member with a body is not implemented by a class.
                        continue;
                    }

                    bool isAbstract = member.IsAbstract &&
                        (ReferenceEquals(declaringInterface, interfaceType) ||
                         interfaceType.FindImplementationForInterfaceMember(member) is null or { IsAbstract: true });
                    builder.Add((member, isAbstract));
                }

                return staticAbstract;
            }
        }

        /// <summary>
        /// The type a member's value converts to in an interface initializer: the delegate of a method that is not generic, or
        /// the type of a property with a getter and no setter that is not an indexer. Also the type over the type parameters
        /// of the interface's definition, for the generated class's field.
        /// </summary>
        private bool TryGetSharperInterfaceInitializerValueType(
            NamedTypeSymbol interfaceType,
            Symbol member,
            SyntaxNode syntax,
            [NotNullWhen(true)] out TypeSymbol? valueType,
            [NotNullWhen(true)] out TypeSymbol? valueTypeOverDefinition)
        {
            valueType = null;
            valueTypeOverDefinition = null;

            var definition = interfaceType.OriginalDefinition;
            var memberOverDefinition = GetSharperMemberOverDefinition(definition, member);
            switch (memberOverDefinition)
            {
                case MethodSymbol { IsGenericMethod: false, IsVararg: false } method:
                    valueTypeOverDefinition = GetMethodGroupOrLambdaDelegateType(syntax, method, hasParams: false);
                    break;

                case PropertySymbol { IsIndexer: false, GetMethod: not null, SetMethod: null, RefKind: RefKind.None } property:
                    valueTypeOverDefinition = property.Type;
                    break;
            }

            if (valueTypeOverDefinition is null)
            {
                return false;
            }

            if (ReferenceEquals(definition, interfaceType))
            {
                valueType = valueTypeOverDefinition;
                return true;
            }

            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            var typeMap = new TypeMap(definition.GetAllTypeParameters(), interfaceType.GetAllTypeArguments(ref discardedUseSiteInfo));
            valueType = typeMap.SubstituteType(valueTypeOverDefinition).Type;
            return true;
        }

        /// <summary>
        /// The member of the interface's definition (or of one of its base interfaces, as the definition sees them) that a
        /// member of a constructed interface stands for.
        /// </summary>
        internal static Symbol GetSharperMemberOverDefinition(NamedTypeSymbol interfaceDefinition, Symbol member)
        {
            Debug.Assert(interfaceDefinition.IsDefinition);
            var builder = ArrayBuilder<(Symbol Member, bool IsAbstract)>.GetInstance();
            GetSharperImplementableMembers(interfaceDefinition, builder);
            Symbol? result = null;
            foreach (var (candidate, _) in builder)
            {
                if (candidate.OriginalDefinition.Equals(member.OriginalDefinition, TypeCompareKind.AllIgnoreOptions))
                {
                    result = candidate;
                    break;
                }
            }

            builder.Free();
            Debug.Assert(result is not null);
            return result;
        }
    }
}
