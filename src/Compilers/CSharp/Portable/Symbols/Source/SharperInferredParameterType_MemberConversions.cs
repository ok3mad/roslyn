// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal static partial class SharperInferredParameterType
    {
        /// <summary>
        /// A body in the accessibility domain of a type's private members: the body of a method, constructor, destructor,
        /// operator, conversion or accessor, or a field or property initializer, of the type or of a type nested in it.
        /// </summary>
        private readonly struct MemberBody
        {
            /// <summary>The method or accessor, or the field that an initializer initializes.</summary>
            internal readonly Symbol Owner;

            /// <summary>The method's syntax, or the initializer's <see cref="EqualsValueClauseSyntax"/>.</summary>
            internal readonly CSharpSyntaxNode Syntax;

            internal MemberBody(Symbol owner, CSharpSyntaxNode syntax)
            {
                Owner = owner;
                Syntax = syntax;
            }
        }

        /// <summary>
        /// Per type, the bodies that name each of its private methods with a parameter inferred from conversions without
        /// invoking it: only such a body can convert the method to a delegate type. Built by one syntax walk per type.
        /// </summary>
        private static readonly ConditionalWeakTable<NamedTypeSymbol, Dictionary<string, ImmutableArray<MemberBody>>> s_bodiesNamingMethods = new();

        /// <summary>
        /// The types that the delegate types a private method <paramref name="owner"/> is converted to give its
        /// <c>infer</c> parameters without a default value (step P7b): <c>static infer Format(infer x)</c> gets
        /// <c>int</c> from <c>xs.Select(Format)</c> in any body of its type. A parameter that no conversion gives has no
        /// result, and the body gives its type (P4 to P6).
        /// </summary>
        /// <remarks>
        /// Only the bodies that name the method without invoking it are bound, once more each, with a stand-in for the
        /// method (<see cref="ReplaceByConversionStandIns"/>), and the conversions recorded at the method groups that their
        /// final bound trees hold count, as for a local function (step P7). The inference runs before the signature lock,
        /// on the stack of the other sources (keyed by the method's return type syntax), and is kept by the compilation
        /// per parameter list and published once.
        /// </remarks>
        internal static BodyInference GetMemberConversionInference(Symbol owner)
        {
            if (owner is not SourceOrdinaryMethodSymbol { SyntaxNode: MethodDeclarationSyntax { ParameterList: var parameterList } declaration, ContainingType: { } containingType } method ||
                !IsConversionCandidate(method))
            {
                return BodyInference.NotApplicable;
            }

            CSharpCompilation compilation = owner.DeclaringCompilation;
            var results = compilation.SharperConversionInferences;
            if (results.TryGetValue(parameterList, out BodyInference? result))
            {
                return result;
            }

            ImmutableArray<MemberBody> bodies = GetBodiesNaming(containingType, method.Name);
            if (bodies.IsEmpty || GetOverriddenSignature(owner).IsApplicable)
            {
                return BodyInference.NotApplicable;
            }

            SyntaxNode key = declaration.ReturnType;
            var inProgress = s_inProgress ??= new List<SyntaxNode>();
            int cycleStart = inProgress.IndexOf(key);
            if (cycleStart >= 0)
            {
                PublishCycle(compilation, inProgress, cycleStart);
                return results.GetValue(parameterList, _ => throw ExceptionUtilities.Unreachable());
            }

            inProgress.Add(key);
            try
            {
                result = InferFromMemberConversions(method, declaration, bodies, compilation);
            }
            finally
            {
                Debug.Assert(inProgress[inProgress.Count - 1] == key);
                inProgress.RemoveAt(inProgress.Count - 1);
            }

            // A method found to be in a cycle while the bodies were bound already has its result.
            return Publish(results, compilation, parameterList, key, result);
        }

        private static bool HasParameterInferredFromConversions(ParameterListSyntax parameterList)
        {
            foreach (var parameter in parameterList.Parameters)
            {
                if (IsInferredFromConversions(parameter))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Binds every body in <paramref name="bodies"/> with the stand-in for <paramref name="method"/>, and gives each
        /// parameter inferred from conversions the type that its conversions agree on.
        /// </summary>
        private static BodyInference InferFromMemberConversions(SourceOrdinaryMethodSymbol method, MethodDeclarationSyntax declaration, ImmutableArray<MemberBody> bodies, CSharpCompilation compilation)
        {
            ParameterListSyntax parameterList = declaration.ParameterList;
            var standIn = CreatePlaceholder(method, declaration, parameterList, useConversionResults: false, out ImmutableArray<bool> isTarget);
            if (!isTarget.Contains(true))
            {
                // A type named 'infer' is in scope.
                return BodyInference.NotApplicable;
            }

            var frame = new ConversionFrame(declaration, standIn);
            var frames = s_conversionFrames ??= new List<ConversionFrame>();
            var placeholderBodies = s_placeholderBodies ??= new List<CSharpSyntaxNode>();
            var walker = new ConversionSitesWalker(frame);
            var dependencies = BindingDiagnosticBag.GetInstance(withDiagnostics: false, withDependencies: true);
            frames.Add(frame);
            object? suspendedReturnTypeInference = SharperInferredReturnType.SuspendInference();
            try
            {
                foreach (var body in bodies)
                {
                    BindForConversions(body, method, standIn, frame, walker, placeholderBodies, dependencies);
                }

                var uses = CollectConversionUses(frame, walker.Sites, method.Name);
                var results = ArrayBuilder<Result?>.GetInstance(parameterList.Parameters.Count);
                for (int i = 0; i < parameterList.Parameters.Count; i++)
                {
                    ParameterSyntax parameter = parameterList.Parameters[i];
                    results.Add(IsInferredFromConversions(parameter) && uses[i] is { Count: > 0 }
                        ? DecideFromUses(parameter, uses[i], firstNotPinning: null, dependencies, compilation, variance: VarianceKind.Out)
                        : null);
                    uses[i]?.Free();
                }

                return new BodyInference(results.ToImmutableAndFree());
            }
            finally
            {
                SharperInferredReturnType.ResumeInference(suspendedReturnTypeInference);
                standIn.FreeCandidates();
                Debug.Assert(frames[frames.Count - 1] == frame);
                frames.RemoveAt(frames.Count - 1);
                frame.Free();
                dependencies.Free();
            }
        }

        /// <summary>
        /// Binds one body for the conversions of the method that <paramref name="frame"/> infers, and adds the method
        /// groups with recorded conversions that its final bound tree holds to <paramref name="walker"/>'s sites.
        /// </summary>
        /// <remarks>
        /// The method's own body, and the body of a method, constructor, operator or conversion, are bound with a
        /// placeholder for their member, so that neither its parameters nor its return statements read its signature
        /// (which would start its own inferences inside this one). Accessors, destructors and static constructors have no
        /// signature from a body and use their own symbol; an initializer is bound as the field's.
        /// </remarks>
        private static void BindForConversions(
            MemberBody body,
            SourceOrdinaryMethodSymbol method,
            SharperPlaceholderMethodSymbol standIn,
            ConversionFrame frame,
            ConversionSitesWalker walker,
            List<CSharpSyntaxNode> placeholderBodies,
            BindingDiagnosticBag dependencies)
        {
            SharperPlaceholderMethodSymbol? placeholder = null;
            Binder? binder;
            switch (body.Owner)
            {
                case SourceMemberMethodSymbol member when (object)member == method:
                    binder = standIn.TryGetBodyBinder();
                    break;
                case SourceMemberMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion } member
                    when body.Syntax is BaseMethodDeclarationSyntax { ParameterList: { } memberParameterList } memberDeclaration:
                    placeholder = CreatePlaceholder(member, memberDeclaration, memberParameterList, useConversionResults: false, out _);
                    binder = placeholder.TryGetBodyBinder();
                    break;
                case SourceMemberMethodSymbol member:
                    binder = member.TryGetBodyBinder();
                    break;
                case FieldSymbol field when body.Syntax is EqualsValueClauseSyntax initializer:
                    Binder parentBinder = field.DeclaringCompilation.GetBinderFactory(initializer.SyntaxTree).GetBinder(initializer).GetFieldInitializerBinder(field);
                    binder = new ExecutableCodeBinder(initializer, field, new LocalScopeBinder(parentBinder));
                    break;
                default:
                    throw ExceptionUtilities.UnexpectedValue(body.Owner);
            }

            if (binder is null)
            {
                return;
            }

            frame.Root = binder;
            placeholderBodies.Add(body.Syntax);
            try
            {
                var bodyDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: true);
                BoundNode? bound = body.Owner is FieldSymbol field
                    ? binder.BindFieldInitializer(field, (EqualsValueClauseSyntax)body.Syntax, bodyDiagnostics)
                    : binder.BindMethodBody(body.Syntax, bodyDiagnostics);
                dependencies.AddDependencies(bodyDiagnostics);
                bodyDiagnostics.Free();
                if (bound is not null && frame.Conversions.Count > 0)
                {
                    walker.Visit(bound);
                }
            }
            finally
            {
                placeholder?.FreeCandidates();
                Debug.Assert(placeholderBodies[placeholderBodies.Count - 1] == body.Syntax);
                placeholderBodies.RemoveAt(placeholderBodies.Count - 1);
                frame.Root = null;
            }
        }

        /// <summary>
        /// Sharper step P7b: in a binding that a private method's conversion inference makes on this thread, replaces every
        /// private method that may infer from conversions among the candidates of an overload resolution (a call, a method
        /// group conversion, <c>&amp;M</c>) by a stand-in, whose parameters inferred from conversions have an error type.
        /// The binding then reads none of their signatures: the method being inferred would be a cycle, and another one
        /// would start its own inference, which binds the same bodies again and finds this one in progress. Only the stand-in
        /// of the method being inferred records conversions (<see cref="RecordMethodGroupConversion"/>).
        /// </summary>
        internal static void ReplaceByConversionStandIns(Binder binder, ArrayBuilder<MethodSymbol> methods)
        {
            if (s_conversionFrames is not { Count: > 0 } frames)
            {
                return;
            }

            ConversionFrame? frame = null;
            for (int i = 0; i < methods.Count; i++)
            {
                if (IsConversionCandidate(methods[i]))
                {
                    frame ??= FindBindingFrame(frames, binder);
                    if (frame is null)
                    {
                        return;
                    }

                    methods[i] = frame.GetStandIn((SourceOrdinaryMethodSymbol)methods[i]);
                }
            }
        }

        /// <summary>
        /// <inheritdoc cref="ReplaceByConversionStandIns(Binder, ArrayBuilder{MethodSymbol})"/> For the natural type of a
        /// method group (<c>var f = Format;</c>).
        /// </summary>
        internal static MethodSymbol GetConversionStandIn(Binder binder, MethodSymbol method)
        {
            if (s_conversionFrames is { Count: > 0 } frames && IsConversionCandidate(method) && FindBindingFrame(frames, binder) is { } frame)
            {
                return frame.GetStandIn((SourceOrdinaryMethodSymbol)method);
            }

            return method;
        }

        /// <summary>The frame (step P7b) whose binding <paramref name="binder"/> is in, if any.</summary>
        private static ConversionFrame? FindBindingFrame(List<ConversionFrame> frames, Binder binder)
        {
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                if (frames[i].StandIn is not null && frames[i].IsInBinding(binder))
                {
                    return frames[i];
                }
            }

            return null;
        }

        /// <summary>
        /// True for a private method that may infer a parameter type from its conversions (step P7b), judged from its
        /// declaration alone, so that no signature is read.
        /// </summary>
        private static bool IsConversionCandidate(MethodSymbol method)
            => method is SourceOrdinaryMethodSymbol
            {
                DeclaredAccessibility: Accessibility.Private,
                MethodKind: MethodKind.Ordinary,
                IsPartialDefinition: false,
                IsPartialImplementation: false,
                SyntaxNode: MethodDeclarationSyntax { ParameterList: var parameterList },
            } && HasParameterInferredFromConversions(parameterList);

        /// <summary>The result of a private method whose conversions need its own signature (CS20063).</summary>
        private static BodyInference CreateConversionCycle(MethodDeclarationSyntax declaration, CSharpCompilation compilation)
        {
            string name = declaration.Identifier.ValueText;
            var results = ArrayBuilder<Result?>.GetInstance(declaration.ParameterList.Parameters.Count);
            foreach (var parameter in declaration.ParameterList.Parameters)
            {
                if (IsInferredFromConversions(parameter))
                {
                    var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
                    diagnostics.Add(ErrorCode.ERR_SharperInferParameterConversionCycle, parameter.Type!.Location, parameter.Identifier.ValueText, name);
                    results.Add(new Result(CreateErrorType(compilation), diagnostics.ToReadOnlyAndFree()));
                }
                else
                {
                    results.Add(null);
                }
            }

            return new BodyInference(results.ToImmutableAndFree());
        }

        /// <summary>
        /// The bodies in the accessibility domain of <paramref name="type"/>'s private members (its declarations and those
        /// of the types nested in it) that name <paramref name="name"/> without invoking it.
        /// </summary>
        private static ImmutableArray<MemberBody> GetBodiesNaming(NamedTypeSymbol type, string name)
        {
            var index = s_bodiesNamingMethods.GetValue(type, static type => BuildIndex(type));
            return index.TryGetValue(name, out var bodies) ? bodies : ImmutableArray<MemberBody>.Empty;
        }

        private static Dictionary<string, ImmutableArray<MemberBody>> BuildIndex(NamedTypeSymbol type)
        {
            // The names of the type's own private methods that may infer from conversions, read from their syntax.
            var names = PooledHashSet<string>.GetInstance();
            foreach (var member in type.GetMembersUnordered())
            {
                if (member is MethodSymbol method && IsConversionCandidate(method))
                {
                    names.Add(member.Name);
                }
            }

            var result = new Dictionary<string, ImmutableArray<MemberBody>>();
            if (names.Count > 0)
            {
                var allBodies = ArrayBuilder<MemberBody>.GetInstance();
                AddBodies(type, allBodies);
                var byName = new Dictionary<string, ArrayBuilder<MemberBody>>();
                var named = PooledHashSet<string>.GetInstance();
                foreach (var body in allBodies)
                {
                    foreach (var node in body.Syntax.DescendantNodes())
                    {
                        if (node is SimpleNameSyntax { Identifier.ValueText: var text } simpleName &&
                            names.Contains(text) &&
                            !SyntaxFacts.IsInvoked(simpleName) &&
                            named.Add(text))
                        {
                            if (!byName.TryGetValue(text, out var builder))
                            {
                                builder = ArrayBuilder<MemberBody>.GetInstance();
                                byName.Add(text, builder);
                            }

                            builder.Add(body);
                        }
                    }

                    named.Clear();
                }

                foreach (var (text, builder) in byName)
                {
                    result.Add(text, builder.ToImmutableAndFree());
                }

                named.Free();
                allBodies.Free();
            }

            names.Free();
            return result;
        }

        private static void AddBodies(NamedTypeSymbol type, ArrayBuilder<MemberBody> bodies)
        {
            foreach (var member in type.GetMembersUnordered())
            {
                switch (member)
                {
                    case NamedTypeSymbol nested:
                        AddBodies(nested, bodies);
                        break;
                    case SourceOrdinaryMethodSymbol { IsPartialDefinition: true } definition:
                        if (definition.PartialImplementationPart is SourceMemberMethodSymbol implementation)
                        {
                            addMethod(implementation, bodies);
                        }

                        break;
                    case SourceMemberMethodSymbol method:
                        addMethod(method, bodies);
                        break;
                }
            }

            if (type is SourceMemberContainerTypeSymbol container)
            {
                addInitializers(container.StaticInitializers, bodies);
                addInitializers(container.InstanceInitializers, bodies);
            }

            // PROTOTYPE(sharper): primary constructor base arguments and partial methods as targets are not covered by
            // step P7b (#27).
            static void addMethod(SourceMemberMethodSymbol method, ArrayBuilder<MemberBody> bodies)
            {
                if (method is not SharperPlaceholderMethodSymbol &&
                    !method.IsImplicitlyDeclared &&
                    method.SyntaxNode is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null } or
                        AccessorDeclarationSyntax { Body: not null } or AccessorDeclarationSyntax { ExpressionBody: not null } or ArrowExpressionClauseSyntax)
                {
                    bodies.Add(new MemberBody(method, method.SyntaxNode));
                }
            }

            static void addInitializers(ImmutableArray<ImmutableArray<FieldOrPropertyInitializer>> initializers, ArrayBuilder<MemberBody> bodies)
            {
                foreach (var siblings in initializers)
                {
                    foreach (var initializer in siblings)
                    {
                        if (initializer.FieldOpt is { IsConst: false } field && initializer.Syntax.GetSyntax() is EqualsValueClauseSyntax syntax)
                        {
                            bodies.Add(new MemberBody(field, syntax));
                        }
                    }
                }
            }
        }
    }
}
