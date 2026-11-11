// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
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
    /// Slice 1: the refinement is bound and checked, and makes the parameter <c>readonly</c> (step 2). The method checks
    /// it at entry (<see cref="SharperRefinementGuard"/>), and the parameter carries its normalized text (<see cref="Text"/>)
    /// in an embedded <c>Sharper.SharperRefinementAttribute</c>, which metadata symbols read back
    /// (<see cref="FromMetadata"/>; step 5). Call sites and switches come in step 4.
    /// </remarks>
    internal sealed partial class SharperRefinement
    {
        /// <summary>The name of the embedded attribute that carries a refinement in metadata, in namespace <c>Sharper</c>.</summary>
        internal const string AttributeName = "SharperRefinementAttribute";

        private string? _lazyText;

        private SharperRefinement(BoundPattern pattern, IConstantValueSet? values, bool matchesNull)
        {
            Pattern = pattern;
            Values = values;
            MatchesNull = matchesNull;
        }

        /// <summary>
        /// The bound pattern, against the parameter type. A refinement read from metadata has a pattern made of constant
        /// and relational patterns, 'and', 'or' and 'not' over literals, as <see cref="MakePattern"/> builds.
        /// </summary>
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

            // The guard throws ArgumentOutOfRangeException, and the parameter carries the embedded attribute (step 5). The
            // attribute type is made here, at declaration time, so that the assembly embeds it.
            CSharpCompilation compilation = parameter.DeclaringCompilation;
            if (GetGuardExceptionConstructor(compilation) is null)
            {
                diagnostics.Add(ErrorCode.ERR_MissingPredefinedMember, syntax.IsKeyword.GetLocation(), GuardExceptionTypeName, WellKnownMemberNames.InstanceConstructorName);
            }

            _ = compilation.SharperImmutability.GetAttributeType(SharperImmutabilityAttribute.Refinement, diagnostics, syntax);

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

        #region Guard

        internal const string GuardExceptionTypeName = "System.ArgumentOutOfRangeException";

        /// <summary>
        /// <c>ArgumentOutOfRangeException(string paramName, object actualValue, string message)</c> from the core library,
        /// which the entry guard throws, or null if it is missing. It is not a well-known member, so it is looked up by name.
        /// </summary>
        internal static MethodSymbol? GetGuardExceptionConstructor(CSharpCompilation compilation)
        {
            var name = MetadataTypeName.FromFullName(GuardExceptionTypeName);
            if (compilation.Assembly.CorLibrary.LookupDeclaredTopLevelMetadataType(ref name) is not { } type || type.IsErrorType())
            {
                return null;
            }

            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor is { DeclaredAccessibility: Accessibility.Public, ParameterCount: 3 } &&
                    constructor.Parameters is [{ Type.SpecialType: SpecialType.System_String, RefKind: RefKind.None }, { Type.SpecialType: SpecialType.System_Object, RefKind: RefKind.None }, { Type.SpecialType: SpecialType.System_String, RefKind: RefKind.None }])
                {
                    return constructor;
                }
            }

            return null;
        }

        /// <summary>The message of the guard's exception: <c>The value does not satisfy the refinement 'is &gt;= 0'.</c></summary>
        internal string GuardMessage => $"The value does not satisfy the refinement 'is {Text}'.";

        /// <summary>
        /// The pattern again, against <paramref name="inputType"/> and with <paramref name="syntax"/>, made of literals, as
        /// the guard of the method that is emitted needs it: a local function's closure method or an extension block
        /// member's implementation method has its own parameters, whose types are substituted.
        /// </summary>
        internal BoundPattern MakePattern(TypeSymbol inputType, SyntaxNode syntax)
        {
            Debug.Assert(!HasErrors);
            return Rebuild(Pattern, inputType, syntax);
        }

        private static BoundPattern Rebuild(BoundPattern pattern, TypeSymbol inputType, SyntaxNode syntax)
        {
            return make(pattern, inputType, syntax);

            static BoundPattern make(BoundPattern pattern, TypeSymbol type, SyntaxNode syntax)
            {
                BoundPattern result = pattern switch
                {
                    BoundConstantPattern constant => new BoundConstantPattern(
                        syntax, new BoundLiteral(syntax, constant.ConstantValue, type) { WasCompilerGenerated = true }, constant.ConstantValue,
                        UnionMatchingMode.None, inputType: type, narrowedType: type),
                    BoundRelationalPattern relational => new BoundRelationalPattern(
                        syntax, relational.Relation, new BoundLiteral(syntax, relational.ConstantValue, type) { WasCompilerGenerated = true }, relational.ConstantValue,
                        isUnionMatching: false, inputType: type, narrowedType: type),
                    BoundBinaryPattern binary => new BoundBinaryPattern(
                        syntax, binary.Disjunction, make(binary.Left, type, syntax), make(binary.Right, type, syntax), inputType: type, narrowedType: type),
                    BoundNegatedPattern negated => new BoundNegatedPattern(
                        syntax, make(negated.Negated, type, syntax), inputType: type, narrowedType: type),
                    _ => throw ExceptionUtilities.UnexpectedValue(pattern.Kind),
                };

                result.WasCompilerGenerated = true;
                return result;
            }
        }

        #endregion

        #region Metadata

        /// <summary>
        /// The normalized text of the pattern, which the embedded attribute holds: constants are written as literals of
        /// the parameter type (enum members by name, <c>Color.Red</c>, or as a cast of the underlying value), with single
        /// spaces and only the parentheses that precedence needs. It is valid C# for the parameter type, and
        /// <see cref="FromMetadata"/> reads it back without binding names.
        /// </summary>
        internal string Text
        {
            get
            {
                Debug.Assert(!HasErrors);
                if (_lazyText is null)
                {
                    Interlocked.CompareExchange(ref _lazyText, FormatPattern(), null);
                }

                return _lazyText;
            }
        }

        private string FormatPattern()
        {
            return format(Pattern);

            static string format(BoundPattern pattern)
            {
                switch (pattern)
                {
                    case BoundConstantPattern constant:
                        return FormatConstant(constant.ConstantValue, constant.InputType);
                    case BoundRelationalPattern relational:
                        return SyntaxFacts.GetText(relational.Relation.Operator() switch
                        {
                            BinaryOperatorKind.LessThan => SyntaxKind.LessThanToken,
                            BinaryOperatorKind.LessThanOrEqual => SyntaxKind.LessThanEqualsToken,
                            BinaryOperatorKind.GreaterThan => SyntaxKind.GreaterThanToken,
                            BinaryOperatorKind.GreaterThanOrEqual => SyntaxKind.GreaterThanEqualsToken,
                            var op => throw ExceptionUtilities.UnexpectedValue(op),
                        }) + " " + FormatConstant(relational.ConstantValue, relational.InputType);
                    case BoundBinaryPattern { Disjunction: true } binary:
                        return format(binary.Left) + " or " + format(binary.Right);
                    case BoundBinaryPattern binary:
                        return operand(binary.Left, andOperand: true) + " and " + operand(binary.Right, andOperand: true);
                    case BoundNegatedPattern negated:
                        return "not " + operand(negated.Negated, andOperand: false);
                    default:
                        throw ExceptionUtilities.UnexpectedValue(pattern.Kind);
                }
            }

            // 'not' binds tighter than 'and', which binds tighter than 'or'.
            static string operand(BoundPattern pattern, bool andOperand)
                => pattern is BoundBinaryPattern { Disjunction: var disjunction } && (disjunction || !andOperand)
                    ? "(" + format(pattern) + ")"
                    : format(pattern);
        }

        private static string FormatConstant(ConstantValue value, TypeSymbol type)
        {
            if (value.IsNull)
            {
                return "null";
            }

            if (type.IsEnumType())
            {
                foreach (var member in type.GetMembers())
                {
                    if (member is FieldSymbol { IsConst: true } field && field.GetConstantValue(ConstantFieldsInProgress.Empty, earlyDecodingWellKnownAttributes: false) is { } fieldValue &&
                        fieldValue.Equals(value))
                    {
                        return escape(type.Name) + "." + escape(field.Name);
                    }
                }

                string number = FormatNumber(value);
                return "(" + escape(type.Name) + ")" + (number.StartsWith("-", StringComparison.Ordinal) ? "(" + number + ")" : number);
            }

            return value.Discriminator switch
            {
                ConstantValueTypeDiscriminator.String => SymbolDisplay.FormatLiteral(value.StringValue!, quote: true),
                ConstantValueTypeDiscriminator.Char => SymbolDisplay.FormatLiteral(value.CharValue, quote: true),
                ConstantValueTypeDiscriminator.Boolean => value.BooleanValue ? "true" : "false",
                _ => FormatNumber(value),
            };

            static string escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
        }

        private static string FormatNumber(ConstantValue value)
        {
            switch (value.Discriminator)
            {
                case ConstantValueTypeDiscriminator.Double:
                    return formatFloatingPoint(value.DoubleValue, "double", suffix: "");
                case ConstantValueTypeDiscriminator.Single:
                    return formatFloatingPoint(value.SingleValue, "float", suffix: "f");
                case ConstantValueTypeDiscriminator.Decimal:
                    return value.DecimalValue.ToString(CultureInfo.InvariantCulture) + "m";
                default:
                    Debug.Assert(value.IsIntegral);
                    return Convert.ToString(value.Value, CultureInfo.InvariantCulture)!;
            }

            static string formatFloatingPoint(double d, string keyword, string suffix)
            {
                return double.IsNaN(d) ? keyword + ".NaN"
                    : double.IsPositiveInfinity(d) ? keyword + ".PositiveInfinity"
                    : double.IsNegativeInfinity(d) ? keyword + ".NegativeInfinity"
                    : (suffix == "f" ? ((float)d).ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture)) + suffix;
            }
        }

        /// <summary>
        /// The embedded <c>SharperRefinementAttribute</c> with <paramref name="text"/> (<see cref="Text"/>, or
        /// <see cref="AnyValueText"/> on a return), for a parameter or a return; null if the attribute type is not usable.
        /// </summary>
        internal static SynthesizedAttributeData? SynthesizeAttribute(CSharpCompilation compilation, string text)
        {
            if (compilation.SharperImmutability.GetAttributeType(SharperImmutabilityAttribute.Refinement, BindingDiagnosticBag.Discarded, compilation.SyntaxTrees[0].GetRoot()) is { } attribute &&
                attribute.InstanceConstructors.FirstOrDefault(c => c.ParameterCount == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_String) is { } constructor)
            {
                return SynthesizedAttributeData.Create(
                    compilation,
                    constructor,
                    [new TypedConstant(constructor.Parameters[0].Type, TypedConstantKind.Primitive, text)],
                    System.Collections.Immutable.ImmutableArray<System.Collections.Generic.KeyValuePair<string, TypedConstant>>.Empty);
            }

            return null;
        }

        /// <summary>
        /// Reads a refinement from the text of an embedded <c>SharperRefinementAttribute</c> on a metadata parameter: the
        /// text that <see cref="Text"/> writes, against <paramref name="type"/>. It is trusted, as <c>[Immutable]</c> is;
        /// text that does not parse, or does not fit the type, gives no refinement.
        /// </summary>
        internal static SharperRefinement? FromMetadata(TypeSymbol type, string text)
        {
            var factory = ValueSetFactory.ForType(type);
            if (factory is null || type.IsNullableType())
            {
                return null;
            }

            var expression = SyntaxFactory.ParseExpression("_ is " + text, consumeFullText: true);
            if (expression is not IsPatternExpressionSyntax { Pattern: var patternSyntax } || expression.ContainsDiagnostics ||
                makePattern(patternSyntax, type) is not { } pattern)
            {
                return null;
            }

            return new SharperRefinement(pattern, ComputeValues(pattern, factory), Matches(pattern, factory, ConstantValue.Null));

            static BoundPattern? makePattern(PatternSyntax syntax, TypeSymbol type)
            {
                switch (syntax)
                {
                    case ParenthesizedPatternSyntax parenthesized:
                        return makePattern(parenthesized.Pattern, type);
                    case BinaryPatternSyntax binary:
                        return makePattern(binary.Left, type) is { } left && makePattern(binary.Right, type) is { } right
                            ? new BoundBinaryPattern(syntax, binary.IsKind(SyntaxKind.OrPattern), left, right, inputType: type, narrowedType: type)
                            : null;
                    case UnaryPatternSyntax unary:
                        return makePattern(unary.Pattern, type) is { } negated
                            ? new BoundNegatedPattern(syntax, negated, inputType: type, narrowedType: type)
                            : null;
                    case ConstantPatternSyntax constant:
                        return ReadConstant(constant.Expression, type) is { } value
                            ? new BoundConstantPattern(syntax, new BoundLiteral(constant.Expression, value, type), value, UnionMatchingMode.None, inputType: type, narrowedType: type)
                            : null;
                    case RelationalPatternSyntax relational:
                        BinaryOperatorKind op = relational.OperatorToken.Kind() switch
                        {
                            SyntaxKind.LessThanToken => BinaryOperatorKind.LessThan,
                            SyntaxKind.LessThanEqualsToken => BinaryOperatorKind.LessThanOrEqual,
                            SyntaxKind.GreaterThanToken => BinaryOperatorKind.GreaterThan,
                            SyntaxKind.GreaterThanEqualsToken => BinaryOperatorKind.GreaterThanOrEqual,
                            _ => BinaryOperatorKind.Error,
                        };
                        BinaryOperatorKind opType = Binder.RelationalOperatorType(type.EnumUnderlyingTypeOrSelf());
                        if (op == BinaryOperatorKind.Error || opType is BinaryOperatorKind.Error or BinaryOperatorKind.String or BinaryOperatorKind.Bool ||
                            ReadConstant(relational.Expression, type) is not { IsNull: false } relationalValue ||
                            (relationalValue.IsFloating && double.IsNaN(relationalValue.DoubleValue)))
                        {
                            return null;
                        }

                        return new BoundRelationalPattern(
                            syntax, op | opType, new BoundLiteral(relational.Expression, relationalValue, type), relationalValue,
                            isUnionMatching: false, inputType: type, narrowedType: type);
                    default:
                        return null;
                }
            }
        }

        /// <summary>A constant that <see cref="FormatConstant"/> wrote, as a constant of <paramref name="type"/>.</summary>
        private static ConstantValue? ReadConstant(ExpressionSyntax expression, TypeSymbol type)
        {
            TypeSymbol underlying = type.EnumUnderlyingTypeOrSelf();
            switch (expression)
            {
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }:
                    return type.IsReferenceType ? ConstantValue.Null : null;
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
                    return type.SpecialType == SpecialType.System_String ? ConstantValue.Create(literal.Token.ValueText) : null;
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.CharacterLiteralExpression, Token.Value: char c }:
                    return type.SpecialType == SpecialType.System_Char ? ConstantValue.Create(c) : null;
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.TrueLiteralExpression or (int)SyntaxKind.FalseLiteralExpression } literal:
                    return type.SpecialType == SpecialType.System_Boolean ? ConstantValue.Create(literal.IsKind(SyntaxKind.TrueLiteralExpression)) : null;
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression }:
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } }:
                    return type.IsEnumType() ? null : ReadNumber(expression.ToString(), underlying);
                case CastExpressionSyntax cast when type.IsEnumType():
                    var operand = cast.Expression is ParenthesizedExpressionSyntax parenthesized ? parenthesized.Expression : cast.Expression;
                    return operand is LiteralExpressionSyntax or PrefixUnaryExpressionSyntax ? ReadNumber(operand.ToString(), underlying) : null;
                case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.ValueText: var name } } memberAccess:
                    if (type.IsEnumType())
                    {
                        return type.GetMembers(name).OfType<FieldSymbol>().FirstOrDefault(f => f.IsConst)?.GetConstantValue(ConstantFieldsInProgress.Empty, earlyDecodingWellKnownAttributes: false);
                    }

                    double? special = name switch
                    {
                        "NaN" => double.NaN,
                        "PositiveInfinity" => double.PositiveInfinity,
                        "NegativeInfinity" => double.NegativeInfinity,
                        _ => null,
                    };
                    return (special, memberAccess.Expression, type.SpecialType) switch
                    {
                        ({ } d, PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.DoubleKeyword }, SpecialType.System_Double) => ConstantValue.Create(d),
                        ({ } d, PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.FloatKeyword }, SpecialType.System_Single) => ConstantValue.Create((float)d),
                        _ => null,
                    };
                default:
                    return null;
            }
        }

        private static ConstantValue? ReadNumber(string text, TypeSymbol type)
        {
            const NumberStyles integer = NumberStyles.AllowLeadingSign;
            const NumberStyles floating = NumberStyles.Float;
            var culture = CultureInfo.InvariantCulture;
            text = text.Replace(" ", "");

            switch (type.SpecialType)
            {
                case SpecialType.System_SByte: return sbyte.TryParse(text, integer, culture, out var sb) ? ConstantValue.Create(sb) : null;
                case SpecialType.System_Byte: return byte.TryParse(text, integer, culture, out var b) ? ConstantValue.Create(b) : null;
                case SpecialType.System_Int16: return short.TryParse(text, integer, culture, out var s) ? ConstantValue.Create(s) : null;
                case SpecialType.System_UInt16: return ushort.TryParse(text, integer, culture, out var us) ? ConstantValue.Create(us) : null;
                case SpecialType.System_Int32: return int.TryParse(text, integer, culture, out var i) ? ConstantValue.Create(i) : null;
                case SpecialType.System_UInt32: return uint.TryParse(text, integer, culture, out var ui) ? ConstantValue.Create(ui) : null;
                case SpecialType.System_Int64: return long.TryParse(text, integer, culture, out var l) ? ConstantValue.Create(l) : null;
                case SpecialType.System_UInt64: return ulong.TryParse(text, integer, culture, out var ul) ? ConstantValue.Create(ul) : null;
                case SpecialType.System_IntPtr when type.IsNativeIntegerType: return int.TryParse(text, integer, culture, out var ni) ? ConstantValue.CreateNativeInt(ni) : null;
                case SpecialType.System_UIntPtr when type.IsNativeIntegerType: return uint.TryParse(text, integer, culture, out var nu) ? ConstantValue.CreateNativeUInt(nu) : null;
                case SpecialType.System_Single: return text.EndsWith("f", StringComparison.Ordinal) && float.TryParse(text[..^1], floating, culture, out var f) ? ConstantValue.Create(f) : null;
                case SpecialType.System_Double: return double.TryParse(text, floating, culture, out var d) ? ConstantValue.Create(d) : null;
                case SpecialType.System_Decimal: return text.EndsWith("m", StringComparison.Ordinal) && decimal.TryParse(text[..^1], floating, culture, out var m) ? ConstantValue.Create(m) : null;
                default: return null;
            }
        }

        #endregion

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
