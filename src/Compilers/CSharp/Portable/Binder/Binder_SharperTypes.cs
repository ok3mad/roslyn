// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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
                    // The element is everything to the left of this rank. Only the first rank has a syntax node for it.
                    var elementLocation = end == 0
                        ? node.ElementType.Location
                        : Location.Create(node.SyntaxTree, TextSpan.FromBounds(node.SpanStart, rankSpecifiers[end - 1].Span.End));
                    var sequence = BindSharperTypeAlias(definition, [type], [elementLocation], node, basesBeingResolved, diagnostics);
                    type = TypeWithAnnotations.Create(AreNullableAnnotationsEnabled(sequenceRank.CloseBracketToken), sequence);
                }

                start = end + 1;
            }

            return type;
        }

        /// <summary>
        /// Binds the Sharper task type <c>T~</c> to <c>System.Threading.Tasks.Task&lt;T&gt;</c>, and <c>void~</c> to the
        /// non-generic <c>System.Threading.Tasks.Task</c>.
        /// </summary>
        private NamedTypeSymbol BindSharperTaskType(TaskTypeSyntax syntax, BindingDiagnosticBag diagnostics, ConsList<TypeSymbol> basesBeingResolved)
        {
            MessageID.IDS_FeatureSharper.CheckFeatureAvailability(diagnostics, syntax.TildeToken);

            if (syntax.ElementType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            {
                return GetWellKnownType(WellKnownType.System_Threading_Tasks_Task, diagnostics, syntax);
            }

            var resultType = BindTypeArgument(syntax.ElementType, diagnostics, basesBeingResolved);
            var definition = GetWellKnownType(WellKnownType.System_Threading_Tasks_Task_T, diagnostics, syntax);
            return BindSharperTypeAlias(definition, [resultType], [syntax.ElementType.Location], syntax, basesBeingResolved, diagnostics);
        }

        /// <summary>
        /// Constructs the BCL generic type that a Sharper type form stands for. Constraints are checked as for a
        /// type written out in C#: here, or by the declaring symbol where the binder suppresses constraint checks (in
        /// signatures). A constraint error on a type argument is reported at its entry in
        /// <paramref name="typeArgumentLocations"/>, the part of the form that stands for it, where the C# form would
        /// report it on the type argument; other diagnostics are reported on <paramref name="syntax"/>, the whole type
        /// form. The caller gets <paramref name="definition"/> with <c>GetSpecialType</c> or <c>GetWellKnownType</c>,
        /// which report a missing type.
        /// </summary>
        private NamedTypeSymbol BindSharperTypeAlias(
            NamedTypeSymbol definition,
            ImmutableArray<TypeWithAnnotations> typeArguments,
            ImmutableArray<Location> typeArgumentLocations,
            SyntaxNode syntax,
            ConsList<TypeSymbol> basesBeingResolved,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(definition.IsDefinition && definition.Arity == typeArguments.Length);
            Debug.Assert(typeArgumentLocations.Length == typeArguments.Length);

            // ConstructNamedType, with locations in place of a type argument list.
            var type = definition.Construct(typeArguments);
            if (ShouldCheckConstraints && ConstraintsHelper.RequiresChecking(type))
            {
                bool includeNullability = Compilation.IsFeatureEnabled(MessageID.IDS_FeatureNullableReferenceTypes);
                type.CheckConstraintsForNamedType(new ConstraintsHelper.CheckConstraintsArgs(this.Compilation, this.Conversions, includeNullability, syntax.Location, diagnostics),
                                                  syntax, typeArgumentsSyntax: default, basesBeingResolved, typeArgumentLocations);
            }

            return type;
        }
    }
}
