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

        /// <summary>
        /// One use of a parameter in the body that pins its type: the parameter's syntax there, what pins it and the type it
        /// pins. What pins it is the candidate of a call (P4, P5), the local, parameter, field or property it is assigned
        /// to, or the return of the method, local function or lambda it is returned from (P6, a message fragment).
        /// </summary>
        private readonly struct PinningUse
        {
            internal readonly SyntaxNode Argument;
            internal readonly object Candidate;
            internal readonly TypeWithAnnotations Type;

            internal PinningUse(SyntaxNode argument, object candidate, TypeWithAnnotations type)
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
            => parameter is { Type.IsInferOrValParameterType: true, Default: null };

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
                LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
                var other => throw ExceptionUtilities.UnexpectedValue(other?.Kind()),
            };

        /// <summary>
        /// Binds the body of <paramref name="method"/> with placeholders for the parameters being inferred, and gives each
        /// of them the type its uses agree on.
        /// </summary>
        private static BodyInference InferFromBody(SourceMemberMethodSymbol method, BaseMethodDeclarationSyntax declaration, ParameterListSyntax parameterList)
        {
            CSharpCompilation compilation = method.DeclaringCompilation;
            var placeholder = CreatePlaceholder(method, declaration, parameterList, useConversionResults: true, out ImmutableArray<bool> isTarget);
            if (!isTarget.Contains(true))
            {
                // A type named 'infer' is in scope, or the conversions of a private method give every type (step P7b).
                return BodyInference.NotApplicable;
            }

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
                    placeholder.FreeCandidates();
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

            dependencies.Free();
            return new BodyInference(results.ToImmutableAndFree());
        }

        /// <summary>
        /// A placeholder for <paramref name="method"/> whose body can be bound before its signature exists (steps P4 and
        /// P7b): its parameters have their written types or the types of their default values (P1), and its <c>infer</c>
        /// parameters without a default value have an error type and are its targets. With
        /// <paramref name="useConversionResults"/>, a parameter that the conversions of a private method give a type (step
        /// P7b) has that type and is no target. The return type is the written one, void for a constructor, and being
        /// inferred for <c>infer</c>.
        /// </summary>
        private static SharperPlaceholderMethodSymbol CreatePlaceholder(
            SourceMemberMethodSymbol method,
            BaseMethodDeclarationSyntax declaration,
            ParameterListSyntax parameterList,
            bool useConversionResults,
            out ImmutableArray<bool> targets)
        {
            CSharpCompilation compilation = method.DeclaringCompilation;
            Binder? signatureBinder = parameterList.Parameters.Count > 0 ? GetDefaultValueBinder(method, parameterList.Parameters[0]) : null;

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

                type = signatureBinder!.BindTypeOrSharperInferKeyword(parameter.Type, BindingDiagnosticBag.Discarded, out bool isInfer);
                if (isInfer)
                {
                    if (parameter.Default is not null)
                    {
                        type = GetOrInfer(method, parameter, GetDefaultValueBinder(method, parameter)).Type;
                    }
                    else if (useConversionResults && GetResultFromConversions(method, parameter) is { } fromConversions)
                    {
                        type = fromConversions.Type;
                    }
                    else
                    {
                        type = CreateErrorType(compilation);
                        target = true;
                    }
                }

                placeholderParameters.Add((type, GetWrittenRefKind(parameter), parameter.Identifier.ValueText));
                isTarget.Add(target);
            }

            // The written return type; a constructor returns void; 'infer' is being inferred.
            TypeWithAnnotations returnType = default;
            if (declaration is ConstructorDeclarationSyntax or DestructorDeclarationSyntax)
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
                signatureBinder ??= method.WithTypeParametersBinder ?? compilation.GetBinderFactory(returnTypeSyntax.SyntaxTree).GetBinder(returnTypeSyntax);
                returnType = signatureBinder.BindTypeOrSharperInferKeyword(returnTypeSyntax.SkipRef(), BindingDiagnosticBag.Discarded, out _);
            }

            targets = isTarget.ToImmutableAndFree();
            return new SharperPlaceholderMethodSymbol(method, placeholderParameters.ToImmutableAndFree(), returnType, targets);
        }

        /// <summary>
        /// The <see cref="Diagnostic.Properties"/> key of CS20027 that lists the types the disagreeing candidates take
        /// (documentation comment reference IDs, one per line). The IDE fix that writes the parameter type reads it.
        /// </summary>
        internal const string CandidateTypesPropertyName = "SharperInferCandidateTypes";

        /// <summary>
        /// The type of <paramref name="parameter"/> from the uses that pin it: none is CS20027, which says why with the
        /// first use that pins nothing (or <paramref name="noUseReason"/>), uses that agree give their type, and uses that
        /// disagree are CS20057, naming the first two that differ. Uses that the parameter flows into merge their
        /// nullability with <see cref="VarianceKind.In"/> (not annotated wins); the delegate types a local function is
        /// converted to (step P7) flow into it, with <see cref="VarianceKind.Out"/> (annotated wins).
        /// </summary>
        private static Result DecideFromUses(
            ParameterSyntax parameter,
            ArrayBuilder<PinningUse>? uses,
            (SyntaxNode Syntax, IFormattable Reason, ImmutableArray<TypeSymbol> Types)? firstNotPinning,
            BindingDiagnosticBag dependencies,
            CSharpCompilation compilation,
            IFormattable? noUseReason = null,
            VarianceKind variance = VarianceKind.In)
        {
            var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: true);
            TypeWithAnnotations type;
            if (uses is null or { Count: 0 })
            {
                // Why nothing pinned the parameter: its first use that pins nothing, or no use.
                if (firstNotPinning is var (syntax, reason, types))
                {
                    var info = new CSDiagnosticInfo(
                        ErrorCode.ERR_SharperInferParameterNoDefault,
                        args: [parameter.Identifier.ValueText, reason],
                        symbols: ImmutableArray<Symbol>.Empty,
                        additionalLocations: [syntax.Location],
                        properties: getCandidateTypesProperty(types));
                    diagnostics.Add(info, parameter.Type!.Location);
                }
                else
                {
                    diagnostics.Add(ErrorCode.ERR_SharperInferParameterNoDefault, parameter.Type!.Location, parameter.Identifier.ValueText, noUseReason ?? MessageID.IDS_SharperInferNotUsedInBody.Localize());
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
                    type = type.MergeEquivalentTypes(use.Type, variance);
                }

                if (disagreeing is { } other)
                {
                    var info = new CSDiagnosticInfo(
                        ErrorCode.ERR_SharperInferParameterUsesDisagree,
                        args: [parameter.Identifier.ValueText, first.Type, first.Candidate, other.Type, other.Candidate],
                        symbols: getSymbols(first.Candidate, other.Candidate),
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

            // The types that the candidates which disagree take (the list in the message), for the IDE fix that writes
            // one of them: documentation comment reference IDs, one per line, in the order of the message.
            static ImmutableDictionary<string, string>? getCandidateTypesProperty(ImmutableArray<TypeSymbol> types)
            {
                if (types.IsDefaultOrEmpty)
                {
                    return null;
                }

                var ids = ArrayBuilder<string>.GetInstance(types.Length);
                foreach (var candidateType in types)
                {
                    if (!candidateType.IsErrorType() && DocumentationCommentId.CreateReferenceId(candidateType.GetPublicSymbol()) is { } id && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }

                return ids.Count == 0
                    ? null
                    : ImmutableDictionary<string, string>.Empty.Add(CandidateTypesPropertyName, string.Join("\n", ids.ToArrayAndFree()));
            }

            static ImmutableArray<Symbol> getSymbols(object first, object other)
                => (first, other) switch
                {
                    (Symbol a, Symbol b) => [a, b],
                    (Symbol a, _) => [a],
                    (_, Symbol b) => [b],
                    _ => ImmutableArray<Symbol>.Empty,
                };
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
        /// with placeholders on this thread (for the member's own parameters, P4, or for a local function's, P7): its inferred type may depend on a placeholder, so it is not published.
        /// </summary>
        private static bool IsInPlaceholderBody(ParameterSyntax parameter)
        {
            if (s_placeholderBodies is not { Count: > 0 } bodies)
            {
                return false;
            }

            foreach (var body in bodies)
            {
                // The body of top-level statements (step P7) holds only the global statements of its compilation unit.
                if (body.SyntaxTree == parameter.SyntaxTree &&
                    body.Span.Contains(parameter.Span) &&
                    parameter.Parent != (body as BaseMethodDeclarationSyntax)?.ParameterList &&
                    (body is not CompilationUnitSyntax || parameter.FirstAncestorOrSelf<GlobalStatementSyntax>() is not null))
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
        /// candidate's own type parameters (step P4); or, for a call with several candidates, a <c>params</c> parameter or
        /// a type parameter of the candidate, the type that every candidate applicable with the placeholder in place takes
        /// there, as overload resolution recorded them (step P5). Step P6 adds the other uses that pin one type: the
        /// parameter as a <c>ref</c>, <c>out</c> or <c>in</c> argument to a parameter of that ref kind (its exact type),
        /// assigned to (or initializing) a local, parameter, field or property whose type is known, and returned from a
        /// method, local function or lambda whose return type is written. Member access, operators, <c>is</c> tests and
        /// interpolations pin nothing.
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
            internal readonly (SyntaxNode Syntax, IFormattable Reason, ImmutableArray<TypeSymbol> Types)?[] FirstNotPinning;

            /// <summary>The lambda, local function or placeholder whose body is being walked (the owner of its binding).</summary>
            private Symbol? _owner;

            internal UsesCollector(ImmutableArray<ParameterSymbol> targets, int parameterCount)
            {
                _targets = targets;
                _owner = targets.IsEmpty ? null : targets[0].ContainingSymbol;
                Uses = new ArrayBuilder<PinningUse>?[parameterCount];
                FirstNotPinning = new (SyntaxNode, IFormattable, ImmutableArray<TypeSymbol>)?[parameterCount];
            }

            public override BoundNode? VisitLambda(BoundLambda node)
            {
                Symbol? owner = _owner;
                _owner = node.Symbol;
                base.VisitLambda(node);
                _owner = owner;
                return null;
            }

            public override BoundNode? VisitLocalFunctionStatement(BoundLocalFunctionStatement node)
            {
                Symbol? owner = _owner;
                _owner = node.Symbol;
                base.VisitLocalFunctionStatement(node);
                _owner = owner;
                return null;
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
                    AddUsesFromApplicableCandidates(node.Arguments, node.ArgumentRefKindsOpt, someCandidate.Name, skipReceiver: node.InvokedAsExtensionMethod);
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

                    if (node.Symbols.Length > 1)
                    {
                        names.Free();
                        AddUsesFromApplicableCandidates(arguments, refKinds.ToImmutableAndFree(), constructor.ContainingType.Name);
                        return base.VisitBadExpression(node);
                    }

                    AddUses(arguments, names.ToImmutableAndFree(), refKinds.ToImmutableAndFree(), constructor, constructor.Parameters);
                }

                return base.VisitBadExpression(node);
            }

            /// <summary>
            /// <c>y = x</c>, <c>r = ref x</c> and <c>new C { P = x }</c> (step P6): the parameter assigned to a local, a
            /// parameter, a field or a property (an indexer too) pins the type of the target.
            /// </summary>
            public override BoundNode? VisitAssignmentOperator(BoundAssignmentOperator node)
            {
                if (GetConvertedTarget(node.Right) is { } value)
                {
                    Symbol? assigned = node.Left switch
                    {
                        BoundLocal { LocalSymbol: var local } => local,
                        BoundParameter { ParameterSymbol: var parameter } => parameter,
                        BoundFieldAccess { FieldSymbol: var field } => field,
                        BoundPropertyAccess { PropertySymbol: var property } => property,
                        BoundIndexerAccess { Indexer: var indexer } => indexer,
                        BoundObjectInitializerMember { MemberSymbol: (FieldSymbol or PropertySymbol) and var member } => member,
                        _ => null,
                    };

                    if (assigned is not null)
                    {
                        AddUseFromTarget(value, node.Right.Syntax, assigned, assigned.GetTypeOrReturnType());
                    }
                }

                return base.VisitAssignmentOperator(node);
            }

            /// <summary>
            /// <c>int y = x;</c> (step P6): the parameter initializing a local whose type is written pins that type; a
            /// <c>var</c> local takes the placeholder's type and pins nothing.
            /// </summary>
            public override BoundNode? VisitLocalDeclaration(BoundLocalDeclaration node)
            {
                if (node.InitializerOpt is { } initializer && GetConvertedTarget(initializer) is { } value)
                {
                    AddUseFromTarget(value, initializer.Syntax, node.LocalSymbol, node.LocalSymbol.TypeWithAnnotations);
                }

                return base.VisitLocalDeclaration(node);
            }

            /// <summary>
            /// <c>return x;</c> (step P6): the parameter returned from the method, a local function or a lambda whose return
            /// type is written pins that type (the result type of an <c>async</c> one). A return type inferred from the
            /// body (<c>infer</c>, a lambda without a written return type) pins nothing.
            /// </summary>
            public override BoundNode? VisitReturnStatement(BoundReturnStatement node)
            {
                if (node.ExpressionOpt is { } expression &&
                    GetConvertedTarget(expression) is { } value &&
                    _owner is MethodSymbol owner &&
                    GetWrittenReturnType(owner) is { HasType: true } returnType)
                {
                    IFormattable description = owner switch
                    {
                        LambdaSymbol => MessageID.IDS_SharperInferReturnOfLambda.Localize(),
                        SharperPlaceholderMethodSymbol placeholder => new MessageFragment(MessageID.IDS_SharperInferReturnOf, [placeholder.Original.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "." + GetMemberName(((BaseMethodDeclarationSyntax)placeholder.Original.SyntaxNode).ParameterList)]),
                        _ => new MessageFragment(MessageID.IDS_SharperInferReturnOf, [owner.Name]),
                    };

                    AddUseFromTarget(value, expression.Syntax, description, returnType);
                }

                return base.VisitReturnStatement(node);
            }

            /// <summary>
            /// The return type that a <c>return x</c> in <paramref name="owner"/> converts to, when it is written and does
            /// not depend on the body: none for <c>void</c>, an iterator, an <c>async</c> method returning <c>Task</c>, an
            /// <c>infer</c> return type or a lambda without a written return type.
            /// </summary>
            private static TypeWithAnnotations GetWrittenReturnType(MethodSymbol owner)
            {
                bool written = owner switch
                {
                    SharperPlaceholderMethodSymbol placeholder => !placeholder.IsReturnTypeInferred,
                    LambdaSymbol or LocalFunctionSymbol => owner.GetNonNullSyntaxNode() switch
                    {
                        ParenthesizedLambdaExpressionSyntax { ReturnType: { IsInfer: false } } => true,
                        LocalFunctionStatementSyntax { ReturnType.IsInfer: false } => true,
                        _ => false,
                    },
                    _ => false,
                };

                if (!written || owner.IsIterator)
                {
                    return default;
                }

                TypeWithAnnotations returnType = owner.ReturnTypeWithAnnotations;
                if (owner.IsAsync)
                {
                    if (returnType.Type is not NamedTypeSymbol { Arity: 1 } taskType || !returnType.Type.IsGenericTaskType(owner.DeclaringCompilation))
                    {
                        return default;
                    }

                    returnType = taskType.TypeArgumentsWithAnnotationsNoUseSiteDiagnostics[0];
                }

                return returnType.Type is null || returnType.IsVoidType() ? default : returnType;
            }

            /// <summary>A use that pins the type of what the parameter is assigned or returned to, if that type is known.</summary>
            private void AddUseFromTarget(BoundParameter value, SyntaxNode syntax, object description, TypeWithAnnotations type)
            {
                if (!type.HasType || type.Type.IsErrorType() || (object)type.Type == LambdaSymbol.ReturnTypeIsBeingInferred || InvolvesTypeParametersNotInScope(type.Type))
                {
                    return;
                }

                var uses = Uses[value.ParameterSymbol.Ordinal] ??= ArrayBuilder<PinningUse>.GetInstance();
                uses.Add(new PinningUse(syntax, description, type));
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

            private void NoteNotPinning(ParameterSymbol target, SyntaxNode syntax, IFormattable reason, ImmutableArray<TypeSymbol> types = default)
                => FirstNotPinning[target.Ordinal] ??= (syntax, reason, types);

            /// <summary>
            /// The uses in a call with several candidates (step P5): each argument that is a parameter being inferred pins
            /// the type that every candidate applicable with the placeholder in place takes there
            /// (<see cref="SharperPlaceholderMethodSymbol.GetCandidates"/>).
            /// </summary>
            private void AddUsesFromApplicableCandidates(ImmutableArray<BoundExpression> arguments, ImmutableArray<RefKind> argumentRefKinds, string name, bool skipReceiver = false)
            {
                for (int i = skipReceiver ? 1 : 0; i < arguments.Length; i++)
                {
                    if (GetTargetArgument(arguments[i]) is not { } argument)
                    {
                        continue;
                    }

                    ParameterSymbol target = argument.ParameterSymbol;
                    bool byReference = !argumentRefKinds.IsDefault && argumentRefKinds[i] != RefKind.None;
                    if (Placeholder.GetCandidates(argument.Syntax, _owner) is { IsDefault: false } candidates &&
                        !(byReference && candidates.IsEmpty))
                    {
                        // A ref, out or in argument counts the candidates whose parameter takes its ref kind (P6).
                        AddUseFromCandidates(target, arguments[i].Syntax, candidates, name);
                    }
                    else if (byReference)
                    {
                        NoteNotPinning(target, argument.Syntax, MessageID.IDS_SharperInferRefArgumentPinsNothing.Localize());
                    }
                    else
                    {
                        NoteNotPinning(target, argument.Syntax, new MessageFragment(MessageID.IDS_SharperInferSeveralCandidatesPinNothing, [name]));
                    }
                }
            }

            /// <summary>
            /// Pins the type that every candidate in <paramref name="candidates"/> takes for the argument, when they agree
            /// and none takes a type parameter that the other arguments leave open. Otherwise notes why nothing is pinned:
            /// P4's reasons when the candidates are the forms of one method (a type parameter, or the two forms of
            /// <c>params</c>), or else the list of candidates and their types.
            /// </summary>
            private void AddUseFromCandidates(ParameterSymbol target, SyntaxNode syntax, ImmutableArray<SharperPlaceholderCandidate> candidates, string name)
            {
                if (candidates.IsEmpty)
                {
                    NoteNotPinning(target, syntax, new MessageFragment(MessageID.IDS_SharperInferNoApplicableCandidate, [name]));
                    return;
                }

                TypeWithAnnotations type = candidates[0].Type;
                bool agree = true;
                bool typeParameter = false;
                foreach (var candidate in candidates)
                {
                    if (InvolvesTypeParametersNotInScope(candidate.Type.Type))
                    {
                        agree = false;
                        typeParameter = true;
                        break;
                    }

                    if (candidate.Type.Type.IsErrorType() ||
                        !type.Type.Equals(candidate.Type.Type, TypeCompareKind.AllNullableIgnoreOptions | TypeCompareKind.IgnoreTupleNames))
                    {
                        agree = false;
                    }
                    else if (agree)
                    {
                        type = type.MergeEquivalentTypes(candidate.Type, VarianceKind.In);
                    }
                }

                if (agree)
                {
                    var uses = Uses[target.Ordinal] ??= ArrayBuilder<PinningUse>.GetInstance();
                    uses.Add(new PinningUse(syntax, candidates[0].Candidate, type));
                }
                else if (isOneMethod(candidates))
                {
                    NoteNotPinning(
                        target,
                        syntax,
                        typeParameter ? new MessageFragment(MessageID.IDS_SharperInferTypeParameterPinsNothing, [name]) : MessageID.IDS_SharperInferParamsPinsNothing.Localize());
                }
                else
                {
                    var list = ArrayBuilder<MessageFragment>.GetInstance(candidates.Length);
                    var types = ArrayBuilder<TypeSymbol>.GetInstance(candidates.Length);
                    var seen = PooledHashSet<string>.GetInstance();
                    foreach (var candidate in candidates)
                    {
                        string member = candidate.Candidate.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        string candidateType = candidate.Type.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        if (seen.Add(member + "|" + candidateType + "|" + candidate.IsExpanded))
                        {
                            list.Add(new MessageFragment(candidate.IsExpanded ? MessageID.IDS_SharperInferCandidateTakesExpanded : MessageID.IDS_SharperInferCandidateTakes, [member, candidateType]));
                            types.Add(candidate.Type.Type);
                        }
                    }

                    seen.Free();
                    NoteNotPinning(target, syntax, new MessageFragment(MessageID.IDS_SharperInferCandidatesDisagree, [name, new MessageFragmentList(list.ToImmutableAndFree())]), types.ToImmutableAndFree());
                }

                static bool isOneMethod(ImmutableArray<SharperPlaceholderCandidate> candidates)
                {
                    foreach (var candidate in candidates)
                    {
                        if (!candidate.Candidate.OriginalDefinition.Equals(candidates[0].Candidate.OriginalDefinition))
                        {
                            return false;
                        }
                    }

                    return true;
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
                    if (GetTargetArgument(arguments[i]) is not { } argument)
                    {
                        continue;
                    }

                    ParameterSymbol target = argument.ParameterSymbol;
                    SyntaxNode syntax = arguments[i].Syntax;
                    ParameterSymbol? parameter = GetParameter(i, argumentNames, parameters);
                    if (parameter is null && (argumentNames.IsDefault || argumentNames[i] is null) && parameters is [.., { IsParams: true } last])
                    {
                        // An argument of the expanded form.
                        parameter = last;
                    }

                    RefKind argumentRefKind = argumentRefKinds.IsDefault ? RefKind.None : argumentRefKinds[i];
                    if (parameter is null)
                    {
                        NoteNotPinning(target, syntax, (argumentRefKind == RefKind.None ? MessageID.IDS_SharperInferUsePinsNothing : MessageID.IDS_SharperInferRefArgumentPinsNothing).Localize());
                    }
                    else if (argumentRefKind != RefKind.None && !acceptsRefKind(parameter, argumentRefKind))
                    {
                        // A ref, out or in argument pins the exact type of a parameter that takes its ref kind (P6).
                        NoteNotPinning(target, syntax, MessageID.IDS_SharperInferRefArgumentPinsNothing.Localize());
                    }
                    else if (parameter.IsParams)
                    {
                        // The normal and the expanded form are two candidates (P5).
                        if (Placeholder.GetCandidates(argument.Syntax, _owner) is { IsDefault: false } candidates)
                        {
                            AddUseFromCandidates(target, syntax, candidates, candidate.Name);
                        }
                        else
                        {
                            NoteNotPinning(target, syntax, MessageID.IDS_SharperInferParamsPinsNothing.Localize());
                        }
                    }
                    else if (argumentRefKind == RefKind.None && parameter.RefKind is RefKind.Ref or RefKind.Out)
                    {
                        NoteNotPinning(target, syntax, MessageID.IDS_SharperInferRefArgumentPinsNothing.Localize());
                    }
                    else if (InvolvesTypeParametersNotInScope(parameter.Type))
                    {
                        // The type arguments that type inference fixes from the other arguments (P5).
                        if (Placeholder.GetCandidates(argument.Syntax, _owner) is { IsDefault: false } candidates)
                        {
                            AddUseFromCandidates(target, syntax, candidates, candidate.Name);
                        }
                        else
                        {
                            NoteNotPinning(target, syntax, new MessageFragment(MessageID.IDS_SharperInferTypeParameterPinsNothing, [candidate.Name]));
                        }
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

                // The ref kinds that overload resolution accepts for an argument (OverloadResolution_Sharper.cs).
                static bool acceptsRefKind(ParameterSymbol parameter, RefKind argumentRefKind)
                    => argumentRefKind == parameter.RefKind ||
                        (parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter && argumentRefKind is RefKind.In or RefKind.Ref);
            }

            /// <summary>The placeholder parameter that the argument is, directly; the binder may wrap it for error recovery.</summary>
            private ParameterSymbol? GetTarget(BoundExpression argument)
                => GetTargetArgument(argument)?.ParameterSymbol;

            /// <summary>The argument as overload resolution saw it, if it is a placeholder parameter.</summary>
            private BoundParameter? GetTargetArgument(BoundExpression argument)
            {
                if (argument is BoundConversion { WasCompilerGenerated: true, ExplicitCastInCode: false } conversion)
                {
                    argument = conversion.Operand;
                }

                return argument is BoundParameter { ParameterSymbol: var parameter } boundParameter && _targets.Contains(parameter) ? boundParameter : null;
            }

            /// <summary>
            /// The placeholder parameter that a value assigned or returned is, directly: the binder converts it to the type
            /// of the target, implicitly, which is no expression around it (P6). A cast is.
            /// </summary>
            private BoundParameter? GetConvertedTarget(BoundExpression value)
            {
                while (value is BoundConversion { ExplicitCastInCode: false } conversion && conversion.Syntax == conversion.Operand.Syntax)
                {
                    value = conversion.Operand;
                }

                return GetTargetArgument(value);
            }

            private SharperPlaceholderMethodSymbol Placeholder => (SharperPlaceholderMethodSymbol)_targets[0].ContainingSymbol;

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
                return type.VisitType(
                    static (t, placeholder, _) => t is TypeParameterSymbol { ContainingSymbol: MethodSymbol typeParameterOwner } &&
                        (object)typeParameterOwner != placeholder.Original,
                    Placeholder) is not null;
            }
        }
    }
}
