// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper's inferred return type. For an expression body, <c>infer F(...) =&gt; e</c>, the return type is the type
    /// that <c>var r = e;</c> would give <c>r</c>, or <c>void</c> when <c>e</c> is a call that returns nothing. For a
    /// block body, it is the type a lambda with that body would infer: the best common type of the returned values,
    /// <c>void</c> when none is returned, and <c>Task</c> or <c>Task&lt;T&gt;</c> when the method is async. An iterator
    /// returns <c>IEnumerable&lt;T&gt;</c> of the best common type of its yielded values. The lazy computation, its
    /// thread-safety and the use of other inferred methods are in <see cref="SharperInferredReturnType"/>.
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// Binds the return type syntax of a method or local function, taking into account that it may be the Sharper
        /// <c>infer</c> keyword. Like <c>var</c>, <c>infer</c> is a keyword only where no type named <c>infer</c> is in
        /// scope, and with Sharper off it is an ordinary type name (CS0246 when there is none).
        /// </summary>
        /// <returns>The bound type, or default when the syntax is the <c>infer</c> keyword.</returns>
        internal TypeWithAnnotations BindTypeOrSharperInferKeyword(TypeSyntax syntax, BindingDiagnosticBag diagnostics, out bool isInfer)
        {
            if (syntax.IsInfer)
            {
                var symbol = BindTypeOrAliasOrKeyword((IdentifierNameSyntax)syntax, diagnostics, out isInfer);
                return isInfer ? default : UnwrapAlias(symbol, diagnostics, syntax).TypeWithAnnotations;
            }

            isInfer = false;
            return BindType(syntax, diagnostics);
        }

        /// <summary>
        /// Binds a type in a position where Sharper does not support <c>infer</c> yet, but will: the <c>infer</c>
        /// keyword reports that, rather than a missing type named <c>infer</c>.
        /// </summary>
        internal TypeWithAnnotations BindTypeReportingSharperInferNotSupportedYet(TypeSyntax syntax, MessageID position, BindingDiagnosticBag diagnostics)
        {
            TypeWithAnnotations type = BindTypeOrSharperInferKeyword(syntax, diagnostics, out bool isInfer);
            if (isInfer)
            {
                diagnostics.Add(ErrorCode.ERR_SharperInferNotSupportedYet, syntax.Location, position.Localize());
                type = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }

            return type;
        }

        /// <summary>
        /// True while the body of <paramref name="method"/> is bound to infer its return type ('infer'). Its
        /// <c>return</c> and <c>yield return</c> statements are then bound as in a lambda whose return type is being
        /// inferred, without a target type, and without reading the return type of the method.
        /// </summary>
        internal static bool IsSharperReturnTypeBeingInferred(MethodSymbol? method)
            => method is not null && SharperInferredReturnType.IsBeingInferred(method);

        /// <summary>
        /// Infers the return type of <paramref name="method"/> from its body. This binder is the binder of the body, as
        /// the method body binding gets it. The body is bound again, and the result thrown away, when the method itself
        /// is compiled; that binding reports the errors of the body, so they are dropped here.
        /// </summary>
        internal TypeWithAnnotations BindSharperInferredReturnType(
            MethodSymbol method,
            CSharpSyntaxNode body,
            TypeSyntax inferSyntax,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(inferSyntax.IsInfer);
            Debug.Assert(IsSharperReturnTypeBeingInferred(method));

            return body is BlockSyntax block
                ? BindSharperInferredReturnTypeOfBlock(method, block, inferSyntax, diagnostics)
                : BindSharperInferredReturnTypeOfExpression(method, (ArrowExpressionClauseSyntax)body, inferSyntax, diagnostics);
        }

        private TypeWithAnnotations BindSharperInferredReturnTypeOfExpression(
            MethodSymbol method,
            ArrowExpressionClauseSyntax body,
            TypeSyntax inferSyntax,
            BindingDiagnosticBag diagnostics)
        {
            var bodyDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            ExpressionSyntax expressionSyntax = body.Expression.CheckAndUnwrapRefExpression(bodyDiagnostics, out RefKind refKind)!;
            BoundExpression value = BindValue(expressionSyntax, bodyDiagnostics, BindValueKind.RValue);
            bool bodyHasErrors = bodyDiagnostics.HasAnyErrors() || refKind != RefKind.None;

            // A lambda or method group gets its natural function type, as for 'var'.
            var naturalTypeDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundExpression expression = value.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup ?
                BindToInferredDelegateType(value, naturalTypeDiagnostics) :
                BindToNaturalType(value, naturalTypeDiagnostics);

            TypeWithAnnotations result;
            if (bodyHasErrors)
            {
                // The body reports its own errors when it is compiled.
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else if (naturalTypeDiagnostics.HasAnyErrors() || !expression.HasExpressionType())
            {
                // Certain expressions (null, default, a method group or lambda without a natural type, a throw
                // expression) have no type of their own, so there is nothing to infer from.
                diagnostics.Add(ErrorCode.ERR_SharperInferNoType, expressionSyntax.Location, method, value.Display);
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else
            {
                diagnostics.AddDependencies(bodyDiagnostics);
                diagnostics.AddDependencies(naturalTypeDiagnostics);
                TypeSymbol bodyType = expression.Type!;
                result = TypeWithAnnotations.Create(bodyType);
                if (method.IsAsync)
                {
                    // As for an async lambda: Task, or Task<T> of the type of the body.
                    result = bodyType.IsVoidType()
                        ? TypeWithAnnotations.Create(GetWellKnownType(WellKnownType.System_Threading_Tasks_Task, diagnostics, inferSyntax))
                        : TypeWithAnnotations.Create(GetWellKnownType(WellKnownType.System_Threading_Tasks_Task_T, diagnostics, inferSyntax).Construct([result]));
                }

                if (!bodyType.IsVoidType())
                {
                    var block = new BoundBlock(
                        body,
                        GetDeclaredLocalsForScope(body),
                        [new BoundReturnStatement(expressionSyntax, RefKind.None, expression, @checked: CheckOverflowAtRuntime) { WasCompilerGenerated = true }])
                    { WasCompilerGenerated = true };
                    result = InferSharperReturnTypeNullability(method, block, inferSyntax, result, isIterator: false);
                }
            }

            bodyDiagnostics.Free();
            naturalTypeDiagnostics.Free();
            return result;
        }

        /// <summary>
        /// Infers the return type from a block body, as for a lambda with that body: <see cref="BoundLambda.InferSharperReturnType"/>
        /// is the lambda's inference with no delegate type. In an iterator, the yielded values take the place of the
        /// returned ones, and the result is <c>IEnumerable&lt;T&gt;</c>.
        /// </summary>
        private TypeWithAnnotations BindSharperInferredReturnTypeOfBlock(
            MethodSymbol method,
            BlockSyntax body,
            TypeSyntax inferSyntax,
            BindingDiagnosticBag diagnostics)
        {
            // Async iterators are rejected with the declaration.
            Debug.Assert(!(method.IsIterator && method.IsAsync));

            var bodyDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundBlock block = BindEmbeddedBlock(body, bodyDiagnostics);

            bool isIterator = method.IsIterator;
            var returns = ArrayBuilder<(BoundReturnStatement, TypeWithAnnotations)>.GetInstance();
            bool returnsHaveErrors = SharperReturnsCollector.Collect(returns, block, isIterator);

            TypeWithAnnotations result;
            if (returnsHaveErrors)
            {
                // A returned value with errors, or a ref return. The body reports the errors when it is compiled.
                result = TypeWithAnnotations.Create(CreateErrorType("infer"));
            }
            else
            {
                InferredLambdaReturnType inferred = BoundLambda.InferSharperReturnType(returns, block, this, method.IsAsync, Conversions, diagnostics.AccumulatesDependencies);
                diagnostics.AddDependencies(inferred.Dependencies);
                result = inferred.TypeWithAnnotations;

                if (!result.HasType && inferred.NumExpressions == 0 && !isIterator)
                {
                    // No return statement with a value.
                    Debug.Assert(!method.IsAsync);
                    result = TypeWithAnnotations.Create(GetSpecialType(SpecialType.System_Void, diagnostics, inferSyntax));
                }

                if (!result.HasType)
                {
                    reportNoType(method, inferSyntax, returns, isIterator, diagnostics);
                    result = TypeWithAnnotations.Create(CreateErrorType("infer"));
                }
                else
                {
                    diagnostics.AddDependencies(bodyDiagnostics);
                    if (isIterator)
                    {
                        result = makeSequence(result, inferSyntax, diagnostics);
                    }

                    if (!result.IsVoidType())
                    {
                        result = InferSharperReturnTypeNullability(method, block, inferSyntax, result, isIterator);
                    }
                }
            }

            returns.Free();
            bodyDiagnostics.Free();
            return result;

            TypeWithAnnotations makeSequence(TypeWithAnnotations elementType, TypeSyntax inferSyntax, BindingDiagnosticBag diagnostics)
                => TypeWithAnnotations.Create(GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T, diagnostics, inferSyntax).Construct([elementType]));

            static void reportNoType(MethodSymbol method, TypeSyntax inferSyntax, ArrayBuilder<(BoundReturnStatement, TypeWithAnnotations)> returns, bool isIterator, BindingDiagnosticBag diagnostics)
            {
                var values = returns.Where(r => r.Item1.ExpressionOpt is not null).Select(r => r.Item1.ExpressionOpt!).ToImmutableArray();
                if (values.IsEmpty)
                {
                    Debug.Assert(isIterator);
                    diagnostics.Add(ErrorCode.ERR_SharperInferIteratorYieldsNothing, inferSyntax.Location, method);
                }
                else if (values.Length == 1)
                {
                    // One value, which has no type of its own (null, default, a lambda without a natural type, ...).
                    diagnostics.Add(ErrorCode.ERR_SharperInferNoType, values[0].Syntax.Location, method, values[0].Display);
                }
                else
                {
                    // PROTOTYPE(sharper): an inferred union of the returned types waits for unions (#31).
                    string types = string.Join(", ", values.Select(v => v.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? v.Display.ToString()).Distinct());
                    diagnostics.Add(ErrorCode.ERR_SharperInferNoBestType, inferSyntax.Location, method, types);
                }
            }
        }

        /// <summary>
        /// The nullable annotations of an inferred return type come from the flow state of the returned values, as for
        /// the return type of a lambda: one maybe-null value makes the type annotated (<c>string?</c>). Where nullable
        /// annotations are disabled at <c>infer</c>, the type is oblivious, as a written type would be.
        /// </summary>
        /// <param name="type">The inferred type, without nullable analysis.</param>
        private TypeWithAnnotations InferSharperReturnTypeNullability(
            MethodSymbol method,
            BoundBlock block,
            TypeSyntax inferSyntax,
            TypeWithAnnotations type,
            bool isIterator)
        {
            Debug.Assert(type.HasType && !type.IsVoidType());

            if (!AreNullableAnnotationsEnabled(((IdentifierNameSyntax)inferSyntax).Identifier))
            {
                return type;
            }

            // PROTOTYPE(sharper): a local function's body is analyzed on its own, so a captured variable has the state of
            // its declared type (maybe null for a 'var' local), not its state where the local function is used.
            var returnTypes = ArrayBuilder<(BoundReturnStatement, TypeWithAnnotations)>.GetInstance();
            TypeWithAnnotations result = type;
            if (NullableWalker.AnalyzeSharperInferredReturnType(Compilation, method, block, this, returnTypes))
            {
                TypeWithAnnotations analyzed = BoundLambda.InferSharperReturnType(returnTypes, block, this, method.IsAsync, Conversions.WithNullability(true), withDependencies: false).TypeWithAnnotations;
                if (analyzed.HasType && isIterator)
                {
                    analyzed = TypeWithAnnotations.Create(((NamedTypeSymbol)type.Type).ConstructedFrom.Construct([analyzed]));
                }

                if (analyzed.HasType && (isIterator || method.IsAsync))
                {
                    // The sequence or task itself is never null, as a written 'IEnumerable<T>' or 'Task<T>' would say.
                    analyzed = TypeWithAnnotations.Create(analyzed.Type, NullableAnnotation.NotAnnotated);
                }

                // The analysis only adds annotations to the type found without it.
                if (analyzed.HasType && analyzed.Type.Equals(type.Type, TypeCompareKind.AllNullableIgnoreOptions))
                {
                    result = analyzed;
                }
            }

            returnTypes.Free();
            return result;
        }

        /// <summary>
        /// Collects the returned values of a block body, as <see cref="BoundLambda.BlockReturns"/> does for a lambda, or
        /// the yielded values of an iterator. Lambdas and local functions in the body have their own returns.
        /// </summary>
        private sealed class SharperReturnsCollector : BoundTreeWalker
        {
            private readonly ArrayBuilder<(BoundReturnStatement, TypeWithAnnotations)> _builder;
            private readonly bool _isIterator;
            private bool _hasErrors;

            private SharperReturnsCollector(ArrayBuilder<(BoundReturnStatement, TypeWithAnnotations)> builder, bool isIterator)
            {
                _builder = builder;
                _isIterator = isIterator;
            }

            /// <returns>True if a returned value has errors, or is returned by reference.</returns>
            internal static bool Collect(ArrayBuilder<(BoundReturnStatement, TypeWithAnnotations)> builder, BoundBlock block, bool isIterator)
            {
                var collector = new SharperReturnsCollector(builder, isIterator);
                collector.Visit(block);
                return collector._hasErrors;
            }

            public override BoundNode? Visit(BoundNode? node)
            {
                return node is BoundExpression ? null : base.Visit(node);
            }

            protected override BoundNode VisitExpressionOrPatternWithoutStackGuard(BoundNode node)
            {
                throw ExceptionUtilities.Unreachable();
            }

            public override BoundNode? VisitLocalFunctionStatement(BoundLocalFunctionStatement node)
            {
                return null;
            }

            public override BoundNode? VisitReturnStatement(BoundReturnStatement node)
            {
                if (!_isIterator)
                {
                    // In an iterator, a return statement is an error that the body reports.
                    _hasErrors |= node.RefKind != RefKind.None || node.ExpressionOpt?.HasAnyErrors == true;
                    TypeSymbol? type = node.ExpressionOpt is { } expression ? expression.Type?.SetUnknownNullabilityForReferenceTypes() : BoundLambda.NoReturnExpression;
                    _builder.Add((node, TypeWithAnnotations.Create(type)));
                }

                return null;
            }

            public override BoundNode? VisitYieldReturnStatement(BoundYieldReturnStatement node)
            {
                _hasErrors |= node.Expression.HasAnyErrors;
                AddValue(node.Syntax, node.Expression);
                return null;
            }

            public override BoundNode? VisitYieldReturnSpreadStatement(BoundYieldReturnSpreadStatement node)
            {
                // Each element of the spread is a yielded value of the element type of the spread sequence.
                if (node.HasErrors || node.ElementPlaceholder is null)
                {
                    _hasErrors = true;
                }
                else
                {
                    AddValue(node.Syntax, node.ElementPlaceholder);
                }

                return null;
            }

            private void AddValue(SyntaxNode syntax, BoundExpression value)
            {
                _builder.Add((new BoundReturnStatement(syntax, RefKind.None, value, @checked: false), TypeWithAnnotations.Create(value.Type?.SetUnknownNullabilityForReferenceTypes())));
            }
        }

        /// <summary>
        /// Binds <c>yield return ..e</c> while the element type of the iterator is being inferred: a spread, if
        /// <c>e</c> is a sequence, whose elements keep the element type of <c>e</c>. Otherwise it is the value
        /// <c>..e</c>, a range, as in C#.
        /// </summary>
        private BoundStatement BindSharperInferredYieldReturnSpread(YieldStatementSyntax node, ExpressionSyntax spreadOperand, BindingDiagnosticBag diagnostics)
        {
            var spreadDiagnostics = BindingDiagnosticBag.GetInstance(withDiagnostics: true, withDependencies: diagnostics.AccumulatesDependencies);
            BoundYieldReturnSpreadStatement spread = BindYieldReturnSpread(node, spreadOperand, elementType: null, spreadDiagnostics);
            if (!spread.HasErrors && !spreadDiagnostics.HasAnyErrors())
            {
                diagnostics.AddRangeAndFree(spreadDiagnostics);
                return spread;
            }

            spreadDiagnostics.Free();
            return new BoundYieldReturnStatement(node, BindYieldReturnArgument(node, elementType: null, diagnostics));
        }
    }
}
