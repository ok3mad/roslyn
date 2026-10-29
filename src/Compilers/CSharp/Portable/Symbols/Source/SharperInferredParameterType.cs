// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// The type of a parameter declared with Sharper's <c>infer</c>, inferred from its default value
    /// (<see cref="Binder.BindSharperInferredParameterType"/>): <c>infer retries = 3</c> is an <c>int</c>.
    /// </summary>
    /// <remarks>
    /// A parameter type is part of a signature, which most members compute under a per-member lock
    /// (<c>LazyMethodChecks</c>, <c>EnsureSignature</c>). Binding a default value can read other members' signatures, so
    /// inferring under that lock could deadlock two members that use each other on two threads. Those members infer
    /// their parameter types first, outside of the lock (<see cref="InferBeforeSignature"/>), and the signature reads the
    /// result. Results are kept per parameter syntax by the compilation and published once; binding is deterministic,
    /// so threads that race compute the same result.
    ///
    /// A default value that depends on the signature of its own member, directly (<c>M(infer x = M())</c>) or through
    /// other members whose parameter types are inferred, is a cycle. The inferences in progress on a thread are kept on
    /// a stack. When a parameter is asked for while it is on the stack, every parameter from it to the top of the stack
    /// is part of the cycle, and each gets an error at once, before the signature that asked for it is completed: a
    /// thread that later finds that signature complete must not infer the rest of the cycle without the error. So the
    /// result does not depend on which member was asked for first, or on which thread.
    /// </remarks>
    internal static class SharperInferredParameterType
    {
        internal sealed class Result
        {
            internal readonly TypeWithAnnotations Type;
            internal readonly ReadOnlyBindingDiagnostic<AssemblySymbol> Diagnostics;

            internal Result(TypeWithAnnotations type, ReadOnlyBindingDiagnostic<AssemblySymbol> diagnostics)
            {
                Type = type;
                Diagnostics = diagnostics;
            }
        }

        /// <summary>The parameters whose types are being inferred on this thread, innermost last.</summary>
        [ThreadStatic] private static List<ParameterSyntax>? s_inProgress;

        /// <summary>
        /// Infers the types of the <c>infer</c> parameters in <paramref name="parameterList"/> that have a default value,
        /// before <paramref name="owner"/> computes its signature under its lock.
        /// </summary>
        internal static void InferBeforeSignature(Symbol owner, BaseParameterListSyntax? parameterList)
        {
            if (parameterList is null)
            {
                return;
            }

            foreach (var parameter in parameterList.Parameters)
            {
                if (parameter is ParameterSyntax { Type.IsInfer: true, Default: { } } concreteParameter)
                {
                    Binder binder = GetDefaultValueBinder(owner, concreteParameter);
                    binder.BindTypeOrSharperInferKeyword(concreteParameter.Type, BindingDiagnosticBag.Discarded, out bool isInfer);
                    if (isInfer)
                    {
                        GetOrInfer(owner, concreteParameter, binder);
                    }
                }
            }
        }

        /// <summary>
        /// The type of the <c>infer</c> parameter <paramref name="parameter"/> of <paramref name="owner"/>, with the
        /// diagnostics of the inference added to <paramref name="diagnostics"/>.
        /// </summary>
        internal static TypeWithAnnotations GetType(Symbol owner, ParameterSyntax parameter, BindingDiagnosticBag diagnostics)
        {
            return GetType(owner, parameter, GetDefaultValueBinder(owner, parameter), diagnostics);
        }

        /// <summary>
        /// The type of the <c>infer</c> parameter <paramref name="parameter"/>, where <paramref name="binder"/> is the
        /// binder its default value is bound with. Lambdas use this, since their symbol does not exist yet when their
        /// parameter types are bound.
        /// </summary>
        internal static TypeWithAnnotations GetType(Symbol owner, ParameterSyntax parameter, Binder binder, BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(parameter.Type is { IsInfer: true });

            if (parameter.Default is null)
            {
                // PROTOTYPE(sharper): step P2 takes the type from the overridden or implemented member.
                diagnostics.Add(ErrorCode.ERR_SharperInferParameterNoDefault, parameter.Type.Location, parameter.Identifier.ValueText);
                return TypeWithAnnotations.Create(binder.CreateErrorType("infer"));
            }

            Result result = GetOrInfer(owner, parameter, binder);
            diagnostics.AddRange(result.Diagnostics, allowMismatchInDependencyAccumulation: true);
            return result.Type;
        }

        private static Result GetOrInfer(Symbol owner, ParameterSyntax parameter, Binder binder)
        {
            var results = binder.Compilation.SharperInferredParameterTypes;
            if (results.TryGetValue(parameter, out Result? result))
            {
                return result;
            }

            var inProgress = s_inProgress ??= new List<ParameterSyntax>();
            int cycleStart = inProgress.IndexOf(parameter);
            if (cycleStart >= 0)
            {
                for (int i = cycleStart; i < inProgress.Count; i++)
                {
                    ParameterSyntax inCycle = inProgress[i];
                    results.GetValue(inCycle, _ => CreateCycleResult(inCycle, binder));
                }

                return results.GetValue(parameter, _ => throw ExceptionUtilities.Unreachable());
            }

            inProgress.Add(parameter);
            try
            {
                var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: true);
                Binder defaultValueBinder = binder.CreateBinderForParameterDefaultValue(owner, parameter.Default!);
                TypeWithAnnotations type = defaultValueBinder.GetBinder(parameter.Default!)!.BindSharperInferredParameterType(parameter, diagnostics);
                result = new Result(type, diagnostics.ToReadOnlyAndFree());
            }
            finally
            {
                Debug.Assert(inProgress[inProgress.Count - 1] == parameter);
                inProgress.RemoveAt(inProgress.Count - 1);
            }

            // A parameter found to be in a cycle while its default value was bound already has its result.
            return results.GetValue(parameter, _ => result);
        }

        private static Result CreateCycleResult(ParameterSyntax parameter, Binder binder)
        {
            var diagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: false);
            diagnostics.Add(ErrorCode.ERR_SharperInferParameterCycle, parameter.Type!.Location, parameter.Identifier.ValueText);
            return new Result(TypeWithAnnotations.Create(binder.CreateErrorType("infer")), diagnostics.ToReadOnlyAndFree());
        }

        /// <summary>
        /// The binder that the default value of a parameter of <paramref name="owner"/> is bound with, as
        /// <see cref="SourceComplexParameterSymbolBase"/> gets it.
        /// </summary>
        private static Binder GetDefaultValueBinder(Symbol owner, ParameterSyntax parameter)
        {
            if ((owner as SourceMethodSymbol)?.WithTypeParametersBinder is { } binder)
            {
                return binder;
            }

            SyntaxNode syntax = (SyntaxNode?)parameter.Default ?? parameter;
            return owner.DeclaringCompilation.GetBinderFactory(syntax.SyntaxTree).GetBinder(syntax);
        }
    }
}
