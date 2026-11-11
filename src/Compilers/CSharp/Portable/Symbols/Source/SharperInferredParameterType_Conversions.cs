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
        /// One local function (step P7) or private method (step P7b) whose <c>infer</c> parameters are being inferred from
        /// the delegate types it is converted to, and the conversions recorded while the bodies that can convert it are
        /// bound with a placeholder for it.
        /// </summary>
        private sealed class ConversionFrame
        {
            /// <summary>The <see cref="LocalFunctionStatementSyntax"/> or the <see cref="MethodDeclarationSyntax"/>.</summary>
            internal readonly CSharpSyntaxNode Declaration;

            internal readonly ParameterListSyntax ParameterList;

            /// <summary>Step P7: the body that declares the local function, bound with the placeholder.</summary>
            internal readonly CSharpSyntaxNode? Body;

            /// <summary>
            /// Step P7b: the stand-in for the private method, which replaces it in the bindings of this frame (its
            /// parameters being inferred have an error type).
            /// </summary>
            internal readonly SharperPlaceholderMethodSymbol? StandIn;

            /// <summary>
            /// Step P7b: the binder that the body being bound starts from. A binding whose binder chain does not reach it was
            /// started by another inference (an inferred return type, a body inference) and sees the real method.
            /// </summary>
            internal Binder? Root;

            /// <summary>Step P7b: the stand-ins of the other private methods that may infer from conversions, made on first use.</summary>
            private Dictionary<MethodSymbol, SharperPlaceholderMethodSymbol>? _otherStandIns;

            /// <summary>
            /// Step P7b: the stand-in for <paramref name="method"/> in this frame's bindings: <see cref="StandIn"/> for the
            /// method being inferred, which records its conversions, and one that records nothing for any other.
            /// </summary>
            internal SharperPlaceholderMethodSymbol GetStandIn(SourceOrdinaryMethodSymbol method)
            {
                Debug.Assert(StandIn is not null);
                if ((object)StandIn.Original == method)
                {
                    return StandIn;
                }

                _otherStandIns ??= new Dictionary<MethodSymbol, SharperPlaceholderMethodSymbol>();
                if (!_otherStandIns.TryGetValue(method, out var standIn))
                {
                    var declaration = (MethodDeclarationSyntax)method.SyntaxNode;
                    standIn = CreatePlaceholder(method, declaration, declaration.ParameterList, useConversionResults: false, out _);
                    _otherStandIns.Add(method, standIn);
                }

                return standIn;
            }

            /// <summary>
            /// Per method group syntax, the conversions of the local function there: the delegate type and the delegate's
            /// parameter types.
            /// </summary>
            internal readonly Dictionary<SyntaxNode, ArrayBuilder<(TypeSymbol? DelegateType, ImmutableArray<TypeWithAnnotations> Types)>> Conversions = new();

            internal ConversionFrame(LocalFunctionStatementSyntax declaration, CSharpSyntaxNode body)
            {
                Declaration = declaration;
                ParameterList = declaration.ParameterList;
                Body = body;
            }

            internal ConversionFrame(MethodDeclarationSyntax declaration, SharperPlaceholderMethodSymbol standIn)
            {
                Declaration = declaration;
                ParameterList = declaration.ParameterList;
                StandIn = standIn;
            }

            /// <summary>True if <paramref name="binder"/> is in the binding of a body that this frame (step P7b) is binding.</summary>
            internal bool IsInBinding(Binder binder)
            {
                if (Root is null)
                {
                    return false;
                }

                for (Binder? current = binder; current is not null; current = current.Next)
                {
                    if (current == Root)
                    {
                        return true;
                    }
                }

                return false;
            }

            internal void Free()
            {
                foreach (var (_, conversions) in Conversions)
                {
                    conversions.Free();
                }

                Conversions.Clear();
                if (_otherStandIns is not null)
                {
                    foreach (var (_, standIn) in _otherStandIns)
                    {
                        standIn.FreeCandidates();
                    }

                    _otherStandIns = null;
                }
            }
        }

        /// <summary>The local functions and private methods whose parameter types are being inferred from conversions on this thread, innermost last.</summary>
        [ThreadStatic] private static List<ConversionFrame>? s_conversionFrames;

        /// <summary>
        /// Sharper steps P7 and P7b: called by overload resolution of a method group that is converted to a delegate type,
        /// with the delegate's parameters as the arguments. When the group holds a local function whose <c>infer</c>
        /// parameters are being inferred from its conversions on this thread, or the stand-in of such a private method
        /// (<see cref="ReplaceByConversionStandIns"/>), records the delegate's parameter types for them.
        /// </summary>
        /// <remarks>
        /// The local function's parameters being inferred have an error type in that binding, so the conversion fails;
        /// the delegate's parameter types are what a lambda in its place would take. A conversion counts when it fits the
        /// rest of the local function's signature: the parameter count, the ref kinds as written, and the written
        /// parameter types, which take the delegate's types by identity or implicit reference conversion. Method type
        /// inference resolves the method group in its second phase, once the delegate's input types are fixed, so
        /// <c>xs.Select(Format)</c> records <c>int</c> from <c>Func&lt;int, TResult&gt;</c>. A delegate parameter type that
        /// involves a type parameter out of the local function's scope (a type parameter of a generic call whose
        /// inference failed) or an error type records nothing.
        /// </remarks>
        internal static void RecordMethodGroupConversion(Binder binder, ArrayBuilder<MethodSymbol> methods, AnalyzedArguments arguments)
        {
            if (s_conversionFrames is not { Count: > 0 } frames || arguments.Arguments.Count == 0)
            {
                return;
            }

            foreach (MethodSymbol method in methods)
            {
                ConversionFrame? frame = null;
                MethodSymbol converted = method.OriginalDefinition;
                if (converted is LocalFunctionSymbol localFunction)
                {
                    LocalFunctionStatementSyntax declaration = localFunction.Syntax;
                    foreach (var candidate in frames)
                    {
                        if (candidate.Declaration == declaration)
                        {
                            frame = candidate;
                            break;
                        }
                    }
                }
                else if (converted is SharperPlaceholderMethodSymbol standIn)
                {
                    foreach (var candidate in frames)
                    {
                        if ((object?)candidate.StandIn == standIn)
                        {
                            frame = candidate;
                            break;
                        }
                    }
                }

                if (frame is null || TryGetConvertedTypes(binder, converted, frame.ParameterList, arguments) is not { } types)
                {
                    continue;
                }

                SyntaxNode syntax = arguments.Arguments[0].Syntax;
                TypeSymbol? delegateType = null;
                foreach (var argument in arguments.Arguments)
                {
                    if (argument is BoundParameter { ParameterSymbol.ContainingSymbol.ContainingType: { } containingType })
                    {
                        delegateType = containingType;
                        break;
                    }
                }

                if (!frame.Conversions.TryGetValue(syntax, out var conversions))
                {
                    conversions = ArrayBuilder<(TypeSymbol?, ImmutableArray<TypeWithAnnotations>)>.GetInstance();
                    frame.Conversions.Add(syntax, conversions);
                }

                conversions.Add((delegateType, types));
            }
        }

        /// <summary>
        /// The delegate's parameter types for a conversion of <paramref name="method"/> (a local function or the stand-in of
        /// a private method) that fits the rest of its signature (default types where the parameter is not being
        /// inferred), or null.
        /// </summary>
        private static ImmutableArray<TypeWithAnnotations>? TryGetConvertedTypes(Binder binder, MethodSymbol method, ParameterListSyntax parameterList, AnalyzedArguments arguments)
        {
            SeparatedSyntaxList<ParameterSyntax> parameterSyntaxes = parameterList.Parameters;
            ImmutableArray<ParameterSymbol> parameters = method.Parameters;
            if (parameters.Length != arguments.Arguments.Count || parameterSyntaxes.Count != parameters.Length)
            {
                return null;
            }

            var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            var types = ArrayBuilder<TypeWithAnnotations>.GetInstance(parameters.Length);
            bool fits = true;
            bool hasInferred = false;
            for (int i = 0; i < parameters.Length && fits; i++)
            {
                if (arguments.Arguments[i] is not BoundParameter { ParameterSymbol: var delegateParameter } ||
                    delegateParameter.RefKind != parameters[i].RefKind)
                {
                    fits = false;
                    break;
                }

                TypeWithAnnotations delegateParameterType = delegateParameter.TypeWithAnnotations;
                if (IsInferredFromConversions(parameterSyntaxes[i]))
                {
                    if (delegateParameterType.Type.IsErrorType() ||
                        delegateParameterType.Type.ContainsErrorType() ||
                        delegateParameterType.Type.VisitType(static (t, method, _) => t is TypeParameterSymbol typeParameter && !IsInScope(typeParameter, method), method) is not null)
                    {
                        fits = false;
                        break;
                    }

                    types.Add(delegateParameterType);
                    hasInferred = true;
                }
                else
                {
                    TypeSymbol parameterType = parameters[i].Type;
                    if (!parameterType.IsErrorType() &&
                        !binder.Conversions.HasIdentityOrImplicitReferenceConversion(delegateParameterType.Type, parameterType, ref useSiteInfo))
                    {
                        fits = false;
                        break;
                    }

                    types.Add(default);
                }
            }

            if (!fits || !hasInferred)
            {
                types.Free();
                return null;
            }

            return types.ToImmutableAndFree();

            static bool IsInScope(TypeParameterSymbol typeParameter, Symbol method)
            {
                // A stand-in has the type parameters of the method it stands for.
                for (Symbol? container = (method as SharperPlaceholderMethodSymbol)?.Original ?? method; container is not null; container = container.ContainingSymbol)
                {
                    if (ReferenceEquals(container, typeParameter.ContainingSymbol))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static bool IsInferredFromConversions(ParameterSyntax parameter)
            => parameter is { Type.IsInfer: true, Default: null };

        /// <summary>
        /// The result for an <c>infer</c> parameter without a default value of a local function, from the delegate types
        /// the local function is converted to (step P7), or of a private method (step P7b, null when no conversion gives
        /// its type); null when the parameter is neither's.
        /// </summary>
        /// <remarks>
        /// While the body that declares the local function is bound with a placeholder for it, the placeholder's parameter
        /// gets an error type (the placeholder converts to nothing), with no diagnostics.
        /// </remarks>
        private static Result? GetResultFromConversions(Symbol owner, ParameterSyntax parameter)
        {
            if (owner is SourceOrdinaryMethodSymbol { SyntaxNode: MethodDeclarationSyntax { ParameterList: var methodParameterList } } &&
                parameter.Parent == methodParameterList)
            {
                // Step P7b: null when no conversion gives the type, which then comes from the body.
                return GetMemberConversionInference(owner).GetResult(methodParameterList.Parameters.IndexOf(parameter));
            }

            if (owner is not LocalFunctionSymbol localFunction ||
                localFunction.Syntax is not { ParameterList: var parameterList } declaration ||
                parameter.Parent != parameterList)
            {
                return null;
            }

            CSharpCompilation compilation = owner.DeclaringCompilation;
            if (s_conversionFrames is { } frames)
            {
                foreach (var frame in frames)
                {
                    if (frame.Declaration == declaration)
                    {
                        return new Result(CreateErrorType(compilation), ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty);
                    }
                }
            }

            return GetConversionInference(localFunction, declaration)?.GetResult(parameterList.Parameters.IndexOf(parameter));
        }

        /// <summary>
        /// The types that the delegate types <paramref name="localFunction"/> is converted to give its <c>infer</c>
        /// parameters without a default value, kept by the compilation per parameter list (with the body inferences of
        /// members, P4, since a parameter list is one or the other) and published once; null when the body that declares
        /// it cannot be bound for it.
        /// </summary>
        private static BodyInference? GetConversionInference(LocalFunctionSymbol localFunction, LocalFunctionStatementSyntax declaration)
        {
            // The member whose body declares the local function, through other local functions and lambdas.
            Symbol? member = localFunction.ContainingSymbol;
            while (member is LocalFunctionSymbol or LambdaSymbol)
            {
                member = member.ContainingSymbol;
            }

            // A body bound with placeholders for its member's own parameters (P4) does not infer its local functions: their
            // results there would depend on the placeholders, and that binding reports nothing.
            (ExecutableCodeBinder? bodyBinder, CSharpSyntaxNode? body) = member switch
            {
                SharperPlaceholderMethodSymbol => (null, null),
                SynthesizedSimpleProgramEntryPointSymbol entryPoint => (entryPoint.CreateBodyBinder(ignoreAccessibility: false), entryPoint.SyntaxNode),
                SourceMemberMethodSymbol method => (method.TryGetBodyBinder(), method.SyntaxNode),
                _ => (null, null),
            };

            if (bodyBinder is null || body is null || body.SyntaxTree != declaration.SyntaxTree || !body.Span.Contains(declaration.Span))
            {
                return null;
            }

            CSharpCompilation compilation = localFunction.DeclaringCompilation;
            ParameterListSyntax parameterList = declaration.ParameterList;
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

            var frames = s_conversionFrames ??= new List<ConversionFrame>();

            // In the placeholder binding of another local function of the same body, this one is a placeholder too:
            // inferring it there would depend on the other placeholder, and bind the body once more per local function.
            foreach (var other in frames)
            {
                if (other.Body == body)
                {
                    return CreatePlaceholderInference(parameterList, compilation);
                }
            }

            var frame = new ConversionFrame(declaration, body);
            inProgress.Add(parameterList);
            frames.Add(frame);
            var placeholderBodies = s_placeholderBodies ??= new List<CSharpSyntaxNode>();
            placeholderBodies.Add(body);
            object? suspendedReturnTypeInference = SharperInferredReturnType.SuspendInference();
            try
            {
                var bodyDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: true);
                BoundNode? boundBody = bodyBinder.BindMethodBody(body, bodyDiagnostics);
                var dependencies = BindingDiagnosticBag.GetInstance(withDiagnostics: false, withDependencies: true);
                dependencies.AddDependencies(bodyDiagnostics);
                bodyDiagnostics.Free();
                result = DecideFromConversions(localFunction, declaration, frame, boundBody, dependencies, compilation);
                dependencies.Free();
            }
            finally
            {
                SharperInferredReturnType.ResumeInference(suspendedReturnTypeInference);
                Debug.Assert(placeholderBodies[placeholderBodies.Count - 1] == body);
                placeholderBodies.RemoveAt(placeholderBodies.Count - 1);
                Debug.Assert(frames[frames.Count - 1] == frame);
                frames.RemoveAt(frames.Count - 1);
                frame.Free();
                Debug.Assert(inProgress[inProgress.Count - 1] == parameterList);
                inProgress.RemoveAt(inProgress.Count - 1);
            }

            // A local function found to be in a cycle while the body was bound already has its result.
            return Publish(results, compilation, parameterList, result);
        }

        /// <summary>An error type, with no diagnostics, for each <c>infer</c> parameter without a default value.</summary>
        private static BodyInference CreatePlaceholderInference(ParameterListSyntax parameterList, CSharpCompilation compilation)
        {
            var results = ArrayBuilder<Result?>.GetInstance(parameterList.Parameters.Count);
            foreach (var parameter in parameterList.Parameters)
            {
                results.Add(IsInferredFromConversions(parameter) ? new Result(CreateErrorType(compilation), ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty) : null);
            }

            return new BodyInference(results.ToImmutableAndFree());
        }

        /// <summary>
        /// The type of each <c>infer</c> parameter without a default value from the conversions recorded at the method
        /// groups that the final bound tree holds, with the binding for error recovery of a lambda that stays unbound. Every conversion must agree (CS20057, which names two delegate types); none is CS20027
        /// with "nothing else gives its type", as before step P7.
        /// </summary>
        private static BodyInference DecideFromConversions(
            LocalFunctionSymbol localFunction,
            LocalFunctionStatementSyntax declaration,
            ConversionFrame frame,
            BoundNode? boundBody,
            BindingDiagnosticBag dependencies,
            CSharpCompilation compilation)
        {
            SeparatedSyntaxList<ParameterSyntax> parameters = declaration.ParameterList.Parameters;
            var walker = new ConversionSitesWalker(frame);
            if (boundBody is not null && frame.Conversions.Count > 0)
            {
                walker.Visit(boundBody);
            }

            var uses = CollectConversionUses(frame, walker.Sites, localFunction.Name);
            var results = ArrayBuilder<Result?>.GetInstance(parameters.Count);
            for (int i = 0; i < parameters.Count; i++)
            {
                results.Add(IsInferredFromConversions(parameters[i])
                    ? DecideFromUses(parameters[i], uses[i], firstNotPinning: null, dependencies, compilation, MessageID.IDS_SharperInferNothingElseGivesType.Localize(), VarianceKind.Out)
                    : null);
                uses[i]?.Free();
            }

            return new BodyInference(results.ToImmutableAndFree());
        }

        /// <summary>
        /// Per parameter, the uses that the conversions recorded at <paramref name="sites"/> give it: one per method group
        /// and delegate parameter type, described by the delegate type (or <paramref name="name"/>).
        /// </summary>
        private static ArrayBuilder<PinningUse>?[] CollectConversionUses(ConversionFrame frame, List<SyntaxNode> sites, string name)
        {
            var uses = new ArrayBuilder<PinningUse>?[frame.ParameterList.Parameters.Count];
            foreach (var syntax in sites)
            {
                foreach (var (delegateType, types) in frame.Conversions[syntax])
                {
                    object description = new MessageFragment(
                        MessageID.IDS_SharperInferConversionTo,
                        [delegateType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? name]);
                    for (int i = 0; i < types.Length; i++)
                    {
                        if (!types[i].HasType)
                        {
                            continue;
                        }

                        var builder = uses[i] ??= ArrayBuilder<PinningUse>.GetInstance();
                        bool seen = false;
                        foreach (var use in builder)
                        {
                            if (use.Argument == syntax && use.Type.Equals(types[i], TypeCompareKind.ConsiderEverything))
                            {
                                seen = true;
                                break;
                            }
                        }

                        if (!seen)
                        {
                            builder.Add(new PinningUse(syntax, description, types[i]));
                        }
                    }
                }
            }

            return uses;
        }

        /// <summary>Finds the method group syntax nodes with recorded conversions that the final bound tree holds.</summary>
        private sealed class ConversionSitesWalker : BoundTreeWalkerWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
        {
            private readonly ConversionFrame _frame;

            internal readonly List<SyntaxNode> Sites = new();

            internal ConversionSitesWalker(ConversionFrame frame)
            {
                _frame = frame;
            }

            public override BoundNode? Visit(BoundNode? node)
            {
                if (node is not null && _frame.Conversions.ContainsKey(node.Syntax) && !Sites.Contains(node.Syntax))
                {
                    Sites.Add(node.Syntax);
                }

                return base.Visit(node);
            }

            /// <summary>
            /// A lambda whose conversion failed stays unbound in the tree, as most do in this binding: the placeholder
            /// converts to nothing. Its body is the binding for error recovery.
            /// </summary>
            public override BoundNode? VisitUnboundLambda(UnboundLambda node)
            {
                Visit(node.BindForErrorRecovery());
                return null;
            }
        }
    }
}
