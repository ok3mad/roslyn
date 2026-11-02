// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// The return type of a method or local function declared with Sharper's <c>infer</c>, inferred from its
    /// expression body or block body (<see cref="Binder.BindSharperInferredReturnType"/>).
    /// </summary>
    /// <remarks>
    /// The type is computed on first use, outside of any lock, and published with a compare-exchange: binding is
    /// deterministic, so threads that race compute the same type, and only the winner reports diagnostics. A method's
    /// <c>MethodChecks</c> runs under a per-method lock and must not infer, or two methods that use each other could
    /// deadlock on two threads.
    ///
    /// The inferences in progress on a thread are kept on a stack. A body that reads the inferred return type of a
    /// method that is not published yet infers that method first, in a nested frame on the same thread. A read of a
    /// method that is already on the stack is a cycle: the method needs its own return type, directly or through other
    /// inferred methods, which is an error, as in F#. The read gets an error type that is never cached.
    ///
    /// Which methods are in a cycle must not depend on where the inference started, or on which thread. The methods
    /// that need each other's return types are found as a strongly connected component of the uses (Tarjan's
    /// algorithm, run as the frames nest): a method that ends up in a cycle is not published when its body is done,
    /// but when the whole component is, and then every member gets the error, which names its cycle as found in the
    /// complete uses of the component. All results of a component are decided before any of them is published, so a
    /// thread that reads one of them, and so cannot see the rest of the cycle, takes the decided results instead of
    /// its own.
    /// </remarks>
    internal abstract class SharperInferredReturnType
    {
        private sealed class Result
        {
            internal readonly TypeWithAnnotations Type;
            internal readonly ReadOnlyBindingDiagnostic<AssemblySymbol> Diagnostics;

            internal Result(TypeWithAnnotations type, ReadOnlyBindingDiagnostic<AssemblySymbol> diagnostics)
            {
                Type = type;
                Diagnostics = diagnostics;
            }
        }

        /// <summary>One inference on the stack of a thread: a node of Tarjan's algorithm.</summary>
        private sealed class Frame
        {
            internal readonly SharperInferredReturnType Inference;
            internal readonly int Index;
            internal int LowLink;

            /// <summary>The inferred methods whose return types the body reads, in order, without the published ones.</summary>
            internal readonly List<SharperInferredReturnType> Uses = new List<SharperInferredReturnType>();
            internal TypeWithAnnotations Type;
            internal BindingDiagnosticBag? Diagnostics;

            internal Frame(SharperInferredReturnType inference, int index)
            {
                Inference = inference;
                Index = index;
                LowLink = index;
            }

            internal void AddUse(SharperInferredReturnType inference)
            {
                if (!Uses.Contains(inference))
                {
                    Uses.Add(inference);
                }
            }
        }

        private sealed class Context
        {
            /// <summary>The frames whose bodies are being bound, innermost last.</summary>
            internal readonly List<Frame> Binding = new List<Frame>();

            /// <summary>
            /// Tarjan's stack: the frames on <see cref="Binding"/> and the finished frames whose component is not
            /// complete yet, because they need a method that is still being bound.
            /// </summary>
            internal readonly List<Frame> Pending = new List<Frame>();
            internal readonly Dictionary<SharperInferredReturnType, Frame> PendingByInference = new Dictionary<SharperInferredReturnType, Frame>();
            internal int NextIndex;
        }

        [ThreadStatic] private static Context? s_context;

        private readonly MethodSymbol _method;
        private readonly TypeSyntax _inferSyntax;
        private readonly CSharpSyntaxNode _body;
        private readonly object _publishLock = new object();

        /// <summary>The result, decided before it is published, so that every member of a cycle is decided at once.</summary>
        private Result? _lazyDecided;
        private TypeWithAnnotations.Boxed? _lazyType;

        protected SharperInferredReturnType(MethodSymbol method, TypeSyntax inferSyntax, CSharpSyntaxNode body)
        {
            Debug.Assert(inferSyntax.IsInfer);
            Debug.Assert(body is ArrowExpressionClauseSyntax or BlockSyntax);
            _method = method;
            _inferSyntax = inferSyntax;
            _body = body;
        }

        /// <summary>The binder of the expression body or block body, as the method body binding gets it.</summary>
        protected abstract Binder? GetBodyBinder(CSharpSyntaxNode body);

        /// <summary>Checks that apply to a written return type, such as the restricted types.</summary>
        protected abstract void CheckInferredType(TypeWithAnnotations type, TypeSyntax inferSyntax, BindingDiagnosticBag diagnostics);

        protected abstract void AddDeclarationDiagnostics(BindingDiagnosticBag diagnostics);

        /// <summary>
        /// True while the body of <paramref name="method"/> is bound to infer its return type on this thread. The binder
        /// then binds <c>return</c> and <c>yield return</c> as for a lambda whose return type is inferred, without
        /// reading the return type, which would count as a use of the method by itself.
        /// </summary>
        internal static bool IsBeingInferred(MethodSymbol method)
            => s_context is { Binding.Count: > 0 } context && (object)context.Binding[context.Binding.Count - 1].Inference._method == method;

        /// <summary>
        /// True for a method whose return type is inferred, where the inference failed with an error. Its body is not
        /// checked for a missing return value (CS0161), since it has no return type to miss.
        /// </summary>
        internal static bool HasFailedInference(MethodSymbol method)
            => method is SourceOrdinaryMethodOrUserDefinedOperatorSymbol { IsSharperInferredReturnType: true } or LocalFunctionSymbol { IsSharperInferredReturnType: true } &&
                method.ReturnType.IsErrorType();

        internal TypeWithAnnotations GetReturnType()
        {
            if (Volatile.Read(ref _lazyType) is { } type)
            {
                return type.Value;
            }

            if (Volatile.Read(ref _lazyDecided) is not null)
            {
                // Another thread decided the result and is publishing it.
                Publish();
                return _lazyType!.Value;
            }

            Context? context = s_context;
            if (context is { Binding.Count: > 0 })
            {
                Frame current = context.Binding[context.Binding.Count - 1];
                current.AddUse(this);
                if (context.PendingByInference.TryGetValue(this, out Frame? pending))
                {
                    // A cycle: this method is being inferred, or needs a method that is.
                    current.LowLink = Math.Min(current.LowLink, pending.Index);
                    return CreateErrorType();
                }

                Frame nested = Infer(context);
                if (context.PendingByInference.ContainsKey(this))
                {
                    // This method needs a method that is being inferred, so it is in a cycle with the current one.
                    current.LowLink = Math.Min(current.LowLink, nested.LowLink);
                    return CreateErrorType();
                }

                return _lazyType!.Value;
            }

            bool ownsContext = context is null;
            context ??= new Context();
            s_context = context;
            try
            {
                Infer(context);
            }
            finally
            {
                if (ownsContext)
                {
                    s_context = null;
                }
            }

            return _lazyType!.Value;
        }

        private Frame Infer(Context context)
        {
            var frame = new Frame(this, context.NextIndex++);
            context.Binding.Add(frame);
            context.Pending.Add(frame);
            context.PendingByInference.Add(this, frame);

            var diagnostics = BindingDiagnosticBag.GetInstance();
            try
            {
                frame.Type = GetBodyBinder(_body) is { } binder
                    ? binder.BindSharperInferredReturnType(_method, _body, _inferSyntax, diagnostics)
                    : CreateErrorType();
                frame.Diagnostics = diagnostics;
            }
            finally
            {
                Debug.Assert(context.Binding[context.Binding.Count - 1] == frame);
                context.Binding.RemoveAt(context.Binding.Count - 1);
            }

            if (frame.LowLink == frame.Index)
            {
                // The frame is the root of a component: the frames above it on Tarjan's stack need each other.
                int start = context.Pending.LastIndexOf(frame);
                var component = context.Pending.GetRange(start, context.Pending.Count - start);
                context.Pending.RemoveRange(start, component.Count);
                foreach (var member in component)
                {
                    context.PendingByInference.Remove(member.Inference);
                }

                DecideAndPublish(component);
            }

            return frame;
        }

        private static void DecideAndPublish(List<Frame> component)
        {
            var members = new HashSet<SharperInferredReturnType>(component.Select(f => f.Inference));
            foreach (var frame in component)
            {
                SharperInferredReturnType inference = frame.Inference;
                BindingDiagnosticBag diagnostics = frame.Diagnostics!;
                frame.Diagnostics = null;
                if (Volatile.Read(ref inference._lazyDecided) is not null)
                {
                    // Another thread decided the result first; its traversal may have seen more of the cycle.
                    diagnostics.Free();
                    continue;
                }

                TypeWithAnnotations type = frame.Type;
                if (frame.Uses.Contains(inference) || component.Count > 1)
                {
                    // Whatever else the binding reported came from the error types of the cycle.
                    diagnostics.Clear();
                    if (frame.Uses.Contains(inference))
                    {
                        diagnostics.Add(ErrorCode.ERR_SharperInferUsesItself, inference._inferSyntax.Location, inference._method);
                    }
                    else
                    {
                        diagnostics.Add(ErrorCode.ERR_SharperInferCycle, inference._inferSyntax.Location, inference._method, FormatCycle(frame, component, members));
                    }

                    type = inference.CreateErrorType();
                }
                else
                {
                    inference.CheckInferredType(type, inference._inferSyntax, diagnostics);
                }

                Interlocked.CompareExchange(ref inference._lazyDecided, new Result(type, diagnostics.ToReadOnlyAndFree()), null);
            }

            foreach (var frame in component)
            {
                frame.Inference.Publish();
            }
        }

        /// <summary>
        /// The shortest cycle from the method of <paramref name="frame"/> back to it through the other members of its
        /// component, preferring the uses that come first in each body: <c>'A()' uses 'B()', which uses 'A()'</c>.
        /// </summary>
        private static string FormatCycle(Frame frame, List<Frame> component, HashSet<SharperInferredReturnType> members)
        {
            var framesByInference = component.ToDictionary(f => f.Inference);
            var previous = new Dictionary<SharperInferredReturnType, SharperInferredReturnType>();
            var queue = new Queue<SharperInferredReturnType>();
            queue.Enqueue(frame.Inference);
            SharperInferredReturnType? last = null;
            while (last is null && queue.Count > 0)
            {
                SharperInferredReturnType from = queue.Dequeue();
                foreach (var use in framesByInference[from].Uses)
                {
                    if (use == frame.Inference)
                    {
                        last = from;
                        break;
                    }

                    if (members.Contains(use) && !previous.ContainsKey(use))
                    {
                        previous.Add(use, from);
                        queue.Enqueue(use);
                    }
                }
            }

            var path = ArrayBuilder<SharperInferredReturnType>.GetInstance();
            path.Add(frame.Inference);
            if (last is null)
            {
                // Not reached: the members of a component reach each other. Name them in the order they were found.
                path.AddRange(component.Select(f => f.Inference).Where(i => i != frame.Inference));
                path.Add(frame.Inference);
            }
            else
            {
                for (SharperInferredReturnType node = last; node != frame.Inference; node = previous[node])
                {
                    path.Add(node);
                }

                path.ReverseContents();
                path.Insert(0, frame.Inference);
            }

            var builder = PooledStringBuilder.GetInstance();
            for (int i = 0; i < path.Count; i++)
            {
                builder.Builder.Append(i switch { 0 => "'", 1 => " uses '", _ => ", which uses '" });
                builder.Builder.Append(path[i]._method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                builder.Builder.Append('\'');
            }

            path.Free();
            return builder.ToStringAndFree();
        }

        private void Publish()
        {
            // The diagnostics are added before the type is published, so that whoever sees the type also sees them.
            lock (_publishLock)
            {
                if (_lazyType is null)
                {
                    Result decided = _lazyDecided!;
                    var diagnostics = BindingDiagnosticBag.GetInstance();
                    diagnostics.AddRange(decided.Diagnostics);
                    AddDeclarationDiagnostics(diagnostics);
                    diagnostics.Free();
                    Volatile.Write(ref _lazyType, new TypeWithAnnotations.Boxed(decided.Type));
                }
            }
        }

        private TypeWithAnnotations CreateErrorType()
            => TypeWithAnnotations.Create(new ExtendedErrorTypeSymbol(_method.DeclaringCompilation, "infer", arity: 0, errorInfo: null));
    }
}
