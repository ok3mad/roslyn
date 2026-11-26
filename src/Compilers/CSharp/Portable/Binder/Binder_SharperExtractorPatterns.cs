// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper: extractor patterns, slice 3 of Feature 13 (#35). A positional pattern whose name is no type,
    /// <c>s is TryParse(var n)</c> or <c>s is Checks.Length(&gt; 0)</c>, is an error in C# (CS0246, CS0426, CS0234 or
    /// CS0118). In a Sharper file, when the name binds to a method group, it calls a pure method with the input:
    /// a <c>bool TryX(T, out U...)</c> that returns true, with the sub-patterns on the <c>out</c> values, or a
    /// <c>U F(T)</c>, with its one sub-pattern on the result.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Returns null where the pattern keeps its C# meaning or error: a type of that name (which always wins), no method
        /// group, no method of either shape, a property clause or a designation, union matching or a <c>dynamic</c> input.
        /// </summary>
        private BoundExtractorPattern? TryBindSharperExtractorPattern(
            RecursivePatternSyntax node,
            NamedTypeSymbol? unionType,
            TypeSymbol inputType,
            bool permitDesignations,
            bool hasErrors,
            BindingDiagnosticBag diagnostics,
            out bool hasUnionMatching)
        {
            hasUnionMatching = false;
            if (node is not { Type: { } typeSyntax, PositionalPatternClause: { } positionalClause, PropertyPatternClause: null, Designation: null } ||
                unionType is not null ||
                !node.IsFeatureEnabled(MessageID.IDS_FeatureSharper) ||
                inputType.IsErrorType() ||
                inputType.IsDynamic() ||
                inputType.IsVoidType() ||
                inputType.IsPointerOrFunctionPointer() ||
                inputType.StrippedType() is NamedTypeSymbol { IsUnionType: true } ||
                !IsSharperExtractorTypeNotFound(typeSyntax))
            {
                return null;
            }

            var expressionTrial = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression expression = BindExpression(typeSyntax, expressionTrial);
            if (expression is not BoundMethodGroup methodGroup || expressionTrial.HasAnyErrors())
            {
                expressionTrial.Free();
                return null;
            }

            // Shape 2, 'bool TryX(T, out U...)', with an out argument per sub-pattern; then shape 3, 'U F(T)', for one
            // sub-pattern without a name. The first shape that finds an applicable method is the one.
            int count = positionalClause.Subpatterns.Count;
            var placeholder = new BoundValuePlaceholder(typeSyntax, inputType) { WasCompilerGenerated = true };
            bool isTryShape = true;
            BoundCall? call = tryBindCall(outCount: count, out BindingDiagnosticBag? callDiagnostics);
            if (call is null && count == 1 && positionalClause.Subpatterns[0] is { NameColon: null, ExpressionColon: null })
            {
                isTryShape = false;
                call = tryBindCall(outCount: 0, out callDiagnostics);
            }

            if (call is null)
            {
                expressionTrial.Free();
                return null;
            }

            Debug.Assert(callDiagnostics is not null);
            diagnostics.AddRangeAndFree(expressionTrial);
            diagnostics.AddRangeAndFree(callDiagnostics);

            // An applicable call with an error, such as an ambiguity, reports it.
            hasErrors |= call.HasAnyErrors;

            // A refinement rejects every opaque pattern as not closed (CS20058), so its call is not checked there.
            if (!hasErrors &&
                node.FirstAncestorOrSelf<RefinementClauseSyntax>() is null &&
                !SharperImmutability.CheckExtractorPatternCall(Compilation, ContainingMemberOrLambda as MethodSymbol, call, diagnostics))
            {
                hasErrors = true;
            }

            var subpatterns = ArrayBuilder<BoundPositionalSubpattern>.GetInstance(count);
            int outIndex = 0;
            for (int i = 0; i < count; i++)
            {
                SubpatternSyntax subpatternSyntax = positionalClause.Subpatterns[i];
                ParameterSymbol? parameter = null;
                TypeSymbol elementType;
                if (!isTryShape)
                {
                    Debug.Assert(call.Type is not null);
                    elementType = call.Type;
                }
                else if (findOutArgument(call, outIndex++) is int argumentIndex)
                {
                    elementType = call.Arguments[argumentIndex].Type ?? CreateErrorType();
                    int parameterIndex = call.ArgsToParamsOpt.IsDefault ? argumentIndex : call.ArgsToParamsOpt[argumentIndex];
                    parameter = parameterIndex < call.Method.ParameterCount ? call.Method.Parameters[parameterIndex] : null;
                }
                else
                {
                    Debug.Assert(hasErrors);
                    elementType = CreateErrorType();
                }

                if (subpatternSyntax.NameColon is { } nameColon)
                {
                    // As for Deconstruct: the name must be the out parameter's.
                    if (parameter is not null && nameColon.Name.Identifier.ValueText != parameter.Name)
                    {
                        diagnostics.Add(ErrorCode.ERR_DeconstructParameterNameMismatch, nameColon.Name.Location, nameColon.Name.Identifier.ValueText, parameter.Name);
                    }
                }
                else if (subpatternSyntax.ExpressionColon is { } expressionColon)
                {
                    diagnostics.Add(ErrorCode.ERR_IdentifierExpected, expressionColon.Expression.Location);
                }

                NamedTypeSymbol? subUnionType = null;
                BoundPattern pattern = BindPattern(
                    subpatternSyntax.Pattern, ref subUnionType, elementType, permitDesignations, hasErrors, diagnostics,
                    out bool subpatternHasUnionMatching, underIsPattern: false);
                hasUnionMatching |= subpatternHasUnionMatching;
                subpatterns.Add(new BoundPositionalSubpattern(subpatternSyntax, parameter, pattern));
            }

            return new BoundExtractorPattern(
                node, call, placeholder, subpatterns.ToImmutableAndFree(), isTryShape, inputType: inputType, narrowedType: inputType, hasErrors);

            // 'M(x, out _, ...)' with the input as the first argument, never the receiver, as a call of that name would be
            // bound. Null if no method applies, or the method found does not have the shape.
            BoundCall? tryBindCall(int outCount, out BindingDiagnosticBag? callDiagnostics)
            {
                var analyzedArguments = AnalyzedArguments.GetInstance();
                analyzedArguments.Arguments.Add(placeholder);
                if (outCount > 0)
                {
                    analyzedArguments.RefKinds.Add(RefKind.None);
                    for (int i = 0; i < outCount; i++)
                    {
                        analyzedArguments.Arguments.Add(new BoundDiscardExpression(positionalClause.Subpatterns[i], NullableAnnotation.Oblivious, isInferred: true, type: null) { WasCompilerGenerated = true });
                        analyzedArguments.RefKinds.Add(RefKind.Out);
                    }
                }

                var trial = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
                BoundExpression bound = BindMethodGroupInvocation(
                    typeSyntax, typeSyntax, GetName(typeSyntax), methodGroup, analyzedArguments, trial, queryClause: null,
                    ignoreNormalFormIfHasValidParamsParameter: false, out bool anyApplicableCandidates,
                    disallowExpandedNonArrayParams: false, acceptOnlyMethods: true);
                analyzedArguments.Free();

                if (bound is BoundCall result &&
                    (result.HasAnyErrors ? anyApplicableCandidates : hasShape(result, isTry: outCount > 0 || count == 0)))
                {
                    callDiagnostics = trial;
                    return result;
                }

                trial.Free();
                callDiagnostics = null;
                return null;
            }

            // A method that returns by value, with no 'params' expansion and no default values; for the Try shape it
            // returns 'bool', and for shape 3 it returns a value.
            static bool hasShape(BoundCall call, bool isTry)
            {
                MethodSymbol method = call.Method;
                if (method.RefKind != RefKind.None || call.Expanded ||
                    (isTry ? method.ReturnType.SpecialType != SpecialType.System_Boolean : method.ReturnsVoid))
                {
                    return false;
                }

                if (!call.DefaultArguments.IsNull)
                {
                    for (int i = 0; i < call.Arguments.Length; i++)
                    {
                        if (call.DefaultArguments[i])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            static int? findOutArgument(BoundCall call, int outIndex)
            {
                if (call.ArgumentRefKindsOpt.IsDefaultOrEmpty)
                {
                    return null;
                }

                int count = 0;
                for (int i = 0; i < call.Arguments.Length; i++)
                {
                    if (call.ArgumentRefKindsOpt[i] == RefKind.Out && count++ == outIndex)
                    {
                        return i;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Whether <paramref name="id"/> is in the name of a positional pattern with a positional clause, in a Sharper file,
        /// which binds as an expression when it names no type (an extractor pattern). For the identifier map of primary
        /// constructor captures (<c>MethodCompiler</c>): <c>s is p.TryParse(var n)</c> may read a parameter <c>p</c>.
        /// </summary>
        internal static bool IsInSharperExtractorPatternName(ExpressionSyntax id)
        {
            SyntaxNode child = id;
            while (child.Parent is QualifiedNameSyntax qualifiedName)
            {
                child = qualifiedName;
            }

            return child.Parent is RecursivePatternSyntax { PositionalPatternClause: not null } recursive &&
                recursive.Type == child &&
                id.IsFeatureEnabled(MessageID.IDS_FeatureSharper);
        }

        /// <summary>
        /// A type named in a positional pattern always wins: the pattern is an extractor only where binding its name as a
        /// type reports that no type of that name exists (CS0246, CS0426, CS0234) or that the name is something else
        /// (CS0118, for 'receiver.TryX').
        /// </summary>
        private bool IsSharperExtractorTypeNotFound(TypeSyntax typeSyntax)
        {
            var trial = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
            BindType(typeSyntax, trial, out _);
            bool notFound = trial.HasAnyErrors();
            if (notFound)
            {
                foreach (Diagnostic diagnostic in trial.DiagnosticBag!.AsEnumerable())
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error &&
                        (ErrorCode)diagnostic.Code is not (ErrorCode.ERR_SingleTypeNameNotFound or ErrorCode.ERR_DottedTypeNameNotFoundInAgg or
                            ErrorCode.ERR_DottedTypeNameNotFoundInNS or ErrorCode.ERR_BadSKknown))
                    {
                        notFound = false;
                        break;
                    }
                }
            }

            trial.Free();
            return notFound;
        }
    }
}
