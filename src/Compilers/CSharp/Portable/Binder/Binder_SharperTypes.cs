// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper's short type forms. Each is an alias for a BCL generic type: the syntax binds to the constructed type,
    /// so metadata, the semantic model and IOperation only ever see the BCL type, and nothing needs lowering.
    /// </summary>
    internal partial class Binder
    {
        private static bool HasSharperSequenceRank(ArrayTypeSyntax node)
        {
            foreach (var rankSpecifier in node.RankSpecifiers)
            {
                if (rankSpecifier.IsSharperSequence)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Binds an array type with at least one Sharper sequence rank, <c>T[..]</c> for <c>IEnumerable&lt;T&gt;</c>.
        /// Sharper type suffixes stack left to right, innermost first, so each <c>[..]</c> wraps everything to its
        /// left: <c>int[..][]</c> is <c>IEnumerable&lt;int&gt;[]</c> and <c>int[][..]</c> is
        /// <c>IEnumerable&lt;int[]&gt;</c>. The plain array ranks between two <c>[..]</c> keep the C# order, in which
        /// the last rank is the innermost.
        /// </summary>
        private TypeWithAnnotations BindSharperSequenceType(
            ArrayTypeSyntax node,
            TypeWithAnnotations elementType,
            BindingDiagnosticBag diagnostics,
            ConsList<TypeSymbol> basesBeingResolved,
            bool disallowRestrictedTypes)
        {
            TypeWithAnnotations type = elementType;
            var rankSpecifiers = node.RankSpecifiers;
            int start = 0;

            while (start < rankSpecifiers.Count)
            {
                int end = start;
                while (end < rankSpecifiers.Count && !rankSpecifiers[end].IsSharperSequence)
                {
                    end++;
                }

                if (end > start)
                {
                    if (start == 0)
                    {
                        // Later runs have a sequence as their element type, which these checks always accept.
                        CheckArrayElementType(node.ElementType, type, diagnostics, disallowRestrictedTypes);
                    }

                    type = BindArrayRanks(node, type, start, end, permitDimensions: false, diagnostics);
                }

                if (end < rankSpecifiers.Count)
                {
                    var sequenceRank = rankSpecifiers[end];
                    var definition = GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T, diagnostics, sequenceRank);
                    // PROTOTYPE(sharper): report an invalid element type on the element type, not the whole sequence type.
                    var sequence = BindSharperTypeAlias(definition, [type], node, basesBeingResolved, diagnostics);
                    type = TypeWithAnnotations.Create(AreNullableAnnotationsEnabled(sequenceRank.CloseBracketToken), sequence);
                }

                start = end + 1;
            }

            return type;
        }

        /// <summary>
        /// Constructs the BCL generic type that a Sharper type form stands for. Constraints are checked as for a
        /// type written out in C#: here, or by the declaring symbol where the binder suppresses constraint checks (in
        /// signatures). Diagnostics are reported on <paramref name="syntax"/>, the whole type form, since the type
        /// arguments have no syntax of their own. The caller gets <paramref name="definition"/> with
        /// <c>GetSpecialType</c> or <c>GetWellKnownType</c>, which report a missing type.
        /// </summary>
        private NamedTypeSymbol BindSharperTypeAlias(
            NamedTypeSymbol definition,
            ImmutableArray<TypeWithAnnotations> typeArguments,
            SyntaxNode syntax,
            ConsList<TypeSymbol> basesBeingResolved,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(definition.IsDefinition && definition.Arity == typeArguments.Length);
            return ConstructNamedType(definition, syntax, typeArgumentsSyntax: default, typeArguments, basesBeingResolved, diagnostics);
        }
    }
}
