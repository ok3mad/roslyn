// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal static partial class SharperInferredParameterType
    {
        /// <summary>
        /// The types of the <c>infer</c> parameters of one member that are inferred from the uses in its body (step P4):
        /// <c>public infer Hello(infer id) =&gt; profiles.Load(id);</c> gives <c>id</c> the type of the one
        /// <c>Load</c>'s parameter.
        /// </summary>
        /// <remarks>
        /// The body is bound once with a placeholder for the member (<see cref="SharperPlaceholderMethodSymbol"/>), whose
        /// parameters being inferred have a placeholder type, and the uses that pin a type are read from the bound tree
        /// (<see cref="UsesCollector"/>). Then the signature is completed with the types, and the body is bound again
        /// normally when the member is compiled; that binding reports the body's errors. The inference runs before the
        /// signature lock (<see cref="InferBeforeSignature"/>), on the stack of default values and overridden members,
        /// and is kept by the compilation per parameter list and published once.
        /// </remarks>
        internal sealed class BodyInference
        {
            /// <summary>The member has no <c>infer</c> parameter that its body could give a type to.</summary>
            internal static readonly BodyInference NotApplicable = new BodyInference(default);

            /// <summary>One result per parameter of the list; null for the parameters not inferred from the body.</summary>
            private readonly ImmutableArray<Result?> _results;

            internal BodyInference(ImmutableArray<Result?> results)
            {
                _results = results;
            }

            internal bool IsApplicable => !_results.IsDefault;

            internal Result? GetResult(int ordinal) => IsApplicable && ordinal < _results.Length ? _results[ordinal] : null;

            /// <summary>The result of a member whose body needs its own signature, directly or through other members.</summary>
            internal static BodyInference CreateCycle(ParameterListSyntax parameterList, CSharpCompilation compilation)
            {
                string memberName = GetMemberName(parameterList);
                var results = ArrayBuilder<Result?>.GetInstance(parameterList.Parameters.Count);
                foreach (var parameter in parameterList.Parameters)
                {
                    if (IsInferredFromBody(parameter))
                    {
                        var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
                        diagnostics.Add(ErrorCode.ERR_SharperInferParameterBodyCycle, parameter.Type!.Location, parameter.Identifier.ValueText, memberName);
                        results.Add(new Result(CreateErrorType(compilation), diagnostics.ToReadOnlyAndFree()));
                    }
                    else
                    {
                        results.Add(null);
                    }
                }

                return new BodyInference(results.ToImmutableAndFree());
            }
        }

        /// <summary>One use of a parameter in the body that pins its type: the argument, the candidate and its parameter type.</summary>
        private readonly struct PinningUse
        {
            internal readonly SyntaxNode Argument;
            internal readonly Symbol Candidate;
            internal readonly TypeWithAnnotations Type;

            internal PinningUse(SyntaxNode argument, Symbol candidate, TypeWithAnnotations type)
            {
                Argument = argument;
                Candidate = candidate;
                Type = type;
            }
        }

        /// <summary>
        /// The member declarations whose bodies are bound with placeholders on this thread. Results that depend on the
        /// placeholders (the default values of lambdas and local functions declared in such a body) are not published.
        /// </summary>
        [ThreadStatic] private static List<CSharpSyntaxNode>? s_placeholderBodies;

        /// <summary>
        /// The types that the body of <paramref name="owner"/> gives its <c>infer</c> parameters that have neither a
        /// default value nor a member to copy from (step P2).
        /// </summary>
        internal static BodyInference GetBodyInference(Symbol owner)
        {
            if (owner is not SourceMemberMethodSymbol method ||
                owner is SharperPlaceholderMethodSymbol ||
                method.SyntaxNode is not BaseMethodDeclarationSyntax { ParameterList: { } parameterList } declaration ||
                declaration is { Body: null, ExpressionBody: null } ||
                method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion) ||
                !HasParameterInferredFromBody(parameterList) ||
                GetOverriddenSignature(owner).IsApplicable)
            {
                return BodyInference.NotApplicable;
            }

            CSharpCompilation compilation = owner.DeclaringCompilation;
            var results = compilation.SharperBodyInferences;
            if (results.TryGetValue(parameterList, out BodyInference? result))
            {
                return result;
            }

            var inProgress = s_inProgress ??= new List<SyntaxNode>();
            int cycleStart = inProgress.IndexOf(parameterList);
            if (cycleStart >= 0)
            {
                PublishCycle(compilation, inProgress, cycleStart);
                return results.GetValue(parameterList, _ => throw ExceptionUtilities.Unreachable());
            }

            inProgress.Add(parameterList);
            try
            {
                result = InferFromBody(method, declaration, parameterList);
            }
            finally
            {
                Debug.Assert(inProgress[inProgress.Count - 1] == parameterList);
                inProgress.RemoveAt(inProgress.Count - 1);
            }

            // A member found to be in a cycle while its body was bound already has its result.
            return Publish(results, compilation, parameterList, result);
        }

        /// <summary>The result for an <c>infer</c> parameter with no default value, when the member's body gives its type.</summary>
        private static Result? GetResultFromBody(Symbol owner, ParameterSyntax parameter)
        {
            // A lambda's parameters are bound with the enclosing member as the owner; they are not in its parameter list.
            if (parameter.Parent is not ParameterListSyntax parameterList ||
                (owner as SourceMemberMethodSymbol)?.SyntaxNode is not BaseMethodDeclarationSyntax { ParameterList: var ownerParameterList } ||
                ownerParameterList != parameterList)
            {
                return null;
            }

            return GetBodyInference(owner).GetResult(parameterList.Parameters.IndexOf(parameter));
        }

        private static bool IsInferredFromBody(ParameterSyntax parameter)
            => parameter is { Type.IsInfer: true, Default: null };

        private static bool HasParameterInferredFromBody(ParameterListSyntax parameterList)
        {
            foreach (var parameter in parameterList.Parameters)
            {
                if (IsInferredFromBody(parameter))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetMemberName(ParameterListSyntax parameterList)
            => parameterList.Parent switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.ValueText,
                ConversionOperatorDeclarationSyntax conversion => conversion.ImplicitOrExplicitKeyword.ValueText + " operator",
                var other => throw ExceptionUtilities.UnexpectedValue(other?.Kind()),
            };

        /// <summary>
        /// Binds the body of <paramref name="method"/> with placeholders for the parameters being inferred, and gives each
        /// of them the type its uses agree on.
        /// </summary>
        private static BodyInference InferFromBody(SourceMemberMethodSymbol method, BaseMethodDeclarationSyntax declaration, ParameterListSyntax parameterList)
        {
            CSharpCompilation compilation = method.DeclaringCompilation;
            Binder signatureBinder = GetDefaultValueBinder(method, parameterList.Parameters[0]);

            // The parameters as the signature will have them, with a placeholder type where the type is being inferred.
            var placeholderParameters = ArrayBuilder<(TypeWithAnnotations Type, RefKind RefKind, string Name)>.GetInstance(parameterList.Parameters.Count);
            var isTarget = ArrayBuilder<bool>.GetInstance(parameterList.Parameters.Count);
            foreach (var parameter in parameterList.Parameters)
            {
                TypeWithAnnotations type;
                bool target = false;
                if (parameter.Type is null)
                {
                    // __arglist is not a parameter.
                    continue;
                }

                type = signatureBinder.BindTypeOrSharperInferKeyword(parameter.Type, BindingDiagnosticBag.Discarded, out bool isInfer);
                if (isInfer)
                {
                    if (parameter.Default is null)
                    {
                        type = CreateErrorType(compilation);
                        target = true;
                    }
                    else
                    {
                        type = GetOrInfer(method, parameter, GetDefaultValueBinder(method, parameter)).Type;
                    }
                }

                placeholderParameters.Add((type, GetWrittenRefKind(parameter), parameter.Identifier.ValueText));
                isTarget.Add(target);
            }

            if (!isTarget.Contains(true))
            {
                // A type named 'infer' is in scope.
                placeholderParameters.Free();
                isTarget.Free();
                return BodyInference.NotApplicable;
            }

            // The written return type; a constructor returns void; 'infer' is being inferred.
            TypeWithAnnotations returnType = default;
            if (declaration is ConstructorDeclarationSyntax)
            {
                returnType = TypeWithAnnotations.Create(compilation.GetSpecialType(SpecialType.System_Void));
            }
            else if (declaration switch
            {
                MethodDeclarationSyntax m => m.ReturnType,
                OperatorDeclarationSyntax o => o.ReturnType,
                ConversionOperatorDeclarationSyntax c => c.Type,
                _ => null,
            } is { } returnTypeSyntax)
            {
                returnType = signatureBinder.BindTypeOrSharperInferKeyword(returnTypeSyntax.SkipRef(), BindingDiagnosticBag.Discarded, out _);
            }

            var placeholder = new SharperPlaceholderMethodSymbol(method, placeholderParameters.ToImmutableAndFree(), returnType);
            var collector = new UsesCollector(ImmutableArray<ParameterSymbol>.Empty, placeholder.ParameterCount);
            var dependencies = BindingDiagnosticBag.GetInstance(withDiagnostics: false, withDependencies: true);
            if (placeholder.TryGetBodyBinder() is { } bodyBinder)
            {
                var placeholderBodies = s_placeholderBodies ??= new List<CSharpSyntaxNode>();
                placeholderBodies.Add(declaration);
                object? suspendedReturnTypeInference = SharperInferredReturnType.SuspendInference();
                try
                {
                    var bodyDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: true);
                    BoundNode body = bodyBinder.BindMethodBody(declaration, bodyDiagnostics);
                    dependencies.AddDependencies(bodyDiagnostics);
                    bodyDiagnostics.Free();
                    var targets = ArrayBuilder<ParameterSymbol>.GetInstance();
                    for (int i = 0; i < placeholder.ParameterCount; i++)
                    {
                        if (isTarget[i])
                        {
                            targets.Add(placeholder.Parameters[i]);
                        }
                    }

                    collector = new UsesCollector(targets.ToImmutableAndFree(), placeholder.ParameterCount);
                    collector.Visit(body);
                }
                finally
                {
                    SharperInferredReturnType.ResumeInference(suspendedReturnTypeInference);
                    Debug.Assert(placeholderBodies[placeholderBodies.Count - 1] == declaration);
                    placeholderBodies.RemoveAt(placeholderBodies.Count - 1);
                }
            }

            // One result per parameter of the list, __arglist included, so that the ordinals of the syntax apply.
            var results = ArrayBuilder<Result?>.GetInstance(parameterList.Parameters.Count);
            int placeholderOrdinal = 0;
            foreach (var parameter in parameterList.Parameters)
            {
                if (parameter.Type is null)
                {
                    results.Add(null);
                    continue;
                }

                int ordinal = placeholderOrdinal++;
                results.Add(isTarget[ordinal] ? DecideFromUses(parameter, collector.Uses[ordinal], collector.FirstNotPinning[ordinal], dependencies, compilation) : null);
                collector.Uses[ordinal]?.Free();
            }

            isTarget.Free();
            dependencies.Free();
            return new BodyInference(results.ToImmutableAndFree());
        }

        /// <summary>
        /// The type of <paramref name="parameter"/> from the uses that pin it: none is CS20027, which says why with the
        /// first use that pins nothing, uses that agree give their type, and uses that disagree are CS20057, naming the
        /// first two that differ.
        /// </summary>
        private static Result DecideFromUses(
            ParameterSyntax parameter,
            ArrayBuilder<PinningUse>? uses,
            (SyntaxNode Syntax, IFormattable Reason)? firstNotPinning,
            BindingDiagnosticBag dependencies,
            CSharpCompilation compilation)
        {
            var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: true);
            TypeWithAnnotations type;
            if (uses is null or { Count: 0 })
            {
                // Why nothing pinned the parameter: its first use that pins nothing, or no use.
                if (firstNotPinning is var (syntax, reason))
                {
                    var info = new CSDiagnosticInfo(
                        ErrorCode.ERR_SharperInferParameterNoDefault,
                        args: [parameter.Identifier.ValueText, reason],
                        symbols: ImmutableArray<Symbol>.Empty,
                        additionalLocations: [syntax.Location]);
                    diagnostics.Add(info, parameter.Type!.Location);
                }
                else
                {
                    diagnostics.Add(ErrorCode.ERR_SharperInferParameterNoDefault, parameter.Type!.Location, parameter.Identifier.ValueText, MessageID.IDS_SharperInferNotUsedInBody.Localize());
                }
                type = CreateErrorType(compilation);
            }
            else
            {
                PinningUse first = uses[0];
                type = first.Type;
                PinningUse? disagreeing = null;
                for (int i = 1; i < uses.Count; i++)
                {
                    PinningUse use = uses[i];
                    if (!type.Type.Equals(use.Type.Type, TypeCompareKind.AllNullableIgnoreOptions | TypeCompareKind.IgnoreTupleNames))
                    {
                        disagreeing = use;
                        break;
                    }

                    // A type that every use accepts: not annotated wins, as for the inputs of a lambda.
                    type = type.MergeEquivalentTypes(use.Type, VarianceKind.In);
                }

                if (disagreeing is { } other)
                {
                    var info = new CSDiagnosticInfo(
                        ErrorCode.ERR_SharperInferParameterUsesDisagree,
                        args: [parameter.Identifier.ValueText, first.Type, first.Candidate, other.Type, other.Candidate],
                        symbols: [first.Candidate, other.Candidate],
                        additionalLocations: [first.Argument.Location, other.Argument.Location]);
                    diagnostics.Add(info, parameter.Type!.Location);
                    type = CreateErrorType(compilation);
                }
                else
                {
                    diagnostics.AddDependencies(dependencies);
                }
            }

            return new Result(type, diagnostics.ToReadOnlyAndFree());
        }

        /// <summary>The ref kind written on a parameter, as the signature reads it: <c>ref</c>, <c>out</c>, <c>in</c>, <c>ref readonly</c>.</summary>
        private static RefKind GetWrittenRefKind(ParameterSyntax parameter)
        {
            RefKind refKind = RefKind.None;
            foreach (var modifier in parameter.Modifiers)
            {
                switch (modifier.Kind())
                {
                    case SyntaxKind.RefKeyword:
                        refKind = RefKind.Ref;
                        break;
                    case SyntaxKind.OutKeyword:
                        refKind = RefKind.Out;
                        break;
                    case SyntaxKind.InKeyword:
                        refKind = RefKind.In;
                        break;
                    case SyntaxKind.ReadOnlyKeyword when refKind == RefKind.Ref:
                        refKind = RefKind.RefReadOnlyParameter;
                        break;
                }
            }

            return refKind;
        }

        /// <summary>
        /// True while <paramref name="parameter"/>, a parameter of a lambda or local function, is in a body that is bound
        /// with placeholders on this thread: its inferred type may depend on a placeholder, so it is not published.
        /// </summary>
        private static bool IsInPlaceholderBody(ParameterSyntax parameter)
        {
            if (s_placeholderBodies is not { Count: > 0 } bodies)
            {
                return false;
            }

            foreach (var body in bodies)
            {
                if (body.SyntaxTree == parameter.SyntaxTree &&
                    body.Span.Contains(parameter.Span) &&
                    parameter.Parent != ((BaseMethodDeclarationSyntax)body).ParameterList)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Collects the uses of the placeholder parameters that pin a type: a parameter passed by value, directly, as an
        /// argument of a call (a method, a constructor or a delegate invocation) that has exactly one candidate, to a
        /// parameter that is neither <c>params</c> nor <c>ref</c> or <c>out</c>, whose type does not involve the
        /// candidate's own type parameters. Member access, operators, <c>is</c> tests and interpolations pin nothing.
        /// </summary>
        /// <remarks>
        /// The bound tree holds the final binding of each lambda, so the trial bindings that overload resolution makes for
        /// other delegate types are not read. Lambdas and local functions in the body count like the rest of it. The
        /// placeholder type converts to nothing, so a call that it pins fails overload resolution, and its candidates are
        /// what the binder keeps for error recovery.
        /// </remarks>
        private sealed class UsesCollector : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly ImmutableArray<ParameterSymbol> _targets;

            /// <summary>Per placeholder parameter, the uses that pin a type, in the order of the body.</summary>
            internal readonly ArrayBuilder<PinningUse>?[] Uses;

            /// <summary>Per placeholder parameter, its first use that pins nothing, and why.</summary>
            internal readonly (SyntaxNode Syntax, IFormattable Reason)?[] FirstNotPinning;

            internal UsesCollector(ImmutableArray<ParameterSymbol> targets, int parameterCount)
            {
                _targets = targets;
                Uses = new ArrayBuilder<PinningUse>?[parameterCount];
                FirstNotPinning = new (SyntaxNode, IFormattable)?[parameterCount];
            }

            public override BoundNode? VisitCall(BoundCall node)
            {
                ImmutableArray<MethodSymbol> candidates = node.OriginalMethodsOpt;
                Symbol? candidate =
                    candidates.IsDefault ? (node.Method is ErrorMethodSymbol ? null : node.Method) :
                    candidates.Length != 1 ? null :
                    node.Method is ErrorMethodSymbol ? candidates[0] :
                    node.Method;

                if (node.InvokedAsExtensionMethod && node.Arguments is [var receiver, ..] && GetTarget(receiver) is { } receiverTarget)
                {
                    // The receiver of an extension method call is a member access, which pins nothing.
                    NoteNotPinning(receiverTarget, receiver.Syntax, MessageID.IDS_SharperInferMemberAccessPinsNothing.Localize());
                }

                if (candidate is MethodSymbol method)
                {
                    AddUses(node.Arguments, node.ArgumentNamesOpt, node.ArgumentRefKindsOpt, method, method.Parameters, skipReceiver: node.InvokedAsExtensionMethod);
                }
                else if (candidates is [var someCandidate, ..])
                {
                    NoteSeveralCandidates(node.Arguments, someCandidate.Name);
                }

                return base.VisitCall(node);
            }

            public override BoundNode? VisitObjectCreationExpression(BoundObjectCreationExpression node)
            {
                AddUses(node.Arguments, node.ArgumentNamesOpt, node.ArgumentRefKindsOpt, node.Constructor, node.Constructor.Parameters);
                return base.VisitObjectCreationExpression(node);
            }

            public override BoundNode? VisitBadExpression(BoundBadExpression node)
            {
                // A constructor call that failed overload resolution keeps its candidates and its arguments.
                if (node.Syntax is BaseObjectCreationExpressionSyntax { ArgumentList: { } argumentList } &&
                    node.Symbols is [MethodSymbol { MethodKind: MethodKind.Constructor } constructor, ..] &&
                    node.ChildBoundNodes.Length >= argumentList.Arguments.Count)
                {
                    ImmutableArray<BoundExpression> arguments = node.ChildBoundNodes.RemoveRange(argumentList.Arguments.Count, node.ChildBoundNodes.Length - argumentList.Arguments.Count);
                    if (node.Symbols.Length > 1)
                    {
                        NoteSeveralCandidates(arguments, constructor.ContainingType.Name);
                        return base.VisitBadExpression(node);
                    }

                    var names = ArrayBuilder<string?>.GetInstance(argumentList.Arguments.Count);
                    var refKinds = ArrayBuilder<RefKind>.GetInstance(argumentList.Arguments.Count);
                    foreach (var argument in argumentList.Arguments)
                    {
                        names.Add(argument.NameColon?.Name.Identifier.ValueText);
                        refKinds.Add(argument.RefKindKeyword.Kind() switch
                        {
                            SyntaxKind.RefKeyword => RefKind.Ref,
                            SyntaxKind.OutKeyword => RefKind.Out,
                            SyntaxKind.InKeyword => RefKind.In,
                            _ => RefKind.None,
                        });
                    }

                    AddUses(arguments, names.ToImmutableAndFree(), refKinds.ToImmutableAndFree(), constructor, constructor.Parameters);
                }

                return base.VisitBadExpression(node);
            }

            /// <summary>
            /// Every use of a placeholder parameter that no call has pinned or explained: member access, operators, type
            /// tests and interpolations pin nothing.
            /// </summary>
            public override BoundNode? VisitParameter(BoundParameter node)
            {
                if (_targets.Contains(node.ParameterSymbol))
                {
                    SyntaxNode syntax = node.Syntax;
                    SyntaxNode? parent = syntax.Parent;
                    while (parent is ParenthesizedExpressionSyntax)
                    {
                        syntax = parent;
                        parent = parent.Parent;
                    }

                    MessageID reason = parent switch
                    {
                        MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == syntax => MessageID.IDS_SharperInferMemberAccessPinsNothing,
                        ConditionalAccessExpressionSyntax conditionalAccess when conditionalAccess.Expression == syntax => MessageID.IDS_SharperInferMemberAccessPinsNothing,
                        ElementAccessExpressionSyntax elementAccess when elementAccess.Expression == syntax => MessageID.IDS_SharperInferMemberAccessPinsNothing,
                        BinaryExpressionSyntax binary when binary.Kind() is SyntaxKind.IsExpression or SyntaxKind.AsExpression => MessageID.IDS_SharperInferTypeTestPinsNothing,
                        IsPatternExpressionSyntax or SwitchExpressionSyntax or SwitchStatementSyntax => MessageID.IDS_SharperInferTypeTestPinsNothing,
                        BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => MessageID.IDS_SharperInferOperatorPinsNothing,
                        AssignmentExpressionSyntax assignment when !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) => MessageID.IDS_SharperInferOperatorPinsNothing,
                        InterpolationSyntax => MessageID.IDS_SharperInferInterpolationPinsNothing,
                        _ => MessageID.IDS_SharperInferUsePinsNothing,
                    };

                    NoteNotPinning(node.ParameterSymbol, syntax, reason.Localize());
                }

                return base.VisitParameter(node);
            }

            private void NoteNotPinning(ParameterSymbol target, SyntaxNode syntax, IFormattable reason)
                => FirstNotPinning[target.Ordinal] ??= (syntax, reason);

            private void NoteSeveralCandidates(ImmutableArray<BoundExpression> arguments, string name)
            {
                foreach (var argument in arguments)
                {
                    if (GetTarget(argument) is { } target)
                    {
                        NoteNotPinning(target, argument.Syntax, new MessageFragment(MessageID.IDS_SharperInferSeveralCandidatesPinNothing, [name]));
                    }
                }
            }

            private void AddUses(
                ImmutableArray<BoundExpression> arguments,
                ImmutableArray<string?> argumentNames,
                ImmutableArray<RefKind> argumentRefKinds,
                MethodSymbol candidate,
                ImmutableArray<ParameterSymbol> parameters,
                bool skipReceiver = false)
            {
                for (int i = skipReceiver ? 1 : 0; i < arguments.Length; i++)
                {
                    if (GetTarget(arguments[i]) is not { } target)
                    {
                        continue;
                    }

                    SyntaxNode syntax = arguments[i].Syntax;
                    if ((!argumentRefKinds.IsDefault && argumentRefKinds[i] != RefKind.None) ||
                        GetParameter(i, argumentNames, parameters) is not { } parameter)
                    {
                        NoteNotPinning(target, syntax, (argumentRefKinds.IsDefault || argumentRefKinds[i] == RefKind.None ? MessageID.IDS_SharperInferUsePinsNothing : MessageID.IDS_SharperInferRefArgumentPinsNothing).Localize());
                    }
                    else if (parameter.IsParams)
                    {
                        NoteNotPinning(target, syntax, MessageID.IDS_SharperInferParamsPinsNothing.Localize());
                    }
                    else if (parameter.RefKind is RefKind.Ref or RefKind.Out)
                    {
                        NoteNotPinning(target, syntax, MessageID.IDS_SharperInferRefArgumentPinsNothing.Localize());
                    }
                    else if (InvolvesTypeParametersNotInScope(parameter.Type))
                    {
                        NoteNotPinning(target, syntax, new MessageFragment(MessageID.IDS_SharperInferTypeParameterPinsNothing, [candidate.Name]));
                    }
                    else if (parameter.Type.IsErrorType())
                    {
                        // A parameter of the placeholder itself (recursion), or of a member whose type has errors.
                        NoteNotPinning(target, syntax, MessageID.IDS_SharperInferUsePinsNothing.Localize());
                    }
                    else
                    {
                        var uses = Uses[target.Ordinal] ??= ArrayBuilder<PinningUse>.GetInstance();
                        uses.Add(new PinningUse(syntax, candidate, parameter.TypeWithAnnotations));
                    }
                }
            }

            /// <summary>The placeholder parameter that the argument is, directly; the binder may wrap it for error recovery.</summary>
            private ParameterSymbol? GetTarget(BoundExpression argument)
            {
                if (argument is BoundConversion { WasCompilerGenerated: true, ExplicitCastInCode: false } conversion)
                {
                    argument = conversion.Operand;
                }

                return argument is BoundParameter { ParameterSymbol: var parameter } && _targets.Contains(parameter) ? parameter : null;
            }

            /// <summary>The parameter an argument goes to: by name if it is named, otherwise by position.</summary>
            private static ParameterSymbol? GetParameter(int argumentIndex, ImmutableArray<string?> argumentNames, ImmutableArray<ParameterSymbol> parameters)
            {
                if (!argumentNames.IsDefault && argumentNames[argumentIndex] is { } name)
                {
                    foreach (var parameter in parameters)
                    {
                        if (parameter.Name == name)
                        {
                            return parameter;
                        }
                    }

                    return null;
                }

                return argumentIndex < parameters.Length ? parameters[argumentIndex] : null;
            }

            /// <summary>
            /// True if <paramref name="type"/> involves a type parameter that is not in scope in the signature being
            /// inferred: the candidate's own, whose type argument is inferred from the other arguments (step P5), or one of
            /// a lambda or local function in the body.
            /// </summary>
            private bool InvolvesTypeParametersNotInScope(TypeSymbol type)
            {
                MethodSymbol owner = (MethodSymbol)_targets[0].ContainingSymbol;
                return type.VisitType(
                    static (t, owner, _) => t is TypeParameterSymbol { ContainingSymbol: MethodSymbol typeParameterOwner } &&
                        (object)typeParameterOwner != ((SharperPlaceholderMethodSymbol)owner).Original,
                    owner) is not null;
            }
        }
    }
}
