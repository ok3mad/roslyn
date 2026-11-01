// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Sharper: the attributes of immutable types. They are recognized by name (namespace <c>Sharper</c>) on source
    /// and metadata symbols, and a Sharper file can use them without declaring them: the compiler then synthesizes them
    /// and embeds them into the assembly, like <c>IsReadOnlyAttribute</c>.
    /// </summary>
    internal enum SharperImmutabilityAttribute
    {
        Immutable,
        ImmutableHierarchy,
        UnobservableWrite,
        AssumePure,
        Phantom,
    }

    /// <summary>
    /// Sharper: immutable types, slice 1 (implementation plan, Feature 10). Decides whether a type is deeply immutable:
    /// for its exact instances (<see cref="IsImmutable"/> with <c>exact: true</c>) or for every value of the static type,
    /// and checks the declarations that claim it (<see cref="CheckDeclaration"/>). One instance per compilation, which
    /// caches the result for each type definition.
    /// </summary>
    internal sealed class SharperImmutability
    {
        internal const string AttributeNamespace = "Sharper";

        private static readonly (string Name, AttributeTargets Targets)[] s_attributes =
        [
            ("ImmutableAttribute", AttributeTargets.Class | AttributeTargets.Struct),
            ("ImmutableHierarchyAttribute", AttributeTargets.Class | AttributeTargets.Interface),
            ("UnobservableWriteAttribute", AttributeTargets.Field),
            ("AssumePureAttribute", AttributeTargets.Property | AttributeTargets.Method),
            ("PhantomAttribute", AttributeTargets.GenericParameter),
        ];

        private readonly CSharpCompilation _compilation;
        private readonly ConcurrentDictionary<NamedTypeSymbol, DefinitionInfo> _definitions = new ConcurrentDictionary<NamedTypeSymbol, DefinitionInfo>(ReferenceEqualityComparer.Instance);
        private readonly NamedTypeSymbol?[] _lazyAttributeTypes = new NamedTypeSymbol?[s_attributes.Length];
        private NamespaceSymbol? _lazyAttributeNamespace;
        private int _lazyHasSharperTrees;

        internal SharperImmutability(CSharpCompilation compilation)
        {
            _compilation = compilation;
        }

        private bool HasSharperTrees
        {
            get
            {
                if (_lazyHasSharperTrees == 0)
                {
                    bool any = _compilation.SyntaxTrees.Any(static t => ((CSharpParseOptions)t.Options).IsFeatureEnabled(MessageID.IDS_FeatureSharper));
                    _lazyHasSharperTrees = any ? 1 : 2;
                }

                return _lazyHasSharperTrees == 1;
            }
        }

        #region Attributes

        /// <summary>
        /// The attribute an attribute name stands for (<c>Immutable</c> or <c>ImmutableAttribute</c>), if any.
        /// </summary>
        internal static bool TryGetAttributeKind(string name, bool isVerbatim, out SharperImmutabilityAttribute kind)
        {
            for (int i = 0; i < s_attributes.Length; i++)
            {
                string fullName = s_attributes[i].Name;
                if (name == fullName || (!isVerbatim && name.Length + "Attribute".Length == fullName.Length && fullName.StartsWith(name, StringComparison.Ordinal)))
                {
                    kind = (SharperImmutabilityAttribute)i;
                    return true;
                }
            }

            kind = default;
            return false;
        }

        /// <summary>
        /// The attribute type that an attribute name binds to in a Sharper file when no type of that name is in scope:
        /// <c>Sharper.ImmutableAttribute</c> declared in this compilation, or else a synthesized one that is embedded.
        /// </summary>
        internal NamedTypeSymbol GetAttributeType(SharperImmutabilityAttribute kind, BindingDiagnosticBag diagnostics, SyntaxNode node)
        {
            int index = (int)kind;
            if (Volatile.Read(ref _lazyAttributeTypes[index]) is { } result)
            {
                return result;
            }

            var metadataName = MetadataTypeName.FromNamespaceAndTypeName(AttributeNamespace, s_attributes[index].Name);
            NamedTypeSymbol? declared = _compilation.SourceModule.LookupTopLevelMetadataType(ref metadataName);
            if (declared is null || declared.IsErrorType())
            {
                NamedTypeSymbol systemAttribute = _compilation.GetWellKnownType(WellKnownType.System_Attribute);
                Binder.ReportUseSite(systemAttribute, diagnostics, node);
                if (_lazyAttributeNamespace is null)
                {
                    Interlocked.CompareExchange(ref _lazyAttributeNamespace, new SynthesizedNamespaceSymbol(_compilation.SourceModule.GlobalNamespace, AttributeNamespace), null);
                }

                declared = new SynthesizedEmbeddedSharperAttributeSymbol(s_attributes[index].Name, s_attributes[index].Targets, _lazyAttributeNamespace, _compilation.SourceModule, systemAttribute);
            }

            Interlocked.CompareExchange(ref _lazyAttributeTypes[index], declared, null);
            return _lazyAttributeTypes[index]!;
        }

        /// <summary>
        /// The synthesized attribute types that binding used, which the assembly embeds.
        /// </summary>
        internal ArrayBuilder<NamedTypeSymbol>? GetSynthesizedAttributeTypes()
        {
            ArrayBuilder<NamedTypeSymbol>? builder = null;
            foreach (var type in _lazyAttributeTypes)
            {
                if (type is SynthesizedEmbeddedSharperAttributeSymbol)
                {
                    (builder ??= ArrayBuilder<NamedTypeSymbol>.GetInstance()).Add(type);
                }
            }

            return builder;
        }

        internal static bool HasAttribute(Symbol symbol, SharperImmutabilityAttribute kind)
        {
            string name = s_attributes[(int)kind].Name;
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.IsTargetAttribute(AttributeNamespace, name))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPhantom(TypeParameterSymbol typeParameter)
            => HasAttribute(typeParameter, SharperImmutabilityAttribute.Phantom);

        #endregion

        #region Hard attestations

        /// <summary>
        /// The built-in hard attestations, until the attestation format exists (#142): types that are immutable without
        /// analysis (their assemblies are usually reference assemblies, which cannot be analyzed). A generic one is
        /// immutable when its type arguments are. Each name is a namespace and a metadata name; "hierarchy" means that
        /// every subclass is immutable too.
        /// </summary>
        private static readonly Dictionary<(string Namespace, string Name), bool> s_attestedTypes = new()
        {
            [("System", "TimeSpan")] = false,
            [("System", "DateTimeOffset")] = false,
            [("System", "DateOnly")] = false,
            [("System", "TimeOnly")] = false,
            [("System", "Guid")] = false,
            [("System", "Half")] = false,
            [("System", "Int128")] = false,
            [("System", "UInt128")] = false,
            [("System", "Index")] = false,
            [("System", "Range")] = false,
            [("System", "Version")] = false,
            [("System.Numerics", "BigInteger")] = false,
            [("System.Numerics", "Complex")] = false,
            [("System", "ValueTuple")] = false,
            [("System", "ValueTuple`1")] = false,
            [("System", "ValueTuple`2")] = false,
            [("System", "ValueTuple`3")] = false,
            [("System", "ValueTuple`4")] = false,
            [("System", "ValueTuple`5")] = false,
            [("System", "ValueTuple`6")] = false,
            [("System", "ValueTuple`7")] = false,
            [("System", "ValueTuple`8")] = false,
            [("System.Collections.Generic", "KeyValuePair`2")] = false,
            [("System.Collections.Immutable", "ImmutableArray`1")] = false,
            [("System.Collections.Immutable", "ImmutableList`1")] = false,
            [("System.Collections.Immutable", "ImmutableDictionary`2")] = false,
            [("System.Collections.Immutable", "ImmutableHashSet`1")] = false,
            [("System.Collections.Immutable", "ImmutableSortedDictionary`2")] = false,
            [("System.Collections.Immutable", "ImmutableSortedSet`1")] = false,
            [("System.Collections.Immutable", "ImmutableQueue`1")] = false,
            [("System.Collections.Immutable", "ImmutableStack`1")] = false,
            [("System.Collections.Frozen", "FrozenDictionary`2")] = true,
            [("System.Collections.Frozen", "FrozenSet`1")] = true,
        };

        private static bool IsAttested(NamedTypeSymbol definition, out bool hierarchy)
        {
            hierarchy = false;
            switch (definition.SpecialType)
            {
                case SpecialType.System_Object: // its exact instances only: it is not sealed
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Decimal:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_String:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                case SpecialType.System_DateTime:
                case SpecialType.System_Nullable_T:
                    return true;
            }

            return definition.ContainingType is null &&
                definition.ContainingNamespace is { } ns &&
                s_attestedTypes.TryGetValue((ns.ToDisplayString(SymbolDisplayFormat.QualifiedNameOnlyFormat), definition.MetadataName), out hierarchy);
        }

        #endregion

        #region The check

        /// <summary>
        /// Whether a type is immutable: for its exact instances, or (<paramref name="exact"/> false) for every value of
        /// the static type. A type parameter is not known to be immutable (that is <c>where T : immutable</c>, #140).
        /// </summary>
        internal bool IsImmutable(TypeSymbol type, bool exact)
            => CheckType(type, exact, context: null) == TypeFailure.None;

        /// <summary>
        /// Why a type in a field, a base or a type argument is not immutable.
        /// </summary>
        private enum TypeFailure
        {
            None,
            NotImmutable,
            NotSealed,
            NotClaimed,
            Phantom,
        }

        /// <param name="context">The definition being analyzed, whose type parameters are assumed immutable unless
        /// they are <c>[Phantom]</c>: a generic type is immutable when its type arguments are.</param>
        /// <param name="localOnly">Assume that the types of this compilation are immutable, to find a reason for a
        /// failure that is not another type's (see <see cref="Explain"/>).</param>
        private TypeFailure CheckType(TypeSymbol type, bool exact, NamedTypeSymbol? context, bool localOnly = false)
        {
            switch (type)
            {
                case TypeParameterSymbol typeParameter:
                    if (context is null || !IsTypeParameterOf(typeParameter, context))
                    {
                        return TypeFailure.NotImmutable;
                    }

                    return IsPhantom(typeParameter) ? TypeFailure.Phantom : TypeFailure.None;

                case NamedTypeSymbol named when !named.IsErrorType():
                    var definition = named.OriginalDefinition;
                    var info = localOnly && IsInThisCompilation(definition) ? DefinitionInfo.Immutable : GetInfo(definition);
                    if (!info.IsImmutable)
                    {
                        return !IsInThisCompilation(definition) && !IsAttested(definition, out _) ? TypeFailure.NotClaimed : TypeFailure.NotImmutable;
                    }

                    if (!exact && !IsClosed(definition))
                    {
                        return TypeFailure.NotSealed;
                    }

                    var typeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();
                    var typeParameters = ArrayBuilder<TypeParameterSymbol>.GetInstance();
                    named.GetAllTypeArgumentsNoUseSiteDiagnostics(typeArguments);
                    definition.GetAllTypeParameters(typeParameters);
                    Debug.Assert(typeArguments.Count == typeParameters.Count);

                    var result = TypeFailure.None;
                    for (int i = 0; i < typeArguments.Count && result == TypeFailure.None; i++)
                    {
                        if (!IsPhantom(typeParameters[i]))
                        {
                            switch (CheckType(typeArguments[i].Type, exact: false, context, localOnly))
                            {
                                case TypeFailure.None:
                                    break;
                                case TypeFailure.Phantom:
                                    result = TypeFailure.Phantom;
                                    break;
                                default:
                                    result = TypeFailure.NotImmutable;
                                    break;
                            }
                        }
                    }

                    typeArguments.Free();
                    typeParameters.Free();
                    return result;

                case NamedTypeSymbol:
                    // An error is reported where the type is written.
                    return TypeFailure.None;

                default:
                    // Arrays, pointers, function pointers and dynamic.
                    return TypeFailure.NotImmutable;
            }
        }

        private static bool IsTypeParameterOf(TypeParameterSymbol typeParameter, NamedTypeSymbol definition)
        {
            for (var type = definition; type is not null; type = type.ContainingType)
            {
                if (typeParameter.ContainingSymbol == (object)type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether every value of the static type is an exact instance of an immutable type, given that the exact
        /// type is immutable: a value type, a sealed class, or a type whose whole hierarchy is immutable.
        /// </summary>
        private static bool IsClosed(NamedTypeSymbol definition)
        {
            if (definition.IsValueType || definition.IsSealed || definition.IsStatic)
            {
                return true;
            }

            if (IsAttested(definition, out bool hierarchy))
            {
                return hierarchy;
            }

            // PROTOTYPE(sharper): an internal [Immutable] class whose subclasses are all visible and immutable is closed too.
            return HasAttribute(definition, SharperImmutabilityAttribute.ImmutableHierarchy);
        }

        private bool IsInThisCompilation(NamedTypeSymbol definition)
            => (object)definition.ContainingModule == _compilation.SourceModule;

        /// <summary>
        /// Whether a type definition is immutable for its exact instances, assuming that its type parameters are
        /// immutable unless they are <c>[Phantom]</c>, and why not.
        /// </summary>
        internal sealed class DefinitionInfo
        {
            internal static readonly DefinitionInfo Immutable = new DefinitionInfo(isImmutable: true, isDisproven: false, reason: null, baseType: null);

            internal readonly bool IsImmutable;
            internal readonly bool IsDisproven;
            internal readonly IFormattable? Reason;
            internal readonly NamedTypeSymbol? BaseType;

            internal DefinitionInfo(bool isImmutable, bool isDisproven, IFormattable? reason, NamedTypeSymbol? baseType)
            {
                IsImmutable = isImmutable;
                IsDisproven = isDisproven;
                Reason = reason;
                BaseType = baseType;
            }

            internal static DefinitionInfo NotProven(MessageID reason, params object[] arguments)
                => new DefinitionInfo(isImmutable: false, isDisproven: false, new Reason(reason, arguments), baseType: null);

            internal static DefinitionInfo Disproven(MessageID reason, params object[] arguments)
                => new DefinitionInfo(isImmutable: false, isDisproven: true, new Reason(reason, arguments), baseType: null);

            /// <summary>The reason as a sentence fragment, for messages that say why a type is not immutable.</summary>
            internal IFormattable GetReasonFragment()
                => Reason ?? new Reason(MessageID.IDS_SharperImmutableReasonBase, [BaseType!]);
        }

        /// <summary>
        /// A localizable sentence fragment with arguments, such as "'Xs' has type 'List&lt;int&gt;', which is not immutable".
        /// </summary>
        private sealed class Reason : IFormattable
        {
            private readonly MessageID _id;
            private readonly object[] _arguments;

            internal Reason(MessageID id, object[] arguments)
            {
                _id = id;
                _arguments = arguments;
            }

            public override string ToString() => ToString(null, null);

            public string ToString(string? format, IFormatProvider? formatProvider)
            {
                var culture = formatProvider as CultureInfo;
                var formatted = _arguments.Select(a => a is Symbol symbol ? symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) : a).ToArray();
                return string.Format(culture, ErrorFacts.GetMessage(_id, culture), formatted);
            }
        }

        private sealed class Frame(SharperImmutability owner, NamedTypeSymbol definition, int index)
        {
            internal readonly SharperImmutability Owner = owner;
            internal readonly NamedTypeSymbol Definition = definition;
            internal readonly int Index = index;
            internal int LowLink = index;
            internal readonly List<(NamedTypeSymbol Definition, DefinitionInfo Info)> Members = new List<(NamedTypeSymbol, DefinitionInfo)>();
        }

        /// <summary>The definitions being analyzed on this thread, for finding cycles (Tarjan's algorithm).</summary>
        [ThreadStatic]
        private static List<Frame>? s_stack;

        /// <summary>The failures of the strongly connected component being resolved on this thread.</summary>
        [ThreadStatic]
        private static Dictionary<NamedTypeSymbol, DefinitionInfo>? s_resolved;

        /// <summary>
        /// The immutability of a type definition. Definitions that depend on each other (a strongly connected
        /// component) are assumed immutable until one of them is disproven, so <c>sealed record Node(int V, Node? Next)</c>
        /// is immutable. Each step only widens what is accepted, so the result does not depend on where the analysis
        /// starts.
        /// </summary>
        internal DefinitionInfo GetInfo(NamedTypeSymbol definition)
        {
            Debug.Assert(definition.IsDefinition);

            if (_definitions.TryGetValue(definition, out var info) ||
                (s_resolved is { } resolved && resolved.TryGetValue(definition, out info)))
            {
                return info;
            }

            var stack = s_stack ??= new List<Frame>();
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i].Definition == (object)definition && stack[i].Owner == this)
                {
                    // A cycle: assume immutable until the component is resolved.
                    var top = stack[^1];
                    top.LowLink = Math.Min(top.LowLink, i);
                    return DefinitionInfo.Immutable;
                }
            }

            var frame = new Frame(this, definition, stack.Count);
            stack.Add(frame);
            try
            {
                info = Analyze(definition);
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (frame.LowLink < frame.Index)
            {
                // Part of a component whose root is further down the stack: the root decides.
                var caller = stack[^1];
                caller.LowLink = Math.Min(caller.LowLink, frame.LowLink);
                caller.Members.AddRange(frame.Members);
                caller.Members.Add((definition, info));
                return info;
            }

            if (frame.Members.Count == 0)
            {
                return Publish(definition, info);
            }

            frame.Members.Add((definition, info));
            ResolveComponent(frame.Members);
            return _definitions[definition];
        }

        private void ResolveComponent(List<(NamedTypeSymbol Definition, DefinitionInfo Info)> members)
        {
            if (members.All(static m => m.Info.IsImmutable))
            {
                foreach (var (definition, info) in members)
                {
                    _definitions.TryAdd(definition, info);
                }

                return;
            }

            // A failure under the optimistic assumption is a real failure. The other members are analyzed again
            // knowing it, then each failure is explained once the whole component is known (see Explain).
            var previous = s_resolved;
            var resolved = s_resolved ??= new Dictionary<NamedTypeSymbol, DefinitionInfo>(ReferenceEqualityComparer.Instance);
            try
            {
                foreach (var (definition, info) in members)
                {
                    if (!info.IsImmutable)
                    {
                        resolved[definition] = info;
                    }
                }

                foreach (var (definition, info) in members)
                {
                    if (info.IsImmutable)
                    {
                        GetInfo(definition);
                    }
                }

                foreach (var (definition, info) in members)
                {
                    if (!info.IsImmutable)
                    {
                        Publish(definition, info);
                    }
                }
            }
            finally
            {
                if (previous is null)
                {
                    s_resolved = null;
                }
            }
        }

        private DefinitionInfo Publish(NamedTypeSymbol definition, DefinitionInfo info)
        {
            if (!info.IsImmutable)
            {
                info = Explain(definition, info);
            }

            return _definitions.GetOrAdd(definition, info);
        }

        /// <summary>
        /// The reason for a failure, which must not depend on where the analysis started: a cycle is found from
        /// whichever member is reached first, and the first check that fails then varies. A reason of its own comes
        /// first (found assuming the other types of this compilation immutable), so that the members of a cycle point
        /// to the one that breaks it. Otherwise the reason is the first check that fails, now that every type it
        /// depends on is known.
        /// </summary>
        private DefinitionInfo Explain(NamedTypeSymbol definition, DefinitionInfo info)
        {
            var previous = s_resolved;
            var resolved = s_resolved ??= new Dictionary<NamedTypeSymbol, DefinitionInfo>(ReferenceEqualityComparer.Instance);
            resolved[definition] = info;
            try
            {
                var explained = Analyze(definition, localOnly: true);
                if (explained.IsImmutable)
                {
                    explained = Analyze(definition);
                }

                Debug.Assert(!explained.IsImmutable);
                return explained;
            }
            finally
            {
                if (previous is null)
                {
                    s_resolved = null;
                }
            }
        }

        private DefinitionInfo Analyze(NamedTypeSymbol definition, bool localOnly = false)
        {
            if (IsAttested(definition, out _))
            {
                return DefinitionInfo.Immutable;
            }

            switch (definition.TypeKind)
            {
                case TypeKind.Enum:
                case TypeKind.Error:
                    return DefinitionInfo.Immutable;
                case TypeKind.Interface:
                    // An interface has no state of its own; every value of it is immutable only with [ImmutableHierarchy]
                    // (see IsClosed).
                    return DefinitionInfo.Immutable;
                case TypeKind.Class:
                case TypeKind.Struct:
                    break;
                default:
                    // Delegates, which can capture anything, and other kinds.
                    return DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonNotClaimed, definition);
            }

            if (!IsInThisCompilation(definition))
            {
                // Across assemblies only claims count; consumers trust them and never check them again.
                return HasAttribute(definition, SharperImmutabilityAttribute.Immutable)
                    ? DefinitionInfo.Immutable
                    : DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonNotClaimed, definition);
            }

            if (definition.BaseTypeNoUseSiteDiagnostics is { } baseType &&
                !baseType.IsObjectType() &&
                definition.TypeKind == TypeKind.Class &&
                CheckType(baseType, exact: true, definition, localOnly) != TypeFailure.None)
            {
                return new DefinitionInfo(isImmutable: false, isDisproven: false, reason: null, baseType: baseType);
            }

            bool isClass = definition.TypeKind == TypeKind.Class;
            var members = definition.GetMembers();
            var capturedParameters = (definition as SourceMemberContainerTypeSymbol)?.PrimaryConstructor?.GetCapturedParameters();

            // State that can be written after construction disproves the claim; check that first. An event can always
            // be subscribed to, and captured primary constructor parameters are fields that are not readonly.
            foreach (var member in members)
            {
                if (member is EventSymbol { IsStatic: false } @event)
                {
                    return DefinitionInfo.Disproven(MessageID.IDS_SharperImmutableReasonEvent, @event);
                }
            }

            if (isClass)
            {
                foreach (var member in members)
                {
                    if (member is PropertySymbol { IsStatic: false, SetMethod: { IsInitOnly: false } } property)
                    {
                        return DefinitionInfo.Disproven(MessageID.IDS_SharperImmutableReasonSetAccessor, property);
                    }
                }

                foreach (var member in members)
                {
                    if (member is FieldSymbol { IsStatic: false, IsConst: false, IsReadOnly: false } field &&
                        !isCapturedParameter(field) &&
                        !HasAttribute(field, SharperImmutabilityAttribute.UnobservableWrite))
                    {
                        return DefinitionInfo.Disproven(MessageID.IDS_SharperImmutableReasonFieldNotReadOnly, field.AssociatedSymbol ?? field);
                    }
                }

                if (capturedParameters?.Count > 0)
                {
                    var parameter = capturedParameters.Keys.OrderBy(static p => p.Ordinal).First();
                    return DefinitionInfo.Disproven(MessageID.IDS_SharperImmutableReasonCapturedParameter, parameter.Name);
                }
            }

            foreach (var member in members)
            {
                if (member is FieldSymbol { IsStatic: false, IsConst: false } field &&
                    !isCapturedParameter(field) &&
                    CheckMemberType(field.AssociatedSymbol ?? field, field, definition, localOnly) is { } failure)
                {
                    return failure;
                }
            }

            if (capturedParameters is not null)
            {
                foreach (var (parameter, field) in capturedParameters.OrderBy(static p => p.Key.Ordinal))
                {
                    if (CheckMemberType(parameter.Name, field, definition, localOnly) is { } failure)
                    {
                        return failure;
                    }
                }
            }

            foreach (var member in members)
            {
                if (member.IsStatic)
                {
                    continue;
                }

                MethodSymbol? observer = member switch
                {
                    PropertySymbol property => property.GetMethod,
                    MethodSymbol { Name: WellKnownMemberNames.ObjectEquals, ParameterCount: 1, MethodKind: MethodKind.Ordinary } method => method,
                    MethodSymbol { Name: WellKnownMemberNames.ObjectGetHashCode, ParameterCount: 0, MethodKind: MethodKind.Ordinary } method => method,
                    _ => null,
                };

                if (observer is not null && FindImpureExpression(observer, definition, localOnly, out bool identity) is { } impure)
                {
                    return DefinitionInfo.NotProven(
                        identity ? MessageID.IDS_SharperImmutableReasonObserverIdentity : MessageID.IDS_SharperImmutableReasonObserver,
                        member,
                        impure.ToString());
                }
            }

            return DefinitionInfo.Immutable;

            bool isCapturedParameter(FieldSymbol field)
                => capturedParameters?.Values.Contains(field) == true;
        }

        /// <summary>The failure for a field (or the auto-property or captured parameter it stores) whose type is not immutable.</summary>
        private DefinitionInfo? CheckMemberType(object member, FieldSymbol field, NamedTypeSymbol definition, bool localOnly)
        {
            if (field.RefKind != RefKind.None)
            {
                return DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonRefField, member);
            }

            var type = field.Type;
            return CheckType(type, exact: false, definition, localOnly) switch
            {
                TypeFailure.None => null,
                TypeFailure.NotSealed => DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonTypeNotSealed, member, type),
                TypeFailure.NotClaimed => DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonTypeNotClaimed, member, type),
                TypeFailure.Phantom => DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonTypePhantom, member, type),
                _ => DefinitionInfo.NotProven(MessageID.IDS_SharperImmutableReasonTypeNotImmutable, member, type),
            };
        }

        #endregion

        #region Observers

        /// <summary>
        /// The first expression of an observer (a getter, <c>Equals</c> or <c>GetHashCode</c>) that is not pure, or null
        /// when it is pure, unless it is <c>[AssumePure]</c>. An observer must return an equivalent result on every
        /// access: it reads fields, constants and other pure observers, and when its return type is statically
        /// immutable it may also create immutable values. Identity is not observable to pure code; <paramref
        /// name="identity"/> says that the expression found compares it.
        /// </summary>
        private SyntaxNode? FindImpureExpression(MethodSymbol observer, NamedTypeSymbol definition, bool localOnly, out bool identity)
        {
            identity = false;
            if (IsAssumedPure(observer) ||
                observer.IsImplicitlyDeclared ||
                observer.IsAbstract ||
                observer is SourcePropertyAccessorSymbol { IsAutoPropertyAccessor: true })
            {
                return null;
            }

            if (observer is not SourceMemberMethodSymbol sourceMethod || sourceMethod.TryGetBodyBinder() is not { } binder)
            {
                return observer.GetNonNullSyntaxNode();
            }

            var body = binder.BindMethodBody(sourceMethod.SyntaxNode, BindingDiagnosticBag.Discarded);
            if (body.HasErrors)
            {
                // The errors are reported when the body is compiled.
                return null;
            }

            // An observer whose result is statically immutable may create one: equivalent results suffice. Otherwise two
            // callers sharing one memoized result would alias a mutable object, so nothing may be allocated.
            bool allowsCreation = CheckType(observer.ReturnType, exact: false, definition, localOnly) == TypeFailure.None;
            var walker = new PurityWalker(this, observer, definition, localOnly, allowsCreation, constructorsInProgress: null);
            if (observer.Name == WellKnownMemberNames.ObjectEquals && observer.ParameterCount == 1)
            {
                walker.Skipped = FindReferenceEqualsFastPath(body, observer.Parameters[0]);
            }

            walker.Visit(body);
            identity = walker.ImpureIsIdentity;
            return walker.Impure?.Syntax;
        }

        /// <summary>
        /// <c>if (ReferenceEquals(this, other)) return true;</c> (or <c>(object)this == other</c>) as the first statement
        /// of a hand-written <c>Equals</c>: identical values are always equivalent, so the fast path is harmless.
        /// </summary>
        private static BoundStatement? FindReferenceEqualsFastPath(BoundNode body, ParameterSymbol other)
        {
            if (body is not BoundNonConstructorMethodBody { BlockBody.Statements: [BoundIfStatement { AlternativeOpt: null } first, ..] })
            {
                return null;
            }

            var consequence = first.Consequence is BoundBlock { Statements: [var single], Locals.IsEmpty: true } ? single : first.Consequence;
            if (consequence is not BoundReturnStatement { ExpressionOpt.ConstantValueOpt: { IsBoolean: true, BooleanValue: true } })
            {
                return null;
            }

            var (left, right) = first.Condition switch
            {
                BoundCall { Method: { IsStatic: true, Name: "ReferenceEquals", ContainingType.SpecialType: SpecialType.System_Object }, Arguments: [var a, var b] } => (a, b),
                BoundBinaryOperator { OperatorKind: BinaryOperatorKind.ObjectEqual } binary => (binary.Left, binary.Right),
                _ => (null, null),
            };

            if (left is null || right is null)
            {
                return null;
            }

            left = StripConversions(left);
            right = StripConversions(right);
            bool isThis(BoundExpression e) => e is BoundThisReference;
            bool isOther(BoundExpression e) => e is BoundParameter { ParameterSymbol: var p } && p == (object)other;
            return (isThis(left) && isOther(right)) || (isOther(left) && isThis(right)) ? first : null;
        }

        private static BoundExpression StripConversions(BoundExpression expression)
        {
            while (expression is BoundConversion { Conversion.IsUserDefined: false } conversion)
            {
                expression = conversion.Operand;
            }

            return expression;
        }

        private static bool IsAssumedPure(MethodSymbol method)
            => HasAttribute(method.OriginalDefinition, SharperImmutabilityAttribute.AssumePure) ||
               (method.AssociatedSymbol is PropertySymbol property && HasAttribute(property.OriginalDefinition, SharperImmutabilityAttribute.AssumePure));

        /// <summary><c>System.HashCode</c>, attested as pure: its seed is per process, part of the outside held fixed.</summary>
        private static bool IsSystemHashCode(TypeSymbol? type)
            => type is NamedTypeSymbol { Name: "HashCode", Arity: 0, ContainingType: null, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };

        /// <summary>
        /// Whether a call of <c>object.Equals(object)</c> or <c>object.GetHashCode()</c> runs <c>object</c>'s identity-based
        /// implementation. A call binds to the least derived declaration, so this is decided by whether the receiver's
        /// type overrides it.
        /// </summary>
        private static bool IsObjectIdentityMember(MethodSymbol method, BoundExpression? receiver)
        {
            if (method.IsStatic ||
                method.OriginalDefinition.ContainingType.SpecialType != SpecialType.System_Object ||
                !IsObserverMethod(method))
            {
                return false;
            }

            var type = receiver?.Type switch
            {
                TypeParameterSymbol typeParameter => typeParameter.EffectiveBaseClassNoUseSiteDiagnostics,
                NamedTypeSymbol named => named,
                _ => null,
            };

            for (; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseTypeNoUseSiteDiagnostics)
            {
                foreach (var member in type.GetMembers(method.Name))
                {
                    if (member is MethodSymbol { IsOverride: true, IsStatic: false } candidate &&
                        candidate.ParameterCount == method.ParameterCount &&
                        (candidate.ParameterCount == 0 || candidate.Parameters[0].Type.IsObjectType()))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsObserverMethod(MethodSymbol method)
            => method is { Name: WellKnownMemberNames.ObjectEquals, ParameterCount: 1 } or { Name: WellKnownMemberNames.ObjectGetHashCode, ParameterCount: 0 };

        private sealed class PurityWalker : BoundTreeWalkerWithStackGuard
        {
            private readonly SharperImmutability _owner;
            private readonly MethodSymbol _observer;
            private readonly NamedTypeSymbol _definition;
            private readonly bool _localOnly;
            private readonly bool _allowsCreation;

            /// <summary>The constructors being checked, outermost first; the last one is this walker's when it checks a constructor.</summary>
            private readonly ArrayBuilder<MethodSymbol>? _constructorsInProgress;

            /// <summary>Boxing conversions that a string concatenation of a <c>char</c> needs.</summary>
            private HashSet<BoundNode>? _allowedConversions;

            internal BoundNode? Impure;
            internal bool ImpureIsIdentity;
            internal BoundStatement? Skipped;

            internal PurityWalker(SharperImmutability owner, MethodSymbol observer, NamedTypeSymbol definition, bool localOnly, bool allowsCreation, ArrayBuilder<MethodSymbol>? constructorsInProgress)
            {
                _owner = owner;
                _observer = observer;
                _definition = definition;
                _localOnly = localOnly;
                _allowsCreation = allowsCreation;
                _constructorsInProgress = constructorsInProgress;
            }

            private bool IsConstructor => _observer.MethodKind == MethodKind.Constructor;

            public override BoundNode? Visit(BoundNode? node)
            {
                if (node is null || Impure is not null || node == (object?)Skipped)
                {
                    return null;
                }

                if (!IsAllowed(node))
                {
                    Impure = node;
                    return null;
                }

                return base.Visit(node);
            }

            public override BoundNode? VisitInterpolatedString(BoundInterpolatedString node)
            {
                // The handler or the string.Format call is how the string is made, not what it depends on: visit the holes.
                foreach (var part in node.Parts)
                {
                    var hole = part is BoundStringInsert insert ? insert.Value : part;
                    if (hole is BoundCall { ReceiverOpt: BoundInterpolatedStringHandlerPlaceholder } append)
                    {
                        // AppendLiteral(string) or AppendFormatted(value, alignment, format).
                        if (append.Arguments.IsEmpty)
                        {
                            continue;
                        }

                        hole = append.Arguments[0];
                    }

                    if (hole is BoundLiteral)
                    {
                        continue;
                    }

                    hole = StripConversions(hole);
                    if (!IsStringOrChar(hole.Type))
                    {
                        // Other holes format with the current culture.
                        Impure = hole;
                        return null;
                    }

                    Visit(hole);
                }

                return null;
            }

            private static bool IsStringOrChar(TypeSymbol? type)
                => type?.SpecialType is SpecialType.System_String or SpecialType.System_Char;

            private bool IsAllowed(BoundNode node)
            {
                if (_allowedConversions?.Contains(node) == true)
                {
                    return true;
                }

                switch (node)
                {
                    case BoundNonConstructorMethodBody:
                    case BoundBlock:
                    case BoundStatementList:
                    case BoundReturnStatement:
                    case BoundExpressionStatement:
                    case BoundLocalDeclaration:
                    case BoundMultipleLocalDeclarations:
                    case BoundIfStatement:
                    case BoundThrowStatement:
                    case BoundNoOpStatement:
                    case BoundLiteral:
                    case BoundThisReference:
                    case BoundBaseReference:
                    case BoundLocal:
                    case BoundParameter:
                    case BoundTypeExpression:
                    case BoundConditionalOperator:
                    case BoundNullCoalescingOperator:
                    case BoundConditionalAccess:
                    case BoundConditionalReceiver:
                    case BoundIsOperator:
                    case BoundAsOperator:
                    case BoundTypeOfOperator:
                    case BoundSizeOfOperator:
                    case BoundDefaultExpression:
                    case BoundThrowExpression:
                    case BoundTupleLiteral:
                    case BoundConvertedTupleLiteral:
                    case BoundIsPatternExpression:
                    case BoundConstantPattern:
                    case BoundTypePattern:
                    case BoundDeclarationPattern:
                    case BoundDiscardPattern:
                    case BoundRelationalPattern:
                    case BoundBinaryPattern:
                    case BoundNegatedPattern:
                    case BoundPropertySubpattern:
                    case BoundConvertedSwitchExpression:
                    case BoundSwitchExpressionArm:
                    case BoundDiscardExpression:
                        return true;

                    case BoundConstructorMethodBody:
                    case BoundFieldEqualsValue:
                    case BoundPropertyEqualsValue:
                        // The constructor writes the fields of its own 'this'.
                        return IsConstructor;

                    case BoundInterpolatedString:
                        // Visited by VisitInterpolatedString: the holes must be strings or chars.
                        return _allowsCreation;

                    case BoundObjectCreationExpression creation:
                        return _allowsCreation && IsPureCreation(creation);

                    case BoundRecursivePattern recursive:
                        return recursive.DeconstructMethod is null;

                    case BoundPropertySubpatternMember member:
                        return member.Symbol switch
                        {
                            FieldSymbol field => IsReadableField(field),
                            PropertySymbol property => IsPureGetter(property, receiver: null),
                            _ => false,
                        };

                    case BoundFieldAccess fieldAccess:
                        return IsReadableField(fieldAccess.FieldSymbol);

                    case BoundPropertyAccess propertyAccess:
                        return IsPureGetter(propertyAccess.PropertySymbol, propertyAccess.ReceiverOpt);

                    case BoundIndexerAccess indexerAccess:
                        return IsPureGetter(indexerAccess.Indexer, indexerAccess.ReceiverOpt);

                    case BoundCall call:
                        if (IsObjectIdentityMember(call.Method, call.ReceiverOpt) ||
                            call.Method is { IsStatic: true, Name: "ReferenceEquals", ContainingType.SpecialType: SpecialType.System_Object })
                        {
                            ImpureIsIdentity = true;
                            return false;
                        }

                        return IsPureCall(call.Method, call.ReceiverOpt);

                    case BoundBinaryOperator binary:
                        return IsPureBinaryOperator(binary);

                    case BoundUnaryOperator unary:
                        return !unary.OperatorKind.IsUserDefined() || (unary.MethodOpt is { } unaryMethod && IsPureOperator(unaryMethod));

                    case BoundConversion conversion:
                        if (conversion.Conversion.IsUserDefined)
                        {
                            return conversion.Conversion.Method is { } conversionMethod && IsPureOperator(conversionMethod);
                        }

                        // Boxing creates an object, and a method group conversion a delegate.
                        return !conversion.Conversion.IsBoxing && !conversion.Conversion.IsMethodGroup && !conversion.Conversion.IsAnonymousFunction;

                    case BoundAssignmentOperator assignment:
                        return IsWritableLocalState(assignment.Left);

                    case BoundCompoundAssignmentOperator compound:
                        return IsWritableLocalState(compound.Left) &&
                            (compound.Operator.Method is not { } compoundMethod || IsPureOperator(compoundMethod));

                    case BoundNullCoalescingAssignmentOperator nullCoalescingAssignment:
                        return IsWritableLocalState(nullCoalescingAssignment.LeftOperand);

                    case BoundIncrementOperator increment:
                        return IsWritableLocalState(increment.Operand) &&
                            (increment.MethodOpt is not { } incrementMethod || IsPureOperator(incrementMethod));

                    default:
                        // Array creation, lambdas, await, dynamic and everything else.
                        return false;
                }
            }

            private bool IsPureBinaryOperator(BoundBinaryOperator binary)
            {
                var kind = binary.OperatorKind;
                if (kind.IsUserDefined())
                {
                    return binary.BinaryOperatorMethod is { } method && IsPureOperator(method);
                }

                switch (kind.Operator())
                {
                    case BinaryOperatorKind.Addition when !binary.Type.IsValueType:
                        // Delegate combination creates an object that is not immutable. String concatenation creates an
                        // equivalent string; an operand that is neither a string nor a char formats with the current culture.
                        return _allowsCreation && kind.OperandTypes() switch
                        {
                            BinaryOperatorKind.String => true,
                            BinaryOperatorKind.StringAndObject => AllowConcatenatedObject(binary.Right),
                            BinaryOperatorKind.ObjectAndString => AllowConcatenatedObject(binary.Left),
                            _ => false,
                        };

                    case BinaryOperatorKind.Equal:
                    case BinaryOperatorKind.NotEqual:
                        // Reference equality observes identity; a comparison with null does not.
                        if (kind.OperandTypes() == BinaryOperatorKind.Object &&
                            binary.Left.ConstantValueOpt?.IsNull != true &&
                            binary.Right.ConstantValueOpt?.IsNull != true)
                        {
                            ImpureIsIdentity = true;
                            return false;
                        }

                        return true;

                    default:
                        return true;
                }
            }

            private bool AllowConcatenatedObject(BoundExpression operand)
            {
                if (operand.ConstantValueOpt?.IsNull == true)
                {
                    return true;
                }

                if (operand is BoundConversion { Conversion.IsBoxing: true, Operand.Type.SpecialType: SpecialType.System_Char })
                {
                    (_allowedConversions ??= new HashSet<BoundNode>(ReferenceEqualityComparer.Instance)).Add(operand);
                    return true;
                }

                return IsStringOrChar(StripConversions(operand).Type);
            }

            private static bool IsReadableField(FieldSymbol field)
                // Statics outside the object are held fixed, but only readonly ones are known to be.
                => !field.IsStatic || field.IsReadOnly || field.IsConst;

            /// <summary>
            /// Locals and the observer's own parameters; a field only with [UnobservableWrite]. A constructor also
            /// writes the fields and auto-properties of its own 'this'.
            /// </summary>
            private bool IsWritableLocalState(BoundExpression target)
            {
                return target switch
                {
                    BoundLocal => true,
                    BoundDiscardExpression => true,
                    BoundParameter parameter => parameter.ParameterSymbol.ContainingSymbol == (object)_observer,
                    BoundFieldAccess { ReceiverOpt: BoundThisReference } fieldAccess when IsConstructor && IsOwnMember(fieldAccess.FieldSymbol) => true,
                    BoundFieldAccess fieldAccess => HasAttribute(fieldAccess.FieldSymbol.OriginalDefinition, SharperImmutabilityAttribute.UnobservableWrite),
                    BoundPropertyAccess { ReceiverOpt: BoundThisReference, PropertySymbol: var property } when IsConstructor && IsOwnMember(property) =>
                        property.OriginalDefinition is SourcePropertySymbolBase source && (source.HasAutoPropertySet || (source.SetMethod is null && source.HasAutoPropertyGet)),
                    _ => false,
                };
            }

            private bool IsOwnMember(Symbol member)
                => member.ContainingType.OriginalDefinition == (object)_observer.ContainingType.OriginalDefinition;

            private bool IsPureGetter(PropertySymbol property, BoundExpression? receiver)
            {
                if (HasAttribute(property.OriginalDefinition, SharperImmutabilityAttribute.AssumePure) ||
                    (property.GetMethod is { } getter && IsAssumedPure(getter)))
                {
                    return true;
                }

                if (property.IsStatic)
                {
                    return false;
                }

                return IsPureObserverOf(property.ContainingType, property.IsVirtual || property.IsAbstract || (property.IsOverride && !property.IsSealed), receiver);
            }

            private bool IsPureCall(MethodSymbol method, BoundExpression? receiver)
            {
                if (IsAssumedPure(method))
                {
                    return true;
                }

                if (_allowsCreation && IsSystemHashCode(method.ContainingType))
                {
                    return true;
                }

                if (method.MethodKind == MethodKind.Constructor && receiver is BoundThisReference or BoundBaseReference)
                {
                    // The constructor initializer of a constructor being checked.
                    return IsConstructor && IsPureConstructor(method);
                }

                if (method.IsStatic || !IsObserverMethod(method))
                {
                    return false;
                }

                return IsPureObserverOf(method.ContainingType, method.IsVirtual || method.IsAbstract || (method.IsOverride && !method.IsSealed), receiver);
            }

            /// <summary>
            /// An observer of an immutable type is pure. On 'this' or 'base' it is one of the observers of this type
            /// or its bases, which are checked themselves. Otherwise either the receiver's static type is immutable,
            /// or the observer is not virtual and its type is immutable for its exact instances.
            /// </summary>
            private bool IsPureObserverOf(NamedTypeSymbol containingType, bool isVirtual, BoundExpression? receiver)
            {
                if (receiver is BoundThisReference or BoundBaseReference)
                {
                    return true;
                }

                if (receiver?.Type is { } receiverType && _owner.CheckType(receiverType, exact: false, _definition, _localOnly) == TypeFailure.None)
                {
                    return true;
                }

                return (!isVirtual || containingType.IsSealed || containingType.IsValueType) &&
                    _owner.CheckType(containingType, exact: true, _definition, _localOnly) == TypeFailure.None;
            }

            /// <summary>
            /// Operators are not observers. Those of the attested types are pure (decimal arithmetic, string equality)
            /// unless they create an object; others need [AssumePure].
            /// </summary>
            private static bool IsPureOperator(MethodSymbol method)
                => IsAssumedPure(method) ||
                   (IsAttested(method.ContainingType.OriginalDefinition, out _) && method.ReturnType.IsValueType);

            /// <summary>
            /// A new value of a statically immutable type, made by a pure constructor, is equivalent to the one made by
            /// the previous access. <c>System.HashCode</c> is attested. An object initializer runs setters and is not
            /// covered yet.
            /// </summary>
            private bool IsPureCreation(BoundObjectCreationExpression creation)
            {
                if (IsSystemHashCode(creation.Type))
                {
                    return creation.InitializerExpressionOpt is null;
                }

                return creation.InitializerExpressionOpt is null &&
                    _owner.CheckType(creation.Type, exact: false, _definition, _localOnly) == TypeFailure.None &&
                    IsPureConstructor(creation.Constructor);
            }

            /// <summary>
            /// A constructor is pure when it writes only the fields of its own 'this' and otherwise follows the observer
            /// rules, as do the instance initializers it runs and the constructor it calls. Synthesized record and
            /// primary constructors are checked the same way, so they are pure unless an initializer or a base argument
            /// is not. Constructors of other assemblies need [AssumePure].
            /// </summary>
            private bool IsPureConstructor(MethodSymbol constructor)
            {
                constructor = constructor.OriginalDefinition;
                if (IsAssumedPure(constructor) ||
                    (constructor.ContainingType.SpecialType == SpecialType.System_Object && constructor.ParameterCount == 0))
                {
                    return true;
                }

                if (_constructorsInProgress?.Contains(constructor) == true)
                {
                    // Assume the best inside a cycle; the outer check decides.
                    return true;
                }

                if (!_owner.IsInThisCompilation(constructor.ContainingType))
                {
                    return false;
                }

                var inProgress = _constructorsInProgress ?? ArrayBuilder<MethodSymbol>.GetInstance();
                inProgress.Add(constructor);
                try
                {
                    var walker = new PurityWalker(_owner, constructor, constructor.ContainingType, _localOnly, allowsCreation: true, inProgress);
                    bool callsThis;
                    if (constructor is SourceMemberMethodSymbol sourceConstructor && sourceConstructor.TryGetBodyBinder() is { } binder)
                    {
                        var body = binder.BindMethodBody(sourceConstructor.SyntaxNode, BindingDiagnosticBag.Discarded);
                        if (body is null)
                        {
                            // A partial or extern constructor without a body.
                            return false;
                        }

                        if (body.HasErrors)
                        {
                            return true;
                        }

                        // A base constructor initializer has a 'this' receiver too; 'this(...)' calls a constructor of the same type.
                        callsThis = body is BoundConstructorMethodBody { Initializer: BoundExpressionStatement { Expression: BoundCall { Method: var initializerMethod } } } &&
                            initializerMethod.ContainingType.OriginalDefinition == (object)constructor.ContainingType;
                        walker.Visit(body);
                    }
                    else if (constructor.IsImplicitlyDeclared)
                    {
                        // A default constructor: the base constructor and the initializers.
                        callsThis = false;
                        if (Binder.BindImplicitConstructorInitializer(constructor, BindingDiagnosticBag.Discarded, _owner._compilation) is { HasErrors: false } baseCall)
                        {
                            walker.Visit(baseCall);
                        }
                    }
                    else
                    {
                        return false;
                    }

                    if (!callsThis && walker.Impure is null && constructor.ContainingType is SourceMemberContainerTypeSymbol type)
                    {
                        foreach (var initializer in Binder.BindFieldInitializers(_owner._compilation, scriptInitializerOpt: null, type.InstanceInitializers, BindingDiagnosticBag.Discarded, out _))
                        {
                            if (!initializer.HasErrors)
                            {
                                walker.Visit(initializer);
                            }
                        }
                    }

                    return walker.Impure is null;
                }
                finally
                {
                    inProgress.RemoveAt(inProgress.Count - 1);
                    if (_constructorsInProgress is null)
                    {
                        inProgress.Free();
                    }
                }
            }
        }

        #endregion

        #region Declaration checks

        /// <summary>
        /// The checks of a source type declaration: a claim of <c>[Immutable]</c> is proven, a type in an
        /// <c>[ImmutableHierarchy]</c> is immutable, and <c>[Phantom]</c> and <c>[UnobservableWrite]</c> are used where
        /// they mean something. Only a project that writes a claim checks it, and only in Sharper files: consumers trust
        /// claims. The exception is a C# type in a hierarchy declared in a Sharper file of the same compilation, which
        /// gets a warning.
        /// </summary>
        internal static void CheckDeclaration(SourceMemberContainerTypeSymbol type, BindingDiagnosticBag diagnostics)
        {
            var compilation = type.DeclaringCompilation;
            var checker = compilation.SharperImmutability;
            if (!checker.HasSharperTrees || type.IsExtension)
            {
                return;
            }

            bool isSharper = IsInSharperFile(type);
            var location = type.GetFirstLocation();

            if (isSharper)
            {
                // [Immutable] elsewhere is reported by its AttributeUsage.
                bool isImmutable = type.TypeKind is TypeKind.Class or TypeKind.Struct && HasAttribute(type, SharperImmutabilityAttribute.Immutable);
                if (type.TypeKind == TypeKind.Class && !isImmutable && HasAttribute(type, SharperImmutabilityAttribute.ImmutableHierarchy))
                {
                    diagnostics.Add(ErrorCode.ERR_SharperImmutableHierarchyWithoutImmutable, location, type);
                }

                if (isImmutable)
                {
                    var info = checker.GetInfo(type);
                    if (!info.IsImmutable)
                    {
                        if (info.BaseType is { } baseType)
                        {
                            diagnostics.Add(ErrorCode.ERR_SharperImmutableBaseNotImmutable, location, type, baseType);
                        }
                        else
                        {
                            diagnostics.Add(info.IsDisproven ? ErrorCode.ERR_SharperImmutableDisproven : ErrorCode.ERR_SharperImmutableNotProven, location, type, info.Reason!);
                        }
                    }
                }

                checker.CheckAttributeUses(type, isImmutable, diagnostics);
            }

            if (type.TypeKind is TypeKind.Class or TypeKind.Struct &&
                FindImmutableHierarchy(type, isSharper) is { } hierarchy &&
                !(isSharper && HasAttribute(type, SharperImmutabilityAttribute.Immutable)))
            {
                // A type marked [Immutable] already reported why it is not.
                var info = checker.GetInfo(type);
                if (!info.IsImmutable)
                {
                    diagnostics.Add(
                        isSharper ? ErrorCode.ERR_SharperImmutableHierarchyViolated : ErrorCode.WRN_SharperImmutableHierarchyViolated,
                        location,
                        type,
                        hierarchy,
                        info.GetReasonFragment());
                }
            }
        }

        private static bool IsInSharperFile(Symbol symbol)
            => symbol.DeclaringSyntaxReferences.Any(static r => ((CSharpParseOptions)r.SyntaxTree.Options).IsFeatureEnabled(MessageID.IDS_FeatureSharper));

        /// <summary>
        /// A base class or interface marked <c>[ImmutableHierarchy]</c>. For a C# type, only one declared in a Sharper
        /// file of this compilation, so that compilations without Sharper behave as upstream.
        /// </summary>
        private static NamedTypeSymbol? FindImmutableHierarchy(NamedTypeSymbol type, bool isSharper)
        {
            for (var baseType = type.BaseTypeNoUseSiteDiagnostics; baseType is not null; baseType = baseType.BaseTypeNoUseSiteDiagnostics)
            {
                if (isHierarchy(baseType.OriginalDefinition))
                {
                    return baseType.OriginalDefinition;
                }
            }

            foreach (var @interface in type.AllInterfacesNoUseSiteDiagnostics)
            {
                if (isHierarchy(@interface.OriginalDefinition))
                {
                    return @interface.OriginalDefinition;
                }
            }

            return null;

            bool isHierarchy(NamedTypeSymbol definition)
            {
                if (!isSharper && ((object)definition.ContainingAssembly != type.ContainingAssembly || !IsInSharperFile(definition)))
                {
                    return false;
                }

                return HasAttribute(definition, SharperImmutabilityAttribute.ImmutableHierarchy);
            }
        }

        private void CheckAttributeUses(SourceMemberContainerTypeSymbol type, bool isImmutable, BindingDiagnosticBag diagnostics)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is FieldSymbol field &&
                    !field.IsImplicitlyDeclared &&
                    HasAttribute(field, SharperImmutabilityAttribute.UnobservableWrite) &&
                    (field.IsStatic || field.IsReadOnly || field.IsConst || !isImmutable || type.TypeKind != TypeKind.Class))
                {
                    diagnostics.Add(ErrorCode.ERR_SharperMisplacedUnobservableWrite, field.GetFirstLocation());
                }
            }

            foreach (var typeParameter in type.TypeParameters)
            {
                if (IsPhantom(typeParameter) && FindPhantomUse(type, typeParameter) is { } use)
                {
                    diagnostics.Add(ErrorCode.ERR_SharperPhantomTypeParameterInState, typeParameter.GetFirstLocation(), typeParameter, use);
                }
            }
        }

        /// <summary>The field or base type that mentions a [Phantom] type parameter, which then is not phantom.</summary>
        private static Symbol? FindPhantomUse(SourceMemberContainerTypeSymbol type, TypeParameterSymbol typeParameter)
        {
            if (type.BaseTypeNoUseSiteDiagnostics is { } baseType && usesInState(baseType))
            {
                return baseType;
            }

            foreach (var member in type.GetMembers())
            {
                if (member is FieldSymbol { IsStatic: false } field && field.Type.ContainsTypeParameter(typeParameter))
                {
                    return field.AssociatedSymbol ?? field;
                }
            }

            if (type.PrimaryConstructor?.GetCapturedParameters() is { } captured)
            {
                foreach (var (parameter, _) in captured.OrderBy(static p => p.Key.Ordinal))
                {
                    if (parameter.Type.ContainsTypeParameter(typeParameter))
                    {
                        return parameter;
                    }
                }
            }

            return null;

            // A base type argument counts unless the base's own type parameter is [Phantom] too.
            bool usesInState(NamedTypeSymbol baseType)
            {
                var typeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();
                var typeParameters = ArrayBuilder<TypeParameterSymbol>.GetInstance();
                baseType.GetAllTypeArgumentsNoUseSiteDiagnostics(typeArguments);
                baseType.OriginalDefinition.GetAllTypeParameters(typeParameters);
                bool result = false;
                for (int i = 0; i < typeArguments.Count && !result; i++)
                {
                    result = typeArguments[i].Type.ContainsTypeParameter(typeParameter) && !IsPhantom(typeParameters[i]);
                }

                typeArguments.Free();
                typeParameters.Free();
                return result;
            }
        }

        #endregion
    }
}
