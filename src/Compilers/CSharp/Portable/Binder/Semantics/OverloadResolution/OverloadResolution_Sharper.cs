// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class OverloadResolution
    {
        /// <summary>
        /// Sharper step P5: in a body bound with placeholders (<see cref="SharperPlaceholderMethodSymbol"/>), records for
        /// each argument that is a parameter being inferred the candidates that are applicable with the placeholder in
        /// place, and the type each takes there.
        /// </summary>
        /// <remarks>
        /// The placeholder type converts to nothing, so the call fails and <paramref name="result"/> holds the complete
        /// results, every bad argument included. A candidate is applicable with the placeholder in place when it is
        /// applicable, or when its only bad arguments are placeholders passed with a ref kind that the parameter accepts:
        /// the other arguments are bound and converted first, and type inference fixes the type parameters from them (an
        /// error type gives no bound). A candidate whose type inference failed is kept when a placeholder goes to a
        /// parameter whose type involves the candidate's type parameters, since the placeholder's type could fix them; it
        /// then takes a type parameter there. A <c>params</c> candidate is applicable in its normal form and in its
        /// expanded form separately: overload resolution keeps one form per candidate, so the expanded form is computed
        /// here again. A call that succeeds (a placeholder that converts) records nothing.
        /// </remarks>
        private void RecordSharperPlaceholderCandidates(
            SharperPlaceholderMethodSymbol placeholder,
            ArrayBuilder<TypeWithAnnotations>? typeArguments,
            AnalyzedArguments arguments,
            OverloadResolutionResult<MethodSymbol> result,
            Options options,
            bool isConstructor)
        {
            ArrayBuilder<int>? targets = null;
            for (int i = 0; i < arguments.Arguments.Count; i++)
            {
                if (arguments.Arguments[i] is BoundParameter { ParameterSymbol: var parameter } && placeholder.IsBeingInferred(parameter))
                {
                    (targets ??= ArrayBuilder<int>.GetInstance()).Add(i);
                }
            }

            if (targets is null)
            {
                return;
            }

            if (result.ResultsBuilder.Any(static r => r.IsValid))
            {
                targets.Free();
                return;
            }

            var candidates = new ArrayBuilder<SharperPlaceholderCandidate>[targets.Count];
            for (int t = 0; t < targets.Count; t++)
            {
                candidates[t] = ArrayBuilder<SharperPlaceholderCandidate>.GetInstance();
            }

            var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            bool allowExpanded = (options & (Options.IsMethodGroupConversion | Options.DisallowExpandedForm)) == 0;
            foreach (var memberResult in result.ResultsBuilder)
            {
                MethodSymbol member = memberResult.Member;
                MethodSymbol leastOverridden = memberResult.LeastOverriddenMember;
                if (allowExpanded &&
                    IsValidParams(_binder, isConstructor ? member : leastOverridden, disallowExpandedNonArrayParams: (options & Options.DisallowExpandedNonArrayParams) != 0, out TypeWithAnnotations definitionElementType))
                {
                    // Both forms of a params candidate; overload resolution kept one of them.
                    MethodSymbol original = member.ConstructedFrom;
                    var typeArgumentsOrEmpty = typeArguments ?? ArrayBuilder<TypeWithAnnotations>.GetInstance();
                    MemberResolutionResult<MethodSymbol> normal = isConstructor
                        ? new MemberResolutionResult<MethodSymbol>(member, member, IsConstructorApplicableInNormalForm(member, arguments, completeResults: true, ref useSiteInfo), hasTypeArgumentInferredFromFunctionType: false)
                        : IsMemberApplicableInNormalForm(original, (MethodSymbol)original.GetLeastOverriddenMember(_binder.ContainingType), typeArgumentsOrEmpty, arguments, options, completeResults: true, ref useSiteInfo);
                    MemberResolutionResult<MethodSymbol> expanded = isConstructor
                        ? new MemberResolutionResult<MethodSymbol>(member, member, IsConstructorApplicableInExpandedForm(member, arguments, definitionElementType, completeResults: true, ref useSiteInfo), hasTypeArgumentInferredFromFunctionType: false)
                        : IsMemberApplicableInExpandedForm(original, (MethodSymbol)original.GetLeastOverriddenMember(_binder.ContainingType), typeArgumentsOrEmpty, arguments, definitionElementType, options, completeResults: true, dynamicConvertsToAnything: false, isMethodGroupConversion: false, ref useSiteInfo);
                    if (typeArguments is null)
                    {
                        typeArgumentsOrEmpty.Free();
                    }

                    addCandidate(normal.Member, normal.Result, expanded: false, elementType: default, arguments, targets, candidates);
                    addCandidate(expanded.Member, expanded.Result, expanded: true, definitionElementType, arguments, targets, candidates);
                }
                else
                {
                    addCandidate(member, memberResult.Result, expanded: false, elementType: default, arguments, targets, candidates);
                }
            }

            for (int t = 0; t < targets.Count; t++)
            {
                placeholder.RecordCandidates(arguments.Arguments[targets[t]].Syntax, _binder.ContainingMemberOrLambda, candidates[t].ToImmutableAndFree());
            }

            targets.Free();

            static void addCandidate(
                MethodSymbol member,
                MemberAnalysisResult analysis,
                bool expanded,
                TypeWithAnnotations elementType,
                AnalyzedArguments arguments,
                ArrayBuilder<int> targets,
                ArrayBuilder<SharperPlaceholderCandidate>[] candidates)
            {
                ImmutableArray<int> argsToParams;
                switch (analysis.Kind)
                {
                    case MemberResolutionKind.ApplicableInNormalForm:
                    case MemberResolutionKind.ApplicableInExpandedForm:
                        argsToParams = analysis.ArgsToParamsOpt;
                        break;

                    case MemberResolutionKind.BadArgumentConversion:
                        argsToParams = analysis.ArgsToParamsOpt;
                        foreach (int bad in analysis.BadArgumentsOpt.TrueBits())
                        {
                            if (!targets.Contains(bad) || !acceptsRefKind(member, argsToParams, bad, expanded, arguments.RefKind(bad)))
                            {
                                return;
                            }
                        }

                        break;

                    case MemberResolutionKind.TypeInferenceFailed:
                        {
                            var argumentAnalysis = AnalyzeArguments(member, arguments, isMethodGroupConversion: false, expanded);
                            if (!argumentAnalysis.IsValid)
                            {
                                return;
                            }

                            argsToParams = argumentAnalysis.ArgsToParamsOpt;
                            bool placeholderCouldFix = false;
                            foreach (int target in targets)
                            {
                                if (getParameter(member, argsToParams, target) is { } parameter &&
                                    parameter.Type.ContainsTypeParameter(member))
                                {
                                    placeholderCouldFix = true;
                                }
                            }

                            if (!placeholderCouldFix)
                            {
                                return;
                            }

                            break;
                        }

                    default:
                        return;
                }

                for (int t = 0; t < targets.Count; t++)
                {
                    if (getParameter(member, argsToParams, targets[t]) is not { } parameter)
                    {
                        continue;
                    }

                    bool inExpandedPart = expanded && parameter.Ordinal == member.GetParametersIncludingExtensionParameter(skipExtensionIfStatic: false).Length - 1;
                    TypeWithAnnotations type = !inExpandedPart ? parameter.TypeWithAnnotations
                        : analysis.ParamsElementTypeOpt.HasType ? analysis.ParamsElementTypeOpt
                        : elementType;
                    if (type.HasType)
                    {
                        candidates[t].Add(new SharperPlaceholderCandidate(member, parameter, type, inExpandedPart));
                    }
                }
            }

            static ParameterSymbol? getParameter(MethodSymbol member, ImmutableArray<int> argsToParams, int argument)
            {
                var parameters = member.GetParametersIncludingExtensionParameter(skipExtensionIfStatic: false);
                int index = argsToParams.IsDefault ? argument : argsToParams[argument];
                return index < parameters.Length ? parameters[index] : null;
            }

            static bool acceptsRefKind(MethodSymbol member, ImmutableArray<int> argsToParams, int argument, bool expanded, RefKind argumentRefKind)
            {
                if (getParameter(member, argsToParams, argument) is not { } parameter)
                {
                    return false;
                }

                RefKind parameterRefKind = expanded && parameter.IsParams ? RefKind.None : parameter.RefKind;
                return argumentRefKind == parameterRefKind ||
                    (parameterRefKind is RefKind.In or RefKind.RefReadOnlyParameter && argumentRefKind is RefKind.None or RefKind.In or RefKind.Ref);
            }
        }
    }
}
