// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Sharper: purity, slice 1 (#173). A <c>[Pure]</c> member writes nothing outside its own frame, and its result
    /// depends only on the contents of what it reads through its arguments (<c>this</c> included) and on state that
    /// cannot change. Purity is declared, never inferred: a pure member calls only <c>[Pure]</c>, <c>[AssumePure]</c>
    /// and attested members. The body is checked by the member mode of <see cref="PurityWalker"/>, from the method
    /// compiler (<see cref="CheckPureBodies"/>).
    /// </summary>
    internal sealed partial class SharperImmutability
    {
        /// <summary>
        /// Whether a method is declared <c>[Pure]</c>: the method itself, or the property of a get accessor. Recognized by
        /// name, so the claims of other assemblies count.
        /// </summary>
        internal static bool IsDeclaredPure(MethodSymbol method)
        {
            method = method.OriginalDefinition;
            return HasAttribute(method, SharperImmutabilityAttribute.Pure) ||
                (method is { MethodKind: MethodKind.PropertyGet, AssociatedSymbol: PropertySymbol property } && HasAttribute(property, SharperImmutabilityAttribute.Pure));
        }

        #region Bodies

        /// <summary>
        /// Checks the body of a method in a Sharper file when it is <c>[Pure]</c>, and the <c>[Pure]</c> local functions
        /// in it. Called by the method compiler with the bound body, and for a constructor the instance initializers it runs.
        /// </summary>
        internal static void CheckPureBodies(MethodSymbol method, BoundBlock body, ImmutableArray<BoundInitializer> initializers, BindingDiagnosticBag diagnostics)
        {
            if (body.HasErrors || !((CSharpParseOptions)body.Syntax.SyntaxTree.Options).IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                return;
            }

            var checker = method.DeclaringCompilation.SharperImmutability;
            if (IsDeclaredPure(method))
            {
                checker.CheckPureBody(method, body, initializers, diagnostics);
            }

            if (body.Syntax.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any(static f => f.AttributeLists.Count > 0))
            {
                var localFunctions = ArrayBuilder<BoundLocalFunctionStatement>.GetInstance();
                new LocalFunctionFinder(localFunctions).Visit(body);
                foreach (var localFunction in localFunctions)
                {
                    if (IsDeclaredPure(localFunction.Symbol) && (localFunction.BlockBody ?? (BoundNode?)localFunction.ExpressionBody) is { } localBody)
                    {
                        checker.CheckPureBody(localFunction.Symbol, localBody, initializers: default, diagnostics);
                    }
                }

                localFunctions.Free();
            }
        }

        private void CheckPureBody(MethodSymbol method, BoundNode body, ImmutableArray<BoundInitializer> initializers, BindingDiagnosticBag diagnostics)
        {
            var walker = new PurityWalker(this, method, definition: null, localOnly: false, allowsCreation: true, constructorsInProgress: null, isMember: true);
            walker.Visit(body);
            if (!initializers.IsDefaultOrEmpty)
            {
                foreach (var initializer in initializers)
                {
                    if (walker.Impure is null && !initializer.HasErrors)
                    {
                        walker.Visit(initializer);
                    }
                }
            }

            if (walker.Impure is { } impure)
            {
                diagnostics.Add(ErrorCode.ERR_SharperPureNotPure, impure.Syntax.Location, method, GetPureReason(walker));
            }
        }

        /// <summary>
        /// Checks the operand of an opaque pattern (Feature 13), such as <c>max</c> in <c>&gt; max</c>, by the rules of a
        /// <c>[Pure]</c> body, and reports CS20062 if it is not pure. <paramref name="containingMethod"/> is the method or
        /// lambda the pattern is in, if any.
        /// </summary>
        internal static bool CheckOpaquePatternOperand(CSharpCompilation compilation, MethodSymbol? containingMethod, BoundExpression operand, BindingDiagnosticBag diagnostics)
        {
            var walker = new PurityWalker(compilation.SharperImmutability, containingMethod, definition: null, localOnly: false, allowsCreation: true, constructorsInProgress: null, isMember: true);
            walker.Visit(operand);
            if (walker.Impure is null)
            {
                return true;
            }

            diagnostics.Add(ErrorCode.ERR_SharperOpaquePatternNotPure, walker.Impure.Syntax.Location, GetPureReason(walker));
            return false;
        }

        /// <summary>Why the expression a pure member's walker stopped at is not pure.</summary>
        private static Reason GetPureReason(PurityWalker walker)
        {
            var node = walker.Impure!;
            string text = node.Syntax.ToString();
            int lineBreak = text.IndexOfAny(['\r', '\n']);
            if (lineBreak >= 0)
            {
                text = text.Substring(0, lineBreak).TrimEnd() + " ...";
            }

            if (walker.ImpureIsIdentity)
            {
                return new Reason(MessageID.IDS_SharperPureReasonIdentity, [text]);
            }

            if (walker.ImpureIsCulture)
            {
                return new Reason(MessageID.IDS_SharperPureReasonCulture, [text]);
            }

            if (walker.ImpureIsWrite)
            {
                return new Reason(MessageID.IDS_SharperPureReasonWrite, [text]);
            }

            Symbol? callee = node switch
            {
                BoundCall call => call.Method,
                BoundPropertyAccess access => access.PropertySymbol,
                BoundIndexerAccess access => access.Indexer,
                BoundObjectCreationExpression creation => creation.Constructor,
                BoundAssignmentOperator { Left: BoundObjectInitializerMember { MemberSymbol: PropertySymbol { SetMethod: { } setter } } } => setter,
                BoundObjectInitializerMember { MemberSymbol: PropertySymbol { SetMethod: { } setter } } => setter,
                BoundWithExpression { CloneMethod: { } clone } => clone,
                BoundBinaryOperator { BinaryOperatorMethod: { } method } => method,
                BoundUnaryOperator { MethodOpt: { } method } => method,
                BoundConversion { Conversion: { IsUserDefined: true, Method: { } method } } => method,
                _ => null,
            };

            if (callee is not null)
            {
                return new Reason(MessageID.IDS_SharperPureReasonCall, [text, callee]);
            }

            return node switch
            {
                BoundAssignmentOperator or BoundCompoundAssignmentOperator or BoundNullCoalescingAssignmentOperator or BoundIncrementOperator
                    => new Reason(MessageID.IDS_SharperPureReasonWrite, [text]),
                BoundFieldAccess { FieldSymbol.IsStatic: true }
                    => new Reason(MessageID.IDS_SharperPureReasonStatic, [text]),
                _ => new Reason(MessageID.IDS_SharperPureReasonOther, [text]),
            };
        }

        private sealed class LocalFunctionFinder(ArrayBuilder<BoundLocalFunctionStatement> found) : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly ArrayBuilder<BoundLocalFunctionStatement> _found = found;

            public override BoundNode? VisitLocalFunctionStatement(BoundLocalFunctionStatement node)
            {
                _found.Add(node);
                return base.VisitLocalFunctionStatement(node);
            }
        }

        #endregion

        #region Attestations

        /// <summary>
        /// The members of other assemblies that are pure without a claim, until the attestation format exists (#142):
        /// <c>System.Math</c> and <c>System.MathF</c>, the <c>string</c> members that do not depend on the culture, and
        /// <c>Equals</c>, <c>CompareTo</c> and <c>GetHashCode</c> of the primitive types.
        /// </summary>
        private static bool IsAttestedMethod(MethodSymbol method)
        {
            var type = method.ContainingType;
            if (method.IsGenericMethod || type is null || type.ContainingModule is SourceModuleSymbol)
            {
                return false;
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                    return IsAttestedStringMember(method);
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
                    return method is { IsStatic: false, Name: WellKnownMemberNames.ObjectEquals or "CompareTo" or WellKnownMemberNames.ObjectGetHashCode };
            }

            return method.IsStatic &&
                type is { Name: "Math" or "MathF", Arity: 0, ContainingType: null, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };
        }

        private static readonly HashSet<string> s_attestedStringMembers =
        [
            "Substring", "Contains", "Equals", "GetHashCode", "IsNullOrEmpty", "IsNullOrWhiteSpace", "Trim", "TrimStart", "TrimEnd",
            "ToUpperInvariant", "ToLowerInvariant", "Replace", "PadLeft", "PadRight", "Insert", "Remove", "CompareOrdinal", "Concat",
            "IndexOf", "LastIndexOf", "IndexOfAny", "LastIndexOfAny", "StartsWith", "EndsWith", "Split", "ToCharArray", "Join",
            "get_Length", "get_Chars", WellKnownMemberNames.EqualityOperatorName, WellKnownMemberNames.InequalityOperatorName,
        ];

        /// <summary>
        /// A <c>string</c> member that does not depend on the culture: its parameters are strings, characters, integers and
        /// their arrays, never an object, a format provider or a <c>StringComparison</c>. <c>IndexOf</c>,
        /// <c>LastIndexOf</c>, <c>StartsWith</c> and <c>EndsWith</c> compare strings with the current culture, so only
        /// their overloads for characters count.
        /// </summary>
        private static bool IsAttestedStringMember(MethodSymbol method)
        {
            if (!s_attestedStringMembers.Contains(method.Name))
            {
                return false;
            }

            bool comparesWithCulture = method.Name is "IndexOf" or "LastIndexOf" or "StartsWith" or "EndsWith";
            return method.Parameters.All(parameter => parameter.RefKind == RefKind.None && parameter.Type switch
            {
                { SpecialType: SpecialType.System_String } => !comparesWithCulture,
                { SpecialType: SpecialType.System_Char or SpecialType.System_Int32 or SpecialType.System_Boolean } => true,
                ArrayTypeSymbol { IsSZArray: true, ElementType.SpecialType: SpecialType.System_String or SpecialType.System_Char } => true,
                { TypeKind: TypeKind.Enum } enumType => enumType.Name != "StringComparison",
                _ => false,
            });
        }

        /// <summary>The length and rank of an array, and the length and characters of a string.</summary>
        private static bool IsAttestedGetter(PropertySymbol property)
            => property.ContainingType.SpecialType switch
            {
                SpecialType.System_Array => property.Name is "Length" or "LongLength" or "Rank",
                SpecialType.System_String => property.IsIndexer || property.Name == "Length",
                _ => false,
            };

        /// <summary>The constructors of exceptions from other assemblies: <c>throw new ArgumentException(...)</c>.</summary>
        private static bool IsAttestedConstructor(MethodSymbol constructor)
        {
            if (constructor.ContainingModule is SourceModuleSymbol)
            {
                return false;
            }

            for (var type = constructor.ContainingType; type is not null; type = type.BaseTypeNoUseSiteDiagnostics)
            {
                if (type is { Name: "Exception", Arity: 0, ContainingType: null, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Declarations and call sites

        /// <summary>
        /// The checks of a <c>[Pure]</c> declaration in a Sharper file. A struct member must be <c>readonly</c>, so the
        /// existing errors cover writes to <c>this</c> and consumers see <c>[IsReadOnly]</c>. Virtual members are not
        /// supported yet: every override would have to be pure.
        /// </summary>
        private static void CheckPureDeclaration(Symbol member, BindingDiagnosticBag diagnostics)
        {
            var location = member.GetFirstLocation();
            if (member.IsVirtual || member.IsAbstract || member.IsOverride)
            {
                // Not supported yet: the rule for overrides and implementations is the second part of slice 1 (#173).
                diagnostics.Add(ErrorCode.ERR_SharperPureVirtual, location, member);
                return;
            }

            var method = member as MethodSymbol ?? ((PropertySymbol)member).GetMethod;
            if (method is { IsStatic: false, MethodKind: not MethodKind.Constructor } &&
                method.ContainingType.IsStructType() &&
                !method.IsEffectivelyReadOnly)
            {
                diagnostics.Add(ErrorCode.ERR_SharperPureStructMemberNotReadOnly, location, member);
            }
        }

        /// <summary>
        /// Warns when a call of a <c>[Pure]</c> member passes an <c>out</c> argument that may alias another argument: the
        /// member would observe its own write (#177). Fresh are discards, new and existing locals, by-value parameters, and
        /// the <c>out</c> parameters of a <c>[Pure]</c> caller, whose own callers are checked.
        /// </summary>
        internal static void ReportPureOutArguments(MethodSymbol method, ImmutableArray<BoundExpression> arguments, ImmutableArray<RefKind> refKinds, Symbol? containingMember, BindingDiagnosticBag diagnostics)
        {
            if (refKinds.IsDefault || !refKinds.Contains(RefKind.Out) || !IsDeclaredPure(method))
            {
                return;
            }

            for (int i = 0; i < arguments.Length; i++)
            {
                if (refKinds[i] == RefKind.Out && !isFresh(arguments[i]))
                {
                    diagnostics.Add(ErrorCode.WRN_SharperPureOutArgumentNotFresh, arguments[i].Syntax.Location, method, arguments[i].Syntax);
                }
            }

            bool isFresh(BoundExpression argument)
                => argument switch
                {
                    BoundDiscardExpression or OutVariablePendingInference or OutDeconstructVarPendingInference => true,
                    BoundLocal { LocalSymbol.RefKind: RefKind.None } => true,
                    BoundParameter { ParameterSymbol.RefKind: RefKind.None } => true,
                    BoundParameter { ParameterSymbol: { RefKind: RefKind.Out } parameter } =>
                        parameter.ContainingSymbol == (object?)containingMember && containingMember is MethodSymbol caller && IsDeclaredPure(caller),
                    _ => false,
                };
        }

        #endregion
    }
}
