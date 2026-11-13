// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal abstract partial class NamedTypeSymbol
    {
        private sealed partial class UncommonProperties
        {
            public SharperFunctionTypeNames? _sharperFunctionTypeNames;
        }

        /// <summary>
        /// Sharper: the parameter names of the function type <c>(int width, int height) -> int</c>, carried on the
        /// constructed <c>Func</c> or <c>Action</c> the way a tuple carries its element names. One entry per parameter
        /// of the delegate, null for a parameter without a name; default when the type has no names. The names are not
        /// part of the type's meaning: a comparison with <see cref="TypeCompareKind.IgnoreTupleNames"/> ignores them.
        /// </summary>
        internal ImmutableArray<string?> SharperFunctionTypeParameterNames
            => _lazyUncommonProperties?._sharperFunctionTypeNames is { } names ? names.Names : default;

        /// <summary>
        /// Called by the constructor of the one type that stores names, before the symbol is published.
        /// </summary>
        protected void InitializeSharperFunctionTypeNames(SharperFunctionTypeNames? names)
        {
            if (names is not null)
            {
                GetUncommonProperties()._sharperFunctionTypeNames = names;
            }
        }

        /// <summary>
        /// Copy this type, with the given parameter names of a Sharper function type. Names where every entry is null
        /// are no names. Only a constructed delegate type stores names; any other type is returned unchanged.
        /// </summary>
        internal NamedTypeSymbol WithSharperFunctionTypeParameterNames(ImmutableArray<string?> names)
        {
            if (!names.IsDefault && names.All(static n => n is null))
            {
                names = default;
            }

            var oldNames = SharperFunctionTypeParameterNames;
            if (SharperFunctionTypeNames.NamesEqual(oldNames, names))
            {
                return this;
            }

            // Func and Action are generic types outside of any other type, so a function type with a parameter is
            // always a ConstructedNamedTypeSymbol. That makes it the only subclass that copies the names, where tuple
            // data needs an override on every subclass (WithTupleDataCore). The definition itself (a function type
            // written inside Func's own declaration) and an error type (Func is missing) drop the names.
            // A function type with ref parameters is an anonymous delegate (https://github.com/ok3mad/roslyn/issues/56),
            // not a ConstructedNamedTypeSymbol, and has the parameter names of its descriptor, so it drops these too.
            if (this is not ConstructedNamedTypeSymbol constructed || IsUnboundGenericType)
            {
                return this;
            }

            return constructed.WithSharperFunctionTypeNames(names.IsDefault ? null : new SharperFunctionTypeNames(names));
        }

        /// <summary>
        /// Keeps a name only where both types have the same one, as <c>MergeTupleNames</c> does for tuples.
        /// </summary>
        private NamedTypeSymbol MergeSharperFunctionTypeNames(NamedTypeSymbol other, NamedTypeSymbol mergedType)
        {
            ImmutableArray<string?> names1 = SharperFunctionTypeParameterNames;
            ImmutableArray<string?> names2 = other.SharperFunctionTypeParameterNames;
            if (names1.IsDefault && mergedType.SharperFunctionTypeParameterNames.IsDefault)
            {
                return mergedType;
            }

            ImmutableArray<string?> mergedNames = names1.IsDefault || names2.IsDefault || names1.Length != names2.Length
                ? default
                : names1.ZipAsArray(names2, static (n1, n2) => string.Equals(n1, n2, StringComparison.Ordinal) ? n1 : null);

            return mergedType.WithSharperFunctionTypeParameterNames(mergedNames);
        }
    }

    /// <summary>
    /// Sharper: the parameter names of a function type, and the <c>Invoke</c> method that has them. The analog of
    /// <see cref="NamedTypeSymbol.TupleExtraData"/> for a constructed <c>Func</c> or <c>Action</c>. One instance
    /// belongs to one type symbol.
    /// </summary>
    internal sealed class SharperFunctionTypeNames
    {
        /// <summary>
        /// One entry per parameter, null for a parameter without a name. At least one entry is not null.
        /// </summary>
        internal ImmutableArray<string?> Names { get; }

        private MethodSymbol? _lazyInvokeMethod;

        internal SharperFunctionTypeNames(ImmutableArray<string?> names)
        {
            Debug.Assert(!names.IsDefault && names.Any(static n => n is not null));
            Names = names;
        }

        internal static bool NamesEqual(ImmutableArray<string?> names1, ImmutableArray<string?> names2)
        {
            return names1.IsDefault ? names2.IsDefault : !names2.IsDefault && names1.SequenceEqual(names2);
        }

        /// <summary>
        /// The delegate's <c>Invoke</c> method with the names of the function type on its parameters. A member lookup
        /// of <c>Invoke</c> finds <paramref name="invokeMethod"/> itself, with the names of <c>Func</c>'s declaration.
        /// </summary>
        internal MethodSymbol GetInvokeMethod(NamedTypeSymbol type, MethodSymbol invokeMethod)
        {
            Debug.Assert(type.SharperFunctionTypeParameterNames == Names);

            if (_lazyInvokeMethod is null)
            {
                // A delegate declaration with other parameters than its type parameters is not a Func or an Action.
                MethodSymbol named = invokeMethod.ParameterCount == Names.Length && !invokeMethod.IsDefinition
                    ? new SharperNamedDelegateInvokeMethodSymbol(type, invokeMethod.OriginalDefinition, Names)
                    : invokeMethod;
                Interlocked.CompareExchange(ref _lazyInvokeMethod, named, null);
            }

            return _lazyInvokeMethod;
        }
    }

    /// <summary>
    /// Sharper: the <c>Invoke</c> method of a function type with parameter names. It is the substituted <c>Invoke</c>
    /// of the constructed delegate, so that it compares equal to it and is emitted like it; only the names of its
    /// parameters differ.
    /// </summary>
    internal sealed class SharperNamedDelegateInvokeMethodSymbol : SubstitutedMethodSymbol
    {
        private readonly ImmutableArray<string?> _names;

        internal SharperNamedDelegateInvokeMethodSymbol(NamedTypeSymbol containingType, MethodSymbol originalDefinition, ImmutableArray<string?> names)
            : base(containingType, originalDefinition)
        {
            _names = names;
        }

        /// <summary>
        /// The same method with the parameter names of the delegate's declaration. Named arguments bind against it.
        /// PROTOTYPE(sharper): named arguments use a function type's names once they are in metadata
        /// (https://github.com/ok3mad/roslyn/issues/215).
        /// </summary>
        internal MethodSymbol WithoutSharperNames
            => UnderlyingMethod.AsMember(ContainingType);

        protected override ParameterSymbol SubstituteParameter(TypeMap map, ParameterSymbol originalParameter)
        {
            return _names[originalParameter.Ordinal] is { } name
                ? new SharperNamedDelegateParameterSymbol(this, map, originalParameter, name)
                : base.SubstituteParameter(map, originalParameter);
        }
    }

    internal sealed class SharperNamedDelegateParameterSymbol : SubstitutedParameterSymbol
    {
        private readonly string _name;

        internal SharperNamedDelegateParameterSymbol(MethodSymbol containingSymbol, TypeMap map, ParameterSymbol originalParameter, string name)
            : base(containingSymbol, map, originalParameter)
        {
            _name = name;
        }

        // The metadata name stays that of Func's or Action's parameter.
        public override string Name => _name;
    }
}
