// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// The refinement of a Sharper parameter, <c>int i is &gt;=0</c>: a closed pattern over constants that every value of
    /// the parameter satisfies (Feature 11 of the implementation plan). It is bound once per parameter symbol
    /// (<see cref="SourceComplexParameterSymbolBase.SharperRefinement"/>), with its value set.
    /// </summary>
    /// <remarks>
    /// Slice 1, steps 1 and 2: the refinement is bound and checked, and makes the parameter <c>readonly</c>, but nothing
    /// reads it yet. Call sites, switches and the runtime guard come in the later steps.
    /// </remarks>
    internal sealed class SharperRefinement
    {
        private SharperRefinement(BoundPattern pattern, IConstantValueSet? values, bool matchesNull)
        {
            Pattern = pattern;
            Values = values;
            MatchesNull = matchesNull;
        }

        /// <summary>The bound pattern, against the parameter type.</summary>
        internal BoundPattern Pattern { get; }

        /// <summary>
        /// The non-null values the refinement allows, from <see cref="ValueSetFactory.ForType"/>; null if the refinement
        /// has errors.
        /// </summary>
        internal IConstantValueSet? Values { get; }

        /// <summary>True if the refinement allows null, as <c>string s is not "x"</c> does.</summary>
        internal bool MatchesNull { get; }

        internal bool HasErrors => Values is null;

        /// <summary>
        /// Binds the refinement of <paramref name="parameter"/> and checks it: the member supports refinements in this
        /// slice, the pattern is closed over constants and its type has a value set, and the default value satisfies it.
        /// Returns null if the pattern is not closed syntactically, and so is not bound.
        /// </summary>
        internal static SharperRefinement? Bind(
            SourceComplexParameterSymbolBase parameter,
            RefinementClauseSyntax syntax,
            Binder binder,
            ConstantValue? defaultValue,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper));

            // Bind with no diagnostics when the member cannot have a refinement, so that only that error is reported.
            MessageID? notSupportedYet = GetNotSupportedYet(parameter);
            if (notSupportedYet is { } cause)
            {
                diagnostics.Add(ErrorCode.ERR_SharperRefinementNotSupportedYet, syntax.IsKeyword.GetLocation(), cause.Localize());
            }

            var patternDiagnostics = notSupportedYet is null ? diagnostics : BindingDiagnosticBag.Discarded;
            TypeSymbol type = parameter.Type;

            // Patterns that declare variables cannot be bound outside a body, so open syntax is rejected before binding;
            // a constant pattern can still bind to a type pattern ('is String'), which the bound check below finds.
            if (FindOpenPatternSyntax(syntax.Pattern) is { } openSyntax)
            {
                if (notSupportedYet is null)
                {
                    diagnostics.Add(ErrorCode.ERR_SharperRefinementNotClosed, openSyntax.Location, openSyntax);
                }

                return null;
            }

            NamedTypeSymbol? unionType = null;
            var pattern = binder.BindPattern(
                syntax.Pattern, ref unionType, type, permitDesignations: false, hasErrors: type.IsErrorType(), patternDiagnostics,
                out bool hasUnionMatching, underIsPattern: true);

            if (notSupportedYet is not null || type.IsErrorType() || pattern.HasErrors)
            {
                return new SharperRefinement(pattern, values: null, matchesNull: false);
            }

            var factory = hasUnionMatching ? null : ValueSetFactory.ForType(type);
            if (factory is null || type.IsNullableType())
            {
                diagnostics.Add(ErrorCode.ERR_SharperRefinementTypeNotSupported, parameter.GetFirstLocation(), parameter.Name, type);
                return new SharperRefinement(pattern, values: null, matchesNull: false);
            }

            if (FindOpenPattern(pattern) is { } open)
            {
                diagnostics.Add(ErrorCode.ERR_SharperRefinementNotClosed, open.Syntax.Location, open.Syntax);
                return new SharperRefinement(pattern, values: null, matchesNull: false);
            }

            var refinement = new SharperRefinement(pattern, ComputeValues(pattern, factory), Matches(pattern, factory, ConstantValue.Null));

            // PROTOTYPE(sharper): a default value from [DefaultParameterValue] is not checked; reading attributes here
            // could make a cycle through attribute constructors with optional parameters.
            if (defaultValue is { IsBad: false } && !refinement.Satisfies(factory, defaultValue) &&
                parameter.GetNonNullSyntaxNode() is ParameterSyntax { Default.Value: { } defaultSyntax })
            {
                diagnostics.Add(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, defaultSyntax.Location, parameter.Name, defaultSyntax, syntax.Pattern);
            }

            return refinement;
        }

        private bool Satisfies(IConstantValueSetFactory factory, ConstantValue value)
            => Matches(Pattern, factory, value);

        /// <summary>
        /// Why <paramref name="parameter"/> cannot have a refinement in slice 1, if it cannot. A refinement is not part of the
        /// signature, so members whose callers do not know which body runs (virtual, interface and partial members,
        /// delegates), and members with no body to guard (extern), report an error, so that overriding rules can be added
        /// later without a break. Lambdas and extension block receivers report theirs where their parameters are bound
        /// (<see cref="ParameterHelpers.CheckSharperRefinementContext"/>).
        /// </summary>
        private static MessageID? GetNotSupportedYet(SourceComplexParameterSymbolBase parameter)
        {
            Symbol owner = parameter.ContainingSymbol;
            NamedTypeSymbol? containingType = owner.ContainingType;

            if (parameter.RefKind != RefKind.None)
            {
                // A write through the reference, by the callee or by an alias of the caller's variable, would break the fact.
                return MessageID.IDS_SharperRefinementOnRefParameter;
            }

            if (containingType is { TypeKind: TypeKind.Delegate })
            {
                return MessageID.IDS_SharperRefinementOnDelegate;
            }

            if (containingType is { IsInterface: true })
            {
                return MessageID.IDS_SharperRefinementOnInterfaceMember;
            }

            if (owner.IsVirtual || owner.IsAbstract || owner.IsOverride)
            {
                return MessageID.IDS_SharperRefinementOnVirtualMember;
            }

            if (owner is MethodSymbol { IsExplicitInterfaceImplementation: true } or PropertySymbol { IsExplicitInterfaceImplementation: true })
            {
                return MessageID.IDS_SharperRefinementOnExplicitImplementation;
            }

            // PROTOTYPE(sharper): an implicit implementation of an interface member, `public void M(int i is >=0)` for
            // `I.M(int i)`, is not detected yet; it needs the interface map, after the members are complete.

            if (owner.IsPartialMember())
            {
                return MessageID.IDS_SharperRefinementOnPartialMember;
            }

            if (owner.IsExtern)
            {
                return MessageID.IDS_SharperRefinementOnExternMember;
            }

            if (owner is SynthesizedPrimaryConstructor && containingType is { IsRecord: true } or { IsRecordStruct: true })
            {
                // Records copy their positional parameters to properties, whose 'with' and 'init' bypass the constructor.
                return MessageID.IDS_SharperRefinementOnRecordParameter;
            }

            return null;
        }

        private static PatternSyntax? FindOpenPatternSyntax(PatternSyntax pattern)
        {
            switch (pattern)
            {
                case ConstantPatternSyntax:
                case RelationalPatternSyntax:
                    return null;
                case BinaryPatternSyntax binary:
                    return FindOpenPatternSyntax(binary.Left) ?? FindOpenPatternSyntax(binary.Right);
                case UnaryPatternSyntax unary:
                    return FindOpenPatternSyntax(unary.Pattern);
                case ParenthesizedPatternSyntax parenthesized:
                    return FindOpenPatternSyntax(parenthesized.Pattern);
                default:
                    return pattern;
            }
        }

        /// <summary>
        /// The first part of <paramref name="pattern"/> that is not a constant or relational pattern, 'and', 'or' or 'not'
        /// (parentheses bind to what they contain). Type, declaration, recursive, list and discard patterns, and the
        /// opaque patterns of #34, are not closed over constants.
        /// </summary>
        private static BoundPattern? FindOpenPattern(BoundPattern pattern)
        {
            switch (pattern)
            {
                case BoundConstantPattern:
                case BoundRelationalPattern:
                    return null;
                case BoundBinaryPattern binary:
                    return FindOpenPattern(binary.Left) ?? FindOpenPattern(binary.Right);
                case BoundNegatedPattern negated:
                    return FindOpenPattern(negated.Negated);
                default:
                    return pattern;
            }
        }

        private static IConstantValueSet ComputeValues(BoundPattern pattern, IConstantValueSetFactory factory)
        {
            switch (pattern)
            {
                case BoundConstantPattern constant:
                    return constant.ConstantValue.IsNull
                        ? factory.NoValues
                        : factory.Related(BinaryOperatorKind.Equal, constant.ConstantValue);
                case BoundRelationalPattern relational:
                    return factory.Related(relational.Relation.Operator(), relational.ConstantValue);
                case BoundBinaryPattern { Disjunction: true } binary:
                    return (IConstantValueSet)ComputeValues(binary.Left, factory).Union(ComputeValues(binary.Right, factory));
                case BoundBinaryPattern binary:
                    return (IConstantValueSet)ComputeValues(binary.Left, factory).Intersect(ComputeValues(binary.Right, factory));
                case BoundNegatedPattern negated:
                    return (IConstantValueSet)ComputeValues(negated.Negated, factory).Complement();
                default:
                    throw ExceptionUtilities.UnexpectedValue(pattern.Kind);
            }
        }

        /// <summary>True if the constant <paramref name="value"/>, possibly null, matches <paramref name="pattern"/>.</summary>
        private static bool Matches(BoundPattern pattern, IConstantValueSetFactory factory, ConstantValue value)
        {
            switch (pattern)
            {
                case BoundConstantPattern constant:
                    return value.IsNull || constant.ConstantValue.IsNull
                        ? value.IsNull && constant.ConstantValue.IsNull
                        : factory.Related(BinaryOperatorKind.Equal, value, constant.ConstantValue);
                case BoundRelationalPattern relational:
                    return !value.IsNull && factory.Related(relational.Relation.Operator(), value, relational.ConstantValue);
                case BoundBinaryPattern { Disjunction: true } binary:
                    return Matches(binary.Left, factory, value) || Matches(binary.Right, factory, value);
                case BoundBinaryPattern binary:
                    return Matches(binary.Left, factory, value) && Matches(binary.Right, factory, value);
                case BoundNegatedPattern negated:
                    return !Matches(negated.Negated, factory, value);
                default:
                    throw ExceptionUtilities.UnexpectedValue(pattern.Kind);
            }
        }
    }
}
