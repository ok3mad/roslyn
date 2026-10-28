// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// The return type of a method or local function declared with Sharper's <c>infer</c>, inferred from its
    /// expression body (<see cref="Binder.BindSharperInferredReturnType"/>).
    /// </summary>
    /// <remarks>
    /// The type is computed on first use, outside of any lock, and published with a compare-exchange: binding is
    /// deterministic, so threads that race compute the same type, and only the winner reports diagnostics. A method's
    /// <c>MethodChecks</c> runs under a per-method lock and must not infer, or two methods that use each other could
    /// deadlock on two threads.
    ///
    /// While a body is bound for inference, the inference in progress is recorded per thread. A read of an inferred
    /// return type during that binding is a use of an inferred method: the method itself (always an error, as in F#),
    /// or another inferred method, which is an error in step R1 because the order in which bodies are inferred is
    /// step R3. Such a read gets an error type, which is never cached, so the result never depends on which method
    /// happened to be inferred first. R3 replaces the single frame with a stack: a read of another inferred method
    /// infers it in a nested frame, and a method already on the stack is a cycle.
    /// </remarks>
    internal abstract class SharperInferredReturnType
    {
        private sealed class Frame
        {
            internal readonly SharperInferredReturnType Inference;
            internal bool UsesItself;
            internal MethodSymbol? UsedInferredMethod;

            internal Frame(SharperInferredReturnType inference)
            {
                Inference = inference;
            }
        }

        /// <summary>The inference in progress on this thread, if any.</summary>
        [ThreadStatic] private static Frame? s_inferenceInProgress;

        private readonly MethodSymbol _method;
        private readonly TypeSyntax _inferSyntax;
        private readonly ArrowExpressionClauseSyntax _body;
        private readonly object _publishLock = new object();
        private TypeWithAnnotations.Boxed? _lazyType;

        protected SharperInferredReturnType(MethodSymbol method, TypeSyntax inferSyntax, ArrowExpressionClauseSyntax body)
        {
            Debug.Assert(inferSyntax.IsInfer);
            _method = method;
            _inferSyntax = inferSyntax;
            _body = body;
        }

        /// <summary>The binder of the expression body, as the method body binding gets it.</summary>
        protected abstract Binder? GetBodyBinder(ArrowExpressionClauseSyntax body);

        /// <summary>Checks that apply to a written return type, such as the restricted types.</summary>
        protected abstract void CheckInferredType(TypeWithAnnotations type, TypeSyntax inferSyntax, BindingDiagnosticBag diagnostics);

        protected abstract void AddDeclarationDiagnostics(BindingDiagnosticBag diagnostics);

        internal TypeWithAnnotations GetReturnType()
        {
            if (s_inferenceInProgress is { } inProgress)
            {
                if (inProgress.Inference == this)
                {
                    inProgress.UsesItself = true;
                }
                else
                {
                    inProgress.UsedInferredMethod ??= _method;
                }

                return CreateErrorType();
            }

            if (Volatile.Read(ref _lazyType) is { } type)
            {
                return type.Value;
            }

            Infer();
            return _lazyType!.Value;
        }

        private void Infer()
        {
            var diagnostics = BindingDiagnosticBag.GetInstance();
            var frame = new Frame(this);
            TypeWithAnnotations type;

            Debug.Assert(s_inferenceInProgress is null);
            s_inferenceInProgress = frame;
            try
            {
                type = GetBodyBinder(_body) is { } binder
                    ? binder.BindSharperInferredReturnType(_method, _body, _inferSyntax, diagnostics)
                    : CreateErrorType();
            }
            finally
            {
                s_inferenceInProgress = null;
            }

            if (frame.UsesItself || frame.UsedInferredMethod is not null)
            {
                // Whatever else the binding reported came from the error type of that use.
                diagnostics.Clear();
                if (frame.UsesItself)
                {
                    diagnostics.Add(ErrorCode.ERR_SharperInferUsesItself, _inferSyntax.Location, _method);
                }
                else
                {
                    diagnostics.Add(ErrorCode.ERR_SharperInferUsesInferredMethod, _inferSyntax.Location, _method, frame.UsedInferredMethod!);
                }

                type = CreateErrorType();
            }
            else
            {
                CheckInferredType(type, _inferSyntax, diagnostics);
            }

            // The diagnostics are added before the type is published, so that whoever sees the type also sees them.
            lock (_publishLock)
            {
                if (_lazyType is null)
                {
                    AddDeclarationDiagnostics(diagnostics);
                    Volatile.Write(ref _lazyType, new TypeWithAnnotations.Boxed(type));
                }
            }

            diagnostics.Free();
        }

        private TypeWithAnnotations CreateErrorType()
            => TypeWithAnnotations.Create(new ExtendedErrorTypeSymbol(_method.DeclaringCompilation, "infer", arity: 0, errorInfo: null));
    }
}
