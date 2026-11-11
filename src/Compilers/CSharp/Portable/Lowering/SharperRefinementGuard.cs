// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper: the entry guard of a method with refined parameters (Feature 11, slice 1 step 5). For each refined parameter,
    /// in order: <c>if (!(i is &gt;=0)) throw new ArgumentOutOfRangeException(nameof(i), i, "...")</c>, lowered through the
    /// <c>is</c>-pattern lowering. It covers every caller the compiler cannot check: C# callers, delegates, reflection.
    /// </summary>
    /// <remarks>
    /// The guard is built against the method that is emitted, lowered on its own, and put first in the emitted body
    /// (<see cref="MethodCompiler"/>), after state machine rewriting: an iterator or <c>async</c> method checks in its
    /// kickoff method, so it throws when called rather than when enumerated or awaited, and a constructor checks before
    /// its field initializers and before a primary constructor stores its captured parameters. The emitted method is the
    /// method itself, the closure method of a local function, or the implementation method of an extension block member;
    /// the last two have parameters of their own, with substituted types, so the pattern is rebuilt against them
    /// (<see cref="SharperRefinement.MakePattern"/>). A refined parameter is readonly, so reading it before a closure
    /// copies it is the same as reading the copy.
    /// Runtime async has no kickoff method: the runtime puts an exception thrown anywhere in a runtime-async method into
    /// the returned task. A method with a guard therefore keeps the compiler's state machine
    /// (<see cref="CSharpCompilation.IsRuntimeAsyncEnabledIn"/>, through <see cref="HasGuard"/>).
    /// PROTOTYPE(sharper): a synthesized wrapper that checks and then calls a runtime-async body would keep runtime async
    /// for these methods (#224).
    /// </remarks>
    internal static class SharperRefinementGuard
    {
        /// <summary>The lowered guard of <paramref name="method"/>, the method being emitted, or null if it needs none.</summary>
        internal static BoundStatement? MakeLoweredGuard(MethodSymbol method, int methodOrdinal, TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            var refined = GetRefinedParameters(method);
            if (refined is null)
            {
                return null;
            }

            CSharpCompilation compilation = method.DeclaringCompilation;
            MethodSymbol? constructor = SharperRefinement.GetGuardExceptionConstructor(compilation);
            if (constructor is null)
            {
                // Reported where the refinement binds (ERR_MissingPredefinedMember).
                refined.Free();
                return null;
            }

            var statements = ArrayBuilder<BoundStatement>.GetInstance(refined.Count);
            SyntheticBoundNodeFactory? factory = null;
            foreach (var (parameter, refinement) in refined)
            {
                SyntaxNode syntax = refinement.Pattern.Syntax;
                factory ??= new SyntheticBoundNodeFactory(method, syntax, compilationState, diagnostics);
                factory.Syntax = syntax;

                TypeSymbol type = parameter.Type;
                BoundExpression input = factory.Parameter(parameter);

                // 'p is not (pattern)', made as the binder makes an is-pattern expression: the decision DAG is for the pattern
                // inside the outer 'not's.
                var pattern = new BoundNegatedPattern(syntax, refinement.MakePattern(type, syntax), inputType: type, narrowedType: type) { WasCompilerGenerated = true };
                bool negated = pattern.IsNegated(out BoundPattern innerPattern);
                var whenTrueLabel = new GeneratedLabelSymbol(negated ? "isPatternFailure" : "isPatternSuccess");
                var whenFalseLabel = new GeneratedLabelSymbol(negated ? "isPatternSuccess" : "isPatternFailure");
                var decisionDag = DecisionDagBuilder.CreateDecisionDagForIsPattern(
                    compilation, syntax, input, innerPattern, hasUnionMatching: false, whenTrueLabel: whenTrueLabel, whenFalseLabel: whenFalseLabel, diagnostics);
                var test = new BoundIsPatternExpression(
                    syntax, input, pattern, hasUnionMatching: false, negated, decisionDag, whenTrueLabel: whenTrueLabel, whenFalseLabel: whenFalseLabel,
                    factory.SpecialType(SpecialType.System_Boolean))
                { WasCompilerGenerated = true };

                statements.Add(factory.If(
                    test,
                    factory.Throw(factory.New(
                        constructor,
                        factory.StringLiteral(parameter.Name),
                        factory.Convert(factory.SpecialType(SpecialType.System_Object), factory.Parameter(parameter), type.IsValueType ? Conversion.Boxing : Conversion.ImplicitReference),
                        factory.StringLiteral(refinement.GuardMessage)))));
            }

            refined.Free();
            Debug.Assert(factory is not null);
            BoundStatement guard = factory.StatementList(statements.ToImmutableAndFree());

            guard = LocalRewriter.Rewrite(
                compilation,
                method,
                methodOrdinal,
                method.ContainingType,
                guard,
                compilationState,
                previousSubmissionFields: null!,
                allowOmissionOfConditionalCalls: true,
                instrumentation: MethodInstrumentation.Empty,
                debugDocumentProvider: null!,
                diagnostics,
                codeCoverageSpans: out _,
                sawLambdas: out _,
                sawLocalFunctions: out _,
                sawAwaitInExceptionHandler: out _);

            // The guard has no sequence points of its own, and comes before the body's first one, so a method with any must
            // start with a hidden one (the debugger does not stop in the guard).
            return factory.StatementList(BoundSequencePoint.CreateHidden(), guard);
        }

        /// <summary>True if <paramref name="method"/>, a method that is compiled, checks refined parameters at entry.</summary>
        internal static bool HasGuard(MethodSymbol method)
        {
            var refined = GetRefinedParameters(method);
            refined?.Free();
            return refined is not null;
        }

        /// <summary>
        /// The parameters of <paramref name="method"/> that have a refinement, with it: the method's own, or those of the
        /// local function or extension block member it implements.
        /// </summary>
        private static ArrayBuilder<(ParameterSymbol Parameter, SharperRefinement Refinement)>? GetRefinedParameters(MethodSymbol method)
        {
            MethodSymbol source = method;
            int offset = 0;
            switch (method)
            {
                case SynthesizedClosureMethod closure:
                    source = closure.OriginalMethod;
                    break;
                case SourceExtensionImplementationMethodSymbol implementation:
                    // An instance member's implementation takes the receiver first.
                    source = implementation.UnderlyingMethod;
                    offset = source.IsStatic ? 0 : 1;
                    break;
            }

            ArrayBuilder<(ParameterSymbol, SharperRefinement)>? result = null;
            ImmutableArray<ParameterSymbol> parameters = method.Parameters;
            foreach (var parameter in source.Parameters)
            {
                if (parameter.SharperRefinement is { HasErrors: false } refinement)
                {
                    (result ??= ArrayBuilder<(ParameterSymbol, SharperRefinement)>.GetInstance()).Add((parameters[parameter.Ordinal + offset], refinement));
                }
            }

            return result;
        }
    }
}
