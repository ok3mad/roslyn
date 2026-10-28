// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    public sealed class SharperTests : CSharpTestBase
    {
        [Fact]
        public void Gating_DisabledByDefault()
        {
            Assert.False(TestOptions.Regular.IsFeatureEnabled(MessageID.IDS_FeatureSharper));
            Assert.False(TestOptions.RegularPreview.IsFeatureEnabled(MessageID.IDS_FeatureSharper));
        }

        [Fact]
        public void Gating_EnabledByFeatureFlag()
        {
            var options = TestOptions.Regular.WithFeatures([new KeyValuePair<string, string>(Feature.Sharper, "true")]);
            Assert.True(options.IsFeatureEnabled(MessageID.IDS_FeatureSharper));
            Assert.True(TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper).IsFeatureEnabled(MessageID.IDS_FeatureSharper));
        }

        [Fact]
        public void Gating_DisabledDiagnostic()
        {
            var info = MessageID.IDS_FeatureSharper.GetFeatureAvailabilityDiagnosticInfo(TestOptions.RegularPreview);
            Assert.NotNull(info);
            Assert.Equal(ErrorCode.ERR_FeatureIsExperimental, (ErrorCode)info.Code);
            Assert.Equal("Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.", info.GetMessage());

            Assert.Null(MessageID.IDS_FeatureSharper.GetFeatureAvailabilityDiagnosticInfo(TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper)));
        }

        private static readonly CSharpParseOptions s_sharperOptions = TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper);

        [Fact]
        public void Gating_PerTree()
        {
            // A compilation can mix Sharper trees (.cx files) and C# trees; other features must still agree.
            var source = """
                class C
                {
                    static int M()
                    {
                        val x = 1;
                        return x;
                    }
                }
                """;
            var csharpTree = SyntaxFactory.ParseSyntaxTree(source, TestOptions.Regular, path: "a.cs");
            var sharperTree = SyntaxFactory.ParseSyntaxTree(source.Replace("class C", "class D"), s_sharperOptions, path: "b.cx");

            var comp = CreateCompilation([csharpTree, sharperTree]);
            Assert.Null(comp.Feature(Feature.Sharper));
            Assert.All(comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), d => Assert.Same(csharpTree, d.Location.SourceTree));
            Assert.NotEmpty(comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

            var otherTree = SyntaxFactory.ParseSyntaxTree("class E { }", s_sharperOptions.WithFeatures([new KeyValuePair<string, string>(Feature.Sharper, "true"), new KeyValuePair<string, string>(Feature.Test, "true")]));
            Assert.Throws<System.ArgumentException>(() => CreateCompilation([csharpTree, otherTree]));
        }

        [Fact]
        public void YieldReturnSpread_Basic()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in Spread(new[] { 1, 2, 3 }))
                    Console.Write(x);

                static IEnumerable<int> Spread(IEnumerable<int> xs)
                {
                    yield return 0;
                    yield return ..xs;
                    yield return 4;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "01234" : null).VerifyDiagnostics();
        }

        [Theory]
        [CombinatorialData]
        public void YieldReturnSpread_ExistingRangeMeaningIsKept(bool sharper)
        {
            // In an iterator of Range, `..i` is already a valid range expression in C#.
            // Sharper must keep that meaning; the spread only applies where C# reports an error.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var r in Ranges(2))
                    Console.Write(r);

                static IEnumerable<Range> Ranges(int i)
                {
                    yield return ..i;
                }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0..2" : null).VerifyDiagnostics();
        }

        [Theory]
        [CombinatorialData]
        public void YieldReturnSpread_ExistingRangeMeaningIsKept_ObjectElement(bool sharper)
        {
            // In an iterator of object, `..i` is a valid range that gets boxed, so it stays a range.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var r in Ranges(2))
                    Console.Write(r);

                static IEnumerable<object> Ranges(int i)
                {
                    yield return ..i;
                }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0..2" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_ObjectElement()
        {
            // In an iterator of object, `..xs` is not a valid range, so it spreads,
            // and each element is boxed rather than the sequence being yielded as one object.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in Spread(new[] { 1, 2, 3 }))
                    Console.Write(x);

                static IEnumerable<object> Spread(IEnumerable<int> xs)
                {
                    yield return ..xs;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_FeatureDisabled()
        {
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(IEnumerable<int> xs)
                    {
                        yield return ..xs;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,24): error CS0029: Cannot implicitly convert type 'System.Collections.Generic.IEnumerable<int>' to 'System.Index'
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "xs").WithArguments("System.Collections.Generic.IEnumerable<int>", "System.Index").WithLocation(7, 24));
        }

        [Fact]
        public void YieldReturnSpread_NotEnumerable()
        {
            // Neither a valid range nor a valid spread: the spread error is reported.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread()
                    {
                        yield return ..5;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,24): error CS9212: Spread operator '..' cannot operate on variables of type 'int' because 'int' does not contain a public instance or extension definition for 'GetEnumerator'
                //         yield return ..5;
                Diagnostic(ErrorCode.ERR_SpreadMissingMember, "5").WithArguments("int", "GetEnumerator").WithLocation(7, 24));
        }

        [Fact]
        public void YieldReturnSpread_ElementConversionIsImplicit()
        {
            // Unlike a foreach iteration variable, the elements are not cast to the iterator element type.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(IEnumerable<object> xs)
                    {
                        yield return ..xs;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,24): error CS0266: Cannot implicitly convert type 'object' to 'int'. An explicit conversion exists (are you missing a cast?)
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_NoImplicitConvCast, "xs").WithArguments("object", "int").WithLocation(7, 24));
        }

        [Fact]
        public void YieldReturnSpread_InFinally()
        {
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(IEnumerable<int> xs)
                    {
                        try { }
                        finally
                        {
                            yield return ..xs;
                        }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (10,13): error CS1625: Cannot yield in the body of a finally clause
                //             yield return ..xs;
                Diagnostic(ErrorCode.ERR_BadYieldInFinally, "yield").WithLocation(10, 13));
        }

        [Fact]
        public void YieldReturnSpread_EnumeratorIsDisposed()
        {
            // A non-array source goes through GetEnumerator/MoveNext/Current inside try/finally. The source's
            // enumerator is disposed both when it is exhausted and when the consumer stops early.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                Console.Write(string.Join(",", Spread(Source())));
                Console.Write(" | ");
                Console.Write(string.Join(",", Spread(Source()).Take(2)));

                static IEnumerable<int> Spread(IEnumerable<int> xs)
                {
                    yield return 0;
                    yield return ..xs;
                    yield return 4;
                }

                static IEnumerable<int> Source()
                {
                    try
                    {
                        yield return 1;
                        yield return 2;
                        yield return 3;
                    }
                    finally
                    {
                        Console.Write("disposed ");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "disposed 0,1,2,3,4 | disposed 0,1" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_String()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var c in Spread("abc"))
                    Console.Write(c);

                static IEnumerable<char> Spread(string s)
                {
                    yield return ..s;
                    yield return '!';
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "abc!" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_RecursiveTreeWalk()
        {
            var source = """
                #nullable enable
                using System;
                using System.Collections.Generic;

                var tree = new Node(new Node(null, 1, null), 2, new Node(new Node(null, 3, null), 4, null));
                Console.Write(string.Join(",", Walk(tree)));

                static IEnumerable<int> Walk(Node? node)
                {
                    if (node is null)
                        yield break;
                    yield return ..Walk(node.Left);
                    yield return node.Value;
                    yield return ..Walk(node.Right);
                }

                record Node(Node? Left, int Value, Node? Right);
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1,2,3,4" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_Nullable_MaybeNullElements()
        {
            // As for `yield return x`, yielding a maybe-null element into a non-nullable sequence is a warning.
            var source = """
                #nullable enable
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<string> Spread(IEnumerable<string?> xs)
                    {
                        yield return ..xs;
                    }

                    static IEnumerable<string> Single(string? x)
                    {
                        yield return x;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,24): warning CS8603: Possible null reference return.
                //         yield return ..xs;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "xs").WithLocation(8, 24),
                // (13,22): warning CS8603: Possible null reference return.
                //         yield return x;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "x").WithLocation(13, 22));
        }

        [Fact]
        public void YieldReturnSpread_Nullable_MaybeNullCollection()
        {
            // As for `foreach (var x in xs)`, spreading a maybe-null collection is a warning.
            var source = """
                #nullable enable
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<string> Spread(IEnumerable<string>? xs)
                    {
                        yield return ..xs;
                    }

                    static IEnumerable<string> ForEach(IEnumerable<string>? xs)
                    {
                        foreach (var x in xs)
                            yield return x;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,24): warning CS8602: Dereference of a possibly null reference.
                //         yield return ..xs;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "xs").WithLocation(8, 24),
                // (13,27): warning CS8602: Dereference of a possibly null reference.
                //         foreach (var x in xs)
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "xs").WithLocation(13, 27));
        }

        [Fact]
        public void YieldReturnSpread_Nullable_NoWarnings()
        {
            var source = """
                #nullable enable
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<string?> Spread(IEnumerable<string> xs, IEnumerable<string?> ys)
                    {
                        yield return ..xs;
                        yield return ..ys;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_IOperation()
        {
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(IEnumerable<int> xs)
                    {
                        /*<bind>*/yield return ..xs;/*</bind>*/
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IOperation:  (OperationKind.None, Type: null) (Syntax: 'yield return ..xs;')
                  Children(1):
                      IConversionOperation (TryCast: False, Unchecked) (OperationKind.Conversion, Type: System.Collections.Generic.IEnumerable<System.Int32>, IsImplicit) (Syntax: 'xs')
                        Conversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                        Operand:
                          IParameterReferenceOperation: xs (OperationKind.ParameterReference, Type: System.Collections.Generic.IEnumerable<System.Int32>) (Syntax: 'xs')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<YieldStatementSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void YieldReturnSpread_FlowGraph()
        {
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(IEnumerable<int> xs)
                    /*<bind>*/{
                        yield return ..xs;
                    }/*</bind>*/
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedFlowGraph = """
                Block[B0] - Entry
                    Statements (0)
                    Next (Regular) Block[B1]
                Block[B1] - Block
                    Predecessors: [B0]
                    Statements (2)
                        IConversionOperation (TryCast: False, Unchecked) (OperationKind.Conversion, Type: System.Collections.Generic.IEnumerable<System.Int32>, IsImplicit) (Syntax: 'xs')
                          Conversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                            (Identity)
                          Operand:
                            IParameterReferenceOperation: xs (OperationKind.ParameterReference, Type: System.Collections.Generic.IEnumerable<System.Int32>) (Syntax: 'xs')
                        IOperation:  (OperationKind.None, Type: null) (Syntax: 'yield return ..xs;')
                    Next (Regular) Block[B2]
                Block[B2] - Exit
                    Predecessors: [B1]
                    Statements (0)
                """;
            VerifyFlowGraphAndDiagnosticsForTest<BlockSyntax>(comp, expectedFlowGraph, []);
        }

        [Fact]
        public void YieldReturnSpread_RegionAnalysis()
        {
            // The IDE's extract method uses region analysis. Like a yield return, the spread is not an exit point.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(IEnumerable<int> xs)
                    {
                        yield return ..xs;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var statement = tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>().Single();

            var controlFlow = model.AnalyzeControlFlow(statement);
            Assert.True(controlFlow.Succeeded);
            Assert.Empty(controlFlow.ExitPoints);
            Assert.True(controlFlow.EndPointIsReachable);

            var dataFlow = model.AnalyzeDataFlow(statement);
            Assert.True(dataFlow.Succeeded);
            Assert.Equal("xs", string.Join(", ", dataFlow.ReadInside.Select(s => s.Name)));
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_SyncSource()
        {
            // A synchronous source is spread in an async iterator as by the C# twin, `foreach (var x in xs) yield return x;`.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                await foreach (var x in Spread([1, 2], new List<int> { 3, 4 }, Seq()))
                    Console.Write(x);
                Console.Write(";");
                await foreach (var x in CSharp([1, 2], new List<int> { 3, 4 }, Seq()))
                    Console.Write(x);

                static async IAsyncEnumerable<int> Spread(int[] array, List<int> list, int[..] seq)
                {
                    await Task.Yield();
                    yield return ..array;
                    await Task.Yield();
                    yield return ..list;
                    yield return ..seq;
                    yield return ..new[,] { { 7, 8 } };
                    yield return ..new Pattern();
                }

                static async IAsyncEnumerable<int> CSharp(int[] array, List<int> list, IEnumerable<int> seq)
                {
                    await Task.Yield();
                    foreach (var x in array) yield return x;
                    await Task.Yield();
                    foreach (var x in list) yield return x;
                    foreach (var x in seq) yield return x;
                    foreach (var x in new[,] { { 7, 8 } }) yield return x;
                    foreach (var x in new Pattern()) yield return x;
                }

                static IEnumerable<int> Seq()
                {
                    yield return 5;
                    yield return 6;
                }

                // A pattern-based enumerator that is not disposable, so the spread has no try/finally.
                class Pattern
                {
                    public Enumerator GetEnumerator() => new Enumerator();

                    public class Enumerator
                    {
                        private bool _done;
                        public int Current => 9;
                        public bool MoveNext() => !_done && (_done = true);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123456789;123456789" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_LocalFunctionAndString()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                await foreach (var c in Chars("ab"))
                    Console.Write(c);

                static async IAsyncEnumerable<char> Chars(string s)
                {
                    await foreach (var c in Inner())
                        yield return c;

                    async IAsyncEnumerable<char> Inner()
                    {
                        await Task.Yield();
                        yield return ..s;
                        yield return '!';
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ab!" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_EnumeratorIsDisposed()
        {
            // The source's enumerator is disposed when it is exhausted, when the consumer breaks out of `await foreach`,
            // and when the consumer calls DisposeAsync directly while the iterator is suspended inside the spread.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                await foreach (var x in Spread(Source()))
                    Console.Write(x);
                Console.Write(" | ");
                await foreach (var x in Spread(Source()))
                {
                    Console.Write(x);
                    if (x == 2)
                        break;
                }
                Console.Write(" | ");
                var e = Spread(Source()).GetAsyncEnumerator();
                await e.MoveNextAsync();
                await e.MoveNextAsync();
                Console.Write(e.Current);
                await e.DisposeAsync();
                Console.Write(" | ");
                await foreach (var x in Spread(Source()))
                {
                    Console.Write(x);
                    if (x == 4)
                        break;
                }

                static async IAsyncEnumerable<int> Spread(IEnumerable<int> xs)
                {
                    yield return 0;
                    await Task.Yield();
                    yield return ..xs;
                    await Task.Yield();
                    yield return 4;
                }

                static IEnumerable<int> Source()
                {
                    try
                    {
                        yield return 1;
                        yield return 2;
                        yield return 3;
                    }
                    finally
                    {
                        Console.Write(" disposed ");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0123 disposed 4 | 012 disposed  | 1 disposed  | 0123 disposed 4" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_InTryFinallyWithAwait()
        {
            // A spread inside the iterator's own try/finally and `await using`, whose cleanup awaits: on an early stop
            // and at the end, the source is disposed first, then the resource, then the outer finally runs.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                await foreach (var x in Spread(Source()))
                {
                    Console.Write(x);
                    if (x == 1)
                        break;
                }
                Console.Write(" | ");
                await foreach (var x in Spread(Source()))
                    Console.Write(x);

                static async IAsyncEnumerable<int> Spread(IEnumerable<int> xs)
                {
                    try
                    {
                        await using (var r = new Resource())
                        {
                            yield return ..xs;
                        }
                    }
                    finally
                    {
                        await Task.Yield();
                        Console.Write(" finally");
                    }
                }

                static IEnumerable<int> Source()
                {
                    try
                    {
                        yield return 1;
                        yield return 2;
                    }
                    finally
                    {
                        Console.Write(" disposed");
                    }
                }

                class Resource : IAsyncDisposable
                {
                    public async ValueTask DisposeAsync()
                    {
                        await Task.Yield();
                        Console.Write(" resource");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1 disposed resource finally | 12 disposed resource finally" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_Cancellation()
        {
            // The spread has no await, so it never observes the token itself; a source that checks it stops the spread
            // with an exception, and the source's enumerator is still disposed.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Runtime.CompilerServices;
                using System.Threading;
                using System.Threading.Tasks;

                var cts = new CancellationTokenSource();
                try
                {
                    await foreach (var x in Spread().WithCancellation(cts.Token))
                    {
                        Console.Write(x);
                        if (x == 2)
                            cts.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.Write(" canceled");
                }

                static async IAsyncEnumerable<int> Spread([EnumeratorCancellation] CancellationToken token = default)
                {
                    await Task.Yield();
                    yield return ..Source(token);
                    await Task.Delay(1, token);
                    yield return 9;
                }

                static IEnumerable<int> Source(CancellationToken token)
                {
                    try
                    {
                        for (int i = 1; i <= 4; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            yield return i;
                        }
                    }
                    finally
                    {
                        Console.Write(" disposed");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "12 disposed canceled" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_Exception()
        {
            // An exception from the source's MoveNext surfaces from MoveNextAsync, after the source is disposed.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                try
                {
                    await foreach (var x in Spread(Source()))
                        Console.Write(x);
                }
                catch (InvalidOperationException e)
                {
                    Console.Write(" " + e.Message);
                }

                static async IAsyncEnumerable<int> Spread(IEnumerable<int> xs)
                {
                    await Task.Yield();
                    yield return ..xs;
                    yield return 9;
                }

                static IEnumerable<int> Source()
                {
                    try
                    {
                        yield return 1;
                        throw new InvalidOperationException("boom");
                    }
                    finally
                    {
                        Console.Write(" disposed");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1 disposed boom" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_SyncAndAsyncSource()
        {
            // A source that is both synchronous and asynchronous is enumerated synchronously, as by a foreach
            // without 'await'.
            var source = """
                using System;
                using System.Collections;
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;

                await foreach (var x in Spread(new Both()))
                    Console.Write(x);
                Console.Write(";");
                await foreach (var x in CSharp(new Both()))
                    Console.Write(x);

                static async IAsyncEnumerable<string> Spread(Both both)
                {
                    await Task.Yield();
                    yield return ..both;
                }

                static async IAsyncEnumerable<string> CSharp(Both both)
                {
                    await Task.Yield();
                    foreach (var x in both) yield return x;
                }

                class Both : IEnumerable<string>, IAsyncEnumerable<string>
                {
                    public IEnumerator<string> GetEnumerator() => new List<string> { "sync" }.GetEnumerator();
                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                    public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken token = default) => throw null;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "sync;sync" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_AsyncSource()
        {
            // An asynchronous source is never spread: it would need an `await foreach`, an 'await' the user never wrote.
            // A source that is not enumerable at all reports the usual spread error, and outside an async iterator an
            // asynchronous source reports the usual foreach error.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static async IAsyncEnumerable<int> AsyncIterator(IAsyncEnumerable<int> xs)
                    {
                        yield return ..xs;
                    }

                    static async IAsyncEnumerable<int> AsyncIteratorCSharp(IAsyncEnumerable<int> xs)
                    {
                        await foreach (var x in xs) yield return x;
                    }

                    static async IAsyncEnumerable<int> AsyncIteratorPattern(Pattern xs)
                    {
                        yield return ..xs;
                    }

                    static async IAsyncEnumerable<int> AsyncIteratorNotEnumerable()
                    {
                        yield return ..5;
                    }

                    static IEnumerable<int> SyncIterator(IAsyncEnumerable<int> xs)
                    {
                        yield return ..xs;
                    }

                    static async IAsyncEnumerable<int> AsyncIteratorDerived(Derived xs)
                    {
                        yield return ..xs;
                    }
                }

                class Pattern
                {
                    public IAsyncEnumerator<int> GetAsyncEnumerator() => null;
                }

                abstract class Derived : IAsyncEnumerable<int>
                {
                    public abstract IAsyncEnumerator<int> GetAsyncEnumerator(System.Threading.CancellationToken token);
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (7,24): error CS20001: A spread 'yield return ..' cannot enumerate the asynchronous sequence 'IAsyncEnumerable<int>', because that would need an 'await' that is not written. Use 'await foreach' with 'yield return' instead.
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_SharperSpreadOfAsyncSequence, "xs").WithArguments("System.Collections.Generic.IAsyncEnumerable<int>").WithLocation(7, 24),
                // (17,24): error CS20001: A spread 'yield return ..' cannot enumerate the asynchronous sequence 'Pattern', because that would need an 'await' that is not written. Use 'await foreach' with 'yield return' instead.
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_SharperSpreadOfAsyncSequence, "xs").WithArguments("Pattern").WithLocation(17, 24),
                // (22,24): error CS9212: Spread operator '..' cannot operate on variables of type 'int' because 'int' does not contain a public instance or extension definition for 'GetEnumerator'
                //         yield return ..5;
                Diagnostic(ErrorCode.ERR_SpreadMissingMember, "5").WithArguments("int", "GetEnumerator").WithLocation(22, 24),
                // (27,24): error CS8414: foreach statement cannot operate on variables of type 'IAsyncEnumerable<int>' because 'IAsyncEnumerable<int>' does not contain a public instance or extension definition for 'GetEnumerator'. Did you mean 'await foreach' rather than 'foreach'?
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_ForEachMissingMemberWrongAsync, "xs").WithArguments("System.Collections.Generic.IAsyncEnumerable<int>", "GetEnumerator").WithLocation(27, 24),
                // (32,24): error CS20001: A spread 'yield return ..' cannot enumerate the asynchronous sequence 'Derived', because that would need an 'await' that is not written. Use 'await foreach' with 'yield return' instead.
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_SharperSpreadOfAsyncSequence, "xs").WithArguments("Derived").WithLocation(32, 24));
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_RefStructSource()
        {
            // As in a synchronous iterator, a span's enumerator cannot live across the yields.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                class C
                {
                    static async IAsyncEnumerable<int> Spread(int[] xs)
                    {
                        await Task.Yield();
                        yield return ..xs.AsSpan();
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (10,24): error CS20012: A spread 'yield return ..' cannot enumerate 'Span<int>' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..xs.AsSpan();
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "xs.AsSpan()").WithArguments("System.Span<int>").WithLocation(10, 24));
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_Nullable()
        {
            // The same warnings as in a synchronous iterator.
            var source = """
                #nullable enable
                using System.Collections.Generic;

                class C
                {
                    static async IAsyncEnumerable<string> Spread(IEnumerable<string?> xs, IEnumerable<string>? ys)
                    {
                        yield return ..xs;
                        yield return ..ys;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (8,24): warning CS8603: Possible null reference return.
                //         yield return ..xs;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "xs").WithLocation(8, 24),
                // (9,24): warning CS8602: Dereference of a possibly null reference.
                //         yield return ..ys;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "ys").WithLocation(9, 24));
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_RangeIsKept()
        {
            // Valid C# keeps its meaning in an async iterator too.
            var source = """
                using System;
                using System.Collections.Generic;

                await foreach (var r in Ranges(2))
                    Console.Write(r);

                static async IAsyncEnumerable<Range> Ranges(int i)
                {
                    yield return ..i;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0..2" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource()
        {
            // A span's enumerator cannot live across the yields. The spread says so, instead of CS4007 about
            // Span<int>.Enumerator, which the user never wrote.
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(int[] xs)
                    {
                        yield return ..xs.AsSpan();
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (8,24): error CS20012: A spread 'yield return ..' cannot enumerate 'Span<int>' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..xs.AsSpan();
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "xs.AsSpan()").WithArguments("System.Span<int>").WithLocation(8, 24));
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource_CSharp()
        {
            // The C# the spread stands for.
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(int[] xs)
                    {
                        foreach (var x in xs.AsSpan())
                            yield return x;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (8,9): error CS4007: Instance of type 'System.Span<int>' cannot be preserved across 'await' or 'yield' boundary.
                //         foreach (var x in xs.AsSpan())
                Diagnostic(ErrorCode.ERR_ByRefTypeAndAwait, @"foreach (var x in xs.AsSpan())
            yield return x;").WithArguments("System.Span<int>").WithLocation(8, 9));
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource_ReadOnlySpan()
        {
            // The element conversion (string to int) would fail too; only the spread error is reported.
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(string s, int[] xs)
                    {
                        yield return ..s.AsSpan();
                        yield return ..new ReadOnlySpan<int>(xs);
                        yield return ..new ReadOnlySpan<string>([s]);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (8,24): error CS20012: A spread 'yield return ..' cannot enumerate 'ReadOnlySpan<char>' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..s.AsSpan();
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "s.AsSpan()").WithArguments("System.ReadOnlySpan<char>").WithLocation(8, 24),
                // (9,24): error CS20012: A spread 'yield return ..' cannot enumerate 'ReadOnlySpan<int>' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..new ReadOnlySpan<int>(xs);
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "new ReadOnlySpan<int>(xs)").WithArguments("System.ReadOnlySpan<int>").WithLocation(9, 24),
                // (10,24): error CS20012: A spread 'yield return ..' cannot enumerate 'ReadOnlySpan<string>' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..new ReadOnlySpan<string>([s]);
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "new ReadOnlySpan<string>([s])").WithArguments("System.ReadOnlySpan<string>").WithLocation(10, 24));
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource_InlineArray()
        {
            // An inline array is not a ref struct, but it is enumerated through a span.
            var source = """
                using System.Collections.Generic;
                using System.Runtime.CompilerServices;

                class C
                {
                    static IEnumerable<int> Spread(Buffer b)
                    {
                        yield return ..b;
                    }
                }

                [InlineArray(3)]
                struct Buffer
                {
                    private int _element0;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (8,24): error CS20012: A spread 'yield return ..' cannot enumerate 'Buffer' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..b;
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "b").WithArguments("Buffer").WithLocation(8, 24));
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource_PatternEnumerator()
        {
            // A ref struct collection whose pattern-based GetEnumerator returns a ref struct.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread()
                    {
                        yield return ..new R();
                    }
                }

                ref struct R
                {
                    public Enumerator GetEnumerator() => default;

                    public ref struct Enumerator
                    {
                        public int Current => 0;
                        public bool MoveNext() => false;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (7,24): error CS20012: A spread 'yield return ..' cannot enumerate 'R' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..new R();
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "new R()").WithArguments("R").WithLocation(7, 24));
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource_ClassEnumerator()
        {
            // A ref struct collection whose enumerator is a class is fine: only the enumerator lives across the yields.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in C.Spread())
                    Console.Write(x);

                class C
                {
                    public static IEnumerable<int> Spread()
                    {
                        yield return ..new R();
                    }
                }

                ref struct R
                {
                    public IEnumerator<int> GetEnumerator() => new List<int> { 1, 2, 3 }.GetEnumerator();
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_RefStructEnumerator_ClassSource()
        {
            // A class collection whose enumerator is a ref struct is rejected too.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<int> Spread(S s)
                    {
                        yield return ..s;
                    }
                }

                class S
                {
                    public Enumerator GetEnumerator() => default;

                    public ref struct Enumerator
                    {
                        public int Current => 0;
                        public bool MoveNext() => false;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (7,24): error CS20012: A spread 'yield return ..' cannot enumerate 'S' because its enumerator is a ref struct, which cannot be kept across 'yield return'. Spread an array or a sequence instead.
                //         yield return ..s;
                Diagnostic(ErrorCode.ERR_SharperSpreadRefStructEnumerator, "s").WithArguments("S").WithLocation(7, 24));
        }

        [Fact]
        public void YieldReturnSpread_RefStructSource_RangeWins()
        {
            // A ref struct that converts to Index makes `..r` a valid C# range in an iterator of Range. C# wins: no error.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in C.Spread())
                    Console.Write(x);

                class C
                {
                    public static IEnumerable<Range> Spread()
                    {
                        yield return ..new R();
                    }
                }

                ref struct R
                {
                    public static implicit operator Index(R r) => 2;
                    public Enumerator GetEnumerator() => default;

                    public ref struct Enumerator
                    {
                        public int Current => 0;
                        public bool MoveNext() => false;
                    }
                }
                """;
            // ILVerify: Return type is ByRef, TypedReference, ArgHandle, or ArgIterator (R.GetEnumerator returns a ref struct).
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, verify: Verification.FailsILVerify, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0..2" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_Dynamic_ObjectElement()
        {
            // `..d` converts d to Index at runtime, so it is valid C# and stays a range, with a warning.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static IEnumerable<object> Spread(dynamic d)
                    {
                        yield return ..d;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,24): warning CS20002: '..d' is a range here, not a spread, because 'd' is dynamic. To yield each element, convert 'd' to a sequence type first.
                //         yield return ..d;
                Diagnostic(ErrorCode.WRN_SharperDynamicRangeNotSpread, "d").WithArguments("d").WithLocation(7, 24));
        }

        [Fact]
        public void YieldReturnSpread_Dynamic_IntElement()
        {
            // A Range is not an int, so `..d` is not valid C# here and spreads, enumerating d dynamically.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in Spread(new[] { 1, 2, 3 }))
                    Console.Write(x);

                static IEnumerable<int> Spread(dynamic d)
                {
                    yield return ..d;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Val_Basic()
        {
            var source = """
                using System;

                val x = 1;
                val s = "a";
                Func<int> f = () => x + 1;
                Console.Write(x);
                Console.Write(s);
                Console.Write(f());
                M();

                static void M()
                {
                    val y = 3;
                    Console.Write(y);
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1a23" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Val_FeatureDisabled()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        val x = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,9): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         val x = 1;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(5, 9));
        }

        [Fact]
        public void Val_Writes()
        {
            // Each write to the val local next to the same write to its var twin, which is allowed.
            var source = """
                using System;

                class C
                {
                    static void Ref(ref int i) { }
                    static void Out(out int i) { i = 0; }
                    static void In(in int i) { }

                    void M()
                    {
                        val x = 1;
                        var y = 1;
                        x = 2;
                        y = 2;
                        x += 1;
                        y += 1;
                        x++;
                        y++;
                        --x;
                        --y;
                        Ref(ref x);
                        Ref(ref y);
                        Out(out x);
                        Out(out y);
                        In(in x);
                        In(in y);
                        ref int r = ref x;
                        ref int q = ref y;
                        ref readonly int rr = ref x;
                        Action a = () => x = 3;
                        Action b = () => y = 3;
                        void Local() { x = 4; }
                        void Local2() { y = 4; }

                        val s = (string)null;
                        var t = (string)null;
                        s ??= "a";
                        t ??= "a";
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,9): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 9),
                // (15,9): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         x += 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(15, 9),
                // (17,9): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(17, 9),
                // (19,11): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         --x;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(19, 11),
                // (21,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         Ref(ref x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(21, 17),
                // (23,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         Out(out x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(23, 17),
                // (27,25): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         ref int r = ref x;
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(27, 25),
                // (30,26): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         Action a = () => x = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(30, 26),
                // (32,14): warning CS8321: The local function 'Local' is declared but never used
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local").WithArguments("Local").WithLocation(32, 14),
                // (32,24): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(32, 24),
                // (33,14): warning CS8321: The local function 'Local2' is declared but never used
                //         void Local2() { y = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local2").WithArguments("Local2").WithLocation(33, 14),
                // (37,9): error CS1656: Cannot assign to 's' because it is a 'val variable'
                //         s ??= "a";
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "val variable").WithLocation(37, 9));
        }

        [Fact]
        public void Val_StructFieldAssignment()
        {
            // The fields of a struct are part of the variable, so assigning one reassigns part of the val.
            var source = """
                struct S
                {
                    public int F;
                    public int P { get; set; }
                }

                class C
                {
                    void M()
                    {
                        val v = new S();
                        var w = new S();
                        v.F = 1;
                        w.F = 1;
                        v.P = 1;
                        w.P = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,9): error CS1654: Cannot modify members of 'v' because it is a 'val variable'
                //         v.F = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.F").WithArguments("v", "val variable").WithLocation(13, 9),
                // (15,9): error CS1654: Cannot modify members of 'v' because it is a 'val variable'
                //         v.P = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.P").WithArguments("v", "val variable").WithLocation(15, 9));
        }

        [Fact]
        public void Val_StructMemberCallOnCopy()
        {
            // A non-readonly member called on a val struct runs on a copy and warns, so the val is never changed.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                val v = new S();
                v.Mutate();
                Console.Write(v.F);
                _ = v.Getter;
                Console.Write(v.F);
                _ = v[0];
                Console.Write(v.F);
                foreach (var x in v) { }
                Console.Write(v.F);
                var (a, b) = v;
                Console.Write(v.F);
                v.E += () => { };
                Console.Write(v.HasHandler);
                v.ReadOnlyMethod();
                _ = v.Auto;
                _ = v.ToString();
                Action f = () => { v.Mutate(); Console.Write(v.F); };
                f();
                await Task.Yield();
                v.Mutate();
                Console.Write(v.F);
                Console.Write(";");

                var w = new S();
                w.Mutate();
                Console.Write(w.F);
                ref val r = ref w;
                r.Mutate();
                Console.Write(w.F);
                Console.Write(";");
                Generic(new S());

                static void Generic<T>(T t) where T : IMutable
                {
                    val g = t;
                    g.Mutate();
                    Console.Write(g.Get());
                }

                interface IMutable
                {
                    void Mutate();
                    int Get();
                }

                struct S : IMutable
                {
                    public int F;
                    public void Mutate() { F++; }
                    public int Get() => F;
                    public int Getter { get { F++; return F; } }
                    public int this[int i] { get { F++; return F; } }
                    public readonly void ReadOnlyMethod() { }
                    public int Auto { get; set; }
                    public IEnumerator<int> GetEnumerator() { F++; return new List<int>().GetEnumerator(); }
                    public void Deconstruct(out int a, out int b) { F++; a = F; b = F; }
                    public event Action E;
                    public readonly bool HasHandler => E is not null;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "00000False00;12;0" : null).VerifyDiagnostics(
                // (6,1): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(6, 1),
                // (8,5): warning CS20003: Call to non-readonly member 'S.Getter.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v.Getter;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Getter.get", "v").WithLocation(8, 5),
                // (10,5): warning CS20003: Call to non-readonly member 'S.this[int].get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[0];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.this[int].get", "v").WithLocation(10, 5),
                // (12,19): warning CS20003: Call to non-readonly member 'S.GetEnumerator()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // foreach (var x in v) { }
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.GetEnumerator()", "v").WithLocation(12, 19),
                // (14,14): warning CS20003: Call to non-readonly member 'S.Deconstruct(out int, out int)' on val variable 'v' runs on a copy, so 'v' is not changed.
                // var (a, b) = v;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Deconstruct(out int, out int)", "v").WithLocation(14, 14),
                // (16,1): warning CS20003: Call to non-readonly member 'S.E.add' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.E += () => { };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.E.add", "v").WithLocation(16, 1),
                // (21,20): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // Action f = () => { v.Mutate(); Console.Write(v.F); };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(21, 20),
                // (24,1): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(24, 1),
                // (40,5): warning CS20003: Call to non-readonly member 'IMutable.Mutate()' on val variable 'g' runs on a copy, so 'g' is not changed.
                //     g.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "g").WithArguments("IMutable.Mutate()", "g").WithLocation(40, 5),
                // (41,19): warning CS20003: Call to non-readonly member 'IMutable.Get()' on val variable 'g' runs on a copy, so 'g' is not changed.
                //     Console.Write(g.Get());
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "g").WithArguments("IMutable.Get()", "g").WithLocation(41, 19));
        }

        [Fact]
        public void Val_PatternBasedMembersOnCopy()
        {
            // Members found by pattern (await, implicit indexers, list and property patterns, spreads, using) must not change a val either.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Runtime.CompilerServices;

                val v = new S();
                await v;
                Console.Write(v.F);
                _ = v[^1];
                Console.Write(v.F);
                _ = v[1..];
                Console.Write(v.F);
                _ = v is [_, ..];
                Console.Write(v.F);
                _ = v is { P: 0 };
                Console.Write(v.F);
                int[] a = [..v];
                Console.Write(v.F);
                using (v) { }
                Console.Write(v.F);

                struct S : IDisposable
                {
                    public int F;
                    public int Length { get { F++; return 1; } }
                    public int this[int i] { get { F++; return 0; } }
                    public S Slice(int start, int length) { F++; return this; }
                    public int P { get { F++; return 0; } }
                    public Awaiter GetAwaiter() { F++; return default; }
                    public IEnumerator<int> GetEnumerator() { F++; return new List<int>().GetEnumerator(); }
                    public void Dispose() { F++; }
                }

                struct Awaiter : INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void GetResult() { }
                    public void OnCompleted(Action continuation) { }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0000000" : null).VerifyDiagnostics(
                // (6,7): warning CS20003: Call to non-readonly member 'S.GetAwaiter()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // await v;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.GetAwaiter()", "v").WithLocation(6, 7),
                // (8,5): warning CS20003: Call to non-readonly member 'S.Length.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[^1];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Length.get", "v").WithLocation(8, 5),
                // (8,5): warning CS20003: Call to non-readonly member 'S.this[int].get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[^1];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.this[int].get", "v").WithLocation(8, 5),
                // (10,5): warning CS20003: Call to non-readonly member 'S.Length.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[1..];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Length.get", "v").WithLocation(10, 5),
                // (10,5): warning CS20003: Call to non-readonly member 'S.Slice(int, int)' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[1..];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Slice(int, int)", "v").WithLocation(10, 5),
                // (12,5): warning CS20003: Call to non-readonly member 'S.Length.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v is [_, ..];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Length.get", "v").WithLocation(12, 5),
                // (14,5): warning CS20003: Call to non-readonly member 'S.P.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v is { P: 0 };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.P.get", "v").WithLocation(14, 5));
        }

        [Fact]
        public void Val_ExtensionReceivers()
        {
            var source = """
                struct S { public int F; }

                static class E
                {
                    public static void ByRef(this ref S s) { s.F++; }
                    public static void ByIn(this in S s) { }
                    public static void ByValue(this S s) { s.F++; }
                }

                class C
                {
                    void M()
                    {
                        val v = new S();
                        var w = new S();
                        v.ByRef();
                        w.ByRef();
                        v.ByIn();
                        v.ByValue();
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (16,9): error CS1657: Cannot use 'v' as a ref or out value because it is a 'val variable'
                //         v.ByRef();
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "v").WithArguments("v", "val variable").WithLocation(16, 9));
        }

        [Theory]
        [CombinatorialData]
        public void Val_TypeNamedValWins(bool sharper)
        {
            // With a type named val in scope, val keeps its C# meaning.
            var source = """
                class val { }

                class C
                {
                    void M()
                    {
                        val x = new val();
                        x = new val();
                        val y = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7),
                // (9,17): error CS0029: Cannot implicitly convert type 'int' to 'val'
                //         val y = 1;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "1").WithArguments("int", "val").WithLocation(9, 17));
        }

        [Fact]
        public void Val_SameErrorsAsVar()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        val a;
                        var a2;
                        val b = 1, c = 2;
                        var b2 = 1, c2 = 2;
                        val d = null;
                        var d2 = null;
                        const val e = 1;
                        const var e2 = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,13): error CS0818: Implicitly-typed variables must be initialized
                //         val a;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableWithNoInitializer, "a").WithLocation(5, 13),
                // (5,13): warning CS0168: The variable 'a' is declared but never used
                //         val a;
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "a").WithArguments("a").WithLocation(5, 13),
                // (6,13): error CS0818: Implicitly-typed variables must be initialized
                //         var a2;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableWithNoInitializer, "a2").WithLocation(6, 13),
                // (6,13): warning CS0168: The variable 'a2' is declared but never used
                //         var a2;
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "a2").WithArguments("a2").WithLocation(6, 13),
                // (7,9): error CS0819: Implicitly-typed variables cannot have multiple declarators
                //         val b = 1, c = 2;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableMultipleDeclarator, "val b = 1, c = 2").WithLocation(7, 9),
                // (7,13): warning CS0219: The variable 'b' is assigned but its value is never used
                //         val b = 1, c = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "b").WithArguments("b").WithLocation(7, 13),
                // (7,20): warning CS0219: The variable 'c' is assigned but its value is never used
                //         val b = 1, c = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "c").WithArguments("c").WithLocation(7, 20),
                // (8,9): error CS0819: Implicitly-typed variables cannot have multiple declarators
                //         var b2 = 1, c2 = 2;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableMultipleDeclarator, "var b2 = 1, c2 = 2").WithLocation(8, 9),
                // (8,13): warning CS0219: The variable 'b2' is assigned but its value is never used
                //         var b2 = 1, c2 = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "b2").WithArguments("b2").WithLocation(8, 13),
                // (8,21): warning CS0219: The variable 'c2' is assigned but its value is never used
                //         var b2 = 1, c2 = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "c2").WithArguments("c2").WithLocation(8, 21),
                // (9,13): error CS0815: Cannot assign <null> to an implicitly-typed variable
                //         val d = null;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableAssignedBadValue, "d = null").WithArguments("<null>").WithLocation(9, 13),
                // (10,13): error CS0815: Cannot assign <null> to an implicitly-typed variable
                //         var d2 = null;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableAssignedBadValue, "d2 = null").WithArguments("<null>").WithLocation(10, 13),
                // (11,15): error CS0822: Implicitly-typed variables cannot be constant
                //         const val e = 1;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableCannotBeConst, "val e = 1").WithLocation(11, 15),
                // (11,19): warning CS0219: The variable 'e' is assigned but its value is never used
                //         const val e = 1;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "e").WithArguments("e").WithLocation(11, 19),
                // (12,15): error CS0822: Implicitly-typed variables cannot be constant
                //         const var e2 = 1;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableCannotBeConst, "var e2 = 1").WithLocation(12, 15),
                // (12,19): warning CS0219: The variable 'e2' is assigned but its value is never used
                //         const var e2 = 1;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "e2").WithArguments("e2").WithLocation(12, 19));
        }

        [Fact]
        public void Val_RefAndScoped()
        {
            var source = """
                using System;

                class C
                {
                    void M(Span<int> span)
                    {
                        int i = 1, j = 2;
                        ref val r = ref i;
                        ref var r2 = ref i;
                        r = 3;
                        r2 = 3;
                        r = ref j;
                        r2 = ref j;
                        scoped val s = span;
                        scoped var s2 = span;
                        s = default;
                        s2 = default;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (12,9): error CS20006: Cannot ref-assign 'r' because it is a ref val: it always refers to the same variable. Writes through 'r' are allowed.
                //         r = ref j;
                Diagnostic(ErrorCode.ERR_SharperValRefReassignment, "r").WithArguments("r").WithLocation(12, 9),
                // (16,9): error CS1656: Cannot assign to 's' because it is a 'val variable'
                //         s = default;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "val variable").WithLocation(16, 9));
        }

        [Fact]
        public void Val_UsingAndFor()
        {
            var source = """
                using System;

                class D : IDisposable { public void Dispose() { } }

                class C
                {
                    void M()
                    {
                        using val d = new D();
                        using var d2 = new D();
                        d = null;
                        d2 = null;
                        for (val i = 0; i < 3; i++) { }
                        for (var j = 0; j < 3; j++) { }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (11,9): error CS1656: Cannot assign to 'd' because it is a 'val variable'
                //         d = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "d").WithArguments("d", "val variable").WithLocation(11, 9),
                // (12,9): error CS1656: Cannot assign to 'd2' because it is a 'using variable'
                //         d2 = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "d2").WithArguments("d2", "using variable").WithLocation(12, 9),
                // (13,32): error CS1656: Cannot assign to 'i' because it is a 'val variable'
                //         for (val i = 0; i < 3; i++) { }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "i").WithArguments("i", "val variable").WithLocation(13, 32));
        }

        [Fact]
        public void Val_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(string? maybe)
                    {
                        val v = maybe;
                        var w = maybe;
                        _ = v.Length;
                        _ = w.Length;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = v.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "v").WithLocation(8, 13),
                // (9,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = w.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "w").WithLocation(9, 13));
        }

        [Fact]
        public void Val_SemanticModel()
        {
            // The val keyword reports the inferred type, as var does, and the local symbol is an ordinary local.
            var source = """
                class C
                {
                    void M()
                    {
                        val x = 1;
                        var y = 1;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (5,13): warning CS0219: The variable 'x' is assigned but its value is never used
                //         val x = 1;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "x").WithArguments("x").WithLocation(5, 13),
                // (6,13): warning CS0219: The variable 'y' is assigned but its value is never used
                //         var y = 1;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "y").WithArguments("y").WithLocation(6, 13));
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var declarations = tree.GetRoot().DescendantNodes().OfType<VariableDeclarationSyntax>().ToArray();
            foreach (var declaration in declarations)
            {
                Assert.Equal("System.Int32", model.GetTypeInfo(declaration.Type).Type.ToTestDisplayString());
                Assert.Equal("System.Int32", model.GetSymbolInfo(declaration.Type).Symbol.ToTestDisplayString());
                Assert.Null(model.GetAliasInfo((IdentifierNameSyntax)declaration.Type));
            }
            Assert.Equal("System.Int32 x", model.GetDeclaredSymbol(declarations[0].Variables[0]).ToTestDisplayString());
        }

        [Fact]
        public void Val_IOperation()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        /*<bind>*/val x = 1;/*</bind>*/
                        _ = x;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IVariableDeclarationGroupOperation (1 declarations) (OperationKind.VariableDeclarationGroup, Type: null) (Syntax: 'val x = 1;')
                  IVariableDeclarationOperation (1 declarators) (OperationKind.VariableDeclaration, Type: null) (Syntax: 'val x = 1')
                    Declarators:
                        IVariableDeclaratorOperation (Symbol: System.Int32 x) (OperationKind.VariableDeclarator, Type: null) (Syntax: 'x = 1')
                          Initializer:
                            IVariableInitializerOperation (OperationKind.VariableInitializer, Type: null) (Syntax: '= 1')
                              ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                    Initializer:
                      null
                """;
            VerifyOperationTreeAndDiagnosticsForTest<LocalDeclarationStatementSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void ValDesignation_Basic()
        {
            var source = """
                using System;
                
                val (a, b) = (1, "x");
                Console.Write(a);
                Console.Write(b);
                (val c, var d) = (2, 3);
                Console.Write(c + d);
                foreach (val x in new[] { 4, 5 })
                    Console.Write(x);
                foreach (val (k, v) in new[] { (6, "y") })
                    Console.Write($"{k}{v}");
                for (val (i, j) = (7, 8); i < 8; )
                {
                    Console.Write(i + j);
                    break;
                }
                if (int.TryParse("9", out val n))
                    Console.Write(n);
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1x5456y159" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ValDesignation_Writes()
        {
            var source = """
                class C
                {
                    void M((int, int)[] pairs)
                    {
                        val (a, b) = (1, 2);
                        var (a2, b2) = (1, 2);
                        a = 3;
                        a2 = 3;
                        (val c, var d) = (1, 2);
                        c = 3;
                        d = 3;
                        foreach (val x in new[] { 1 }) { x = 2; }
                        foreach (var x2 in new[] { 1 }) { x2 = 2; }
                        foreach (val (k, v) in pairs) { k = 2; }
                        foreach (var (k2, v2) in pairs) { k2 = 2; }
                        int.TryParse("1", out val n);
                        int.TryParse("1", out var n2);
                        n = 2;
                        n2 = 2;
                        (a, b) = (5, 6);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,9): error CS1656: Cannot assign to 'a' because it is a 'val variable'
                //         a = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "a").WithArguments("a", "val variable").WithLocation(7, 9),
                // (10,9): error CS1656: Cannot assign to 'c' because it is a 'val variable'
                //         c = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "c").WithArguments("c", "val variable").WithLocation(10, 9),
                // (12,42): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         foreach (val x in new[] { 1 }) { x = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(12, 42),
                // (13,43): error CS1656: Cannot assign to 'x2' because it is a 'foreach iteration variable'
                //         foreach (var x2 in new[] { 1 }) { x2 = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x2").WithArguments("x2", "foreach iteration variable").WithLocation(13, 43),
                // (14,41): error CS1656: Cannot assign to 'k' because it is a 'val variable'
                //         foreach (val (k, v) in pairs) { k = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "k").WithArguments("k", "val variable").WithLocation(14, 41),
                // (15,43): error CS1656: Cannot assign to 'k2' because it is a 'foreach iteration variable'
                //         foreach (var (k2, v2) in pairs) { k2 = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "k2").WithArguments("k2", "foreach iteration variable").WithLocation(15, 43),
                // (18,9): error CS1656: Cannot assign to 'n' because it is a 'val variable'
                //         n = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "n").WithArguments("n", "val variable").WithLocation(18, 9),
                // (20,10): error CS1656: Cannot assign to 'a' because it is a 'val variable'
                //         (a, b) = (5, 6);
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "a").WithArguments("a", "val variable").WithLocation(20, 10),
                // (20,13): error CS1656: Cannot assign to 'b' because it is a 'val variable'
                //         (a, b) = (5, 6);
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "b").WithArguments("b", "val variable").WithLocation(20, 13));
        }

        [Theory]
        [CombinatorialData]
        public void ValDesignation_FeatureDisabledAndTypeNamedVal(bool sharper, bool typeNamedVal)
        {
            var source = (typeNamedVal ? "class val { }\n" : "") + """
                class C
                {
                    void M(object[] xs)
                    {
                        val (a, b) = (1, 2);
                        foreach (val x in xs) { }
                        int.TryParse("1", out val n);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90);
            // With Sharper on and no type named val, every form is a val declaration.
            if (sharper && !typeNamedVal)
            {
                comp.VerifyDiagnostics();
            }
            else if (typeNamedVal)
            {
                // A type named val keeps its C# meaning; `val (...)` then reports the val form of the error `var (...)` gets with a type named var.
                comp.VerifyDiagnostics(sharper ? [
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7),
                // (6,13): error CS20005: Deconstruction 'val (...)' form disallows a specific type for 'val'. Use '@val' for the type.
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_SharperValDeconstructionDisallowsSpecificType, "(a, b)").WithLocation(6, 13),
                // (6,23): error CS0029: Cannot implicitly convert type 'int' to 'val'
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "1").WithArguments("int", "val").WithLocation(6, 23),
                // (6,26): error CS0029: Cannot implicitly convert type 'int' to 'val'
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "2").WithArguments("int", "val").WithLocation(6, 26),
                // (8,22): error CS1503: Argument 1: cannot convert from 'string' to 'System.ReadOnlySpan<byte>'
                //         int.TryParse("1", out val n);
                Diagnostic(ErrorCode.ERR_BadArgType, @"""1""").WithArguments("1", "string", "System.ReadOnlySpan<byte>").WithLocation(8, 22),
                // (8,31): error CS1503: Argument 2: cannot convert from 'out val' to 'out int'
                //         int.TryParse("1", out val n);
                Diagnostic(ErrorCode.ERR_BadArgType, "val n").WithArguments("2", "out val", "out int").WithLocation(8, 31)] : [
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7),
                // (6,9): error CS1955: Non-invocable member 'val' cannot be used like a method.
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NonInvocableMemberCalled, "val").WithArguments("val").WithLocation(6, 9),
                // (6,14): error CS0103: The name 'a' does not exist in the current context
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "a").WithArguments("a").WithLocation(6, 14),
                // (6,17): error CS0103: The name 'b' does not exist in the current context
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "b").WithArguments("b").WithLocation(6, 17),
                // (8,22): error CS1503: Argument 1: cannot convert from 'string' to 'System.ReadOnlySpan<byte>'
                //         int.TryParse("1", out val n);
                Diagnostic(ErrorCode.ERR_BadArgType, @"""1""").WithArguments("1", "string", "System.ReadOnlySpan<byte>").WithLocation(8, 22),
                // (8,31): error CS1503: Argument 2: cannot convert from 'out val' to 'out int'
                //         int.TryParse("1", out val n);
                Diagnostic(ErrorCode.ERR_BadArgType, "val n").WithArguments("2", "out val", "out int").WithLocation(8, 31)]);
            }
            else
            {
                comp.VerifyDiagnostics(
                // (5,9): error CS0103: The name 'val' does not exist in the current context
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "val").WithArguments("val").WithLocation(5, 9),
                // (5,14): error CS0103: The name 'a' does not exist in the current context
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "a").WithArguments("a").WithLocation(5, 14),
                // (5,17): error CS0103: The name 'b' does not exist in the current context
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "b").WithArguments("b").WithLocation(5, 17),
                // (6,18): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         foreach (val x in xs) { }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(6, 18),
                // (7,31): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         int.TryParse("1", out val n);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(7, 31));
            }
        }

        [Theory]
        [CombinatorialData]
        public void ValDesignation_MethodNamedValIsAcceptedBreak(bool sharper)
        {
            // `val (a, b) = e` assigns through a ref-returning method named val in C#. Sharper makes it a declaration
            // (an accepted break; the sharpener will escape the method as @val).
            var source = """
                class C
                {
                    static int s;
                    static ref int val(int x, int y) => ref s;

                    void M()
                    {
                        int a = 1, b = 2;
                        val (a, b) = 3;
                        @val (a, b) = 4;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(sharper ? [
                // (9,14): error CS0128: A local variable or function named 'a' is already defined in this scope
                //         val (a, b) = 3;
                Diagnostic(ErrorCode.ERR_LocalDuplicate, "a").WithArguments("a").WithLocation(9, 14),
                // (9,14): error CS8130: Cannot infer the type of implicitly-typed deconstruction variable 'a'.
                //         val (a, b) = 3;
                Diagnostic(ErrorCode.ERR_TypeInferenceFailedForImplicitlyTypedDeconstructionVariable, "a").WithArguments("a").WithLocation(9, 14),
                // (9,17): error CS0128: A local variable or function named 'b' is already defined in this scope
                //         val (a, b) = 3;
                Diagnostic(ErrorCode.ERR_LocalDuplicate, "b").WithArguments("b").WithLocation(9, 17),
                // (9,17): error CS8130: Cannot infer the type of implicitly-typed deconstruction variable 'b'.
                //         val (a, b) = 3;
                Diagnostic(ErrorCode.ERR_TypeInferenceFailedForImplicitlyTypedDeconstructionVariable, "b").WithArguments("b").WithLocation(9, 17),
                // (9,22): error CS1061: 'int' does not contain a definition for 'Deconstruct' and no accessible extension method 'Deconstruct' accepting a first argument of type 'int' could be found (are you missing a using directive or an assembly reference?)
                //         val (a, b) = 3;
                Diagnostic(ErrorCode.ERR_NoSuchMemberOrExtension, "3").WithArguments("int", "Deconstruct").WithLocation(9, 22),
                // (9,22): error CS8129: No suitable 'Deconstruct' instance or extension method was found for type 'int', with 2 out parameters and a void return type.
                //         val (a, b) = 3;
                Diagnostic(ErrorCode.ERR_MissingDeconstruct, "3").WithArguments("int", "2").WithLocation(9, 22)] : []);
        }

        [Fact]
        public void ValDesignation_StructCopies()
        {
            var source = """
                using System;

                foreach (val s in new[] { new S() })
                {
                    s.Mutate();
                    Console.Write(s.F);
                }
                val (t, u) = (new S(), 0);
                t.Mutate();
                Console.Write(t.F);
                Make(out val o);
                o.Mutate();
                Console.Write(o.F);

                static void Make(out S s) => s = new S();

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "000" : null).VerifyDiagnostics(
                // (5,5): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                //     s.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(5, 5),
                // (9,1): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 't' runs on a copy, so 't' is not changed.
                // t.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "t").WithArguments("S.Mutate()", "t").WithLocation(9, 1),
                // (12,1): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 'o' runs on a copy, so 'o' is not changed.
                // o.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "o").WithArguments("S.Mutate()", "o").WithLocation(12, 1));
        }

        [Fact]
        public void ValDesignation_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(string? maybe, string?[] items)
                    {
                        val (v, w) = (maybe, maybe);
                        _ = v.Length;
                        foreach (val x in items) { _ = x.Length; }
                        Get(out val o);
                        _ = o.Length;
                    }

                    static void Get(out string? s) => s = null;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = v.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "v").WithLocation(7, 13),
                // (8,40): warning CS8602: Dereference of a possibly null reference.
                //         foreach (val x in items) { _ = x.Length; }
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "x").WithLocation(8, 40),
                // (10,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = o.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "o").WithLocation(10, 13));
        }

        [Fact]
        public void ValDesignation_SemanticModel()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        val (a, b) = (1, "x");
                        foreach (val x in new[] { 1L }) { }
                        int.TryParse("1", out val n);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var valTypes = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == "val").ToArray();
            Assert.Equal(3, valTypes.Length);
            Assert.Equal("(System.Int32 a, System.String b)", model.GetTypeInfo(valTypes[0]).Type.ToTestDisplayString());
            Assert.Equal("System.Int64", model.GetTypeInfo(valTypes[1]).Type.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetTypeInfo(valTypes[2]).Type.ToTestDisplayString());
        }

        [Fact]
        public void ValDesignation_IOperation()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        /*<bind>*/val (a, b) = (1, 2)/*</bind>*/;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IDeconstructionAssignmentOperation (OperationKind.DeconstructionAssignment, Type: (System.Int32 a, System.Int32 b)) (Syntax: 'val (a, b) = (1, 2)')
                  Left:
                    IDeclarationExpressionOperation (OperationKind.DeclarationExpression, Type: (System.Int32 a, System.Int32 b)) (Syntax: 'val (a, b)')
                      ITupleOperation (OperationKind.Tuple, Type: (System.Int32 a, System.Int32 b)) (Syntax: '(a, b)')
                        NaturalType: (System.Int32 a, System.Int32 b)
                        Elements(2):
                            ILocalReferenceOperation: a (IsDeclaration: True) (OperationKind.LocalReference, Type: System.Int32) (Syntax: 'a')
                            ILocalReferenceOperation: b (IsDeclaration: True) (OperationKind.LocalReference, Type: System.Int32) (Syntax: 'b')
                  Right:
                    ITupleOperation (OperationKind.Tuple, Type: (System.Int32, System.Int32)) (Syntax: '(1, 2)')
                      NaturalType: (System.Int32, System.Int32)
                      Elements(2):
                          ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                          ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 2) (Syntax: '2')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<AssignmentExpressionSyntax>(comp, expectedOperationTree, []);
        }

        [Theory]
        [CombinatorialData]
        public void ValDesignation_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            var statement = (ExpressionStatementSyntax)SyntaxFactory.ParseStatement("val (a, b) = e;", options: options);
            var left = ((AssignmentExpressionSyntax)statement.Expression).Left;
            Assert.Equal(sharper ? SyntaxKind.DeclarationExpression : SyntaxKind.InvocationExpression, left.Kind());

            var forEach = (ForEachVariableStatementSyntax)SyntaxFactory.ParseStatement("foreach (val (a, b) in e) { }", options: options);
            Assert.Equal(sharper ? SyntaxKind.DeclarationExpression : SyntaxKind.InvocationExpression, forEach.Variable.Kind());

            // A verbatim @val is always an identifier.
            statement = (ExpressionStatementSyntax)SyntaxFactory.ParseStatement("@val (a, b) = e;", options: options);
            Assert.Equal(SyntaxKind.InvocationExpression, ((AssignmentExpressionSyntax)statement.Expression).Left.Kind());
        }

        [Fact]
        public void ValPattern_Basic()
        {
            var source = """
                using System;

                object o = 1;
                if (o is val x)
                    Console.Write(x);
                switch (o)
                {
                    case val y:
                        Console.Write(y);
                        break;
                }
                Console.Write(o switch { val z => z });
                if ((3, 4) is (val a, val b))
                    Console.Write(a + b);
                if ((5, 6) is val (c, d))
                    Console.Write(c + d);
                if (new P(8) is { X: val px })
                    Console.Write(px);

                record P(int X);
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1117118" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ValPattern_Writes()
        {
            var source = """
                class C
                {
                    void M(object o, (int, int) t)
                    {
                        if (o is val x) { x = null; }
                        if (o is var x2) { x2 = null; }
                        if (t is val (a, b)) { a = 1; }
                        if (t is (val c, var d)) { c = 1; d = 1; }
                        switch (o)
                        {
                            case val y:
                                y = null;
                                break;
                        }
                        _ = o switch { val z => z = null };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,27): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         if (o is val x) { x = null; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(5, 27),
                // (7,32): error CS1656: Cannot assign to 'a' because it is a 'val variable'
                //         if (t is val (a, b)) { a = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "a").WithArguments("a", "val variable").WithLocation(7, 32),
                // (8,36): error CS1656: Cannot assign to 'c' because it is a 'val variable'
                //         if (t is (val c, var d)) { c = 1; d = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "c").WithArguments("c", "val variable").WithLocation(8, 36),
                // (12,17): error CS1656: Cannot assign to 'y' because it is a 'val variable'
                //                 y = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "y").WithArguments("y", "val variable").WithLocation(12, 17),
                // (15,33): error CS1656: Cannot assign to 'z' because it is a 'val variable'
                //         _ = o switch { val z => z = null };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "z").WithArguments("z", "val variable").WithLocation(15, 33));
        }

        [Theory]
        [CombinatorialData]
        public void ValPattern_KeywordEvenWithTypeNamedVal(bool sharper)
        {
            // In a pattern, val is a keyword like var, even where a type named val exists (an accepted break). As for var,
            // that is an error rather than a silent change of meaning. Without a designation, or as @val, it is still the type.
            var source = """
                using System;

                object o = new val();
                Console.Write(o is val v ? v.GetType().Name : "-");
                Console.Write(o is val ? "T" : "F");
                Console.Write(o is @val w ? w.GetType().Name : "-");

                class val { }
                """;
            if (sharper)
            {
                CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                    // (4,20): error CS20004: The syntax 'val' for a pattern is not permitted to refer to a type, but 'val' is in scope here. Use '@val' for the type.
                    // Console.Write(o is val v ? v.GetType().Name : "-");
                    Diagnostic(ErrorCode.ERR_SharperValMayNotBindToType, "val").WithArguments("val").WithLocation(4, 20),
                    // (8,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                    // class val { }
                    Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(8, 7));
            }
            else
            {
                CompileAndVerify(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "valTval" : null).VerifyDiagnostics(
                    // (8,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                    // class val { }
                    Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(8, 7));
            }
        }

        [Fact]
        public void ValPattern_FeatureDisabled()
        {
            var source = """
                class C
                {
                    void M(object o)
                    {
                        if (o is val x) { }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,18): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         if (o is val x) { }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(5, 18));
        }

        [Fact]
        public void ValPattern_StructCopy()
        {
            var source = """
                using System;

                S s = new S();
                if (s is val t)
                {
                    t.Mutate();
                    Console.Write(t.F);
                }

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0" : null).VerifyDiagnostics(
                // (6,5): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 't' runs on a copy, so 't' is not changed.
                //     t.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "t").WithArguments("S.Mutate()", "t").WithLocation(6, 5));
        }

        [Fact]
        public void ValPattern_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(string? maybe)
                    {
                        if (maybe is val v) { _ = v.Length; }
                        if (maybe is var w) { _ = w.Length; }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,35): warning CS8602: Dereference of a possibly null reference.
                //         if (maybe is val v) { _ = v.Length; }
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "v").WithLocation(6, 35),
                // (7,35): warning CS8602: Dereference of a possibly null reference.
                //         if (maybe is var w) { _ = w.Length; }
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "w").WithLocation(7, 35));
        }

        [Theory]
        [CombinatorialData]
        public void ValPattern_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            var isPattern = (IsPatternExpressionSyntax)SyntaxFactory.ParseExpression("o is val x", options: options);
            if (sharper)
            {
                var varPattern = (VarPatternSyntax)isPattern.Pattern;
                Assert.Equal(SyntaxKind.ValKeyword, varPattern.VarKeyword.Kind());
                Assert.Equal("val", varPattern.VarKeyword.Text);
                Assert.Equal("o is val x", isPattern.ToFullString());
            }
            else
            {
                Assert.Equal(SyntaxKind.DeclarationPattern, isPattern.Pattern.Kind());
            }

            // Without a designation, or as @val, it is a type or constant pattern.
            Assert.Equal(SyntaxKind.IsExpression, SyntaxFactory.ParseExpression("o is val", options: options).Kind());
            Assert.Equal(SyntaxKind.DeclarationPattern, ((IsPatternExpressionSyntax)SyntaxFactory.ParseExpression("o is @val x", options: options)).Pattern.Kind());
        }

        [Fact]
        public void ValPattern_IOperation()
        {
            var source = """
                class C
                {
                    void M(object o)
                    {
                        _ = /*<bind>*/o is val x/*</bind>*/;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'o is val x')
                  Value:
                    IParameterReferenceOperation: o (OperationKind.ParameterReference, Type: System.Object) (Syntax: 'o')
                  Pattern:
                    IDeclarationPatternOperation (OperationKind.DeclarationPattern, Type: null) (Syntax: 'val x') (InputType: System.Object, NarrowedType: System.Object, DeclaredSymbol: System.Object? x, MatchesNull: True)
                """;
            VerifyOperationTreeAndDiagnosticsForTest<IsPatternExpressionSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void ValPolish_TypeNamedValMessages()
        {
            var source = """
                class val { }

                class C
                {
                    void M(object o)
                    {
                        val (a, b) = (1, 2);
                        _ = o is val x;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7),
                // (7,13): error CS20005: Deconstruction 'val (...)' form disallows a specific type for 'val'. Use '@val' for the type.
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_SharperValDeconstructionDisallowsSpecificType, "(a, b)").WithLocation(7, 13),
                // (7,23): error CS0029: Cannot implicitly convert type 'int' to 'val'
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "1").WithArguments("int", "val").WithLocation(7, 23),
                // (7,26): error CS0029: Cannot implicitly convert type 'int' to 'val'
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "2").WithArguments("int", "val").WithLocation(7, 26),
                // (8,18): error CS20004: The syntax 'val' for a pattern is not permitted to refer to a type, but 'val' is in scope here. Use '@val' for the type.
                //         _ = o is val x;
                Diagnostic(ErrorCode.ERR_SharperValMayNotBindToType, "val").WithArguments("val").WithLocation(8, 18));
        }

        [Fact]
        public void ValPolish_ForEachAndUsingCause()
        {
            var source = """
                using System;

                class D : IDisposable { public void Dispose() { } }

                class C
                {
                    void M()
                    {
                        foreach (val x in new[] { 1 }) { x = 2; }
                        foreach (var y in new[] { 1 }) { y = 2; }
                        using val d = new D();
                        using var e = new D();
                        d = null;
                        e = null;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (9,42): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         foreach (val x in new[] { 1 }) { x = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(9, 42),
                // (10,42): error CS1656: Cannot assign to 'y' because it is a 'foreach iteration variable'
                //         foreach (var y in new[] { 1 }) { y = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "y").WithArguments("y", "foreach iteration variable").WithLocation(10, 42),
                // (13,9): error CS1656: Cannot assign to 'd' because it is a 'val variable'
                //         d = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "d").WithArguments("d", "val variable").WithLocation(13, 9),
                // (14,9): error CS1656: Cannot assign to 'e' because it is a 'using variable'
                //         e = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "e").WithArguments("e", "using variable").WithLocation(14, 9));
        }

        [Fact]
        public void ValPolish_RefReassignment()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        int i = 1, j = 2;
                        ref val r = ref i;
                        r = 3;
                        r = ref j;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,9): error CS20006: Cannot ref-assign 'r' because it is a ref val: it always refers to the same variable. Writes through 'r' are allowed.
                //         r = ref j;
                Diagnostic(ErrorCode.ERR_SharperValRefReassignment, "r").WithArguments("r").WithLocation(8, 9));
        }

        private static readonly CSharpParseOptions s_sharperScriptOptions = TestOptions.Script.WithExperimental(MessageID.IDS_FeatureSharper);

        [Fact]
        public void ValScript_Fields()
        {
            // In a script, top-level declarations are fields. val is var plus read-only there too.
            var source = """
                val x = 1;
                var y = 1;
                x = 2;
                y = 2;
                x++;
                int.TryParse("3", out val n);
                int.TryParse("3", out var m);
                n = 4;
                m = 4;
                val (a, b) = (5, 6);
                var (c, d) = (5, 6);
                a = 7;
                c = 7;
                object o = 8;
                if (o is val p) { }
                p = null;
                """;
            CreateCompilation(source, parseOptions: s_sharperScriptOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,1): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                // x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(3, 1),
                // (5,1): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                // x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(5, 1),
                // (8,1): error CS1656: Cannot assign to 'n' because it is a 'val variable'
                // n = 4;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "n").WithArguments("n", "val variable").WithLocation(8, 1),
                // (12,1): error CS1656: Cannot assign to 'a' because it is a 'val variable'
                // a = 7;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "a").WithArguments("a", "val variable").WithLocation(12, 1),
                // (16,1): error CS1656: Cannot assign to 'p' because it is a 'val variable'
                // p = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "p").WithArguments("p", "val variable").WithLocation(16, 1));
        }

        [Fact]
        public void ValScript_Runtime()
        {
            var source = """
                using System;

                val x = 1;
                int.TryParse("2", out val n);
                val (a, b) = (3, 4);
                object o = 5;
                if (o is val p) { }
                val s = new S();
                s.Mutate();
                Console.Write($"{x}{n}{a}{b}{p}{s.F}");

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperScriptOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123450" : null).VerifyDiagnostics(
                // (9,1): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                // s.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(9, 1));
        }

        [Fact]
        public void ValScript_TypeNamedValWins()
        {
            var source = """
                class val { }
                val x = new val();
                x = new val();
                """;
            CreateCompilation(source, parseOptions: s_sharperScriptOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7));
        }

        [Fact]
        public void SharperDiagnostics_NumberedFromBase()
        {
            Assert.Equal(20001, (int)ErrorCode.ERR_SharperSpreadOfAsyncSequence);
            Assert.Equal("CS20003", MessageProvider.Instance.GetIdForErrorCode((int)ErrorCode.WRN_SharperValMemberCallOnCopy));

            // Five-digit IDs work with #pragma and the command-line warning options.
            var source = """
                S s = new S();
                val v = s;
                #pragma warning disable CS20003
                v.Mutate();
                #pragma warning restore CS20003
                v.Mutate();

                struct S { public void Mutate() { } }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,1): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(6, 1));
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                options: TestOptions.DebugExe.WithSpecificDiagnosticOptions("CS20003", ReportDiagnostic.Suppress)).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_Basic()
        {
            var source = """
                using System;

                (int, int) -> int add = (a, b) => a + b;
                () -> string thunk = () => "thunk";
                (string) -> void print = s => Console.Write(s);
                (int) -> (int) -> int adder = a => b => a + b;
                (int) -> (int, string) pair = i => (i, i.ToString());

                print(add(1, 2).ToString());
                print(" " + thunk());
                print(" " + adder(3)(4));
                print(" " + pair(5));
                print(" " + Twice(i => i * 10, 6));
                print(" " + Compose(i => i + 1, i => i * 2)(7));

                // Each function type is the Func or Action it stands for.
                Console.Write(" " + (typeof((int, int) -> int) == typeof(Func<int, int, int>)));
                Console.Write(" " + (typeof(() -> void) == typeof(Action)));
                Console.Write(" " + (typeof((string) -> void) == typeof(Action<string>)));
                Console.Write(" " + (typeof((int) -> (int) -> int) == typeof(Func<int, Func<int, int>>)));

                static int Twice((int) -> int f, int x) => f(f(x));
                static (int) -> int Compose((int) -> int f, (int) -> int g) => x => g(f(x));
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "3 thunk 7 (5, 5) 600 16 True True True True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_TypePositions()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using Adder = (int, int) -> int;

                var c = new C();
                c.Field = i => i + 1;
                c.Property = () => "p";
                var list = new List<(int) -> int> { i => i * 2, c.Field };
                Adder add = (a, b) => a + b;
                var cast = ((int) -> int)(i => -i);
                object o = cast;
                var asFunc = o as (int) -> int;
                var defaultFunc = default((int) -> int);
                var apply = ((int) -> int f, int x) => f(x);
                (int) -> int Local((int) -> int f) => f;

                Console.Write(c.Field(1));
                Console.Write(" " + c.Property());
                Console.Write(" " + list[0](2) + list[1](2));
                Console.Write(" " + add(1, 2));
                Console.Write(" " + cast(3));
                Console.Write(" " + asFunc!(4));
                Console.Write(" " + (defaultFunc is null));
                Console.Write(" " + apply(i => i * 3, 5));
                Console.Write(" " + Local(i => i)(6));
                Console.Write(" " + C.Generic<string, int>(s => s.Length)("seven"));

                class C
                {
                    public (int) -> int Field = i => i;
                    public () -> string Property { get; set; } = () => "";
                    public static (T) -> R Generic<T, R>((T) -> R f) => f;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "2 p 43 3 -3 -4 True 15 6 5" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_IdentityWithFunc()
        {
            // A function type is its Func or Action, so the two spellings convert both ways and clash as overloads.
            var source = """
                using System;

                class C
                {
                    void M((int) -> int f) { Func<int, int> g = f; f = g; }
                    void M(Func<int, int> f) { }
                    void N(() -> void a) { Action b = a; a = b; }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,10): error CS0111: Type 'C' already defines a member called 'M' with the same parameter types
                //     void M(Func<int, int> f) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "M").WithArguments("M", "C").WithLocation(6, 10));
        }

        [Fact]
        public void FunctionType_Variance()
        {
            var source = """
                using System;

                (object) -> string f = o => o.ToString()!;
                (string) -> object g = f;
                Func<string, object> h = f;
                Console.Write(g("a") + (string)h("b"));
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ab" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_Suffixes()
        {
            // A suffix applies to the return type: (int) -> int? is Func<int, int?>.
            var source = """
                #nullable enable
                using System;

                (int) -> int? nullable = i => i > 0 ? i : null;
                (int) -> int[] array = i => [i, i];
                (int) -> int[]? both = i => null;
                Console.Write(typeof((int) -> int?) == typeof(Func<int, int?>));
                Console.Write(" " + (typeof((int) -> int[]) == typeof(Func<int, int[]>)));
                Console.Write(" " + (nullable(0) is null) + array(1).Length + (both(2) is null));
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "True True True2True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_Nullable()
        {
            var source = """
                #nullable enable
                using System;

                (string?) -> string f = s => s;
                Func<string?, string> g = s => s;
                (string) -> string? h = s => null;
                string x = h("a");
                Func<string, string?> i = s => null;
                string y = i("a");
                """;
            // The same warnings as for the Func twins.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (4,30): warning CS8603: Possible null reference return.
                // (string?) -> string f = s => s;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "s").WithLocation(4, 30),
                // (5,32): warning CS8603: Possible null reference return.
                // Func<string?, string> g = s => s;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "s").WithLocation(5, 32),
                // (7,12): warning CS8600: Converting null literal or possible null value to non-nullable type.
                // string x = h("a");
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, @"h(""a"")").WithLocation(7, 12),
                // (9,12): warning CS8600: Converting null literal or possible null value to non-nullable type.
                // string y = i("a");
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, @"i(""a"")").WithLocation(9, 12));
        }

        [Fact]
        public void FunctionType_Errors()
        {
            var source = """
                #pragma warning disable CS0169 // The field is never used
                unsafe class C
                {
                    (ref int, out string, in long) -> bool modifiers;
                    (int x, int y) -> int names;
                    (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int tooMany;
                    (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int sixteen;
                    (void) -> int voidParameter;
                    (int*) -> int pointer;
                    System.Func<int*, int> pointerTwin;
                    (int) -> System.Span<int> span;
                    (int) -> System.Console staticReturn;
                    System.Func<int, System.Console> staticReturnTwin;
                    void M()
                    {
                        (int*) -> int local = null;
                        System.Func<int*, int> localTwin = null;
                        (int, System.Span<int>) -> int span2 = null;
                        System.Func<int, System.Span<int>, int> span2Twin = null;
                        (int) -> System.Console staticLocal = null;
                        System.Func<int, System.Console> staticLocalTwin = null;
                        ((int*) -> int) -> int nested = null;
                        System.Func<System.Func<int*, int>, int> nestedTwin = null;
                        (int*[..]) -> int sequence = null;
                        System.Func<System.Collections.Generic.IEnumerable<int*>, int> sequenceTwin = null;
                        (int) -> int*~ task = null;
                        System.Func<int, System.Threading.Tasks.Task<int*>> taskTwin = null;
                        (S<string>) -> int constrained = null;
                        System.Func<S<string>, int> constrainedTwin = null;
                    }
                    void P((int*) -> int p, System.Func<int*, int> pTwin, (S<string>) -> void q, System.Action<S<string>> qTwin) { }
                    (S<string>) -> int constrainedField;
                    System.Func<S<string>, int> constrainedFieldTwin;
                }
                struct S<T> where T : struct { }
                """;
            // Constraint errors are the same as for the Func twins, and are reported at the same place: on the
            // parameter or return type that stands for the offending type argument.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.UnsafeReleaseDll).VerifyDiagnostics(
                // (4,6): error CS20008: A function type parameter cannot be 'ref', because a function type is a 'System.Func' or 'System.Action' delegate.
                //     (ref int, out string, in long) -> bool modifiers;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "ref").WithArguments("ref").WithLocation(4, 6),
                // (4,15): error CS20008: A function type parameter cannot be 'out', because a function type is a 'System.Func' or 'System.Action' delegate.
                //     (ref int, out string, in long) -> bool modifiers;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "out").WithArguments("out").WithLocation(4, 15),
                // (4,27): error CS20008: A function type parameter cannot be 'in', because a function type is a 'System.Func' or 'System.Action' delegate.
                //     (ref int, out string, in long) -> bool modifiers;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "in").WithArguments("in").WithLocation(4, 27),
                // (5,10): error CS20007: A function type cannot name its parameters. Remove the name 'x'.
                //     (int x, int y) -> int names;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterName, "x").WithArguments("x").WithLocation(5, 10),
                // (5,17): error CS20007: A function type cannot name its parameters. Remove the name 'y'.
                //     (int x, int y) -> int names;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterName, "y").WithArguments("y").WithLocation(5, 17),
                // (6,5): error CS20009: A function type cannot have more than 16 parameters, because no 'System.Func' or 'System.Action' delegate has more.
                //     (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int tooMany;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeTooManyParameters, "(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int").WithArguments("16").WithLocation(6, 5),
                // (8,6): error CS1536: Invalid parameter type 'void'
                //     (void) -> int voidParameter;
                Diagnostic(ErrorCode.ERR_NoVoidParameter, "void").WithLocation(8, 6),
                // (8,19): error CS0306: The type 'void' may not be used as a type argument
                //     (void) -> int voidParameter;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "voidParameter").WithArguments("void").WithLocation(8, 19),
                // (9,19): error CS0306: The type 'int*' may not be used as a type argument
                //     (int*) -> int pointer;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "pointer").WithArguments("int*").WithLocation(9, 19),
                // (10,28): error CS0306: The type 'int*' may not be used as a type argument
                //     System.Func<int*, int> pointerTwin;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "pointerTwin").WithArguments("int*").WithLocation(10, 28),
                // (12,29): error CS0718: 'Console': static types cannot be used as type arguments
                //     (int) -> System.Console staticReturn;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "staticReturn").WithArguments("System.Console").WithLocation(12, 29),
                // (13,38): error CS0718: 'Console': static types cannot be used as type arguments
                //     System.Func<int, System.Console> staticReturnTwin;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "staticReturnTwin").WithArguments("System.Console").WithLocation(13, 38),
                // (16,10): error CS0306: The type 'int*' may not be used as a type argument
                //         (int*) -> int local = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(16, 10),
                // (16,23): warning CS0219: The variable 'local' is assigned but its value is never used
                //         (int*) -> int local = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "local").WithArguments("local").WithLocation(16, 23),
                // (17,21): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Func<int*, int> localTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(17, 21),
                // (17,32): warning CS0219: The variable 'localTwin' is assigned but its value is never used
                //         System.Func<int*, int> localTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "localTwin").WithArguments("localTwin").WithLocation(17, 32),
                // (18,40): warning CS0219: The variable 'span2' is assigned but its value is never used
                //         (int, System.Span<int>) -> int span2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "span2").WithArguments("span2").WithLocation(18, 40),
                // (19,49): warning CS0219: The variable 'span2Twin' is assigned but its value is never used
                //         System.Func<int, System.Span<int>, int> span2Twin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "span2Twin").WithArguments("span2Twin").WithLocation(19, 49),
                // (20,18): error CS0718: 'Console': static types cannot be used as type arguments
                //         (int) -> System.Console staticLocal = null;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "System.Console").WithArguments("System.Console").WithLocation(20, 18),
                // (20,33): warning CS0219: The variable 'staticLocal' is assigned but its value is never used
                //         (int) -> System.Console staticLocal = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "staticLocal").WithArguments("staticLocal").WithLocation(20, 33),
                // (21,26): error CS0718: 'Console': static types cannot be used as type arguments
                //         System.Func<int, System.Console> staticLocalTwin = null;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "System.Console").WithArguments("System.Console").WithLocation(21, 26),
                // (21,42): warning CS0219: The variable 'staticLocalTwin' is assigned but its value is never used
                //         System.Func<int, System.Console> staticLocalTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "staticLocalTwin").WithArguments("staticLocalTwin").WithLocation(21, 42),
                // (22,11): error CS0306: The type 'int*' may not be used as a type argument
                //         ((int*) -> int) -> int nested = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(22, 11),
                // (22,32): warning CS0219: The variable 'nested' is assigned but its value is never used
                //         ((int*) -> int) -> int nested = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "nested").WithArguments("nested").WithLocation(22, 32),
                // (23,33): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Func<System.Func<int*, int>, int> nestedTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(23, 33),
                // (23,50): warning CS0219: The variable 'nestedTwin' is assigned but its value is never used
                //         System.Func<System.Func<int*, int>, int> nestedTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "nestedTwin").WithArguments("nestedTwin").WithLocation(23, 50),
                // (24,10): error CS0306: The type 'int*' may not be used as a type argument
                //         (int*[..]) -> int sequence = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(24, 10),
                // (24,27): warning CS0219: The variable 'sequence' is assigned but its value is never used
                //         (int*[..]) -> int sequence = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "sequence").WithArguments("sequence").WithLocation(24, 27),
                // (25,60): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Func<System.Collections.Generic.IEnumerable<int*>, int> sequenceTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(25, 60),
                // (25,72): warning CS0219: The variable 'sequenceTwin' is assigned but its value is never used
                //         System.Func<System.Collections.Generic.IEnumerable<int*>, int> sequenceTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "sequenceTwin").WithArguments("sequenceTwin").WithLocation(25, 72),
                // (26,18): error CS0306: The type 'int*' may not be used as a type argument
                //         (int) -> int*~ task = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(26, 18),
                // (26,24): warning CS0219: The variable 'task' is assigned but its value is never used
                //         (int) -> int*~ task = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "task").WithArguments("task").WithLocation(26, 24),
                // (27,54): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Func<int, System.Threading.Tasks.Task<int*>> taskTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(27, 54),
                // (27,61): warning CS0219: The variable 'taskTwin' is assigned but its value is never used
                //         System.Func<int, System.Threading.Tasks.Task<int*>> taskTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "taskTwin").WithArguments("taskTwin").WithLocation(27, 61),
                // (28,12): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //         (S<string>) -> int constrained = null;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "string").WithArguments("S<T>", "T", "string").WithLocation(28, 12),
                // (28,28): warning CS0219: The variable 'constrained' is assigned but its value is never used
                //         (S<string>) -> int constrained = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "constrained").WithArguments("constrained").WithLocation(28, 28),
                // (29,23): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //         System.Func<S<string>, int> constrainedTwin = null;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "string").WithArguments("S<T>", "T", "string").WithLocation(29, 23),
                // (29,37): warning CS0219: The variable 'constrainedTwin' is assigned but its value is never used
                //         System.Func<S<string>, int> constrainedTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "constrainedTwin").WithArguments("constrainedTwin").WithLocation(29, 37),
                // (31,26): error CS0306: The type 'int*' may not be used as a type argument
                //     void P((int*) -> int p, System.Func<int*, int> pTwin, (S<string>) -> void q, System.Action<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "p").WithArguments("int*").WithLocation(31, 26),
                // (31,52): error CS0306: The type 'int*' may not be used as a type argument
                //     void P((int*) -> int p, System.Func<int*, int> pTwin, (S<string>) -> void q, System.Action<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "pTwin").WithArguments("int*").WithLocation(31, 52),
                // (31,79): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     void P((int*) -> int p, System.Func<int*, int> pTwin, (S<string>) -> void q, System.Action<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "q").WithArguments("S<T>", "T", "string").WithLocation(31, 79),
                // (31,107): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     void P((int*) -> int p, System.Func<int*, int> pTwin, (S<string>) -> void q, System.Action<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "qTwin").WithArguments("S<T>", "T", "string").WithLocation(31, 107),
                // (32,24): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     (S<string>) -> int constrainedField;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "constrainedField").WithArguments("S<T>", "T", "string").WithLocation(32, 24),
                // (33,33): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     System.Func<S<string>, int> constrainedFieldTwin;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "constrainedFieldTwin").WithArguments("S<T>", "T", "string").WithLocation(33, 33));
        }

        [Fact]
        public void FunctionType_Disabled()
        {
            // Without Sharper, the function type reports the usual C# errors.
            var source = """
                class C
                {
                    void M()
                    {
                        (int, int) -> int add = null;
                        () -> int thunk = null;
                    }
                }
                """;
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,10): error CS1525: Invalid expression term 'int'
                //         (int, int) -> int add = null;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "int").WithArguments("int").WithLocation(5, 10),
                // (5,15): error CS1525: Invalid expression term 'int'
                //         (int, int) -> int add = null;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "int").WithArguments("int").WithLocation(5, 15),
                // (5,23): error CS1001: Identifier expected
                //         (int, int) -> int add = null;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "int").WithLocation(5, 23),
                // (5,23): error CS1002: ; expected
                //         (int, int) -> int add = null;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "int").WithLocation(5, 23),
                // (5,33): error CS0037: Cannot convert null to 'int' because it is a non-nullable value type
                //         (int, int) -> int add = null;
                Diagnostic(ErrorCode.ERR_ValueCantBeNull, "null").WithArguments("int").WithLocation(5, 33),
                // (6,10): error CS1525: Invalid expression term ')'
                //         () -> int thunk = null;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, ")").WithArguments(")").WithLocation(6, 10),
                // (6,15): error CS1001: Identifier expected
                //         () -> int thunk = null;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "int").WithLocation(6, 15),
                // (6,15): error CS1002: ; expected
                //         () -> int thunk = null;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "int").WithLocation(6, 15),
                // (6,27): error CS0037: Cannot convert null to 'int' because it is a non-nullable value type
                //         () -> int thunk = null;
                Diagnostic(ErrorCode.ERR_ValueCantBeNull, "null").WithArguments("int").WithLocation(6, 27));
        }

        [Fact]
        public void FunctionType_FactoryWithoutSharper()
        {
            // A function type built with SyntaxFactory in a C# tree reports that Sharper is not enabled.
            var tree = SyntaxFactory.ParseSyntaxTree("class C { (int) -> int F; }", s_sharperOptions);
            var csharpTree = tree.WithRootAndOptions(tree.GetRoot(), TestOptions.Regular);
            CreateCompilation(csharpTree, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,17): error CS8058: Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.
                // class C { (int) -> int F; }
                Diagnostic(ErrorCode.ERR_FeatureIsExperimental, "->").WithArguments("Sharper", "sharper").WithLocation(1, 17),
                // (1,24): warning CS0169: The field 'C.F' is never used
                // class C { (int) -> int F; }
                Diagnostic(ErrorCode.WRN_UnreferencedField, "F").WithArguments("C.F").WithLocation(1, 24));
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;

            var statement = SyntaxFactory.ParseStatement("(int, int) -> int add = null;", options: options);
            Assert.Equal(sharper ? SyntaxKind.LocalDeclarationStatement : SyntaxKind.ExpressionStatement, statement.Kind());
            if (sharper)
            {
                var type = (FunctionTypeSyntax)((LocalDeclarationStatementSyntax)statement).Declaration.Type;
                Assert.Equal(2, type.Parameters.Count);
                Assert.Equal("int", type.ReturnType.ToString());
                Assert.False(statement.ContainsDiagnostics);
            }

            // The arrow is right-associative, and a suffix applies to the return type.
            statement = SyntaxFactory.ParseStatement("(int) -> (int) -> int? f;", options: options);
            if (sharper)
            {
                var type = (FunctionTypeSyntax)((LocalDeclarationStatementSyntax)statement).Declaration.Type;
                var inner = (FunctionTypeSyntax)type.ReturnType;
                Assert.Equal(SyntaxKind.NullableType, inner.ReturnType.Kind());
            }

            // `(p) -> F x` is never valid C#, so it is a declaration, like `p.F x`.
            statement = SyntaxFactory.ParseStatement("(p) -> F x;", options: options);
            Assert.Equal(sharper ? SyntaxKind.LocalDeclarationStatement : SyntaxKind.ExpressionStatement, statement.Kind());

            // A tuple of function types, and a function type taking a tuple.
            statement = SyntaxFactory.ParseStatement("((int) -> int, string) t;", options: options);
            Assert.Equal(sharper ? SyntaxKind.LocalDeclarationStatement : SyntaxKind.ExpressionStatement, statement.Kind());
            if (sharper)
            {
                var tuple = (TupleTypeSyntax)((LocalDeclarationStatementSyntax)statement).Declaration.Type;
                Assert.Equal(SyntaxKind.FunctionType, tuple.Elements[0].Type.Kind());
            }

            statement = SyntaxFactory.ParseStatement("((int, int)) -> int f;", options: options);
            if (sharper)
            {
                var type = (FunctionTypeSyntax)((LocalDeclarationStatementSyntax)statement).Declaration.Type;
                Assert.Equal(SyntaxKind.TupleType, type.Parameters.Single().Type.Kind());
            }

            // A generic argument list.
            var expression = SyntaxFactory.ParseExpression("F<(int) -> int>(x)", options: options);
            Assert.Equal(sharper, expression is InvocationExpressionSyntax { Expression: GenericNameSyntax });
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Parsing_PointerMemberAccess(bool sharper)
        {
            // `->` keeps its C# meaning in expressions, with or without Sharper.
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            foreach (var text in new[] { "(p)->F(x);", "((p)->F)(x);", "(p)->F = 1;", "x = (p)->F;", "(p)->F<int>(x);", "(a.b)->F(x);", "p->F(x);", "M((p)->F);" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.Equal(SyntaxKind.ExpressionStatement, statement.Kind());
                Assert.False(statement.ContainsDiagnostics, text);
                Assert.Empty(statement.DescendantNodes().OfType<FunctionTypeSyntax>());
                Assert.Empty(statement.DescendantNodes().OfType<CastExpressionSyntax>());
            }
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Parsing_RefTupleElement(bool sharper)
        {
            // C# scans a 'ref' tuple element as part of the tuple type and reports the 'ref', so Sharper must too.
            // Found by the parser tests with ROSLYN_TEST_SHARPER (RefReadonlyTests.RefReadonlyReturn_UnexpectedBindTime).
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            foreach (var text in new[] { "(ref readonly int, ref readonly int Alice)? t = null;", "(ref int, int) t = default;" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.Equal(SyntaxKind.LocalDeclarationStatement, statement.Kind());
                Assert.IsAssignableFrom<TupleTypeSyntax>(((LocalDeclarationStatementSyntax)statement).Declaration.Type.DescendantNodesAndSelf().First(n => n is TupleTypeSyntax));
                Assert.All(statement.GetDiagnostics(), d => Assert.Equal((int)ErrorCode.ERR_UnexpectedToken, d.Code));
            }
        }

        [Fact]
        public void FunctionType_Parsing_DeeplyNestedParentheses()
        {
            // Tuples and function types are scanned in one pass, so nested parentheses do not take exponential time.
            var depth = 200;
            var text = new string('(', depth) + "a" + new string(')', depth) + ";";
            var statement = SyntaxFactory.ParseStatement(text, options: s_sharperOptions);
            Assert.Equal(SyntaxKind.ExpressionStatement, statement.Kind());
            Assert.False(statement.ContainsDiagnostics);
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_PointerMemberAccess_KeepsCSharpMeaning(bool sharper)
        {
            var source = """
                using System;

                unsafe
                {
                    var s = new S { F = i => i * 10 };
                    S* p = &s;
                    Console.Write(p->F(1));
                    Console.Write((p)->F(2));
                    Console.Write(((p)->F)(3));
                    (p)->F = i => i;
                    Console.Write(p->F(4));
                }

                struct S { public Func<int, int> F; }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90,
                options: TestOptions.UnsafeReleaseExe, verify: Verification.Skipped,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1020304" : null).VerifyDiagnostics(
                // (6,5): warning CS8500: This takes the address of, gets the size of, or declares a pointer to a managed type ('S')
                //     S* p = &s;
                Diagnostic(ErrorCode.WRN_ManagedAddr, "S*").WithArguments("S").WithLocation(6, 5),
                // (6,12): warning CS8500: This takes the address of, gets the size of, or declares a pointer to a managed type ('S')
                //     S* p = &s;
                Diagnostic(ErrorCode.WRN_ManagedAddr, "&s").WithArguments("S").WithLocation(6, 12));
        }

        [Fact]
        public void FunctionType_SemanticModel()
        {
            var source = """
                class C
                {
                    (int, string) -> bool F;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (3,27): warning CS0169: The field 'C.F' is never used
                //     (int, string) -> bool F;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "F").WithArguments("C.F").WithLocation(3, 27));

            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var functionType = tree.GetRoot().DescendantNodes().OfType<FunctionTypeSyntax>().Single();
            Assert.Equal("System.Func<System.Int32, System.String, System.Boolean>", model.GetTypeInfo(functionType).Type.ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32, System.String, System.Boolean>", model.GetSymbolInfo(functionType).Symbol.ToTestDisplayString());
            Assert.Equal("System.String", model.GetTypeInfo(functionType.Parameters[1].Type).Type.ToTestDisplayString());
            Assert.Equal("System.Boolean", model.GetTypeInfo(functionType.ReturnType).Type.ToTestDisplayString());
            Assert.True(SyntaxFacts.IsInTypeOnlyContext(functionType.ReturnType));
            Assert.True(SyntaxFacts.IsTypeSyntax(SyntaxKind.FunctionType));
        }

        [Fact]
        public void FunctionType_SyntaxKind()
        {
            // Sharper syntax nodes sit apart from the upstream ones; an upstream merge must not reuse the value.
            Assert.Equal(9900, (int)SyntaxKind.FunctionType);
            Assert.Single(Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == (int)SyntaxKind.FunctionType);
        }

        [Fact]
        public void FunctionType_Parenthesized_Basic()
        {
            // Parentheses let a suffix apply to the whole function type: ((int) -> int)? is Func<int, int>?.
            var source = """
                #nullable enable
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                ((int) -> int)? none = null;
                ((int) -> int)[] handlers = [i => i + 1, i => i * 2];
                ((int) -> int)~ pending = Task.FromResult<(int) -> int>(i => -i);
                List<((string) -> void)?> actions = [null, s => Console.Write(" " + s)];
                () -> ((int) -> int)[] factory = () => handlers;

                Console.Write(none is null);
                Console.Write(" " + handlers[0](1) + handlers[1](5));
                Console.Write(" " + (await pending)(3));
                actions[1]?.Invoke("action");
                Console.Write(" " + factory().Length);
                Console.Write(" " + Apply(handlers, 10));
                Console.Write(" " + (typeof(((int) -> int)[]) == typeof(Func<int, int>[])));
                Console.Write(" " + (typeof(((int) -> int)~) == typeof(Task<Func<int, int>>)));
                Console.Write(" " + (typeof(((int) -> int)[..]) == typeof(IEnumerable<Func<int, int>>)));
                Console.Write(" " + (typeof(((int) -> int)) == typeof(Func<int, int>)));

                static int Apply(((int) -> int)[] fs, int x)
                {
                    foreach (((int) -> int)? f in fs) { x = f?.Invoke(x) ?? x; }
                    return x;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "True 210 -3 action 2 22 True True True True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_Parenthesized_TypePositions()
        {
            // Each parenthesized function type is the type written with Func, Action, Task and IEnumerable.
            var source = """
                #nullable enable
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;
                using Handlers = ((int) -> int)[];

                #pragma warning disable CS0169, CS0649, CS8618 // The field is never used, assigned or initialized
                class C
                {
                    ((int) -> int)? A; Func<int, int>? ATwin;
                    ((int) -> int)[] B; Func<int, int>[] BTwin;
                    ((int) -> int)~ T; Task<Func<int, int>> TTwin;
                    ((int) -> int)[]? D; Func<int, int>[]? DTwin;
                    ((int) -> int)~? E; Task<Func<int, int>>? ETwin;
                    ((int) -> int)?[] F; Func<int, int>?[] FTwin;
                    ((int) -> int?)? G; Func<int, int?>? GTwin;
                    ((int) -> int)[,] H; Func<int, int>[,] HTwin;
                    ((int) -> int)[..] I; IEnumerable<Func<int, int>> ITwin;
                    ((string) -> void)? J; Action<string>? JTwin;
                    (((int) -> int)?) -> int K; Func<Func<int, int>?, int> KTwin;
                    (int) -> ((int) -> int)? L; Func<int, Func<int, int>?> LTwin;
                    ((int) -> (int) -> int)? M; Func<int, Func<int, int>>? MTwin;
                    (((int) -> int)[], string) N; (Func<int, int>[], string) NTwin;
                    List<((int) -> int)?> O; List<Func<int, int>?> OTwin;
                    Handlers P; Func<int, int>[] PTwin;
                    ((int, int) -> int)? Q; Func<int, int, int>? QTwin;
                    (() -> void)[] R; Action[] RTwin;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();

            var c = comp.GetMember<NamedTypeSymbol>("C");
            var fields = c.GetMembers().OfType<FieldSymbol>().ToArray();
            Assert.Equal(36, fields.Length);
            for (int i = 0; i < fields.Length; i += 2)
            {
                Assert.Equal(fields[i].Name + "Twin", fields[i + 1].Name);
                Assert.Equal(fields[i + 1].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true), fields[i].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
            }
        }

        [Fact]
        public void FunctionType_Parenthesized_Locals()
        {
            // The same in local positions, compared with the local of the twin.
            var source = """
                #nullable enable
                using System;
                using System.Threading.Tasks;

                class C
                {
                    async Task M(object o, ((int) -> int)?[] fs)
                    {
                        #pragma warning disable CS0219 // The variable is assigned but its value is never used
                        ((int) -> int)? a = null; Func<int, int>? aTwin = null;
                        var b = (((int) -> int)[])o; var bTwin = (Func<int, int>[])o;
                        var c = o as ((int) -> int)[]; var cTwin = o as Func<int, int>[];
                        var d = default(((int) -> int)~); var dTwin = default(Task<Func<int, int>>);
                        var e = new ((int) -> int)[3]; var eTwin = new Func<int, int>[3];
                        foreach (((int) -> int)? f in fs) { } foreach (Func<int, int>? fTwin in fs) { }
                        G(out ((int) -> int)[] g); G(out Func<int, int>[] gTwin);
                        (((int) -> int)[] h, int i) = (new ((int) -> int)[0], 1); (Func<int, int>[] hTwin, int iTwin) = (new Func<int, int>[0], 1);
                        using (((int) -> int)~? j = null) { } using (Task<Func<int, int>>? jTwin = null) { }
                        ((int) -> int)? k = await Task.FromResult<((int) -> int)?>(null); Func<int, int>? kTwin = await Task.FromResult<Func<int, int>?>(null);
                        var l = (((int) -> int)? x) => x; var lTwin = (Func<int, int>? xTwin) => xTwin;
                    }

                    void G(out ((int) -> int)[] g) => g = [];
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();

            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var locals = tree.GetRoot().DescendantNodes()
                .Where(n => n is VariableDeclaratorSyntax or SingleVariableDesignationSyntax or ForEachStatementSyntax or ParameterSyntax { Parent.Parent: ParenthesizedLambdaExpressionSyntax })
                .Select(n => model.GetDeclaredSymbol(n))
                .OfType<ISymbol>()
                .Where(s => s is ILocalSymbol or IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } })
                .ToDictionary(s => s.Name, s => s switch { ILocalSymbol l => l.Type.ToTestDisplayString(includeNonNullable: true), IParameterSymbol p => p.Type.ToTestDisplayString(includeNonNullable: true), _ => null });
            Assert.Equal(26, locals.Count);
            foreach (var (name, type) in locals)
            {
                if (!name.EndsWith("Twin"))
                {
                    Assert.Equal(locals[name + "Twin"], type);
                }
            }
        }

        [Fact]
        public void FunctionType_Parenthesized_Nullable()
        {
            var source = """
                #nullable enable
                using System;

                ((int) -> int)? f = null;
                Func<int, int>? g = null;
                f(1);
                g(1);
                ((int) -> int)[] fs = [null];
                Func<int, int>[] gs = [null];
                """;
            // The same warnings as for the Func twins.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,1): warning CS8602: Dereference of a possibly null reference.
                // f(1);
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "f").WithLocation(6, 1),
                // (7,1): warning CS8602: Dereference of a possibly null reference.
                // g(1);
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "g").WithLocation(7, 1),
                // (8,24): warning CS8625: Cannot convert null literal to non-nullable reference type.
                // ((int) -> int)[] fs = [null];
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(8, 24),
                // (9,24): warning CS8625: Cannot convert null literal to non-nullable reference type.
                // Func<int, int>[] gs = [null];
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(9, 24));
        }

        [Fact]
        public void FunctionType_Parenthesized_Errors()
        {
            // Parentheses are a type only around a function type.
            foreach (var text in new[] { "((int)) a = null;", "((int) -> int c) d = null;", "(ref (int) -> int) e = null;", "((int, int)) f = null;" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: s_sharperOptions);
                Assert.Empty(statement.DescendantNodes().OfType<ParenthesizedTypeSyntax>());
                Assert.NotEqual(SyntaxKind.LocalDeclarationStatement, statement.Kind());
            }
        }

        [Fact]
        public void FunctionType_Parenthesized_Disabled()
        {
            // Without Sharper, the parenthesized function type reports the usual C# errors.
            var source = """
                class C
                {
                    void M()
                    {
                        ((int) -> int)? f = null;
                        ((int) -> int)[] g = null;
                    }
                }
                """;
            var comp = CreateCompilation(source, targetFramework: TargetFramework.Net90);
            Assert.NotEmpty(comp.GetDiagnostics());
            Assert.Empty(comp.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<ParenthesizedTypeSyntax>());
            Assert.Empty(comp.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<FunctionTypeSyntax>());
        }

        [Fact]
        public void FunctionType_Parenthesized_FactoryWithoutSharper()
        {
            // A parenthesized type built with SyntaxFactory in a C# tree reports that Sharper is not enabled.
            var tree = SyntaxFactory.ParseSyntaxTree("class C { ((int) -> int)? F; }", s_sharperOptions);
            var csharpTree = tree.WithRootAndOptions(tree.GetRoot(), TestOptions.Regular);
            CreateCompilation(csharpTree, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,11): error CS8058: Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.
                // class C { ((int) -> int)? F; }
                Diagnostic(ErrorCode.ERR_FeatureIsExperimental, "(").WithArguments("Sharper", "sharper").WithLocation(1, 11),
                // (1,18): error CS8058: Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.
                // class C { ((int) -> int)? F; }
                Diagnostic(ErrorCode.ERR_FeatureIsExperimental, "->").WithArguments("Sharper", "sharper").WithLocation(1, 18),
                // (1,25): warning CS8632: The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
                // class C { ((int) -> int)? F; }
                Diagnostic(ErrorCode.WRN_MissingNonNullTypesContextForAnnotation, "?").WithLocation(1, 25),
                // (1,27): warning CS0169: The field 'C.F' is never used
                // class C { ((int) -> int)? F; }
                Diagnostic(ErrorCode.WRN_UnreferencedField, "F").WithArguments("C.F").WithLocation(1, 27));
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Parenthesized_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;

            // Every one of these is a syntax error in C#.
            foreach (var text in new[] { "((int) -> int)? f;", "((int) -> int)[] f;", "((int) -> int)~ f;", "((int) -> int)[]? f = null;", "((p) -> F)? x;", "((p) -> F)[] x;", "((p) -> F)~ x;", "(((int) -> int)[] a, int b) = t;", "foreach (((int) -> int)? f in fs) { }", "M(out ((int) -> int)? f);", "x = (((int) -> int)?)y;", "x = (((int) -> int)[])y;", "x = (((int) -> int))y;", "x = y as ((int) -> int)[];", "x = typeof(((int) -> int)[]);", "x = default(((int) -> int)?);", "x = F<((int) -> int)?>(y);", "x = new ((int) -> int)[3];", "((int) -> int)? L() => null;", "x = (((int) -> int)? f) => f;" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.True(sharper != statement.ContainsDiagnostics, text);
                Assert.True(sharper == statement.DescendantNodes().OfType<ParenthesizedTypeSyntax>().Any(), text);
            }

            // The suffix applies to the parenthesized type, and the parentheses hold the function type.
            var declaration = SyntaxFactory.ParseStatement("((int) -> int)? f;", options: options);
            if (sharper)
            {
                var type = (NullableTypeSyntax)((LocalDeclarationStatementSyntax)declaration).Declaration.Type;
                var parenthesized = (ParenthesizedTypeSyntax)type.ElementType;
                Assert.Equal(SyntaxKind.FunctionType, parenthesized.Type.Kind());
                Assert.Equal("int", ((FunctionTypeSyntax)parenthesized.Type).ReturnType.ToString());
            }

            // A parenthesized function type as a parameter of a function type, and as its return type.
            declaration = SyntaxFactory.ParseStatement("(((int) -> int)?) -> ((int) -> int)[] f;", options: options);
            if (sharper)
            {
                var type = (FunctionTypeSyntax)((LocalDeclarationStatementSyntax)declaration).Declaration.Type;
                Assert.Equal(SyntaxKind.NullableType, type.Parameters.Single().Type.Kind());
                Assert.Equal(SyntaxKind.ArrayType, type.ReturnType.Kind());
                Assert.False(declaration.ContainsDiagnostics);
            }

            // A function type taking a function type, which needs no extra parentheses.
            declaration = SyntaxFactory.ParseStatement("((int) -> int) -> int f;", options: options);
            if (sharper)
            {
                var type = (FunctionTypeSyntax)((LocalDeclarationStatementSyntax)declaration).Declaration.Type;
                Assert.Equal(SyntaxKind.FunctionType, type.Parameters.Single().Type.Kind());
            }
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Parenthesized_Parsing_KeepsCSharpMeaning(bool sharper)
        {
            // A parenthesized pointer member access keeps its C# meaning, with or without Sharper.
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            foreach (var text in new[] { "x = ((p)->F)[i];", "((p)->F)[i] = 1;", "x = ((p)->F)?.X;", "((p)->F)?.M();", "x = ((p)->F)?[i];", "x = ((p)->F) ? a : b;", "x = ((p)->F) ? [a] : b;", "x = c ? ((p)->F) : b;", "((p)->F)(x);", "(((p)->F))(x);", "x = (((p)->F))(y);", "x = (((p)->F<int>))(y);", "x = ((p)->F)!;", "x = (((p)->F))!;", "((p)->F)++;", "x = ((p)->F) * y;", "x = ((p)->F) < y;", "M(((p)->F)?.X, y);", "x = ((p)->F, y);", "x = (((p)->F), y);" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.False(statement.ContainsDiagnostics, text);
                Assert.Empty(statement.DescendantNodes().OfType<ParenthesizedTypeSyntax>());
                Assert.Empty(statement.DescendantNodes().OfType<FunctionTypeSyntax>());
                Assert.Empty(statement.DescendantNodes().OfType<CastExpressionSyntax>());
                Assert.True(statement.IsEquivalentTo(SyntaxFactory.ParseStatement(text, options: TestOptions.Regular)), text);
            }
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Parenthesized_PointerMemberAccess_KeepsCSharpMeaning(bool sharper)
        {
            var source = """
                using System;

                unsafe
                {
                    var s = new S { A = [10, 20], B = true, F = i => i * 10 };
                    S* p = &s;
                    Console.Write(((p)->A)[1]);
                    Console.Write(" " + (((p)->N)?.Length ?? -1));
                    Console.Write(" " + (((p)->A)?[0]));
                    Console.Write(" " + (((p)->B) ? "yes" : "no"));
                    Console.Write(" " + ((p)->F)(1));
                    Console.Write(" " + (((p)->F))(2));
                    ((p)->A)[0] = 30;
                    Console.Write(" " + s.A[0]);
                }

                struct S { public int[] A; public string N; public bool B; public Func<int, int> F; }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90,
                options: TestOptions.UnsafeReleaseExe, verify: Verification.Skipped,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "20 -1 10 yes 10 20 30" : null).VerifyDiagnostics(
                // (6,5): warning CS8500: This takes the address of, gets the size of, or declares a pointer to a managed type ('S')
                //     S* p = &s;
                Diagnostic(ErrorCode.WRN_ManagedAddr, "S*").WithArguments("S").WithLocation(6, 5),
                // (6,12): warning CS8500: This takes the address of, gets the size of, or declares a pointer to a managed type ('S')
                //     S* p = &s;
                Diagnostic(ErrorCode.WRN_ManagedAddr, "&s").WithArguments("S").WithLocation(6, 12));
        }

        [Fact]
        public void FunctionType_Parenthesized_DeeplyNested()
        {
            // Function types nested in parentheses are scanned in one pass, so they do not take exponential time.
            var depth = 100;
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < depth; i++)
            {
                builder.Append("((int) -> ");
            }

            builder.Append("int");
            for (int i = 0; i < depth; i++)
            {
                builder.Append(")?");
            }

            builder.Append(" f;");
            var statement = SyntaxFactory.ParseStatement(builder.ToString(), options: s_sharperOptions);
            Assert.Equal(SyntaxKind.LocalDeclarationStatement, statement.Kind());
            Assert.False(statement.ContainsDiagnostics);
            Assert.Equal(depth, statement.DescendantNodes().OfType<ParenthesizedTypeSyntax>().Count());
        }

        [Fact]
        public void FunctionType_Parenthesized_SemanticModel()
        {
            var source = """
                #pragma warning disable CS0169 // The field is never used
                class C
                {
                    ((int) -> string)[] F;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();

            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var parenthesized = tree.GetRoot().DescendantNodes().OfType<ParenthesizedTypeSyntax>().Single();
            Assert.Equal("System.Func<System.Int32, System.String>", model.GetTypeInfo(parenthesized).Type.ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32, System.String>", model.GetSymbolInfo(parenthesized).Symbol.ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32, System.String>", model.GetTypeInfo(parenthesized.Type).Type.ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32, System.String>[]", model.GetTypeInfo(parenthesized.Parent!).Type.ToTestDisplayString());
            Assert.True(SyntaxFacts.IsInTypeOnlyContext(parenthesized.Type));
            Assert.True(SyntaxFacts.IsTypeSyntax(SyntaxKind.ParenthesizedType));
        }

        [Fact]
        public void FunctionType_Pattern_Basic()
        {
            // Function types in declaration, type, recursive, list and property patterns, and in switch statements and
            // expressions.
            var source = """
                using System;

                object[] values = [(Func<int, int>)(i => i + 1), (Action)(() => Console.Write("tick")), new Func<int, int>[] { i => i * 2 }, (Func<string, int>)(s => s.Length), "text"];
                foreach (var o in values)
                {
                    Console.Write(Describe(o) + " " + Classify(o) + " " + (o is (int) -> int ? "func" : "not") + (o is not () -> void) + "; ");
                }

                Console.Write(values is [(int) -> int first, ..] ? first(10) : -1);
                Console.Write(new Box(values[0]) is { Value: (int) -> int g } ? " " + g(20) : " none");
                Console.Write(values[1] is () -> void { } a and not null ? " action" : " none");

                static string Describe(object o) => o switch
                {
                    (int) -> int f => "f" + f(1),
                    () -> void a => Run(a),
                    ((int) -> int)[] fs => "fs" + fs[0](3),
                    (string) -> int => "string",
                    _ => "other",
                };

                static string Run(() -> void a) { a(); return ""; }

                static string Classify(object o)
                {
                    switch (o)
                    {
                        case (int) -> int f when f(0) > 0:
                            return "positive";
                        case (int) -> int:
                            return "zero";
                        case () -> void:
                            return "action";
                        default:
                            return "other";
                    }
                }

                record Box(object Value);
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "f2 positive funcTrue; tick action notFalse; fs6 other notTrue; string other notTrue; other other notTrue; 11 21 action" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionType_Pattern_SameAsFunc()
        {
            // A function type pattern reports what its Func twin reports.
            var source = """
                #nullable enable
                using System;

                class C
                {
                    void M(object o, Func<int, int> f)
                    {
                        switch (o)
                        {
                            case Func<int, int>: break;
                            case (int) -> int: break;
                        }
                        switch (o)
                        {
                            case (int) -> int: break;
                            case Func<int, int>: break;
                        }
                        _ = o is ((int) -> int)? a;
                        _ = o is Func<int, int>? b;
                        _ = f is (int) -> int;
                        _ = f is Func<int, int>;
                        _ = o is (ref int) -> int c;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (11,18): error CS8120: The switch case is unreachable. It has already been handled by a previous case or it is impossible to match.
                //             case (int) -> int: break;
                Diagnostic(ErrorCode.ERR_SwitchCaseSubsumed, "(int) -> int").WithLocation(11, 18),
                // (16,18): error CS8120: The switch case is unreachable. It has already been handled by a previous case or it is impossible to match.
                //             case Func<int, int>: break;
                Diagnostic(ErrorCode.ERR_SwitchCaseSubsumed, "Func<int, int>").WithLocation(16, 18),
                // (18,18): error CS8116: It is not legal to use nullable type 'Func<int, int>?' in a pattern; use the underlying type 'Func<int, int>' instead.
                //         _ = o is ((int) -> int)? a;
                Diagnostic(ErrorCode.ERR_PatternNullableType, "((int) -> int)?").WithArguments("System.Func<int, int>").WithLocation(18, 18),
                // (19,18): error CS8116: It is not legal to use nullable type 'Func<int, int>?' in a pattern; use the underlying type 'Func<int, int>' instead.
                //         _ = o is Func<int, int>? b;
                Diagnostic(ErrorCode.ERR_PatternNullableType, "Func<int, int>?").WithArguments("System.Func<int, int>").WithLocation(19, 18),
                // (22,19): error CS20008: A function type parameter cannot be 'ref', because a function type is a 'System.Func' or 'System.Action' delegate.
                //         _ = o is (ref int) -> int c;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "ref").WithArguments("ref").WithLocation(22, 19));
        }

        [Fact]
        public void FunctionType_Pattern_SemanticModel()
        {
            var source = """
                class C
                {
                    bool M(object o) => o is (int, string) -> bool f && f(1, "");
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();

            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var pattern = tree.GetRoot().DescendantNodes().OfType<DeclarationPatternSyntax>().Single();
            Assert.Equal(SyntaxKind.FunctionType, pattern.Type.Kind());
            Assert.Equal("System.Func<System.Int32, System.String, System.Boolean>", model.GetTypeInfo(pattern.Type).Type.ToTestDisplayString());
            var local = (ILocalSymbol)model.GetDeclaredSymbol(pattern.Designation)!;
            Assert.Equal("System.Func<System.Int32, System.String, System.Boolean>", local.Type.ToTestDisplayString());
        }

        [Fact]
        public void FunctionType_Pattern_IOperation()
        {
            var source = """
                class C
                {
                    bool M(object o)
                    {
                        return /*<bind>*/o is (int) -> int f/*</bind>*/;
                    }
                }
                """;
            var expectedOperationTree = """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'o is (int) -> int f')
                  Value:
                    IParameterReferenceOperation: o (OperationKind.ParameterReference, Type: System.Object) (Syntax: 'o')
                  Pattern:
                    IDeclarationPatternOperation (OperationKind.DeclarationPattern, Type: null) (Syntax: '(int) -> int f') (InputType: System.Object, NarrowedType: System.Func<System.Int32, System.Int32>, DeclaredSymbol: System.Func<System.Int32, System.Int32> f, MatchesNull: False)
                """;
            VerifyOperationTreeAndDiagnosticsForTest<IsPatternExpressionSyntax>(source, expectedOperationTree, DiagnosticDescription.None,
                parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
        }

        [Fact]
        public void FunctionType_Pattern_Disabled()
        {
            // Without Sharper, a function type in a pattern reports the usual C# syntax errors.
            var source = """
                class C
                {
                    bool M(object o) => o is (int) -> int f;
                    int N(object o) => o switch { () -> void => 1, _ => 0 };
                }
                """;
            var comp = CreateCompilation(source, targetFramework: TargetFramework.Net90);
            Assert.Contains(comp.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            Assert.Empty(comp.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<FunctionTypeSyntax>());
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Pattern_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;

            // Every one of these is a syntax error in C#: a parenthesized pattern or expression cannot be followed by `->`.
            foreach (var text in new[] { "x = o is (int) -> int f;", "x = o is (int) -> int;", "x = o is () -> void;", "x = o is not (int) -> int;", "x = o is (int) -> int { } f;", "x = o is ((int) -> int)[] fs;", "x = o is (int) -> int f and not null;", "x = o is (int) -> (int) -> int f;", "x = o is (int, string) -> bool;", "x = o is (int) -> int ? a : b;", "x = o switch { (int) -> int f => 1, _ => 0 };", "x = o switch { (int) -> int f when f(0) > 0 => 1, _ => 0 };", "switch (o) { case () -> void a: break; }", "switch (o) { case (int) -> int: break; }", "x = o is { P: (int) -> int f };", "x = o is [(int) -> int f, ..];", "x = o is (p)->F;", "x = o is ((p)->F)[];" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.True(sharper != statement.ContainsDiagnostics, text);
                Assert.True(sharper == statement.DescendantNodes().OfType<FunctionTypeSyntax>().Any(), text);
            }

            if (sharper)
            {
                // `is` with a type, as for `o is Func<int, int>`.
                var expression = SyntaxFactory.ParseExpression("o is (int) -> int", options: options);
                Assert.Equal(SyntaxKind.FunctionType, ((BinaryExpressionSyntax)expression).Right.Kind());

                // A conditional, as `o is int ? a : b` is.
                expression = SyntaxFactory.ParseExpression("o is (int) -> int ? a : b", options: options);
                var conditional = (ConditionalExpressionSyntax)expression;
                Assert.Equal("int", ((FunctionTypeSyntax)((BinaryExpressionSyntax)conditional.Condition).Right).ReturnType.ToString());

                // A nullable return type before a designation.
                expression = SyntaxFactory.ParseExpression("o is (int) -> int? f", options: options);
                var pattern = (DeclarationPatternSyntax)((IsPatternExpressionSyntax)expression).Pattern;
                Assert.Equal(SyntaxKind.NullableType, ((FunctionTypeSyntax)pattern.Type).ReturnType.Kind());
            }
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Pattern_Parsing_KeepsCSharpMeaning(bool sharper)
        {
            // Patterns that start with `(` keep their C# meaning, with or without Sharper.
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            foreach (var text in new[] { "x = o is (1);", "x = o is (A);", "x = o is (A.B);", "x = o is (int)1;", "x = o is (A, B);", "x = o is (A, B) t;", "x = o is (int, int)[] t;", "x = o is (> 1 and < 3);", "x = o is (var a, var b);", "x = o is (A) { };", "x = o is > (p)->F;", "x = o is p->F;", "x = o is (int)p->F;", "x = o is ((A, B), C);", "x = o switch { (1) => a, (A, B) => b, _ => c };", "switch (o) { case (1): break; case (A, B) t: break; }", "x = o is (A) ? a : b;", "x = o is (A) and B;" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.False(statement.ContainsDiagnostics, text);
                Assert.Empty(statement.DescendantNodes().OfType<FunctionTypeSyntax>());
                Assert.True(statement.IsEquivalentTo(SyntaxFactory.ParseStatement(text, options: TestOptions.Regular)), text);
            }
        }

        [Theory]
        [CombinatorialData]
        public void FunctionType_Pattern_PointerMemberAccess(bool sharper)
        {
            // `e is (p)->F` does not compile in C# (a syntax error), and neither with Sharper, where it is a type pattern
            // with an unknown type. The C# constant pattern is written `e is p->F`, which keeps its meaning.
            var source = """
                unsafe class C
                {
                    bool M(object e, S* p) => e is (p)->F;
                    bool N(object e, S* p) => e is p->F;
                }

                struct S { public int F; }
                """;
            var comp = CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90,
                options: TestOptions.UnsafeReleaseDll);
            if (sharper)
            {
                comp.VerifyDiagnostics(
                    // (3,37): error CS0118: 'p' is a variable but is used like a type
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_BadSKknown, "p").WithArguments("p", "variable", "type").WithLocation(3, 37),
                    // (3,41): error CS0246: The type or namespace name 'F' could not be found (are you missing a using directive or an assembly reference?)
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "F").WithArguments("F").WithLocation(3, 41),
                    // (4,36): error CS0150: A constant value is expected
                    //     bool N(object e, S* p) => e is p->F;
                    Diagnostic(ErrorCode.ERR_ConstantExpected, "p->F").WithLocation(4, 36),
                    // (7,23): warning CS0649: Field 'S.F' is never assigned to, and will always have its default value 0
                    // struct S { public int F; }
                    Diagnostic(ErrorCode.WRN_UnassignedInternalField, "F").WithArguments("S.F", "0").WithLocation(7, 23));
            }
            else
            {
                comp.VerifyDiagnostics(
                    // (3,37): error CS0029: Cannot implicitly convert type 'S*' to 'object'
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_NoImplicitConv, "p").WithArguments("S*", "object").WithLocation(3, 37),
                    // (3,39): error CS1002: ; expected
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_SemicolonExpected, "->").WithLocation(3, 39),
                    // (3,39): error CS1519: Invalid token '->' in a member declaration
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_InvalidMemberDecl, "->").WithArguments("->").WithLocation(3, 39),
                    // (3,42): error CS1519: Invalid token ';' in a member declaration
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_InvalidMemberDecl, ";").WithArguments(";").WithLocation(3, 42),
                    // (3,42): error CS1519: Invalid token ';' in a member declaration
                    //     bool M(object e, S* p) => e is (p)->F;
                    Diagnostic(ErrorCode.ERR_InvalidMemberDecl, ";").WithArguments(";").WithLocation(3, 42),
                    // (4,36): error CS0150: A constant value is expected
                    //     bool N(object e, S* p) => e is p->F;
                    Diagnostic(ErrorCode.ERR_ConstantExpected, "p->F").WithLocation(4, 36),
                    // (7,23): warning CS0649: Field 'S.F' is never assigned to, and will always have its default value 0
                    // struct S { public int F; }
                    Diagnostic(ErrorCode.WRN_UnassignedInternalField, "F").WithArguments("S.F", "0").WithLocation(7, 23));
            }
        }

        [Fact]
        public void FunctionType_Pattern_DeeplyNestedParentheses()
        {
            // Every pattern that starts with `(` is scanned for a function type, without taking exponential time.
            var depth = 200;
            var text = "x = o is " + new string('(', depth) + "1" + new string(')', depth) + ";";
            var statement = SyntaxFactory.ParseStatement(text, options: s_sharperOptions);
            Assert.False(statement.ContainsDiagnostics);
            Assert.True(statement.IsEquivalentTo(SyntaxFactory.ParseStatement(text, options: TestOptions.Regular)));
        }

        [Fact]
        public void FunctionType_Parenthesized_SyntaxKind()
        {
            // Sharper syntax nodes sit apart from the upstream ones; an upstream merge must not reuse the value.
            Assert.Equal(9902, (int)SyntaxKind.ParenthesizedType);
            Assert.Single(Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == (int)SyntaxKind.ParenthesizedType);
        }

        [Fact]
        public void TaskType_Basic()
        {
            var source = """
                #nullable enable
                using System;
                using System.Threading.Tasks;

                int~ answer = Answer();
                void~ done = Run();
                string?~ maybe = Maybe(false);
                Console.Write(await answer);
                await done;
                Console.Write(" " + (await maybe is null));
                Console.Write(" " + await Local());
                int~ Local() => Task.FromResult(7);

                // Each task type is the Task it stands for.
                Console.Write(" " + (typeof(int~) == typeof(Task<int>)));
                Console.Write(" " + (typeof(void~) == typeof(Task)));
                Console.Write(" " + (typeof(int[..]~) == typeof(Task<System.Collections.Generic.IEnumerable<int>>)));
                Console.Write(" " + (typeof(int~[..]) == typeof(System.Collections.Generic.IEnumerable<Task<int>>)));
                Console.Write(" " + (typeof(int~[]) == typeof(Task<int>[])));
                Console.Write(" " + (typeof(int[]~) == typeof(Task<int[]>)));
                Console.Write(" " + (typeof(int~~) == typeof(Task<Task<int>>)));
                Console.Write(" " + (typeof((int) -> int~) == typeof(Func<int, Task<int>>)));
                Console.Write(" " + (typeof(() -> void~) == typeof(Func<Task>)));

                static async int~ Answer() { await Task.Yield(); return 42; }
                static async void~ Run() => await Task.Yield();
                static async string?~ Maybe(bool b) { await Task.Yield(); return b ? "x" : null; }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "42 True 7 True True True True True True True True True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TaskType_TypePositions()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;
                using IntTask = int~;

                var c = new C();
                c.Field = Task.FromResult(1);
                c.Property = Task.CompletedTask;
                var list = new List<int~> { Task.FromResult(2), c.Field };
                int~[] array = new int~[2];
                array[0] = Task.FromResult(3);
                IntTask alias = Task.FromResult(4);
                object o = alias;
                var cast = (int~)o;
                var asTask = o as int~;
                var isTask = o is int~ t ? t : null;
                var defaultTask = default(int~);
                (int~ a, string b) tuple = (Task.FromResult(5), "b");
                (int~ x, int y) = (Task.FromResult(6), 0);
                foreach (int~ each in list) { }
                C.Out(out int~ outTask);
                var lambda = int~ (int i) => Task.FromResult(i);
                var asyncLambda = async void~ () => await Task.Yield();
                await asyncLambda();

                Console.Write(await c.Field);
                Console.Write(" " + await list[0] + await array[0] + await alias + await cast + await asTask! + await isTask!);
                Console.Write(" " + (defaultTask is null));
                Console.Write(" " + await tuple.a + await x + await outTask + await lambda(8));
                Console.Write(" " + await C.Generic<string>(Task.FromResult("g")));

                class C
                {
                    public int~ Field = Task.FromResult(0);
                    public void~ Property { get; set; } = Task.CompletedTask;
                    public static void Out(out int~ t) => t = Task.FromResult(7);
                    public static T~ Generic<T>(T~ t) => t;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1 234444 True 5678 g" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TaskType_IdentityWithTask()
        {
            // A task type is its Task, so the two spellings convert both ways and clash as overloads.
            var source = """
                using System.Threading.Tasks;

                class C
                {
                    void M(int~ t) { Task<int> u = t; t = u; }
                    void M(Task<int> t) { }
                    void N(void~ t) { Task u = t; t = u; }
                    void N(Task t) { }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,10): error CS0111: Type 'C' already defines a member called 'M' with the same parameter types
                //     void M(Task<int> t) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "M").WithArguments("M", "C").WithLocation(6, 10),
                // (8,10): error CS0111: Type 'C' already defines a member called 'N' with the same parameter types
                //     void N(Task t) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "N").WithArguments("N", "C").WithLocation(8, 10));
        }

        [Fact]
        public void TaskType_Nullable()
        {
            var source = """
                #nullable enable
                using System.Threading.Tasks;

                class C
                {
                    async string?~ F() { await Task.Yield(); return null; }
                    async Task<string?> FTwin() { await Task.Yield(); return null; }
                    async string~ G() { await Task.Yield(); return null; }
                    async Task<string> GTwin() { await Task.Yield(); return null; }
                    async void M()
                    {
                        string s = await F();
                        string sTwin = await FTwin();
                    }
                    string~? H() => null;
                    Task<string>? HTwin() => null;
                    string~ I() => null;
                    Task<string> ITwin() => null;
                    void~? J() => null;
                }
                """;
            // The same warnings as for the Task twins.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,52): warning CS8603: Possible null reference return.
                //     async string~ G() { await Task.Yield(); return null; }
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "null").WithLocation(8, 52),
                // (9,61): warning CS8603: Possible null reference return.
                //     async Task<string> GTwin() { await Task.Yield(); return null; }
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "null").WithLocation(9, 61),
                // (12,20): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         string s = await F();
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "await F()").WithLocation(12, 20),
                // (13,24): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         string sTwin = await FTwin();
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "await FTwin()").WithLocation(13, 24),
                // (17,20): warning CS8603: Possible null reference return.
                //     string~ I() => null;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "null").WithLocation(17, 20),
                // (18,29): warning CS8603: Possible null reference return.
                //     Task<string> ITwin() => null;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "null").WithLocation(18, 29));
        }

        [Fact]
        public void TaskType_Errors()
        {
            var source = """
                #pragma warning disable CS0169 // The field is never used
                unsafe class C
                {
                    int*~ pointer;
                    System.Threading.Tasks.Task<int*> pointerTwin;
                    System.Span<int>~ span;
                    System.Threading.Tasks.Task<System.Span<int>> spanTwin;
                    System.Console~ staticResult;
                    System.Threading.Tasks.Task<System.Console> staticResultTwin;
                    void~ ok;
                    void M()
                    {
                        int*~ local = null;
                        System.Threading.Tasks.Task<int*> localTwin = null;
                        System.Span<int>~ spanLocal = null;
                        System.Threading.Tasks.Task<System.Span<int>> spanLocalTwin = null;
                        System.Console~ staticLocal = null;
                        System.Threading.Tasks.Task<System.Console> staticLocalTwin = null;
                        int*~~ nested = null;
                        System.Threading.Tasks.Task<System.Threading.Tasks.Task<int*>> nestedTwin = null;
                        int*[..]~ sequence = null;
                        System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<int*>> sequenceTwin = null;
                        (int*) -> int~ function = null;
                        System.Func<int*, System.Threading.Tasks.Task<int>> functionTwin = null;
                        S<string>~ constrained = null;
                        System.Threading.Tasks.Task<S<string>> constrainedTwin = null;
                    }
                    void P(int*~ p, System.Threading.Tasks.Task<int*> pTwin, S<string>~ q, System.Threading.Tasks.Task<S<string>> qTwin) { }
                    S<string>~ constrainedField;
                    System.Threading.Tasks.Task<S<string>> constrainedFieldTwin;
                }
                struct S<T> where T : struct { }
                """;
            // Constraint errors are the same as for the Task<T> twins, and are reported at the same place: on the
            // result type.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.UnsafeReleaseDll).VerifyDiagnostics(
                // (4,11): error CS0306: The type 'int*' may not be used as a type argument
                //     int*~ pointer;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "pointer").WithArguments("int*").WithLocation(4, 11),
                // (5,39): error CS0306: The type 'int*' may not be used as a type argument
                //     System.Threading.Tasks.Task<int*> pointerTwin;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "pointerTwin").WithArguments("int*").WithLocation(5, 39),
                // (6,23): error CS9244: The type 'Span<int>' may not be a ref struct or a type parameter allowing ref structs in order to use it as parameter 'TResult' in the generic type or method 'Task<TResult>'
                //     System.Span<int>~ span;
                Diagnostic(ErrorCode.ERR_NotRefStructConstraintNotSatisfied, "span").WithArguments("System.Threading.Tasks.Task<TResult>", "TResult", "System.Span<int>").WithLocation(6, 23),
                // (7,51): error CS9244: The type 'Span<int>' may not be a ref struct or a type parameter allowing ref structs in order to use it as parameter 'TResult' in the generic type or method 'Task<TResult>'
                //     System.Threading.Tasks.Task<System.Span<int>> spanTwin;
                Diagnostic(ErrorCode.ERR_NotRefStructConstraintNotSatisfied, "spanTwin").WithArguments("System.Threading.Tasks.Task<TResult>", "TResult", "System.Span<int>").WithLocation(7, 51),
                // (8,21): error CS0718: 'Console': static types cannot be used as type arguments
                //     System.Console~ staticResult;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "staticResult").WithArguments("System.Console").WithLocation(8, 21),
                // (9,49): error CS0718: 'Console': static types cannot be used as type arguments
                //     System.Threading.Tasks.Task<System.Console> staticResultTwin;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "staticResultTwin").WithArguments("System.Console").WithLocation(9, 49),
                // (13,9): error CS0306: The type 'int*' may not be used as a type argument
                //         int*~ local = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(13, 9),
                // (13,15): warning CS0219: The variable 'local' is assigned but its value is never used
                //         int*~ local = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "local").WithArguments("local").WithLocation(13, 15),
                // (14,37): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Threading.Tasks.Task<int*> localTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(14, 37),
                // (14,43): warning CS0219: The variable 'localTwin' is assigned but its value is never used
                //         System.Threading.Tasks.Task<int*> localTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "localTwin").WithArguments("localTwin").WithLocation(14, 43),
                // (15,9): error CS9244: The type 'Span<int>' may not be a ref struct or a type parameter allowing ref structs in order to use it as parameter 'TResult' in the generic type or method 'Task<TResult>'
                //         System.Span<int>~ spanLocal = null;
                Diagnostic(ErrorCode.ERR_NotRefStructConstraintNotSatisfied, "System.Span<int>").WithArguments("System.Threading.Tasks.Task<TResult>", "TResult", "System.Span<int>").WithLocation(15, 9),
                // (15,27): warning CS0219: The variable 'spanLocal' is assigned but its value is never used
                //         System.Span<int>~ spanLocal = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "spanLocal").WithArguments("spanLocal").WithLocation(15, 27),
                // (16,37): error CS9244: The type 'Span<int>' may not be a ref struct or a type parameter allowing ref structs in order to use it as parameter 'TResult' in the generic type or method 'Task<TResult>'
                //         System.Threading.Tasks.Task<System.Span<int>> spanLocalTwin = null;
                Diagnostic(ErrorCode.ERR_NotRefStructConstraintNotSatisfied, "System.Span<int>").WithArguments("System.Threading.Tasks.Task<TResult>", "TResult", "System.Span<int>").WithLocation(16, 37),
                // (16,55): warning CS0219: The variable 'spanLocalTwin' is assigned but its value is never used
                //         System.Threading.Tasks.Task<System.Span<int>> spanLocalTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "spanLocalTwin").WithArguments("spanLocalTwin").WithLocation(16, 55),
                // (17,9): error CS0718: 'Console': static types cannot be used as type arguments
                //         System.Console~ staticLocal = null;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "System.Console").WithArguments("System.Console").WithLocation(17, 9),
                // (17,25): warning CS0219: The variable 'staticLocal' is assigned but its value is never used
                //         System.Console~ staticLocal = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "staticLocal").WithArguments("staticLocal").WithLocation(17, 25),
                // (18,37): error CS0718: 'Console': static types cannot be used as type arguments
                //         System.Threading.Tasks.Task<System.Console> staticLocalTwin = null;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "System.Console").WithArguments("System.Console").WithLocation(18, 37),
                // (18,53): warning CS0219: The variable 'staticLocalTwin' is assigned but its value is never used
                //         System.Threading.Tasks.Task<System.Console> staticLocalTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "staticLocalTwin").WithArguments("staticLocalTwin").WithLocation(18, 53),
                // (19,9): error CS0306: The type 'int*' may not be used as a type argument
                //         int*~~ nested = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(19, 9),
                // (19,16): warning CS0219: The variable 'nested' is assigned but its value is never used
                //         int*~~ nested = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "nested").WithArguments("nested").WithLocation(19, 16),
                // (20,65): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Threading.Tasks.Task<System.Threading.Tasks.Task<int*>> nestedTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(20, 65),
                // (20,72): warning CS0219: The variable 'nestedTwin' is assigned but its value is never used
                //         System.Threading.Tasks.Task<System.Threading.Tasks.Task<int*>> nestedTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "nestedTwin").WithArguments("nestedTwin").WithLocation(20, 72),
                // (21,9): error CS0306: The type 'int*' may not be used as a type argument
                //         int*[..]~ sequence = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(21, 9),
                // (21,19): warning CS0219: The variable 'sequence' is assigned but its value is never used
                //         int*[..]~ sequence = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "sequence").WithArguments("sequence").WithLocation(21, 19),
                // (22,76): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<int*>> sequenceTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(22, 76),
                // (22,83): warning CS0219: The variable 'sequenceTwin' is assigned but its value is never used
                //         System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<int*>> sequenceTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "sequenceTwin").WithArguments("sequenceTwin").WithLocation(22, 83),
                // (23,10): error CS0306: The type 'int*' may not be used as a type argument
                //         (int*) -> int~ function = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(23, 10),
                // (23,24): warning CS0219: The variable 'function' is assigned but its value is never used
                //         (int*) -> int~ function = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "function").WithArguments("function").WithLocation(23, 24),
                // (24,21): error CS0306: The type 'int*' may not be used as a type argument
                //         System.Func<int*, System.Threading.Tasks.Task<int>> functionTwin = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(24, 21),
                // (24,61): warning CS0219: The variable 'functionTwin' is assigned but its value is never used
                //         System.Func<int*, System.Threading.Tasks.Task<int>> functionTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "functionTwin").WithArguments("functionTwin").WithLocation(24, 61),
                // (25,11): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //         S<string>~ constrained = null;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "string").WithArguments("S<T>", "T", "string").WithLocation(25, 11),
                // (25,20): warning CS0219: The variable 'constrained' is assigned but its value is never used
                //         S<string>~ constrained = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "constrained").WithArguments("constrained").WithLocation(25, 20),
                // (26,39): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //         System.Threading.Tasks.Task<S<string>> constrainedTwin = null;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "string").WithArguments("S<T>", "T", "string").WithLocation(26, 39),
                // (26,48): warning CS0219: The variable 'constrainedTwin' is assigned but its value is never used
                //         System.Threading.Tasks.Task<S<string>> constrainedTwin = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "constrainedTwin").WithArguments("constrainedTwin").WithLocation(26, 48),
                // (28,18): error CS0306: The type 'int*' may not be used as a type argument
                //     void P(int*~ p, System.Threading.Tasks.Task<int*> pTwin, S<string>~ q, System.Threading.Tasks.Task<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "p").WithArguments("int*").WithLocation(28, 18),
                // (28,55): error CS0306: The type 'int*' may not be used as a type argument
                //     void P(int*~ p, System.Threading.Tasks.Task<int*> pTwin, S<string>~ q, System.Threading.Tasks.Task<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "pTwin").WithArguments("int*").WithLocation(28, 55),
                // (28,73): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     void P(int*~ p, System.Threading.Tasks.Task<int*> pTwin, S<string>~ q, System.Threading.Tasks.Task<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "q").WithArguments("S<T>", "T", "string").WithLocation(28, 73),
                // (28,115): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     void P(int*~ p, System.Threading.Tasks.Task<int*> pTwin, S<string>~ q, System.Threading.Tasks.Task<S<string>> qTwin) { }
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "qTwin").WithArguments("S<T>", "T", "string").WithLocation(28, 115),
                // (29,16): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     S<string>~ constrainedField;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "constrainedField").WithArguments("S<T>", "T", "string").WithLocation(29, 16),
                // (30,44): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     System.Threading.Tasks.Task<S<string>> constrainedFieldTwin;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "constrainedFieldTwin").WithArguments("S<T>", "T", "string").WithLocation(30, 44));
        }

        [Fact]
        public void TaskType_Disabled()
        {
            // Without Sharper, the task type reports the usual C# errors.
            var source = """
                class C
                {
                    void M()
                    {
                        int~ t = null;
                        void~ u = null;
                    }
                }
                """;
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,12): error CS1001: Identifier expected
                //         int~ t = null;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "~").WithLocation(5, 12),
                // (5,12): error CS1002: ; expected
                //         int~ t = null;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "~").WithLocation(5, 12),
                // (5,14): error CS0103: The name 't' does not exist in the current context
                //         int~ t = null;
                Diagnostic(ErrorCode.ERR_NameNotInContext, "t").WithArguments("t").WithLocation(5, 14),
                // (6,9): error CS1547: Keyword 'void' cannot be used in this context
                //         void~ u = null;
                Diagnostic(ErrorCode.ERR_NoVoidHere, "void").WithLocation(6, 9),
                // (6,13): error CS1001: Identifier expected
                //         void~ u = null;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "~").WithLocation(6, 13),
                // (6,13): error CS1002: ; expected
                //         void~ u = null;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "~").WithLocation(6, 13),
                // (6,15): error CS0103: The name 'u' does not exist in the current context
                //         void~ u = null;
                Diagnostic(ErrorCode.ERR_NameNotInContext, "u").WithArguments("u").WithLocation(6, 15));
        }

        [Fact]
        public void TaskType_FactoryWithoutSharper()
        {
            // A task type built with SyntaxFactory in a C# tree reports that Sharper is not enabled.
            var tree = SyntaxFactory.ParseSyntaxTree("class C { int~ F; }", s_sharperOptions);
            var csharpTree = tree.WithRootAndOptions(tree.GetRoot(), TestOptions.Regular);
            CreateCompilation(csharpTree, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,14): error CS8058: Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.
                // class C { int~ F; }
                Diagnostic(ErrorCode.ERR_FeatureIsExperimental, "~").WithArguments("Sharper", "sharper").WithLocation(1, 14),
                // (1,16): warning CS0169: The field 'C.F' is never used
                // class C { int~ F; }
                Diagnostic(ErrorCode.WRN_UnreferencedField, "F").WithArguments("C.F").WithLocation(1, 16));
        }

        [Theory]
        [CombinatorialData]
        public void TaskType_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;

            // Every one of these is a syntax error in C#.
            foreach (var text in new[] { "User~ x = y;", "User~ x;", "void~ x;", "User?~ x;", "User~? x;", "User~[] x;", "User[]~ x;", "User~~ x;", "List<User~> x;", "(int~, string) x;", "(User~ u, int n) = y;", "(List<int>~ a, int b) = y;", "int~ F() => null;", "using (User~ x = y) { }", "for (User~ x = y; ; ) { }", "foreach (User~ x in y) { }", "M(out User~ x);", "x = (User~)y;", "x = y is User~ u;", "x = y as User~;", "x = typeof(void~);", "x = new User~[3];", "x = F<User~>(y);" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.True(sharper != statement.ContainsDiagnostics, text);
                Assert.True(sharper == statement.DescendantNodes().OfType<TaskTypeSyntax>().Any(), text);
            }

            // `A.B < C > ~x;` parses in C# as a comparison, which is not a valid statement (CS0201), so the declaration
            // takes nothing from C#.
            var generic = SyntaxFactory.ParseStatement("A.B<C>~ x;", options: options);
            Assert.False(generic.ContainsDiagnostics);
            Assert.Equal(sharper ? SyntaxKind.LocalDeclarationStatement : SyntaxKind.ExpressionStatement, generic.Kind());
        }

        [Theory]
        [CombinatorialData]
        public void TaskType_Parsing_KeepsCSharpMeaning(bool sharper)
        {
            // `~` keeps its C# meaning, the prefix complement, with or without Sharper.
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            foreach (var text in new[] { "x = ~y;", "x = (T)~y;", "x = c ? ~y : z;", "x = y is T ? ~a : b;", "x = y as T ? ~a : b;", "using (c ? ~x : y) { }", "x = (A < B > ~c, d);", "x = (a, A < B > ~c);", "M(A < B > ~c);", "x = a < b > ~c;", "x = F<T>(~y);", "x = y[~i];" })
            {
                var statement = SyntaxFactory.ParseStatement(text, options: options);
                Assert.False(statement.ContainsDiagnostics, text);
                Assert.Empty(statement.DescendantNodes().OfType<TaskTypeSyntax>());
            }
        }

        [Fact]
        public void TaskType_SemanticModel()
        {
            var source = """
                class C
                {
                    string~ F;
                    void~ G;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var taskTypes = tree.GetRoot().DescendantNodes().OfType<TaskTypeSyntax>().ToArray();
            Assert.Equal("System.Threading.Tasks.Task<System.String>", model.GetTypeInfo(taskTypes[0]).Type.ToTestDisplayString());
            Assert.Equal("System.Threading.Tasks.Task<System.String>", model.GetSymbolInfo(taskTypes[0]).Symbol.ToTestDisplayString());
            Assert.Equal("System.String", model.GetTypeInfo(taskTypes[0].ElementType).Type.ToTestDisplayString());
            Assert.Equal("System.Threading.Tasks.Task", model.GetTypeInfo(taskTypes[1]).Type.ToTestDisplayString());
            Assert.True(SyntaxFacts.IsInTypeOnlyContext(taskTypes[0].ElementType));
            Assert.True(SyntaxFacts.IsTypeSyntax(SyntaxKind.TaskType));
        }

        [Fact]
        public void TaskType_SyntaxKind()
        {
            // Sharper syntax nodes sit apart from the upstream ones; an upstream merge must not reuse the value.
            Assert.Equal(9901, (int)SyntaxKind.TaskType);
            Assert.Single(Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == (int)SyntaxKind.TaskType);
        }

        [Fact]
        public void ValKeyword_SyntaxKind()
        {
            // ValKeyword sits apart from the upstream contextual keywords; an upstream merge must not reuse its value
            // (duplicate enum values compile silently).
            Assert.Equal(8466, (int)SyntaxKind.ValKeyword);
            Assert.Single(Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == (int)SyntaxKind.ValKeyword);
            Assert.True(SyntaxKind.ValKeyword < SyntaxKind.ElifKeyword);

            // A ValKeyword token, which only the parser makes, has the text val and is a keyword.
            Assert.Equal("val", SyntaxFacts.GetText(SyntaxKind.ValKeyword));
            Assert.True(SyntaxFacts.IsContextualKeyword(SyntaxKind.ValKeyword));
            Assert.True(SyntaxFacts.IsKeywordKind(SyntaxKind.ValKeyword));
        }

        [Fact]
        public void ValKeyword_SyntaxFactsLikeUpstream()
        {
            // Unlike var, val is not a contextual keyword of C#: callers such as Razor treat every contextual keyword as one
            // everywhere, which broke valid code that names something val (https://github.com/ok3mad/roslyn/issues/109).
            Assert.Equal(SyntaxKind.None, SyntaxFacts.GetContextualKeywordKind("val"));
            Assert.DoesNotContain(SyntaxKind.ValKeyword, SyntaxFacts.GetContextualKeywordKinds());
            Assert.DoesNotContain("val", SyntaxFacts.GetContextualKeywordKinds().Select(SyntaxFacts.GetText));
            Assert.Equal(SyntaxKind.None, SyntaxFacts.GetKeywordKind("val"));
            Assert.False(SyntaxFacts.IsReservedKeyword(SyntaxFacts.GetKeywordKind("val")));
            Assert.Equal(SyntaxKind.VarKeyword, SyntaxFacts.GetContextualKeywordKind("var"));

            // The lexer leaves val an identifier, with or without Sharper; only the parser makes the keyword.
            foreach (var options in new[] { TestOptions.Regular, s_sharperOptions })
            {
                foreach (var text in new[] { "val", "@val", "v\\u0061l" })
                {
                    var token = SyntaxFactory.ParseTokens(text, options: options).First();
                    Assert.Equal(SyntaxKind.IdentifierToken, token.Kind());
                    Assert.Equal(SyntaxKind.IdentifierToken, token.ContextualKind());
                    Assert.False(SyntaxFacts.IsContextualKeyword(token.ContextualKind()));
                }

                var tokens = SyntaxFactory.ParseTokens("val x = val.y; val (a, b) = e;", options: options);
                Assert.DoesNotContain(tokens, t => t.IsKind(SyntaxKind.ValKeyword) || t.ContextualKind() == SyntaxKind.ValKeyword);
            }
        }

        [Theory]
        [InlineData("o is val x", 5)]
        [InlineData("o is (val a, var b)", 6)]
        [InlineData("o is { P: val p }", 10)]
        [InlineData("o is val (a, b)", 5)]
        [InlineData("o switch { val x => x }", 11)]
        public void ValKeyword_PatternToken(string source, int position)
        {
            // With Sharper on, the parser makes the keyword of a val pattern a ValKeyword token; with it off, val stays an
            // identifier, as in C#.
            var sharper = SyntaxFactory.ParseExpression(source, options: s_sharperOptions);
            var keyword = sharper.FindToken(position);
            Assert.Equal(SyntaxKind.ValKeyword, keyword.Kind());
            Assert.Equal(SyntaxKind.ValKeyword, keyword.ContextualKind());
            Assert.Equal("val", keyword.Text);
            Assert.True(keyword.IsKeyword());
            Assert.IsType<VarPatternSyntax>(keyword.Parent);
            Assert.Equal(source, sharper.ToFullString());

            var csharp = SyntaxFactory.ParseExpression(source, options: TestOptions.Regular);
            var identifier = csharp.FindToken(position);
            Assert.Equal(SyntaxKind.IdentifierToken, identifier.Kind());
            Assert.Equal(SyntaxKind.IdentifierToken, identifier.ContextualKind());
            Assert.Equal("val", identifier.Text);
            Assert.DoesNotContain(csharp.DescendantTokens(), t => t.IsKind(SyntaxKind.ValKeyword));
        }

        [Fact]
        public void ValKeyword_PatternTokenKeepsTriviaAndOthersStayIdentifiers()
        {
            // The keyword keeps the identifier's trivia; @val and val used as a name stay identifiers.
            var parsed = (IsPatternExpressionSyntax)SyntaxFactory.ParseExpression("o is\n/*a*/ val /*b*/ x", options: s_sharperOptions);
            var keyword = ((VarPatternSyntax)parsed.Pattern).VarKeyword;
            Assert.Equal(SyntaxKind.ValKeyword, keyword.Kind());
            Assert.Equal("/*a*/ val /*b*/ ", keyword.ToFullString());

            var escaped = (IsPatternExpressionSyntax)SyntaxFactory.ParseExpression("o is @val x", options: s_sharperOptions);
            Assert.Equal(SyntaxKind.DeclarationPattern, escaped.Pattern.Kind());

            var tree = SyntaxFactory.ParseSyntaxTree("val.M(val); var val = 1; int x = val!.Length + val?.Length;", s_sharperOptions);
            Assert.DoesNotContain(tree.GetRoot().DescendantTokens(), t => t.IsKind(SyntaxKind.ValKeyword) || t.ContextualKind() == SyntaxKind.ValKeyword);
        }

        [Fact]
        public void ValKeyword_FactoryRoundTrip()
        {
            // Rebuilding a val pattern from its kind keeps val (a VarKeyword token with the text val would come back as var).
            var token = SyntaxFactory.Token(SyntaxKind.ValKeyword);
            Assert.Equal("val", token.Text);
            var pattern = SyntaxFactory.VarPattern(token, SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("x")));
            Assert.Equal("val x", pattern.NormalizeWhitespace().ToFullString());

            var parsed = (IsPatternExpressionSyntax)SyntaxFactory.ParseExpression("o is val x", options: s_sharperOptions);
            var rebuilt = ((VarPatternSyntax)parsed.Pattern).WithVarKeyword(SyntaxFactory.Token(((VarPatternSyntax)parsed.Pattern).VarKeyword.Kind()));
            Assert.Equal("val", rebuilt.VarKeyword.Text);
            Assert.Throws<ArgumentException>(() => SyntaxFactory.VarPattern(SyntaxFactory.Token(SyntaxKind.LetKeyword), SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("x"))));
        }

        [Fact]
        public void ValKeyword_VarPatternFactoryWithoutToken()
        {
            // The shipped overload without a keyword token still exists, and still creates a var pattern
            // (https://github.com/ok3mad/roslyn/issues/61).
            var pattern = SyntaxFactory.VarPattern(SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("x")));
            Assert.Equal(SyntaxKind.VarKeyword, pattern.VarKeyword.Kind());
            Assert.Equal("var x", pattern.NormalizeWhitespace().ToFullString());
        }

        [Theory]
        [InlineData("int[..]")]
        [InlineData("IEnumerable<int>")]
        public void SequenceType_Runtime(string sequence)
        {
            // int[..] is IEnumerable<int>; each test runs the Sharper form next to its C# twin.
            var source = $$"""
                using System;
                using System.Collections.Generic;
                using System.Linq;

                {{sequence}} xs = new[] { 1, 2, 3, 4 };
                Console.Write(string.Join(",", C.Evens(xs)));
                Console.Write(C.Field.Count());
                Console.Write(new List<{{sequence}}> { xs }.Count);
                Func<{{sequence}}, int> sum = ({{sequence}} s) => s.Sum();
                Console.Write(sum(xs));
                Console.Write(C.Iterate().Last());

                class C
                {
                    public static {{sequence}} Field = new List<int> { 5 };
                    public static {{sequence}} Evens({{sequence}} xs) => xs.Where(x => x % 2 == 0);
                    public static {{sequence}} Iterate()
                    {
                        yield return 6;
                        yield return 7;
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "2,411107" : null).VerifyDiagnostics();
        }

        [Fact]
        public void SequenceType_FlagOff()
        {
            var source = """
                class C
                {
                    int[..] F;
                    int[..] M(int[..] xs)
                    {
                        int[..] local = xs;
                        System.Func<int[..], int> f = null;
                        return local;
                    }
                }
                """;
            // Without Sharper, the parser does not take a generic argument int[..] for a type, so the statement with
            // Func<int[..], int> is not a declaration.
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,8): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //     int[..] F;
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[..]").WithLocation(3, 8),
                // (3,13): warning CS0169: The field 'C.F' is never used
                //     int[..] F;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "F").WithArguments("C.F").WithLocation(3, 13),
                // (4,8): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //     int[..] M(int[..] xs)
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[..]").WithLocation(4, 8),
                // (4,18): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //     int[..] M(int[..] xs)
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[..]").WithLocation(4, 18),
                // (6,12): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //         int[..] local = xs;
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[..]").WithLocation(6, 12),
                // (6,13): error CS0029: Cannot implicitly convert type 'System.Range' to 'int'
                //         int[..] local = xs;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "..").WithArguments("System.Range", "int").WithLocation(6, 13),
                // (7,16): error CS0305: Using the generic type 'Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, TResult>' requires 17 type arguments
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_BadArity, "Func").WithArguments("System.Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, TResult>", "type", "17").WithLocation(7, 16),
                // (7,21): error CS1525: Invalid expression term 'int'
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "int").WithArguments("int").WithLocation(7, 21),
                // (7,28): error CS1002: ; expected
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, ",").WithLocation(7, 28),
                // (7,28): error CS1513: } expected
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_RbraceExpected, ",").WithLocation(7, 28),
                // (7,33): error CS1001: Identifier expected
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ">").WithLocation(7, 33),
                // (7,33): error CS1002: ; expected
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, ">").WithLocation(7, 33),
                // (7,33): error CS1525: Invalid expression term '>'
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, ">").WithArguments(">").WithLocation(7, 33),
                // (7,35): error CS0103: The name 'f' does not exist in the current context
                //         System.Func<int[..], int> f = null;
                Diagnostic(ErrorCode.ERR_NameNotInContext, "f").WithArguments("f").WithLocation(7, 35));
        }

        [Fact]
        public void SequenceType_Parsing()
        {
            // T[..] reuses the array type syntax: one rank whose size is a range with no operands. The parser change is
            // only that a statement may start with it in a generic argument or a tuple element.
            var statement = SyntaxFactory.ParseStatement("Func<A[..], (int, B[..])> f = null;", options: s_sharperOptions);
            var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(statement);
            var arrayTypes = declaration.Declaration.Type.DescendantNodes().OfType<ArrayTypeSyntax>().ToArray();
            Assert.Equal(["A[..]", "B[..]"], arrayTypes.Select(t => t.ToString()));
            Assert.All(arrayTypes, t => Assert.IsType<RangeExpressionSyntax>(Assert.Single(Assert.Single(t.RankSpecifiers).Sizes)));
            Assert.False(statement.ContainsDiagnostics);

            Assert.IsNotType<LocalDeclarationStatementSyntax>(SyntaxFactory.ParseStatement("Func<A[..], (int, B[..])> f = null;", options: TestOptions.Regular));

            // Expressions are unchanged: a[..] and a?[..] are still element accesses, also where a cast could start.
            foreach (var options in new[] { s_sharperOptions, TestOptions.Regular })
            {
                Assert.IsType<BinaryExpressionSyntax>(SyntaxFactory.ParseExpression("(a[..]) - 1", options: options));
                Assert.IsType<InvocationExpressionSyntax>(SyntaxFactory.ParseExpression("(a[..])(b)", options: options));
                Assert.IsType<InvocationExpressionSyntax>(SyntaxFactory.ParseExpression("(a?[..])(b)", options: options));
                Assert.IsType<PostfixUnaryExpressionSyntax>(SyntaxFactory.ParseExpression("(a[..])!", options: options));
                Assert.IsType<MemberAccessExpressionSyntax>(SyntaxFactory.ParseExpression("(a?[..]).b", options: options));
                Assert.IsType<WithExpressionSyntax>(SyntaxFactory.ParseExpression("(a[..]) with { }", options: options));
                Assert.IsType<ParenthesizedExpressionSyntax>(SyntaxFactory.ParseExpression("(a?[..])", options: options));
                Assert.IsType<ExpressionStatementSyntax>(SyntaxFactory.ParseStatement("a[..] = b;", options: options));
            }

            // Accepted break: like F<a>(x), F<a[..]>(x) is a generic call, where C# reads two comparisons (valid only
            // with user-defined operators). The sharpener will parenthesize: F < (a[..]) > (x).
            Assert.IsType<GenericNameSyntax>(((InvocationExpressionSyntax)SyntaxFactory.ParseExpression("F<a[..]>(x)", options: s_sharperOptions)).Expression);
            Assert.IsType<BinaryExpressionSyntax>(SyntaxFactory.ParseExpression("F<a[..]>(x)", options: TestOptions.Regular));
            Assert.IsType<BinaryExpressionSyntax>(SyntaxFactory.ParseExpression("F < (a[..]) > (x)", options: s_sharperOptions));

            // Casts to a sequence type: definite for int[..], and for A[..] before an identifier or a literal.
            Assert.IsType<CastExpressionSyntax>(SyntaxFactory.ParseExpression("(int[..])(a)", options: s_sharperOptions));
            Assert.IsType<CastExpressionSyntax>(SyntaxFactory.ParseExpression("(A[..])b", options: s_sharperOptions));
            Assert.IsType<CastExpressionSyntax>(SyntaxFactory.ParseExpression("(A[..])null", options: s_sharperOptions));
            Assert.IsType<CastExpressionSyntax>(SyntaxFactory.ParseExpression("(int?[..])b", options: s_sharperOptions));
        }

        [Fact]
        public void SequenceType_TypePositions()
        {
            // Every member signature reads the same as with IEnumerable<T> spelled out.
            var template = """
                #nullable enable
                using System;
                using System.Collections.Generic;
                using System.Linq;
                using Seq = {2};

                delegate {0} D({0} xs);

                interface I<T> where T : {0} {{ }}

                class C<T> : List<{0}>
                {{
                    public {0} Field = null!;
                    public {0} Property {{ get; set; }} = null!;
                    public {0} this[{0} xs] => xs;
                    public event Func<{0}>? Event;
                    public {0}? Nullable = null;
                    public {1} Generic = null!;
                    public {0} Method({0} xs, out {0} o, ref {0} r, params {0}[] rest) {{ o = xs; return xs; }}
                    public Seq Alias = null!;
                    public void Body(object o)
                    {{
                        {0} local = Field;
                        {1} generic = Generic;
                        Func<{0}> g = {0} () => generic.Count() > 0 ? local : Field;
                        (int, {0}) tuple = (1, local);
                        _ = tuple;
                        Func<{0}, {0}> f = ({0} s) => s;
                        {0} Local({0} s) => s;
                        _ = Local(local);
                        _ = typeof({0});
                        _ = default({0});
                        _ = ({0})o;
                        _ = o as {0};
                        _ = o is {0} p;
                        _ = o is {0};
                        _ = GenericMethod<{0}>();
                        _ = int.TryParse("1", out _) ? Field : null;
                        foreach ({0} item in new List<{0}>()) {{ }}
                        using var d = (IDisposable)null!;
                        Event?.Invoke();
                    }}
                    T? GenericMethod<U>() => default;
                }}
                """;
            var sharper = CreateCompilation(string.Format(template, "int[..]", "T[..]", "int[..]"), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var csharp = CreateCompilation(string.Format(template, "IEnumerable<int>", "IEnumerable<T>", "System.Collections.Generic.IEnumerable<int>"), targetFramework: TargetFramework.Net90);
            sharper.VerifyDiagnostics();
            csharp.VerifyDiagnostics();
            Assert.Equal(describe(csharp), describe(sharper));

            static string describe(CSharpCompilation comp)
            {
                var lines = new List<string?>();
                foreach (var type in new[] { "D", "I`1", "C`1" }.Select(name => ((Compilation)comp).GetTypeByMetadataName(name)!))
                {
                    lines.Add(type.ToTestDisplayString());
                    lines.Add(type.BaseType?.ToTestDisplayString());
                    lines.AddRange(type.Interfaces.Select(i => i.ToTestDisplayString()));
                    lines.AddRange(type.TypeParameters.SelectMany(t => t.ConstraintTypes).Select(t => t.ToTestDisplayString()));
                    lines.AddRange(type.GetMembers().Select(m => m.ToTestDisplayString()));
                }

                var tree = comp.SyntaxTrees[0];
                var model = comp.GetSemanticModel(tree);
                var nodes = tree.GetRoot().DescendantNodes();
                lines.AddRange(nodes
                    .Where(n => n is VariableDeclaratorSyntax or SingleVariableDesignationSyntax or ForEachStatementSyntax or LocalFunctionStatementSyntax or ParameterSyntax)
                    .Select(n => model.GetDeclaredSymbol(n)?.ToTestDisplayString()));
                lines.AddRange(nodes.OfType<TypeOfExpressionSyntax>().Select(t => model.GetTypeInfo(t.Type).Type.ToTestDisplayString()));
                lines.AddRange(nodes.OfType<CastExpressionSyntax>().Select(t => model.GetTypeInfo(t).Type.ToTestDisplayString()));
                return string.Join(Environment.NewLine, lines);
            }
        }

        [Fact]
        public void SequenceType_Stacking()
        {
            // Sharper type suffixes stack left to right, innermost first: each [..] wraps everything to its left.
            // Plain array ranks between two [..] keep their C# order. Each parameter is followed by its C# twin.
            var source = """
                #nullable enable
                using System.Collections.Generic;
                class C
                {
                    void M(
                        int[..][] a, IEnumerable<int>[] a2,
                        int[][..] b, IEnumerable<int[]> b2,
                        int[..][..] c, IEnumerable<IEnumerable<int>> c2,
                        int[,][..][][,] d, IEnumerable<int[,]>[][,] d2,
                        int?[..] e, IEnumerable<int?> e2,
                        int[..]? f, IEnumerable<int>? f2,
                        string?[..] g, IEnumerable<string?> g2,
                        string[..]?[] h, IEnumerable<string>?[] h2,
                        (int, string)[..] i, IEnumerable<(int, string)> i2,
                        List<int[..]>[..] j, IEnumerable<List<IEnumerable<int>>> j2)
                    {
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var parameters = comp.GetMember<MethodSymbol>("C.M").Parameters.Select(p => p.TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true)).ToArray();
            for (int i = 0; i < parameters.Length; i += 2)
            {
                Assert.Equal(parameters[i + 1], parameters[i]);
            }

            AssertEx.Equal([
                "System.Collections.Generic.IEnumerable<System.Int32>![]!",
                "System.Collections.Generic.IEnumerable<System.Int32[]!>!",
                "System.Collections.Generic.IEnumerable<System.Collections.Generic.IEnumerable<System.Int32>!>!",
                "System.Collections.Generic.IEnumerable<System.Int32[,]!>![,]![]!",
                "System.Collections.Generic.IEnumerable<System.Int32?>!",
                "System.Collections.Generic.IEnumerable<System.Int32>?",
                "System.Collections.Generic.IEnumerable<System.String?>!",
                "System.Collections.Generic.IEnumerable<System.String!>?[]!",
                "System.Collections.Generic.IEnumerable<(System.Int32, System.String!)>!",
                "System.Collections.Generic.IEnumerable<System.Collections.Generic.List<System.Collections.Generic.IEnumerable<System.Int32>!>!>!",
                ], parameters.Where((_, i) => i % 2 == 0));
        }

        [Fact]
        public void SequenceType_IsIEnumerable()
        {
            // An alias, not a new type: it collides with an IEnumerable<T> overload and implements IEnumerable<T> members.
            var source = """
                using System.Collections.Generic;

                interface I
                {
                    IEnumerable<int> Get(IEnumerable<string> s);
                }

                class C : I
                {
                    public int[..] Get(string[..] s) => [];
                    void M(int[..] xs) { }
                    void M(IEnumerable<int> xs) { }
                }

                class D : I
                {
                    IEnumerable<int> I.Get(string[..] s) => [];
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (12,10): error CS0111: Type 'C' already defines a member called 'M' with the same parameter types
                //     void M(IEnumerable<int> xs) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "M").WithArguments("M", "C").WithLocation(12, 10));
        }

        [Theory, CombinatorialData]
        public void SequenceType_ElementAccessKeepsCSharpMeaning(bool sharper)
        {
            // a[..] is also an element access with a range, and keeps that meaning in expressions.
            var source = """
                using System;

                var a = new A();
                var x = (a[..]);
                var y = (a[..]) - 1;
                a[..] = 5;
                Console.Write(F(a[..], 1));
                Console.Write(a[..] is var z ? z : 0);
                Console.Write($"{x}{y}");
                A n = a;
                Console.Write((n?[..]).ToString());
                var g = new G();
                Console.Write((g[..])(7));
                Console.Write((g?[..])(8));
                var h = new H();
                int one = 1, two = 2;
                Console.Write(F(h[..] ? one : two, 0));
                var tuple = (h[..] ? one : two, h?[..] is true ? one : two);
                Console.Write(tuple);
                int F(int i, int j) => i + j;

                class A
                {
                    public int Value = 2;
                    public int this[Range r] { get => Value; set => Value = value; }
                }

                class G
                {
                    public Func<int, int> this[Range r] => i => i * 10;
                }

                class H
                {
                    public bool this[Range r] => true;
                }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "6521570801(1, 1)" : null).VerifyDiagnostics();
        }

        [Fact]
        public void SequenceType_Errors()
        {
            var source = """
                using System.Collections.Generic;
                class C
                {
                    int[1..] A;
                    int[..1] B;
                    int[.., ..] C1;
                    System.Console[..] D;
                    IEnumerable<System.Console> D2;
                    unsafe int*[..] E;
                    unsafe IEnumerable<int*> E2;
                    void[..] F;
                    void[] F2;
                    int[. .] G;
                    void M()
                    {
                        _ = new int[..];
                        _ = new int[3][..];
                        System.Console[..] d = null;
                        IEnumerable<System.Console> d2 = null;
                        unsafe
                        {
                            int*[..] e = null;
                            IEnumerable<int*> e2 = null;
                            int*[..][..] nested = null;
                            IEnumerable<IEnumerable<int*>> nested2 = null;
                            int*[][..] pointerArrays = null;
                            IEnumerable<int*[]> pointerArrays2 = null;
                            int*[..][][..] mixed = null;
                            IEnumerable<IEnumerable<int*>[]> mixed2 = null;
                            int*~[..] task = null;
                            IEnumerable<System.Threading.Tasks.Task<int*>> task2 = null;
                        }
                        System.Span<int>[..] span = null;
                        IEnumerable<System.Span<int>> span2 = null;
                        System.Console[][..] staticArrays = null;
                        IEnumerable<System.Console[]> staticArrays2 = null;
                        S<string>[..] constrained = null;
                        IEnumerable<S<string>> constrained2 = null;
                    }
                    void P(System.Console[..] p, IEnumerable<System.Console> p2, S<string>[..] q, IEnumerable<S<string>> q2) { }
                    S<string>[..] H;
                    IEnumerable<S<string>> H2;
                }
                struct S<T> where T : struct { }
                """;
            // Sizes other than a lone `..` stay array sizes, and an array creation keeps its C# meaning. A sequence of
            // an invalid element type reports what its IEnumerable<T> twin reports, at the same place: on the element
            // type.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.UnsafeDebugDll).VerifyDiagnostics(
                // (4,8): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //     int[1..] A;
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[1..]").WithLocation(4, 8),
                // (4,14): warning CS0169: The field 'C.A' is never used
                //     int[1..] A;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "A").WithArguments("C.A").WithLocation(4, 14),
                // (5,8): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //     int[..1] B;
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[..1]").WithLocation(5, 8),
                // (5,14): warning CS0169: The field 'C.B' is never used
                //     int[..1] B;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "B").WithArguments("C.B").WithLocation(5, 14),
                // (6,8): error CS0270: Array size cannot be specified in a variable declaration (try initializing with a 'new' expression)
                //     int[.., ..] C1;
                Diagnostic(ErrorCode.ERR_ArraySizeInDeclaration, "[.., ..]").WithLocation(6, 8),
                // (6,17): warning CS0169: The field 'C.C1' is never used
                //     int[.., ..] C1;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "C1").WithArguments("C.C1").WithLocation(6, 17),
                // (7,24): error CS0718: 'Console': static types cannot be used as type arguments
                //     System.Console[..] D;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "D").WithArguments("System.Console").WithLocation(7, 24),
                // (7,24): warning CS0169: The field 'C.D' is never used
                //     System.Console[..] D;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "D").WithArguments("C.D").WithLocation(7, 24),
                // (8,33): error CS0718: 'Console': static types cannot be used as type arguments
                //     IEnumerable<System.Console> D2;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "D2").WithArguments("System.Console").WithLocation(8, 33),
                // (8,33): warning CS0169: The field 'C.D2' is never used
                //     IEnumerable<System.Console> D2;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "D2").WithArguments("C.D2").WithLocation(8, 33),
                // (9,21): error CS0306: The type 'int*' may not be used as a type argument
                //     unsafe int*[..] E;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "E").WithArguments("int*").WithLocation(9, 21),
                // (9,21): warning CS0169: The field 'C.E' is never used
                //     unsafe int*[..] E;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "E").WithArguments("C.E").WithLocation(9, 21),
                // (10,30): error CS0306: The type 'int*' may not be used as a type argument
                //     unsafe IEnumerable<int*> E2;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "E2").WithArguments("int*").WithLocation(10, 30),
                // (10,30): warning CS0169: The field 'C.E2' is never used
                //     unsafe IEnumerable<int*> E2;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "E2").WithArguments("C.E2").WithLocation(10, 30),
                // (11,9): error CS1519: Invalid token '[' in a member declaration
                //     void[..] F;
                Diagnostic(ErrorCode.ERR_InvalidMemberDecl, "[").WithArguments("[").WithLocation(11, 9),
                // (11,10): error CS1001: Identifier expected
                //     void[..] F;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ".").WithLocation(11, 10),
                // (11,11): error CS1001: Identifier expected
                //     void[..] F;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ".").WithLocation(11, 11),
                // (11,12): error CS1001: Identifier expected
                //     void[..] F;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "]").WithLocation(11, 12),
                // (11,15): error CS1519: Invalid token ';' in a member declaration
                //     void[..] F;
                Diagnostic(ErrorCode.ERR_InvalidMemberDecl, ";").WithArguments(";").WithLocation(11, 15),
                // (12,9): error CS1519: Invalid token '[' in a member declaration
                //     void[] F2;
                Diagnostic(ErrorCode.ERR_InvalidMemberDecl, "[").WithArguments("[").WithLocation(12, 9),
                // (12,10): error CS1001: Identifier expected
                //     void[] F2;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "]").WithLocation(12, 10),
                // (12,14): error CS1519: Invalid token ';' in a member declaration
                //     void[] F2;
                Diagnostic(ErrorCode.ERR_InvalidMemberDecl, ";").WithArguments(";").WithLocation(12, 14),
                // (13,9): error CS1003: Syntax error, ',' expected
                //     int[. .] G;
                Diagnostic(ErrorCode.ERR_SyntaxError, ".").WithArguments(",").WithLocation(13, 9),
                // (13,14): warning CS0169: The field 'C.G' is never used
                //     int[. .] G;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "G").WithArguments("C.G").WithLocation(13, 14),
                // (16,21): error CS0029: Cannot implicitly convert type 'System.Range' to 'int'
                //         _ = new int[..];
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "..").WithArguments("System.Range", "int").WithLocation(16, 21),
                // (17,24): error CS0178: Invalid rank specifier: expected ',' or ']'
                //         _ = new int[3][..];
                Diagnostic(ErrorCode.ERR_InvalidArray, "..").WithLocation(17, 24),
                // (18,9): error CS0718: 'Console': static types cannot be used as type arguments
                //         System.Console[..] d = null;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "System.Console").WithArguments("System.Console").WithLocation(18, 9),
                // (18,28): warning CS0219: The variable 'd' is assigned but its value is never used
                //         System.Console[..] d = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "d").WithArguments("d").WithLocation(18, 28),
                // (19,21): error CS0718: 'Console': static types cannot be used as type arguments
                //         IEnumerable<System.Console> d2 = null;
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "System.Console").WithArguments("System.Console").WithLocation(19, 21),
                // (19,37): warning CS0219: The variable 'd2' is assigned but its value is never used
                //         IEnumerable<System.Console> d2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "d2").WithArguments("d2").WithLocation(19, 37),
                // (22,13): error CS0306: The type 'int*' may not be used as a type argument
                //             int*[..] e = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(22, 13),
                // (22,22): warning CS0219: The variable 'e' is assigned but its value is never used
                //             int*[..] e = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "e").WithArguments("e").WithLocation(22, 22),
                // (23,25): error CS0306: The type 'int*' may not be used as a type argument
                //             IEnumerable<int*> e2 = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(23, 25),
                // (23,31): warning CS0219: The variable 'e2' is assigned but its value is never used
                //             IEnumerable<int*> e2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "e2").WithArguments("e2").WithLocation(23, 31),
                // (24,13): error CS0306: The type 'int*' may not be used as a type argument
                //             int*[..][..] nested = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(24, 13),
                // (24,26): warning CS0219: The variable 'nested' is assigned but its value is never used
                //             int*[..][..] nested = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "nested").WithArguments("nested").WithLocation(24, 26),
                // (25,37): error CS0306: The type 'int*' may not be used as a type argument
                //             IEnumerable<IEnumerable<int*>> nested2 = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(25, 37),
                // (25,44): warning CS0219: The variable 'nested2' is assigned but its value is never used
                //             IEnumerable<IEnumerable<int*>> nested2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "nested2").WithArguments("nested2").WithLocation(25, 44),
                // (26,24): warning CS0219: The variable 'pointerArrays' is assigned but its value is never used
                //             int*[][..] pointerArrays = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "pointerArrays").WithArguments("pointerArrays").WithLocation(26, 24),
                // (27,33): warning CS0219: The variable 'pointerArrays2' is assigned but its value is never used
                //             IEnumerable<int*[]> pointerArrays2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "pointerArrays2").WithArguments("pointerArrays2").WithLocation(27, 33),
                // (28,13): error CS0306: The type 'int*' may not be used as a type argument
                //             int*[..][][..] mixed = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(28, 13),
                // (28,28): warning CS0219: The variable 'mixed' is assigned but its value is never used
                //             int*[..][][..] mixed = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "mixed").WithArguments("mixed").WithLocation(28, 28),
                // (29,37): error CS0306: The type 'int*' may not be used as a type argument
                //             IEnumerable<IEnumerable<int*>[]> mixed2 = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(29, 37),
                // (29,46): warning CS0219: The variable 'mixed2' is assigned but its value is never used
                //             IEnumerable<IEnumerable<int*>[]> mixed2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "mixed2").WithArguments("mixed2").WithLocation(29, 46),
                // (30,13): error CS0306: The type 'int*' may not be used as a type argument
                //             int*~[..] task = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(30, 13),
                // (30,23): warning CS0219: The variable 'task' is assigned but its value is never used
                //             int*~[..] task = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "task").WithArguments("task").WithLocation(30, 23),
                // (31,53): error CS0306: The type 'int*' may not be used as a type argument
                //             IEnumerable<System.Threading.Tasks.Task<int*>> task2 = null;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "int*").WithArguments("int*").WithLocation(31, 53),
                // (31,60): warning CS0219: The variable 'task2' is assigned but its value is never used
                //             IEnumerable<System.Threading.Tasks.Task<int*>> task2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "task2").WithArguments("task2").WithLocation(31, 60),
                // (33,30): warning CS0219: The variable 'span' is assigned but its value is never used
                //         System.Span<int>[..] span = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "span").WithArguments("span").WithLocation(33, 30),
                // (34,39): warning CS0219: The variable 'span2' is assigned but its value is never used
                //         IEnumerable<System.Span<int>> span2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "span2").WithArguments("span2").WithLocation(34, 39),
                // (35,9): error CS0719: 'Console': array elements cannot be of static type
                //         System.Console[][..] staticArrays = null;
                Diagnostic(ErrorCode.ERR_ArrayOfStaticClass, "System.Console").WithArguments("System.Console").WithLocation(35, 9),
                // (35,30): warning CS0219: The variable 'staticArrays' is assigned but its value is never used
                //         System.Console[][..] staticArrays = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "staticArrays").WithArguments("staticArrays").WithLocation(35, 30),
                // (36,21): error CS0719: 'Console': array elements cannot be of static type
                //         IEnumerable<System.Console[]> staticArrays2 = null;
                Diagnostic(ErrorCode.ERR_ArrayOfStaticClass, "System.Console").WithArguments("System.Console").WithLocation(36, 21),
                // (36,39): warning CS0219: The variable 'staticArrays2' is assigned but its value is never used
                //         IEnumerable<System.Console[]> staticArrays2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "staticArrays2").WithArguments("staticArrays2").WithLocation(36, 39),
                // (37,11): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //         S<string>[..] constrained = null;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "string").WithArguments("S<T>", "T", "string").WithLocation(37, 11),
                // (37,23): warning CS0219: The variable 'constrained' is assigned but its value is never used
                //         S<string>[..] constrained = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "constrained").WithArguments("constrained").WithLocation(37, 23),
                // (38,23): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //         IEnumerable<S<string>> constrained2 = null;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "string").WithArguments("S<T>", "T", "string").WithLocation(38, 23),
                // (38,32): warning CS0219: The variable 'constrained2' is assigned but its value is never used
                //         IEnumerable<S<string>> constrained2 = null;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "constrained2").WithArguments("constrained2").WithLocation(38, 32),
                // (40,31): error CS0718: 'Console': static types cannot be used as type arguments
                //     void P(System.Console[..] p, IEnumerable<System.Console> p2, S<string>[..] q, IEnumerable<S<string>> q2) { }
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "p").WithArguments("System.Console").WithLocation(40, 31),
                // (40,62): error CS0718: 'Console': static types cannot be used as type arguments
                //     void P(System.Console[..] p, IEnumerable<System.Console> p2, S<string>[..] q, IEnumerable<S<string>> q2) { }
                Diagnostic(ErrorCode.ERR_GenericArgIsStaticClass, "p2").WithArguments("System.Console").WithLocation(40, 62),
                // (40,80): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     void P(System.Console[..] p, IEnumerable<System.Console> p2, S<string>[..] q, IEnumerable<S<string>> q2) { }
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "q").WithArguments("S<T>", "T", "string").WithLocation(40, 80),
                // (40,106): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     void P(System.Console[..] p, IEnumerable<System.Console> p2, S<string>[..] q, IEnumerable<S<string>> q2) { }
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "q2").WithArguments("S<T>", "T", "string").WithLocation(40, 106),
                // (41,19): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     S<string>[..] H;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "H").WithArguments("S<T>", "T", "string").WithLocation(41, 19),
                // (41,19): warning CS0169: The field 'C.H' is never used
                //     S<string>[..] H;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "H").WithArguments("C.H").WithLocation(41, 19),
                // (42,28): error CS0453: The type 'string' must be a non-nullable value type in order to use it as parameter 'T' in the generic type or method 'S<T>'
                //     IEnumerable<S<string>> H2;
                Diagnostic(ErrorCode.ERR_ValConstraintNotSatisfied, "H2").WithArguments("S<T>", "T", "string").WithLocation(42, 28),
                // (42,28): warning CS0169: The field 'C.H2' is never used
                //     IEnumerable<S<string>> H2;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "H2").WithArguments("C.H2").WithLocation(42, 28));
        }

        [Fact]
        public void SequenceType_MissingIEnumerable()
        {
            var source = """
                class C
                {
                    int[..] F;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.MakeTypeMissing(SpecialType.System_Collections_Generic_IEnumerable_T);
            comp.VerifyDiagnostics(
                // (3,8): error CS0518: Predefined type 'System.Collections.Generic.IEnumerable`1' is not defined or imported
                //     int[..] F;
                Diagnostic(ErrorCode.ERR_PredefinedTypeNotFound, "[..]").WithArguments("System.Collections.Generic.IEnumerable`1").WithLocation(3, 8),
                // (3,13): warning CS0169: The field 'C.F' is never used
                //     int[..] F;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "F").WithArguments("C.F").WithLocation(3, 13));
        }

        [Fact]
        public void SequenceType_Metadata()
        {
            // A C# caller sees IEnumerable<T>.
            var library = """
                #nullable enable
                using System.Linq;

                public static class Lib
                {
                    public static int[..] Evens(int[..] xs) => xs.Where(x => x % 2 == 0);
                    public static string[..]? Maybe;
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            libraryComp.VerifyDiagnostics();

            var source = """
                using System;
                using System.Collections.Generic;

                IEnumerable<int> evens = Lib.Evens(new List<int> { 1, 2, 3, 4 });
                Console.Write(string.Join(",", evens));
                """;
            foreach (var reference in new[] { libraryComp.ToMetadataReference(), libraryComp.EmitToImageReference() })
            {
                var comp = CreateCompilation(source, references: [reference], targetFramework: TargetFramework.Net90);
                CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "2,4" : null).VerifyDiagnostics();
                Assert.Equal("System.Collections.Generic.IEnumerable<System.Int32> Lib.Evens(System.Collections.Generic.IEnumerable<System.Int32> xs)", comp.GetMember<MethodSymbol>("Lib.Evens").ToTestDisplayString());
                Assert.Equal("System.Collections.Generic.IEnumerable<System.String!>? Lib.Maybe", comp.GetMember<FieldSymbol>("Lib.Maybe").ToTestDisplayString(includeNonNullable: true));
            }
        }

        [Fact]
        public void SequenceType_SemanticModel()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        int[..] xs = new[] { 1 };
                        System.Collections.Generic.IEnumerable<int> ys = xs;
                        _ = ys;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var types = tree.GetRoot().DescendantNodes().OfType<VariableDeclarationSyntax>().Select(d => d.Type).ToArray();
            var sharper = (ArrayTypeSyntax)types[0];
            Assert.Equal(model.GetTypeInfo(types[1]).Type, model.GetTypeInfo(sharper).Type);
            Assert.Equal(model.GetSymbolInfo(types[1]).Symbol, model.GetSymbolInfo(sharper).Symbol);
            Assert.Equal("System.Collections.Generic.IEnumerable<System.Int32>", model.GetSymbolInfo(sharper).Symbol.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetTypeInfo(sharper.ElementType).Type.ToTestDisplayString());
            var range = sharper.RankSpecifiers[0].Sizes[0];
            Assert.Null(model.GetSymbolInfo(range).Symbol);
            Assert.Null(model.GetOperation(range));
        }

        [Fact]
        public void SequenceType_IOperation()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        /*<bind>*/int[..] xs = new[] { 1 };/*</bind>*/
                        _ = xs;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IVariableDeclarationGroupOperation (1 declarations) (OperationKind.VariableDeclarationGroup, Type: null) (Syntax: 'int[..] xs  ... ew[] { 1 };')
                  IVariableDeclarationOperation (1 declarators) (OperationKind.VariableDeclaration, Type: null) (Syntax: 'int[..] xs = new[] { 1 }')
                    Declarators:
                        IVariableDeclaratorOperation (Symbol: System.Collections.Generic.IEnumerable<System.Int32> xs) (OperationKind.VariableDeclarator, Type: null) (Syntax: 'xs = new[] { 1 }')
                          Initializer:
                            IVariableInitializerOperation (OperationKind.VariableInitializer, Type: null) (Syntax: '= new[] { 1 }')
                              IConversionOperation (TryCast: False, Unchecked) (OperationKind.Conversion, Type: System.Collections.Generic.IEnumerable<System.Int32>, IsImplicit) (Syntax: 'new[] { 1 }')
                                Conversion: CommonConversion (Exists: True, IsIdentity: False, IsNumeric: False, IsReference: True, IsUserDefined: False) (MethodSymbol: null)
                                Operand:
                                  IArrayCreationOperation (OperationKind.ArrayCreation, Type: System.Int32[]) (Syntax: 'new[] { 1 }')
                                    Dimension Sizes(1):
                                        ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1, IsImplicit) (Syntax: 'new[] { 1 }')
                                    Initializer:
                                      IArrayInitializerOperation (1 elements) (OperationKind.ArrayInitializer, Type: null) (Syntax: '{ 1 }')
                                        Element Values(1):
                                            ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                    Initializer:
                      null
                """;
            VerifyOperationTreeAndDiagnosticsForTest<LocalDeclarationStatementSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void SequenceType_Nullable()
        {
            var template = """
                #nullable enable
                class C
                {
                    {0} M({1} xs)
                    {
                        foreach (var x in xs) { _ = x.Length; }
                        return null;
                    }
                }
                """;
            var sharper = CreateCompilation(template.Replace("{0}", "string[..]").Replace("{1}", "string?[..]"), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var csharp = CreateCompilation(template.Replace("{0}", "IEnumerable<string>").Replace("{1}", "IEnumerable<string?>").Replace("class C", "using System.Collections.Generic;\nclass C"), targetFramework: TargetFramework.Net90);
            var expected = new[]
            {
                // (6,37): warning CS8602: Dereference of a possibly null reference.
                //         foreach (var x in xs) { _ = x.Length; }
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "x").WithLocation(6, 37),
                // (7,16): warning CS8603: Possible null reference return.
                //         return null;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "null").WithLocation(7, 16)
            };
            sharper.VerifyDiagnostics(expected);
            csharp.VerifyDiagnostics(expected.Select(d => d.WithLocation(d.LocationLine + 1, d.LocationCharacter)).ToArray());
        }


        [Fact]
        public void ValLambda_Basic()
        {
            // (val x) => ... is an implicitly typed lambda parameter, like (x) => ..., in every lambda form.
            var source = """
                using System;
                using System.Linq;
                using System.Linq.Expressions;
                using System.Threading.Tasks;

                Func<int, int> f = (val x) => x * 2;
                Console.Write(f(1));
                Console.Write(string.Concat(new[] { 1, 2 }.Select((val x) => x * 3)));
                Func<int, int, int> g = (val a, val b) => a + b;
                Console.Write(g(1, 2));
                Func<int, int, int> h = (val a, b) => a + b;
                Console.Write(h(1, 3));
                Func<int, Task<int>> t = async (val x) => { await Task.Yield(); return x; };
                Console.Write(await t(5));
                Func<int, int> s = static (val x) => x + 5;
                Console.Write(s(1));
                Func<int, long> r = long (val x) => x + 6;
                Console.Write(r(1));
                Expression<Func<int, int>> e = (val x) => x + 7;
                Console.Write(e.Compile()(1));
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "236345678" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ValLambda_Writes()
        {
            // Each write to the val parameter next to the same write to its implicitly typed twin, which is allowed.
            var source = """
                using System;

                delegate void D(int i);
                struct S { public int F; public int P { get; set; } public void Mutate() { F++; } }

                class C
                {
                    static void Ref(ref int i) { }
                    static void Out(out int i) { i = 0; }

                    void M()
                    {
                        D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                        D b = (y) => { y = 1; y += 1; y++; Ref(ref y); Out(out y); ref int r = ref y; Action l = () => y = 2; };
                        Action<S> c = (val v) => { v.F = 1; v.P = 1; v.Mutate(); };
                        Action<S> e = (w) => { w.F = 1; w.P = 1; w.Mutate(); };
                        Action<string> n = (val s) => { s ??= ""; };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,28): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 28),
                // (13,35): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 35),
                // (13,43): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 43),
                // (13,56): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 56),
                // (13,68): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 68),
                // (13,88): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 88),
                // (13,108): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         D a = (val x) => { x = 1; x += 1; x++; Ref(ref x); Out(out x); ref int r = ref x; Action l = () => x = 2; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 108),
                // (15,36): error CS1654: Cannot modify members of 'v' because it is a 'val variable'
                //         Action<S> c = (val v) => { v.F = 1; v.P = 1; v.Mutate(); };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.F").WithArguments("v", "val variable").WithLocation(15, 36),
                // (15,45): error CS1654: Cannot modify members of 'v' because it is a 'val variable'
                //         Action<S> c = (val v) => { v.F = 1; v.P = 1; v.Mutate(); };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.P").WithArguments("v", "val variable").WithLocation(15, 45),
                // (15,54): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                //         Action<S> c = (val v) => { v.F = 1; v.P = 1; v.Mutate(); };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(15, 54),
                // (17,41): error CS1656: Cannot assign to 's' because it is a 'val variable'
                //         Action<string> n = (val s) => { s ??= ""; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "val variable").WithLocation(17, 41));
        }

        [Fact]
        public void ValLambda_FeatureDisabled()
        {
            var source = """
                using System;
                class C
                {
                    void M()
                    {
                        Func<int, int> f = (val x) => x;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,29): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         Func<int, int> f = (val x) => x;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(6, 29));
        }

        [Theory]
        [CombinatorialData]
        public void ValLambda_TypeNamedValWins(bool sharper)
        {
            // With a type named val in scope, (val x) is an explicitly typed parameter of that type, which can be reassigned.
            var source = """
                using System;

                class val { }

                class C
                {
                    void M()
                    {
                        Func<val, val> f = (val x) => { x = new val(); return x; };
                        Func<int, int> g = (val x) => x;
                        Func<val, int, int> h = (val x, y) => 0;
                        Func<val, int, int> i = (val x, int y) => 0;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(3, 7),
                // (10,33): error CS1678: Parameter 1 is declared as type 'val' but should be 'int'
                //         Func<int, int> g = (val x) => x;
                Diagnostic(ErrorCode.ERR_BadParamType, "x").WithArguments("1", "", "val", "", "int").WithLocation(10, 33),
                // (10,36): error CS1661: Cannot convert lambda expression to type 'Func<int, int>' because the parameter types do not match the delegate parameter types
                //         Func<int, int> g = (val x) => x;
                Diagnostic(ErrorCode.ERR_CantConvAnonMethParams, "=>").WithArguments("lambda expression", "System.Func<int, int>").WithLocation(10, 36),
                // (11,41): error CS0748: Inconsistent lambda parameter usage; parameter types must be all explicit or all implicit
                //         Func<val, int, int> h = (val x, y) => 0;
                Diagnostic(ErrorCode.ERR_InconsistentLambdaParameterUsage, "y").WithLocation(11, 41));
        }

        [Fact]
        public void ValLambda_MixedParameters()
        {
            // val parameters are implicitly typed, so they mix with (x) but not with (int x), as C# requires.
            var source = """
                using System;
                class C
                {
                    void M()
                    {
                        Func<int, int, int> f = (val x, y) => x + y;
                        Func<int, int, int> g = (val x, int y) => x + y;
                        Func<int, int, int> h = (int x, val y) => x + y;
                        Func<int, int, int> i = (x, int y) => x + y;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,38): error CS0748: Inconsistent lambda parameter usage; parameter types must be all explicit or all implicit
                //         Func<int, int, int> g = (val x, int y) => x + y;
                Diagnostic(ErrorCode.ERR_InconsistentLambdaParameterUsage, "x").WithLocation(7, 38),
                // (8,45): error CS0748: Inconsistent lambda parameter usage; parameter types must be all explicit or all implicit
                //         Func<int, int, int> h = (int x, val y) => x + y;
                Diagnostic(ErrorCode.ERR_InconsistentLambdaParameterUsage, "y").WithLocation(8, 45),
                // (9,34): error CS0748: Inconsistent lambda parameter usage; parameter types must be all explicit or all implicit
                //         Func<int, int, int> i = (x, int y) => x + y;
                Diagnostic(ErrorCode.ERR_InconsistentLambdaParameterUsage, "x").WithLocation(9, 34));
        }

        [Fact]
        public void ValLambda_Modifiers()
        {
            // val combines with the modifiers C# allows on implicitly typed lambda parameters, and reports the same errors.
            var source = """
                using System;
                delegate void R(ref int i);
                delegate void O(out int i);
                delegate void I(in int i);
                delegate void Sp(scoped Span<int> s);
                delegate void P(params int[] a);
                delegate void Def(int i = 1);

                class C
                {
                    void M()
                    {
                        int j = 0;
                        R r = (ref val x) => { x = 1; x = ref j; };
                        R r2 = (ref y) => { y = 1; y = ref j; };
                        O o = (out val x) => { x = 1; };
                        I i = (in val x) => { x = 1; };
                        I i2 = (in y) => { y = 1; };
                        Sp s = (scoped val x) => { x = default; x[0] = 1; };
                        Sp s2 = (scoped y) => { y = default; };
                        P p = (params val x) => { };
                        P p2 = (params y) => { };
                        Def d = (val x = 1) => { };
                        Def d2 = (y = 1) => { };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (14,39): error CS20006: Cannot ref-assign 'x' because it is a ref val: it always refers to the same variable. Writes through 'x' are allowed.
                //         R r = (ref val x) => { x = 1; x = ref j; };
                Diagnostic(ErrorCode.ERR_SharperValRefReassignment, "x").WithArguments("x").WithLocation(14, 39),
                // (15,36): error CS8374: Cannot ref-assign 'j' to 'y' because 'j' has a narrower escape scope than 'y'.
                //         R r2 = (ref y) => { y = 1; y = ref j; };
                Diagnostic(ErrorCode.ERR_RefAssignNarrower, "y = ref j").WithArguments("y", "j").WithLocation(15, 36),
                // (17,31): error CS8331: Cannot assign to variable 'x' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         I i = (in val x) => { x = 1; };
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "x").WithArguments("variable", "x").WithLocation(17, 31),
                // (18,28): error CS8331: Cannot assign to variable 'y' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         I i2 = (in y) => { y = 1; };
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "y").WithArguments("variable", "y").WithLocation(18, 28),
                // (19,36): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         Sp s = (scoped val x) => { x = default; x[0] = 1; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(19, 36),
                // (21,16): error CS9272: Implicitly typed lambda parameter 'x' cannot have the 'params' modifier.
                //         P p = (params val x) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedParamsParameter, "params").WithArguments("x").WithLocation(21, 16),
                // (22,17): error CS9272: Implicitly typed lambda parameter 'y' cannot have the 'params' modifier.
                //         P p2 = (params y) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedParamsParameter, "params").WithArguments("y").WithLocation(22, 17),
                // (23,22): error CS9098: Implicitly typed lambda parameter 'x' cannot have a default value.
                //         Def d = (val x = 1) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedDefaultParameter, "x").WithArguments("x").WithLocation(23, 22),
                // (24,19): error CS9098: Implicitly typed lambda parameter 'y' cannot have a default value.
                //         Def d2 = (y = 1) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedDefaultParameter, "y").WithArguments("y").WithLocation(24, 19));
        }

        [Fact]
        public void ValLambda_NotInAnonymousMethodsOrLocalFunctions()
        {
            // Their parameters are always explicitly typed, so val there is a type name, as in C#. Natural types need explicit
            // parameter types too, as for (x) => x.
            var source = """
                using System;
                class C
                {
                    void M()
                    {
                        Action<int> e = delegate (val x) { };
                        void Local(val x) { }
                        Local(1);
                        var f = (val x) => x;
                        var g = (y) => y;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,35): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         Action<int> e = delegate (val x) { };
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(6, 35),
                // (7,20): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         void Local(val x) { }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(7, 20),
                // (8,15): error CS1503: Argument 1: cannot convert from 'int' to 'val'
                //         Local(1);
                Diagnostic(ErrorCode.ERR_BadArgType, "1").WithArguments("1", "int", "val").WithLocation(8, 15),
                // (9,17): error CS8917: The delegate type could not be inferred.
                //         var f = (val x) => x;
                Diagnostic(ErrorCode.ERR_CannotInferDelegateType, "(val x) => x").WithLocation(9, 17),
                // (10,17): error CS8917: The delegate type could not be inferred.
                //         var g = (y) => y;
                Diagnostic(ErrorCode.ERR_CannotInferDelegateType, "(y) => y").WithLocation(10, 17));
        }

        [Theory]
        [CombinatorialData]
        public void ValLambda_SimpleLambdaNeedsParentheses(bool sharper)
        {
            // Like C#'s parameter modifiers, val needs the parenthesized form: val x => x is the same syntax error with or without
            // Sharper. A clearer error is https://github.com/ok3mad/roslyn/issues/98.
            var source = """
                using System;
                class C
                {
                    void M()
                    {
                        Func<int, int> f = val x => x;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,28): error CS0103: The name 'val' does not exist in the current context
                //         Func<int, int> f = val x => x;
                Diagnostic(ErrorCode.ERR_NameNotInContext, "val").WithArguments("val").WithLocation(6, 28),
                // (6,32): error CS1002: ; expected
                //         Func<int, int> f = val x => x;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "x").WithLocation(6, 32),
                // (6,32): error CS0201: Only assignment, call, increment, decrement, await, and new object expressions can be used as a statement
                //         Func<int, int> f = val x => x;
                Diagnostic(ErrorCode.ERR_IllegalStatement, "x => x").WithLocation(6, 32));
        }

        [Fact]
        public void ValLambda_StructCopies()
        {
            // A non-readonly member of a val struct parameter runs on a copy and warns, also when captured, across await and in
            // patterns. A ref val parameter is written through.
            var source = """
                using System;
                using System.Threading.Tasks;

                Action<S> m = (val s) => { s.Mutate(); Console.Write(s.F); Func<int> c = () => { s.Mutate(); return s.F; }; Console.Write(c()); };
                m(new S());
                Func<S, Task<int>> t = async (val s) => { await Task.Yield(); s.Mutate(); return s.F; };
                Console.Write(await t(new S()));
                Func<S, int> p = (val s) => s is { P: 1 } ? s.F : -1;
                Console.Write(p(new S()));
                R r = (ref val s) => s.Mutate();
                var w = new S();
                r(ref w);
                Console.Write(w.F);
                Action<S> q = (s) => { s.Mutate(); Console.Write(s.F); };
                q(new S());

                delegate void R(ref S s);
                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                    public int P { get { F++; return F; } }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "000011" : null).VerifyDiagnostics(
                // (4,28): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                // Action<S> m = (val s) => { s.Mutate(); Console.Write(s.F); Func<int> c = () => { s.Mutate(); return s.F; }; Console.Write(c()); };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(4, 28),
                // (4,82): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                // Action<S> m = (val s) => { s.Mutate(); Console.Write(s.F); Func<int> c = () => { s.Mutate(); return s.F; }; Console.Write(c()); };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(4, 82),
                // (6,63): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                // Func<S, Task<int>> t = async (val s) => { await Task.Yield(); s.Mutate(); return s.F; };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(6, 63),
                // (8,29): warning CS20003: Call to non-readonly member 'S.P.get' on val variable 's' runs on a copy, so 's' is not changed.
                // Func<S, int> p = (val s) => s is { P: 1 } ? s.F : -1;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.P.get", "s").WithLocation(8, 29));
        }

        [Fact]
        public void ValLambda_Nullable()
        {
            var source = """
                #nullable enable
                using System;
                class C
                {
                    void M()
                    {
                        Func<string?, int> f = (val x) => x.Length;
                        Func<string?, int> g = (y) => y.Length;
                        Func<string, int> h = (val x) => x.Length;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,43): warning CS8602: Dereference of a possibly null reference.
                //         Func<string?, int> f = (val x) => x.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "x").WithLocation(7, 43),
                // (8,39): warning CS8602: Dereference of a possibly null reference.
                //         Func<string?, int> g = (y) => y.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "y").WithLocation(8, 39));
        }

        [Fact]
        public void ValLambda_SemanticModel()
        {
            // The val keyword reports the inferred type, as var does for locals, and the parameter is an ordinary parameter.
            var source = """
                #nullable enable
                using System;
                class C
                {
                    void M()
                    {
                        Func<int, int> f = (val x) => x;
                        Func<string?, int> g = (val s) => 0;
                        Func<int, int> h = (y) => y;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var parameters = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().ToArray();

            var x = model.GetDeclaredSymbol(parameters[0]);
            Assert.Equal("System.Int32 x", x.ToTestDisplayString());
            Assert.Equal(RefKind.None, x.RefKind);
            Assert.False(x.IsImplicitlyDeclared);
            Assert.Equal("System.Int32", model.GetTypeInfo(parameters[0].Type).Type.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetSymbolInfo(parameters[0].Type).Symbol.ToTestDisplayString());
            Assert.Null(model.GetAliasInfo((IdentifierNameSyntax)parameters[0].Type));

            var s = model.GetDeclaredSymbol(parameters[1]);
            Assert.Equal("System.String? s", s.ToTestDisplayString());
            Assert.Equal(CodeAnalysis.NullableAnnotation.Annotated, model.GetTypeInfo(parameters[1].Type).Nullability.Annotation);
            Assert.Equal(model.GetDeclaredSymbol(parameters[2]).Type, x.Type);
            Assert.Equal(x, model.GetSymbolInfo(tree.GetRoot().DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>().First().ExpressionBody).Symbol);
        }

        [Fact]
        public void ValLambda_IOperation()
        {
            var source = """
                using System;
                class C
                {
                    void M()
                    {
                        Func<int, int> f = /*<bind>*/(val x) => x/*</bind>*/;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IAnonymousFunctionOperation (Symbol: lambda expression) (OperationKind.AnonymousFunction, Type: null) (Syntax: '(val x) => x')
                  IBlockOperation (1 statements) (OperationKind.Block, Type: null, IsImplicit) (Syntax: 'x')
                    IReturnOperation (OperationKind.Return, Type: null, IsImplicit) (Syntax: 'x')
                      ReturnedValue:
                        IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<ParenthesizedLambdaExpressionSyntax>(comp, expectedOperationTree, []);
        }

        [Theory]
        [CombinatorialData]
        public void ValLambda_Parsing(bool sharper)
        {
            // No parser change: (val x) already parses as a parameter whose type is the identifier val; the binder decides.
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            foreach (var text in new[] { "(val x) => x", "async (val x) => x", "static (val x) => x", "(scoped val x) => x", "(ref val x) => x", "(val x = 1) => x", "(val a, b) => a" })
            {
                var lambda = (ParenthesizedLambdaExpressionSyntax)SyntaxFactory.ParseExpression(text, options: options);
                Assert.Empty(lambda.GetDiagnostics());
                var type = (IdentifierNameSyntax)lambda.ParameterList.Parameters[0].Type;
                Assert.Equal("val", type.Identifier.Text);
                Assert.Equal(SyntaxKind.IdentifierToken, type.Identifier.Kind());
            }
        }

        [Fact]
        public void ValLambda_RegionAnalysis()
        {
            var source = """
                using System;
                class C
                {
                    void M()
                    {
                        Func<int, int> f = (val x) => x + 1;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var lambda = tree.GetRoot().DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>().Single();
            var dataFlow = model.AnalyzeDataFlow(lambda.ExpressionBody);
            Assert.True(dataFlow.Succeeded);
            Assert.Equal("x", string.Join(", ", dataFlow.ReadInside.Select(s => s.Name)));
            Assert.Empty(dataFlow.WrittenInside);
            dataFlow = model.AnalyzeDataFlow(lambda);
            Assert.Equal("x", string.Join(", ", dataFlow.VariablesDeclared.Select(s => s.Name)));
        }

        [Fact]
        public void ReadonlyLocal_Basic()
        {
            var source = """
                using System;

                readonly int x = 1;
                readonly var s = "a";
                readonly string t = "b";
                Func<int> f = () => x + 1;
                Console.Write(x);
                Console.Write(s);
                Console.Write(t);
                Console.Write(f());
                M();

                static void M()
                {
                    readonly long y = 3;
                    Console.Write(y);
                    using readonly var d = new D("d");
                    using readonly D e = new D("e");
                    Console.Write(";");
                }

                class D(string name) : IDisposable { public void Dispose() { Console.Write(name); } }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1ab23;ed" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ReadonlyLocal_FeatureDisabled()
        {
            // Without Sharper, readonly on a local keeps the C# errors, and the local stays writable.
            var source = """
                using System;

                class D : IDisposable { public void Dispose() { } }

                class C
                {
                    void M()
                    {
                        readonly int x = 1;
                        readonly var y = 2;
                        x = 3;
                        y = 3;
                        using readonly var d = new D();
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (9,9): error CS0106: The modifier 'readonly' is not valid for this item
                //         readonly int x = 1;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(9, 9),
                // (9,22): warning CS0219: The variable 'x' is assigned but its value is never used
                //         readonly int x = 1;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "x").WithArguments("x").WithLocation(9, 22),
                // (10,9): error CS0106: The modifier 'readonly' is not valid for this item
                //         readonly var y = 2;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(10, 9),
                // (10,22): warning CS0219: The variable 'y' is assigned but its value is never used
                //         readonly var y = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "y").WithArguments("y").WithLocation(10, 22),
                // (13,15): error CS9229: Modifiers cannot be placed on using declarations
                //         using readonly var d = new D();
                Diagnostic(ErrorCode.ERR_NoModifiersOnUsing, "readonly").WithLocation(13, 15));
        }

        [Fact]
        public void ReadonlyLocal_FeatureDisabled_TopLevel()
        {
            var source = """
                readonly int t = 1;
                t = 2;
                System.Console.Write(t);
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,1): error CS0106: The modifier 'readonly' is not valid for this item
                // readonly int t = 1;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(1, 1));
        }

        [Fact]
        public void ReadonlyLocal_Writes()
        {
            // Each write to the readonly local next to the same write to its plain twin, which is allowed.
            var source = """
                using System;

                class C
                {
                    static void Ref(ref int i) { }
                    static void Out(out int i) { i = 0; }
                    static void In(in int i) { }

                    void M()
                    {
                        readonly int x = 1;
                        int y = 1;
                        x = 2;
                        y = 2;
                        x += 1;
                        y += 1;
                        x++;
                        y++;
                        --x;
                        --y;
                        Ref(ref x);
                        Ref(ref y);
                        Out(out x);
                        Out(out y);
                        In(in x);
                        In(in y);
                        ref int r = ref x;
                        ref int q = ref y;
                        ref readonly int rr = ref x;
                        Action a = () => x = 3;
                        Action b = () => y = 3;
                        void Local() { x = 4; }
                        void Local2() { y = 4; }

                        readonly string s = null;
                        string t = null;
                        s ??= "a";
                        t ??= "a";
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,9): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(13, 9),
                // (15,9): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         x += 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(15, 9),
                // (17,9): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(17, 9),
                // (19,11): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         --x;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(19, 11),
                // (21,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly local'
                //         Ref(ref x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(21, 17),
                // (23,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly local'
                //         Out(out x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(23, 17),
                // (27,25): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly local'
                //         ref int r = ref x;
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(27, 25),
                // (30,26): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         Action a = () => x = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(30, 26),
                // (32,14): warning CS8321: The local function 'Local' is declared but never used
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local").WithArguments("Local").WithLocation(32, 14),
                // (32,24): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(32, 24),
                // (33,14): warning CS8321: The local function 'Local2' is declared but never used
                //         void Local2() { y = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local2").WithArguments("Local2").WithLocation(33, 14),
                // (37,9): error CS1656: Cannot assign to 's' because it is a 'readonly local'
                //         s ??= "a";
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "readonly local").WithLocation(37, 9));
        }

        [Fact]
        public void ReadonlyLocal_ReadonlyVarIsVal()
        {
            // readonly var is val: the same inference, the same errors where var cannot be used, and the same writes rejected.
            var source = """
                class C
                {
                    void M()
                    {
                        readonly var x = 1;
                        val y = 1;
                        x = 2;
                        y = 2;
                        readonly var a;
                        val a2;
                        readonly var b = 1, c = 2;
                        val b2 = 1, c2 = 2;
                        readonly var d = null;
                        val d2 = null;
                        readonly val e = 1;
                        e = 2;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,9): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(7, 9),
                // (8,9): error CS1656: Cannot assign to 'y' because it is a 'val variable'
                //         y = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "y").WithArguments("y", "val variable").WithLocation(8, 9),
                // (9,22): error CS0818: Implicitly-typed variables must be initialized
                //         readonly var a;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableWithNoInitializer, "a").WithLocation(9, 22),
                // (9,22): warning CS0168: The variable 'a' is declared but never used
                //         readonly var a;
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "a").WithArguments("a").WithLocation(9, 22),
                // (10,13): error CS0818: Implicitly-typed variables must be initialized
                //         val a2;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableWithNoInitializer, "a2").WithLocation(10, 13),
                // (10,13): warning CS0168: The variable 'a2' is declared but never used
                //         val a2;
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "a2").WithArguments("a2").WithLocation(10, 13),
                // (11,18): error CS0819: Implicitly-typed variables cannot have multiple declarators
                //         readonly var b = 1, c = 2;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableMultipleDeclarator, "var b = 1, c = 2").WithLocation(11, 18),
                // (11,22): warning CS0219: The variable 'b' is assigned but its value is never used
                //         readonly var b = 1, c = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "b").WithArguments("b").WithLocation(11, 22),
                // (11,29): warning CS0219: The variable 'c' is assigned but its value is never used
                //         readonly var b = 1, c = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "c").WithArguments("c").WithLocation(11, 29),
                // (12,9): error CS0819: Implicitly-typed variables cannot have multiple declarators
                //         val b2 = 1, c2 = 2;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableMultipleDeclarator, "val b2 = 1, c2 = 2").WithLocation(12, 9),
                // (12,13): warning CS0219: The variable 'b2' is assigned but its value is never used
                //         val b2 = 1, c2 = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "b2").WithArguments("b2").WithLocation(12, 13),
                // (12,21): warning CS0219: The variable 'c2' is assigned but its value is never used
                //         val b2 = 1, c2 = 2;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "c2").WithArguments("c2").WithLocation(12, 21),
                // (13,22): error CS0815: Cannot assign <null> to an implicitly-typed variable
                //         readonly var d = null;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableAssignedBadValue, "d = null").WithArguments("<null>").WithLocation(13, 22),
                // (14,13): error CS0815: Cannot assign <null> to an implicitly-typed variable
                //         val d2 = null;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableAssignedBadValue, "d2 = null").WithArguments("<null>").WithLocation(14, 13),
                // (16,9): error CS1656: Cannot assign to 'e' because it is a 'val variable'
                //         e = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "e").WithArguments("e", "val variable").WithLocation(16, 9));
        }

        [Fact]
        public void ReadonlyLocal_NoInitializer()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        readonly int x;
                        readonly int y = 1, z;
                        int w;
                        x = 1;
                        w = 1;
                        System.Console.Write(x + y + z + w);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,22): error CS20011: A readonly local must be initialized in its declaration, because it cannot be assigned later.
                //         readonly int x;
                Diagnostic(ErrorCode.ERR_SharperReadOnlyLocalWithNoInitializer, "x").WithLocation(5, 22),
                // (6,29): error CS20011: A readonly local must be initialized in its declaration, because it cannot be assigned later.
                //         readonly int y = 1, z;
                Diagnostic(ErrorCode.ERR_SharperReadOnlyLocalWithNoInitializer, "z").WithLocation(6, 29),
                // (8,9): error CS1656: Cannot assign to 'x' because it is a 'readonly local'
                //         x = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly local").WithLocation(8, 9),
                // (8,9): error CS0165: Use of unassigned local variable 'x'
                //         x = 1;
                Diagnostic(ErrorCode.ERR_UseDefViolation, "x").WithArguments("x").WithLocation(8, 9),
                // (10,38): error CS0165: Use of unassigned local variable 'z'
                //         System.Console.Write(x + y + z + w);
                Diagnostic(ErrorCode.ERR_UseDefViolation, "z").WithArguments("z").WithLocation(10, 38));
        }

        [Fact]
        public void ReadonlyLocal_StructFieldAssignment()
        {
            var source = """
                struct S
                {
                    public int F;
                    public int P { get; set; }
                }

                class C
                {
                    void M()
                    {
                        readonly S v = new S();
                        S w = new S();
                        v.F = 1;
                        w.F = 1;
                        v.P = 1;
                        w.P = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,9): error CS1654: Cannot modify members of 'v' because it is a 'readonly local'
                //         v.F = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.F").WithArguments("v", "readonly local").WithLocation(13, 9),
                // (15,9): error CS1654: Cannot modify members of 'v' because it is a 'readonly local'
                //         v.P = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.P").WithArguments("v", "readonly local").WithLocation(15, 9));
        }

        [Fact]
        public void ReadonlyLocal_StructMemberCallOnCopy()
        {
            // As for val, a non-readonly member runs on a copy and warns, also when captured or hoisted, and for members found by pattern.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                readonly S v = new S();
                v.Mutate();
                Console.Write(v.F);
                _ = v.Getter;
                Console.Write(v.F);
                foreach (var x in v) { }
                Console.Write(v.F);
                v.ReadOnlyMethod();
                Action f = () => { v.Mutate(); Console.Write(v.F); };
                f();
                await Task.Yield();
                v.Mutate();
                Console.Write(v.F);
                if (v is { Getter: 1 }) { }
                Console.Write(v.F);
                readonly var u = new S();
                u.Mutate();
                Console.Write(u.F);
                Console.Write(";");

                var w = new S();
                readonly ref S r = ref w;
                r.Mutate();
                Console.Write(w.F);

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                    public int Getter { get { F++; return F; } }
                    public readonly void ReadOnlyMethod() { }
                    public IEnumerator<int> GetEnumerator() { F++; return new List<int>().GetEnumerator(); }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0000000;1" : null).VerifyDiagnostics(
                // (6,1): warning CS20010: Call to non-readonly member 'S.Mutate()' on readonly local 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(6, 1),
                // (8,5): warning CS20010: Call to non-readonly member 'S.Getter.get' on readonly local 'v' runs on a copy, so 'v' is not changed.
                // _ = v.Getter;
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "v").WithArguments("S.Getter.get", "v").WithLocation(8, 5),
                // (10,19): warning CS20010: Call to non-readonly member 'S.GetEnumerator()' on readonly local 'v' runs on a copy, so 'v' is not changed.
                // foreach (var x in v) { }
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "v").WithArguments("S.GetEnumerator()", "v").WithLocation(10, 19),
                // (13,20): warning CS20010: Call to non-readonly member 'S.Mutate()' on readonly local 'v' runs on a copy, so 'v' is not changed.
                // Action f = () => { v.Mutate(); Console.Write(v.F); };
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(13, 20),
                // (16,1): warning CS20010: Call to non-readonly member 'S.Mutate()' on readonly local 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(16, 1),
                // (18,5): warning CS20010: Call to non-readonly member 'S.Getter.get' on readonly local 'v' runs on a copy, so 'v' is not changed.
                // if (v is { Getter: 1 }) { }
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "v").WithArguments("S.Getter.get", "v").WithLocation(18, 5),
                // (21,1): warning CS20010: Call to non-readonly member 'S.Mutate()' on readonly local 'u' runs on a copy, so 'u' is not changed.
                // u.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyLocalMemberCallOnCopy, "u").WithArguments("S.Mutate()", "u").WithLocation(21, 1));
        }

        [Fact]
        public void ReadonlyLocal_Ref()
        {
            // readonly on a ref local forbids ref reassignment and allows writes through the ref, as for a readonly ref field.
            // ref readonly keeps its C# meaning, and readonly ref readonly is both.
            var source = """
                using System;

                class C
                {
                    void M(Span<int> span)
                    {
                        int i = 1, j = 2;
                        readonly ref int r1 = ref i;
                        ref readonly int r2 = ref i;
                        readonly ref readonly int r3 = ref i;
                        readonly ref var r4 = ref i;
                        r1 = 5;
                        r2 = 5;
                        r3 = 5;
                        r4 = 5;
                        r1 = ref j;
                        r2 = ref j;
                        r3 = ref j;
                        r4 = ref j;
                        readonly ref int r5;
                        readonly scoped Span<int> s1 = span;
                        s1 = default;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,9): error CS0131: The left-hand side of an assignment must be a variable, property or indexer
                //         r2 = 5;
                Diagnostic(ErrorCode.ERR_AssgLvalueExpected, "r2").WithLocation(13, 9),
                // (14,9): error CS0131: The left-hand side of an assignment must be a variable, property or indexer
                //         r3 = 5;
                Diagnostic(ErrorCode.ERR_AssgLvalueExpected, "r3").WithLocation(14, 9),
                // (16,9): error CS1656: Cannot assign to 'r1' because it is a 'readonly local'
                //         r1 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r1").WithArguments("r1", "readonly local").WithLocation(16, 9),
                // (18,9): error CS1656: Cannot assign to 'r3' because it is a 'readonly local'
                //         r3 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r3").WithArguments("r3", "readonly local").WithLocation(18, 9),
                // (19,9): error CS1656: Cannot assign to 'r4' because it is a 'readonly local'
                //         r4 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r4").WithArguments("r4", "readonly local").WithLocation(19, 9),
                // (20,26): error CS8174: A declaration of a by-reference variable must have an initializer
                //         readonly ref int r5;
                Diagnostic(ErrorCode.ERR_ByReferenceVariableMustBeInitialized, "r5").WithLocation(20, 26),
                // (20,26): warning CS0168: The variable 'r5' is declared but never used
                //         readonly ref int r5;
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "r5").WithArguments("r5").WithLocation(20, 26),
                // (22,9): error CS1656: Cannot assign to 's1' because it is a 'readonly local'
                //         s1 = default;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s1").WithArguments("s1", "readonly local").WithLocation(22, 9));
        }

        [Fact]
        public void ReadonlyLocal_UsingDeclaration()
        {
            var source = """
                using System;

                class D : IDisposable { public void Dispose() { } }

                class C
                {
                    void M()
                    {
                        using readonly var d = new D();
                        using var d2 = new D();
                        d = null;
                        d2 = null;
                        using readonly static D e = null;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (11,9): error CS1656: Cannot assign to 'd' because it is a 'readonly local'
                //         d = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "d").WithArguments("d", "readonly local").WithLocation(11, 9),
                // (12,9): error CS1656: Cannot assign to 'd2' because it is a 'using variable'
                //         d2 = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "d2").WithArguments("d2", "using variable").WithLocation(12, 9),
                // (13,24): error CS9229: Modifiers cannot be placed on using declarations
                //         using readonly static D e = null;
                Diagnostic(ErrorCode.ERR_NoModifiersOnUsing, "static").WithLocation(13, 24));
        }

        [Fact]
        public void ReadonlyLocal_InvalidModifiers()
        {
            // const is already read-only, and readonly does not apply to local functions: both keep the C# error.
            var source = """
                class C
                {
                    void M()
                    {
                        const readonly int k1 = 1;
                        readonly const int k2 = 1;
                        readonly readonly int r = 1;
                        static readonly int s = 1;
                        readonly void F() { }
                        F();
                        _ = k1 + k2 + r + s;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,15): error CS0106: The modifier 'readonly' is not valid for this item
                //         const readonly int k1 = 1;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(5, 15),
                // (6,9): error CS0106: The modifier 'readonly' is not valid for this item
                //         readonly const int k2 = 1;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(6, 9),
                // (7,18): error CS1004: Duplicate 'readonly' modifier
                //         readonly readonly int r = 1;
                Diagnostic(ErrorCode.ERR_DuplicateModifier, "readonly").WithArguments("readonly").WithLocation(7, 18),
                // (8,9): error CS0106: The modifier 'static' is not valid for this item
                //         static readonly int s = 1;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "static").WithArguments("static").WithLocation(8, 9),
                // (9,9): error CS0106: The modifier 'readonly' is not valid for this item
                //         readonly void F() { }
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(9, 9));
        }

        [Fact]
        public void ReadonlyLocal_NotSupported()
        {
            // for initializers, foreach, deconstruction and out variables have no place for a modifier; they stay syntax errors.
            // val covers foreach, deconstruction and out.
            var source = """
                class C
                {
                    void M(int[] xs, (int, int) t)
                    {
                        for (readonly int i = 0; i < 3; ) { }
                        foreach (readonly int e in xs) { }
                        readonly var (p, q) = t;
                        int.TryParse("1", out readonly int n);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,14): error CS1002: ; expected
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "readonly").WithLocation(5, 14),
                // (5,14): error CS1525: Invalid expression term 'readonly'
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "readonly").WithArguments("readonly").WithLocation(5, 14),
                // (5,14): error CS1002: ; expected
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "readonly").WithLocation(5, 14),
                // (5,14): error CS1026: ) expected
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_CloseParenExpected, "readonly").WithLocation(5, 14),
                // (5,14): error CS1023: Embedded statement cannot be a declaration or labeled statement
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_BadEmbeddedStmt, "readonly int i = 0;").WithLocation(5, 14),
                // (5,27): warning CS0219: The variable 'i' is assigned but its value is never used
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "i").WithArguments("i").WithLocation(5, 27),
                // (5,34): error CS0103: The name 'i' does not exist in the current context
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_NameNotInContext, "i").WithArguments("i").WithLocation(5, 34),
                // (5,41): error CS1513: } expected
                //         for (readonly int i = 0; i < 3; ) { }
                Diagnostic(ErrorCode.ERR_RbraceExpected, ")").WithLocation(5, 41),
                // (6,18): error CS1525: Invalid expression term 'readonly'
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "readonly").WithArguments("readonly").WithLocation(6, 18),
                // (6,18): error CS1515: 'in' expected
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_InExpected, "readonly").WithLocation(6, 18),
                // (6,18): error CS0230: Type and identifier are both required in a foreach statement
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_BadForeachDecl, "readonly").WithLocation(6, 18),
                // (6,18): error CS1525: Invalid expression term 'readonly'
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "readonly").WithArguments("readonly").WithLocation(6, 18),
                // (6,18): error CS1026: ) expected
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_CloseParenExpected, "readonly").WithLocation(6, 18),
                // (6,18): error CS1023: Embedded statement cannot be a declaration or labeled statement
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_BadEmbeddedStmt, "readonly int e in ").WithLocation(6, 18),
                // (6,31): warning CS0168: The variable 'e' is declared but never used
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "e").WithArguments("e").WithLocation(6, 31),
                // (6,33): error CS1003: Syntax error, ',' expected
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_SyntaxError, "in").WithArguments(",").WithLocation(6, 33),
                // (6,36): error CS1002: ; expected
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "xs").WithLocation(6, 36),
                // (6,38): error CS1002: ; expected
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_SemicolonExpected, ")").WithLocation(6, 38),
                // (6,38): error CS1513: } expected
                //         foreach (readonly int e in xs) { }
                Diagnostic(ErrorCode.ERR_RbraceExpected, ")").WithLocation(6, 38),
                // (7,9): error CS0106: The modifier 'readonly' is not valid for this item
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_BadMemberFlag, "readonly").WithArguments("readonly").WithLocation(7, 9),
                // (7,18): error CS0825: The contextual keyword 'var' may only appear within a local variable declaration or in script code
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_TypeVarNotFound, "var").WithLocation(7, 18),
                // (7,22): error CS1001: Identifier expected
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, "(").WithLocation(7, 22),
                // (7,22): error CS8112: Local function '(p, q)' must declare a body because it is not marked 'static extern'.
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_LocalFunctionMissingBody, "").WithArguments("(p, q)").WithLocation(7, 22),
                // (7,23): error CS0246: The type or namespace name 'p' could not be found (are you missing a using directive or an assembly reference?)
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "p").WithArguments("p").WithLocation(7, 23),
                // (7,24): error CS1001: Identifier expected
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ",").WithLocation(7, 24),
                // (7,26): error CS0246: The type or namespace name 'q' could not be found (are you missing a using directive or an assembly reference?)
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "q").WithArguments("q").WithLocation(7, 26),
                // (7,27): error CS1001: Identifier expected
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ")").WithLocation(7, 27),
                // (7,29): error CS1002: ; expected
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "=").WithLocation(7, 29),
                // (7,29): error CS1525: Invalid expression term '='
                //         readonly var (p, q) = t;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "=").WithArguments("=").WithLocation(7, 29),
                // (8,31): error CS1525: Invalid expression term 'readonly'
                //         int.TryParse("1", out readonly int n);
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "readonly").WithArguments("readonly").WithLocation(8, 31),
                // (8,31): error CS1026: ) expected
                //         int.TryParse("1", out readonly int n);
                Diagnostic(ErrorCode.ERR_CloseParenExpected, "readonly").WithLocation(8, 31),
                // (8,31): error CS1002: ; expected
                //         int.TryParse("1", out readonly int n);
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "readonly").WithLocation(8, 31),
                // (8,44): warning CS0168: The variable 'n' is declared but never used
                //         int.TryParse("1", out readonly int n);
                Diagnostic(ErrorCode.WRN_UnreferencedVar, "n").WithArguments("n").WithLocation(8, 44),
                // (8,45): error CS1003: Syntax error, ',' expected
                //         int.TryParse("1", out readonly int n);
                Diagnostic(ErrorCode.ERR_SyntaxError, ")").WithArguments(",").WithLocation(8, 45));
        }

        [Theory]
        [CombinatorialData]
        public void ReadonlyLocal_ScriptFieldsKeepCSharpMeaning(bool sharper)
        {
            // In a script, top-level declarations are fields, and readonly fields are already C#.
            var source = """
                readonly int t = 1;
                readonly var u = 2;
                t = 2;
                u = 3;
                """;
            CreateCompilation(source, parseOptions: sharper ? s_sharperScriptOptions : TestOptions.Script, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,1): error CS0191: A readonly field cannot be assigned to (except in a constructor or init-only setter of the type in which the field is defined or a variable initializer)
                // t = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonly, "t").WithLocation(3, 1),
                // (4,1): error CS0191: A readonly field cannot be assigned to (except in a constructor or init-only setter of the type in which the field is defined or a variable initializer)
                // u = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonly, "u").WithLocation(4, 1));
        }

        [Fact]
        public void ReadonlyLocal_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(string? maybe)
                    {
                        readonly string? v = maybe;
                        readonly var w = maybe;
                        readonly string x = maybe;
                        _ = v.Length;
                        _ = w.Length;
                        _ = x.Length;
                        if (v is not null) _ = v.Length;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,29): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         readonly string x = maybe;
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "maybe").WithLocation(8, 29),
                // (9,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = v.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "v").WithLocation(9, 13),
                // (10,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = w.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "w").WithLocation(10, 13),
                // (11,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = x.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "x").WithLocation(11, 13));
        }

        [Fact]
        public void ReadonlyLocal_SemanticModel()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        readonly int x = 1;
                        readonly var y = 2;
                        _ = x + y;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var declarations = tree.GetRoot().DescendantNodes().OfType<LocalDeclarationStatementSyntax>().ToArray();
            foreach (var declaration in declarations)
            {
                Assert.Equal(SyntaxKind.ReadOnlyKeyword, declaration.Modifiers.Single().Kind());
                Assert.Equal("System.Int32", model.GetTypeInfo(declaration.Declaration.Type).Type.ToTestDisplayString());
                var local = (ILocalSymbol)model.GetDeclaredSymbol(declaration.Declaration.Variables[0]);
                Assert.False(local.IsConst);
                Assert.Equal(RefKind.None, local.RefKind);
            }
        }

        [Fact]
        public void ReadonlyLocal_IOperation()
        {
            var source = """
                class C
                {
                    void M()
                    {
                        /*<bind>*/readonly int x = 1;/*</bind>*/
                        _ = x;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IVariableDeclarationGroupOperation (1 declarations) (OperationKind.VariableDeclarationGroup, Type: null) (Syntax: 'readonly int x = 1;')
                  IVariableDeclarationOperation (1 declarators) (OperationKind.VariableDeclaration, Type: null) (Syntax: 'int x = 1')
                    Declarators:
                        IVariableDeclaratorOperation (Symbol: System.Int32 x) (OperationKind.VariableDeclarator, Type: null) (Syntax: 'x = 1')
                          Initializer:
                            IVariableInitializerOperation (OperationKind.VariableInitializer, Type: null) (Syntax: '= 1')
                              ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                    Initializer:
                      null
                """;
            VerifyOperationTreeAndDiagnosticsForTest<LocalDeclarationStatementSyntax>(comp, expectedOperationTree, []);
        }

        [Theory]
        [CombinatorialData]
        public void ReadonlyLocal_Parsing(bool sharper)
        {
            // The readonly modifier parses the same way with and without Sharper; only its error goes away.
            var options = sharper ? s_sharperOptions : TestOptions.Regular;
            var statement = (LocalDeclarationStatementSyntax)SyntaxFactory.ParseStatement("readonly int x = 1;", options: options);
            Assert.Equal(SyntaxKind.ReadOnlyKeyword, statement.Modifiers.Single().Kind());
            Assert.Equal(!sharper, statement.Modifiers.Single().ContainsDiagnostics);

            statement = (LocalDeclarationStatementSyntax)SyntaxFactory.ParseStatement("using readonly var x = e;", options: options);
            Assert.Equal(SyntaxKind.ReadOnlyKeyword, statement.Modifiers.Single().Kind());
            Assert.Equal(!sharper, statement.Modifiers.Single().ContainsDiagnostics);

            var localFunction = (LocalFunctionStatementSyntax)SyntaxFactory.ParseStatement("readonly void F() { }", options: options);
            Assert.True(localFunction.Modifiers.Single().ContainsDiagnostics);
        }

        [Fact]
        public void ReadonlyParameter_Basic()
        {
            // readonly works on every kind of parameter list with a body, and the parameters read as usual.
            var source = """
                using System;

                var c = new C(1);
                c.M(2);
                Console.Write(c[3]);
                Console.Write((c + 4).P);
                Console.Write(5.Ext());
                Func<int, int> f = (readonly int x) => x + 1;
                Console.Write(f(5));
                Func<int, int> g = delegate (readonly int x) { return x + 2; };
                Console.Write(g(5));
                Local(8);
                Console.Write(C.Sum(1, 2, 6));
                Console.Write(C.Default());
                Console.Write(new P(10).Get());
                Console.Write(new R(11).X);

                static void Local(readonly int x) => Console.Write(x);

                class C
                {
                    public int P;
                    public C(readonly int p) { P = p; Console.Write(p); }
                    public void M(readonly int x) => Console.Write(x);
                    public int this[readonly int i] => i;
                    public static C operator +(readonly C c, readonly int i) => new C(c.P + i);
                    public static int Sum(readonly params int[] xs) => xs.Length + xs[2];
                    public static int Default(readonly int x = 7) => x;
                }

                static class E
                {
                    public static int Ext(readonly this int x) => x + 1;
                }

                class P(readonly int x)
                {
                    public int Get() => x;
                }

                record R(readonly int X);
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123556678971011" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ReadonlyParameter_FeatureDisabled()
        {
            // Without Sharper, readonly without ref keeps the C# error, and the parameter stays writable.
            var source = """
                class C
                {
                    void M(readonly int x)
                    {
                        x = 1;
                        System.Func<int, int> f = (readonly int y) => y = 2;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,12): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     void M(readonly int x)
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(3, 12),
                // (6,36): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //         System.Func<int, int> f = (readonly int y) => y = 2;
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(6, 36));
        }

        [Fact]
        public void ReadonlyParameter_Writes()
        {
            // Each write to the readonly parameter next to the same write to its plain twin, which is allowed.
            var source = """
                using System;

                class C
                {
                    static void Ref(ref int i) { }
                    static void Out(out int i) { i = 0; }
                    static void In(in int i) { }

                    void M(readonly int x, int y, readonly string s, string t)
                    {
                        x = 2;
                        y = 2;
                        x += 1;
                        y += 1;
                        x++;
                        y++;
                        --x;
                        --y;
                        Ref(ref x);
                        Ref(ref y);
                        Out(out x);
                        Out(out y);
                        In(in x);
                        In(in y);
                        ref int r = ref x;
                        ref int q = ref y;
                        ref readonly int rr = ref x;
                        Action a = () => x = 3;
                        Action b = () => y = 3;
                        void Local() { x = 4; }
                        void Local2() { y = 4; }
                        s ??= "a";
                        t ??= "a";
                        (x, y) = (5, 5);
                    }

                    C(readonly int x, int y) { x = 1; y = 1; }
                    int this[readonly int i, int j] { get { i = 1; j = 1; return 0; } set { i = value; j = value; } }
                    public static C operator -(readonly C c, C d) { c = null; d = null; return null; }
                    void LocalAndLambda()
                    {
                        void Local(readonly int x, int y) { x = 1; y = 1; }
                        Action<int, int> f = (readonly int x, int y) => { x = 1; y = 1; };
                        Action<int, int> g = delegate (readonly int x, int y) { x = 1; y = 1; };
                        Local(0, 0);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (11,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(11, 9),
                // (13,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x += 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(13, 9),
                // (15,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(15, 9),
                // (17,11): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         --x;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(17, 11),
                // (19,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly parameter'
                //         Ref(ref x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(19, 17),
                // (21,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly parameter'
                //         Out(out x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(21, 17),
                // (25,25): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly parameter'
                //         ref int r = ref x;
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(25, 25),
                // (28,26): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         Action a = () => x = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(28, 26),
                // (30,14): warning CS8321: The local function 'Local' is declared but never used
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local").WithArguments("Local").WithLocation(30, 14),
                // (30,24): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(30, 24),
                // (31,14): warning CS8321: The local function 'Local2' is declared but never used
                //         void Local2() { y = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local2").WithArguments("Local2").WithLocation(31, 14),
                // (32,9): error CS1656: Cannot assign to 's' because it is a 'readonly parameter'
                //         s ??= "a";
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "readonly parameter").WithLocation(32, 9),
                // (34,10): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         (x, y) = (5, 5);
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(34, 10),
                // (37,32): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     C(readonly int x, int y) { x = 1; y = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(37, 32),
                // (39,53): error CS1656: Cannot assign to 'c' because it is a 'readonly parameter'
                //     public static C operator -(readonly C c, C d) { c = null; d = null; return null; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "c").WithArguments("c", "readonly parameter").WithLocation(39, 53),
                // (42,45): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         void Local(readonly int x, int y) { x = 1; y = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(42, 45),
                // (43,59): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         Action<int, int> f = (readonly int x, int y) => { x = 1; y = 1; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(43, 59),
                // (44,65): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         Action<int, int> g = delegate (readonly int x, int y) { x = 1; y = 1; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(44, 65));
        }

        [Fact]
        public void ReadonlyParameter_StructFieldAssignment()
        {
            var source = """
                struct S { public int F; public int P { get; set; } }

                class C
                {
                    void M(readonly S v, S w)
                    {
                        v.F = 1;
                        w.F = 1;
                        v.P = 1;
                        w.P = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,9): error CS1654: Cannot modify members of 'v' because it is a 'readonly parameter'
                //         v.F = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.F").WithArguments("v", "readonly parameter").WithLocation(7, 9),
                // (9,9): error CS1654: Cannot modify members of 'v' because it is a 'readonly parameter'
                //         v.P = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "v.P").WithArguments("v", "readonly parameter").WithLocation(9, 9));
        }

        [Fact]
        public void ReadonlyParameter_Ref()
        {
            // readonly on a ref, in or out parameter forbids ref reassignment and keeps the C# rules for writes through the ref,
            // as for readonly ref locals. ref readonly keeps its C# meaning, and readonly ref readonly is both.
            var source = """
                class C
                {
                    static int j;

                    void M(readonly ref int r1, ref readonly int r2, readonly ref readonly int r3, readonly in int r4, readonly out int r5, ref int r6)
                    {
                        r5 = 5;
                        r1 = 5;
                        r2 = 5;
                        r3 = 5;
                        r4 = 5;
                        r6 = 5;
                        r1 = ref j;
                        r2 = ref j;
                        r3 = ref j;
                        r4 = ref j;
                        r5 = ref j;
                        r6 = ref j;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (9,9): error CS8331: Cannot assign to variable 'r2' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         r2 = 5;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "r2").WithArguments("variable", "r2").WithLocation(9, 9),
                // (10,9): error CS8331: Cannot assign to variable 'r3' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         r3 = 5;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "r3").WithArguments("variable", "r3").WithLocation(10, 9),
                // (11,9): error CS8331: Cannot assign to variable 'r4' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         r4 = 5;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "r4").WithArguments("variable", "r4").WithLocation(11, 9),
                // (13,9): error CS1656: Cannot assign to 'r1' because it is a 'readonly parameter'
                //         r1 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r1").WithArguments("r1", "readonly parameter").WithLocation(13, 9),
                // (15,9): error CS1656: Cannot assign to 'r3' because it is a 'readonly parameter'
                //         r3 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r3").WithArguments("r3", "readonly parameter").WithLocation(15, 9),
                // (16,9): error CS1656: Cannot assign to 'r4' because it is a 'readonly parameter'
                //         r4 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r4").WithArguments("r4", "readonly parameter").WithLocation(16, 9),
                // (17,9): error CS1656: Cannot assign to 'r5' because it is a 'readonly parameter'
                //         r5 = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r5").WithArguments("r5", "readonly parameter").WithLocation(17, 9));
        }

        [Fact]
        public void ReadonlyParameter_Modifiers()
        {
            // readonly must come first. Elsewhere, and on extension block receivers and function pointers, it keeps the C# errors.
            var source = """
                using System;

                unsafe class C
                {
                    void M1(readonly scoped Span<int> s) { s = default; }
                    void M2(scoped readonly Span<int> s) { }
                    void M3(readonly readonly int x) { }
                    void M4(in readonly int x) { }
                    void M5(readonly ref readonly readonly int x) { }
                    void M6(params readonly int[] x) { }
                    void M7(delegate*<readonly int, void> f) { }
                    void M8()
                    {
                        Action<int> f = (readonly x) => { x = 1; };
                        Action<int> g = (readonly int x) => { x = 1; };
                    }
                }

                static class E
                {
                    public static void Ext1(this readonly int x) { }
                    public static void Ext2(readonly this ref int x) { x = 1; x = ref x; }
                    extension(readonly int x)
                    {
                        public void Ext3() { }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.UnsafeDebugDll).VerifyDiagnostics(
                // (5,44): error CS1656: Cannot assign to 's' because it is a 'readonly parameter'
                //     void M1(readonly scoped Span<int> s) { s = default; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "readonly parameter").WithLocation(5, 44),
                // (6,20): error CS9350: The 'readonly' modifier cannot immediately follow the 'scoped' modifier.
                //     void M2(scoped readonly Span<int> s) { }
                Diagnostic(ErrorCode.ERR_InvalidModifierAfterScoped, "readonly").WithArguments("readonly").WithLocation(6, 20),
                // (6,20): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     void M2(scoped readonly Span<int> s) { }
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(6, 20),
                // (7,22): error CS1107: A parameter can only have one 'readonly' modifier
                //     void M3(readonly readonly int x) { }
                Diagnostic(ErrorCode.ERR_DupParamMod, "readonly").WithArguments("readonly").WithLocation(7, 22),
                // (8,16): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     void M4(in readonly int x) { }
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(8, 16),
                // (9,35): error CS1107: A parameter can only have one 'readonly' modifier
                //     void M5(readonly ref readonly readonly int x) { }
                Diagnostic(ErrorCode.ERR_DupParamMod, "readonly").WithArguments("readonly").WithLocation(9, 35),
                // (10,20): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     void M6(params readonly int[] x) { }
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(10, 20),
                // (11,23): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     void M7(delegate*<readonly int, void> f) { }
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(11, 23),
                // (14,43): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         Action<int> f = (readonly x) => { x = 1; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(14, 43),
                // (15,47): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         Action<int> g = (readonly int x) => { x = 1; };
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(15, 47),
                // (21,34): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     public static void Ext1(this readonly int x) { }
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(21, 34),
                // (22,63): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     public static void Ext2(readonly this ref int x) { x = 1; x = ref x; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(22, 63),
                // (23,15): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     extension(readonly int x)
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(23, 15));
        }

        [Fact]
        public void ReadonlyParameter_NotPartOfSignature()
        {
            // readonly is a rule about the body: overriding, implementing, partial methods, delegate conversions and
            // overload resolution ignore it, and it is not emitted.
            var source = """
                using System;

                abstract class B
                {
                    public abstract int M(int x);
                    public virtual int N(readonly int x) => x;
                    public abstract int A(readonly int x);
                }

                interface I { int M(readonly int x); }

                partial class C : B, I
                {
                    public override int M(readonly int x) => x;
                    public override int N(int x) { x++; return x; }
                    public override int A(int x) { x++; return x; }
                    int I.M(int x) { x++; return x; }
                    partial void P(readonly int x);
                    partial void P(int x) { x = 1; }
                }

                delegate int D(readonly int x);

                class Program
                {
                    static int F(int x) => x;
                    static int G(readonly int x) => x;
                    static void H(int x) { }
                    static void H(readonly int x) { }

                    static void Main()
                    {
                        D d1 = F;
                        Func<int, int> d2 = G;
                        D d3 = (int x) => x;
                        Func<int, int> d4 = (readonly int x) => x;
                        Console.Write(d1(1) + d2(2) + d3(3) + d4(4) + new C().N(5));
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (29,17): error CS0111: Type 'Program' already defines a member called 'H' with the same parameter types
                //     static void H(readonly int x) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "H").WithArguments("H", "Program").WithLocation(29, 17));
        }

        [Fact]
        public void ReadonlyParameter_StructMemberCallOnCopy()
        {
            // As for readonly locals, a non-readonly member runs on a copy and warns, also when captured or hoisted, and for members found by pattern.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                await M(new S(), new S());
                Console.Write(";");
                var p = new P(new S());
                p.Run();

                static async Task M(readonly S v, S w)
                {
                    v.Mutate();
                    Console.Write(v.F);
                    _ = v.Getter;
                    Console.Write(v.F);
                    foreach (var x in v) { }
                    Console.Write(v.F);
                    v.ReadOnlyMethod();
                    Action f = () => { v.Mutate(); Console.Write(v.F); };
                    f();
                    await Task.Yield();
                    v.Mutate();
                    Console.Write(v.F);
                    if (v is { Getter: 1 }) { }
                    Console.Write(v.F);
                    w.Mutate();
                    Console.Write(w.F);
                }

                class P(readonly S s)
                {
                    public void Run() { s.Mutate(); Console.Write(s.F); }
                }

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                    public int Getter { get { F++; return F; } }
                    public readonly void ReadOnlyMethod() { }
                    public IEnumerator<int> GetEnumerator() { F++; return new List<int>().GetEnumerator(); }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0000001;0" : null).VerifyDiagnostics(
                // (12,5): warning CS20013: Call to non-readonly member 'S.Mutate()' on readonly parameter 'v' runs on a copy, so 'v' is not changed.
                //     v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(12, 5),
                // (14,9): warning CS20013: Call to non-readonly member 'S.Getter.get' on readonly parameter 'v' runs on a copy, so 'v' is not changed.
                //     _ = v.Getter;
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "v").WithArguments("S.Getter.get", "v").WithLocation(14, 9),
                // (16,23): warning CS20013: Call to non-readonly member 'S.GetEnumerator()' on readonly parameter 'v' runs on a copy, so 'v' is not changed.
                //     foreach (var x in v) { }
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "v").WithArguments("S.GetEnumerator()", "v").WithLocation(16, 23),
                // (19,24): warning CS20013: Call to non-readonly member 'S.Mutate()' on readonly parameter 'v' runs on a copy, so 'v' is not changed.
                //     Action f = () => { v.Mutate(); Console.Write(v.F); };
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(19, 24),
                // (22,5): warning CS20013: Call to non-readonly member 'S.Mutate()' on readonly parameter 'v' runs on a copy, so 'v' is not changed.
                //     v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(22, 5),
                // (24,9): warning CS20013: Call to non-readonly member 'S.Getter.get' on readonly parameter 'v' runs on a copy, so 'v' is not changed.
                //     if (v is { Getter: 1 }) { }
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "v").WithArguments("S.Getter.get", "v").WithLocation(24, 9),
                // (32,25): warning CS20013: Call to non-readonly member 'S.Mutate()' on readonly parameter 's' runs on a copy, so 's' is not changed.
                //     public void Run() { s.Mutate(); Console.Write(s.F); }
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(32, 25));
        }

        [Fact]
        public void ReadonlyParameter_PrimaryConstructor()
        {
            // A captured readonly primary constructor parameter cannot be written in members or initializers, and its field is readonly.
            var source = """
                class C(readonly int x, int y)
                {
                    int A = x++;
                    int B = y++;
                    void M() { x = 1; y = 1; }
                    int Get() => x + y;
                }

                struct S(readonly int x)
                {
                    void M() { x = 1; }
                    int Get() => x;
                }

                record R(readonly int X)
                {
                    int Y = X++;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (3,13): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     int A = x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(3, 13),
                // (5,16): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     void M() { x = 1; y = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(5, 16),
                // (11,16): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     void M() { x = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(11, 16),
                // (17,13): error CS1656: Cannot assign to 'X' because it is a 'readonly parameter'
                //     int Y = X++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "X").WithArguments("X", "readonly parameter").WithLocation(17, 13));
        }

        [Fact]
        public void ReadonlyParameter_PrimaryConstructor_Field()
        {
            var source = """
                class C(readonly int x, int y)
                {
                    public int Get() => x + y;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugDll.WithMetadataImportOptions(MetadataImportOptions.All), symbolValidator: module =>
            {
                var c = module.GlobalNamespace.GetTypeMember("C");
                Assert.True(c.GetField("<x>P").IsReadOnly);
                Assert.False(c.GetField("<y>P").IsReadOnly);
            }).VerifyDiagnostics();
        }

        [Fact]
        public void ReadonlyParameter_Metadata()
        {
            // Nothing is emitted for readonly: plain C# sees ordinary parameters, and can override and call the members.
            var library = """
                public class B
                {
                    public virtual int M(readonly int x, readonly ref int r, readonly in int i, readonly out int o) { o = x; return r + i; }
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            libraryComp.VerifyDiagnostics();

            var parameters = libraryComp.GetMember<MethodSymbol>("B.M").Parameters;
            Assert.Equal([RefKind.None, RefKind.Ref, RefKind.In, RefKind.Out], parameters.Select(p => p.RefKind));
            Assert.All(parameters, p => Assert.Equal(ScopedKind.None, p.DeclaredScope));

            var source = """
                class C : B
                {
                    public override int M(int x, ref int r, in int i, out int o) { x++; o = 1; return x; }

                    static void Main()
                    {
                        int r = 2;
                        System.Console.Write(new B().M(1, ref r, 3, out int o) + o);
                        System.Console.Write(new C().M(1, ref r, 3, out o) + o);
                    }
                }
                """;
            foreach (var reference in new[] { libraryComp.ToMetadataReference(), libraryComp.EmitToImageReference() })
            {
                CompileAndVerify(source, references: [reference], parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "63" : null).VerifyDiagnostics();
            }

            CompileAndVerify(libraryComp, symbolValidator: module =>
            {
                var method = module.GlobalNamespace.GetMember<MethodSymbol>("B.M");
                Assert.All(method.Parameters, p => Assert.Empty(p.GetAttributes()));
                Assert.Equal([RefKind.None, RefKind.Ref, RefKind.In, RefKind.Out], method.Parameters.Select(p => p.RefKind));
            });
        }

        [Fact]
        public void ReadonlyParameter_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(readonly string? maybe, readonly string s)
                    {
                        _ = maybe.Length;
                        if (maybe is not null) _ = maybe.Length;
                        _ = s.Length;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = maybe.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "maybe").WithLocation(6, 13));
        }

        [Fact]
        public void ReadonlyParameter_SemanticModel()
        {
            // The parameter is an ordinary IParameterSymbol; (readonly x) => ... is implicitly typed, like (val x) => ....
            var source = """
                using System;

                class C
                {
                    void M(readonly int x, readonly ref int r)
                    {
                        Func<int, int> f = (readonly y) => y;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var parameters = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().Select(p => model.GetDeclaredSymbol(p)).ToArray();
            Assert.Equal(["System.Int32 x", "ref System.Int32 r", "System.Int32 y"], parameters.Select(p => p.ToTestDisplayString()));
            Assert.Equal([RefKind.None, RefKind.Ref, RefKind.None], parameters.Select(p => p.RefKind));
        }

        [Fact]
        public void ReadonlyParameter_IOperation()
        {
            var source = """
                class C
                {
                    int M(readonly int x)
                    /*<bind>*/{
                        return x + 1;
                    }/*</bind>*/
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IBlockOperation (1 statements) (OperationKind.Block, Type: null) (Syntax: '{ ... }')
                  IReturnOperation (OperationKind.Return, Type: null) (Syntax: 'return x + 1;')
                    ReturnedValue:
                      IBinaryOperation (BinaryOperatorKind.Add) (OperationKind.Binary, Type: System.Int32) (Syntax: 'x + 1')
                        Left:
                          IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                        Right:
                          ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<BlockSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void ReadonlyParameter_DataFlow()
        {
            var source = """
                class C
                {
                    int M(readonly int x, int y)
                    {
                        y = x;
                        return y;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var statement = tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>().Single();
            var dataFlow = model.AnalyzeDataFlow(statement);
            Assert.Equal("x", string.Join(", ", dataFlow.ReadInside.Select(s => s.Name)));
            Assert.Equal("y", string.Join(", ", dataFlow.WrittenInside.Select(s => s.Name)));
            Assert.Equal("this, x, y", string.Join(", ", dataFlow.WrittenOutside.Select(s => s.Name)));
        }

        [Fact]
        public void Infer_Basic()
        {
            // Each method with an inferred return type next to its C# twin, which writes the type out.
            var source = """
                using System;

                class C
                {
                    private static infer Greeting(int id) => $"Hello, {id}!";
                    private static string GreetingCSharp(int id) => $"Hello, {id}!";

                    private int _offset = 10;
                    infer Add(int x) => x + _offset;
                    int AddCSharp(int x) => x + _offset;

                    static void Main()
                    {
                        infer Twice(int x) => x * 2;
                        int TwiceCSharp(int x) => x * 2;
                        static infer Pair(int x) => (x, x.ToString());
                        static (int, string) PairCSharp(int x) => (x, x.ToString());
                        Console.Write(Pair(1));
                        Console.Write(PairCSharp(1));
                        Console.Write(Greeting(1));
                        Console.Write(GreetingCSharp(1));
                        Console.Write(Twice(21));
                        Console.Write(TwiceCSharp(21));
                        Console.Write(new C().Add(1));
                        Console.Write(new C().AddCSharp(1));
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "(1, 1)(1, 1)Hello, 1!Hello, 1!42421111" : null);
            verifier.VerifyDiagnostics();
        }

        [Fact]
        public void Infer_Types()
        {
            // The inferred type is the type 'var' would give a local initialized with the body, or void.
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    private static infer M01(int i) => i > 0 ? "a" : null;
                    private static string M01CSharp(int i) => i > 0 ? "a" : null;
                    private static infer M02() => (1, "a");
                    private static (int, string) M02CSharp() => (1, "a");
                    private static infer M03() => (A: 1, B: 2L);
                    private static (int A, long B) M03CSharp() => (A: 1, B: 2L);
                    private static infer M04() => new List<int> { 1 };
                    private static List<int> M04CSharp() => new List<int> { 1 };
                    private static infer M05() => new[] { 1, 2 };
                    private static int[] M05CSharp() => new[] { 1, 2 };
                    private static infer M06() => () => 1;
                    private static Func<int> M06CSharp() => () => 1;
                    private static infer M07() => M04CSharp;
                    private static Func<List<int>> M07CSharp() => M04CSharp;
                    private static infer M08<T>(T t) => t;
                    private static T M08CSharp<T>(T t) => t;
                    private static infer M09(int? i) => i;
                    private static int? M09CSharp(int? i) => i;
                    private static infer M10(int i) => i switch { 0 => 1L, _ => 2 };
                    private static long M10CSharp(int i) => i switch { 0 => 1L, _ => 2 };
                    private static infer M11() => Console.WriteLine();
                    private static void M11CSharp() => Console.WriteLine();
                    private static infer M12() => default(int);
                    private static int M12CSharp() => default(int);
                    private static infer M13(dynamic d) => d;
                    private static dynamic M13CSharp(dynamic d) => d;
                    private static infer M14(object o) => o is string s ? s : "";
                    private static string M14CSharp(object o) => o is string s ? s : "";
                    private static infer M15() => (byte)1;
                    private static byte M15CSharp() => (byte)1;
                    private static infer M16() => checked(1 + 2);
                    private static int M16CSharp() => checked(1 + 2);
                    private static infer M17(string s) => int.TryParse(s, out var n) ? n : s is { Length: var l } ? l : -1;
                    private static int M17CSharp(string s) => int.TryParse(s, out var n) ? n : s is { Length: var l } ? l : -1;
                    private static infer M18() => new Func<int>(() => { var x = 1; return x; });
                    private static Func<int> M18CSharp() => new Func<int>(() => { var x = 1; return x; });
                    private static infer M19(Span<int> s) => s.Slice(1);
                    private static Span<int> M19CSharp(Span<int> s) => s.Slice(1);
                    private static infer M20(int x) => (Func<int, string>)(i => (i + x).ToString());
                    private static Func<int, string> M20CSharp(int x) => (Func<int, string>)(i => (i + x).ToString());
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            foreach (var method in comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Where(m => m.Name.StartsWith("M") && !m.Name.EndsWith("CSharp")))
            {
                var twin = comp.GetMember<MethodSymbol>("C." + method.Name + "CSharp");
                Assert.Equal(twin.ReturnTypeWithAnnotations.ToTestDisplayString(includeNonNullable: true), method.ReturnTypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
                Assert.Equal(twin.ReturnsVoid, method.ReturnsVoid);
            }
        }

        [Fact]
        public void Infer_NoType()
        {
            // An expression with no type of its own gives nothing to infer from, as for 'var' (CS0815, CS8917).
            var source = """
                class C
                {
                    private static infer M1() => null;
                    private static infer M2() => default;
                    private static infer M3() => x => x;
                    private static infer M4() => System.Console.Write;
                    private static infer M5() => throw null;
                    private static infer M6() => new();
                    private static infer M7() => [1, 2];
                    private static infer M8() => (1, null);

                    static void Main()
                    {
                        infer L1() => null;
                        infer L2() => x => x;
                        var v1 = null;
                        var v2 = x => x;
                        var v3 = default;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,34): error CS20014: Cannot infer the return type of 'C.M1()', because its body (<null>) has no type. Write the return type.
                //     private static infer M1() => null;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "null").WithArguments("C.M1()", "<null>").WithLocation(3, 34),
                // (4,34): error CS20014: Cannot infer the return type of 'C.M2()', because its body (default) has no type. Write the return type.
                //     private static infer M2() => default;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "default").WithArguments("C.M2()", "default").WithLocation(4, 34),
                // (5,34): error CS20014: Cannot infer the return type of 'C.M3()', because its body (lambda expression) has no type. Write the return type.
                //     private static infer M3() => x => x;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "x => x").WithArguments("C.M3()", "lambda expression").WithLocation(5, 34),
                // (6,34): error CS20014: Cannot infer the return type of 'C.M4()', because its body (method group) has no type. Write the return type.
                //     private static infer M4() => System.Console.Write;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "System.Console.Write").WithArguments("C.M4()", "method group").WithLocation(6, 34),
                // (7,34): error CS20014: Cannot infer the return type of 'C.M5()', because its body (<throw expression>) has no type. Write the return type.
                //     private static infer M5() => throw null;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "throw null").WithArguments("C.M5()", "<throw expression>").WithLocation(7, 34),
                // (8,34): error CS20014: Cannot infer the return type of 'C.M6()', because its body (new()) has no type. Write the return type.
                //     private static infer M6() => new();
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "new()").WithArguments("C.M6()", "new()").WithLocation(8, 34),
                // (9,34): error CS20014: Cannot infer the return type of 'C.M7()', because its body (collection expression) has no type. Write the return type.
                //     private static infer M7() => [1, 2];
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "[1, 2]").WithArguments("C.M7()", "collection expression").WithLocation(9, 34),
                // (10,34): error CS20014: Cannot infer the return type of 'C.M8()', because its body ((int, <null>)) has no type. Write the return type.
                //     private static infer M8() => (1, null);
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "(1, null)").WithArguments("C.M8()", "(int, <null>)").WithLocation(10, 34),
                // (14,15): warning CS8321: The local function 'L1' is declared but never used
                //         infer L1() => null;
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L1").WithArguments("L1").WithLocation(14, 15),
                // (14,23): error CS20014: Cannot infer the return type of 'L1()', because its body (<null>) has no type. Write the return type.
                //         infer L1() => null;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "null").WithArguments("L1()", "<null>").WithLocation(14, 23),
                // (15,15): warning CS8321: The local function 'L2' is declared but never used
                //         infer L2() => x => x;
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L2").WithArguments("L2").WithLocation(15, 15),
                // (15,23): error CS20014: Cannot infer the return type of 'L2()', because its body (lambda expression) has no type. Write the return type.
                //         infer L2() => x => x;
                Diagnostic(ErrorCode.ERR_SharperInferNoType, "x => x").WithArguments("L2()", "lambda expression").WithLocation(15, 23),
                // (16,13): error CS0815: Cannot assign <null> to an implicitly-typed variable
                //         var v1 = null;
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedVariableAssignedBadValue, "v1 = null").WithArguments("<null>").WithLocation(16, 13),
                // (17,18): error CS8917: The delegate type could not be inferred.
                //         var v2 = x => x;
                Diagnostic(ErrorCode.ERR_CannotInferDelegateType, "x => x").WithLocation(17, 18),
                // (18,18): error CS8716: There is no target type for the default literal.
                //         var v3 = default;
                Diagnostic(ErrorCode.ERR_DefaultLiteralNoTargetType, "default").WithLocation(18, 18));
        }

        [Fact]
        public void Infer_FeatureDisabled()
        {
            var source = """
                class C
                {
                    private static infer M() => 1;

                    static void Main()
                    {
                        infer L() => 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,20): error CS0246: The type or namespace name 'infer' could not be found (are you missing a using directive or an assembly reference?)
                //     private static infer M() => 1;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "infer").WithArguments("infer").WithLocation(3, 20),
                // (7,9): error CS0246: The type or namespace name 'infer' could not be found (are you missing a using directive or an assembly reference?)
                //         infer L() => 1;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "infer").WithArguments("infer").WithLocation(7, 9),
                // (7,15): warning CS8321: The local function 'L' is declared but never used
                //         infer L() => 1;
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L").WithArguments("L").WithLocation(7, 15));
        }

        [Fact]
        public void Infer_TypeNamedInfer()
        {
            // A type named 'infer' in scope wins, as it does for 'var'.
            var source = """
                class infer { }

                class C
                {
                    private static infer M() => new infer();
                    private static infer N() => 1;

                    static void Main()
                    {
                        infer L() => new infer();
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (1,7): warning CS8981: The type name 'infer' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class infer { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "infer").WithArguments("infer").WithLocation(1, 7),
                // (6,33): error CS0029: Cannot implicitly convert type 'int' to 'infer'
                //     private static infer N() => 1;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "1").WithArguments("int", "infer").WithLocation(6, 33),
                // (10,15): warning CS8321: The local function 'L' is declared but never used
                //         infer L() => new infer();
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L").WithArguments("L").WithLocation(10, 15));
            Assert.Equal("infer", comp.GetMember<MethodSymbol>("C.M").ReturnType.ToTestDisplayString());
            Assert.False(((SourceOrdinaryMethodSymbol)comp.GetMember<MethodSymbol>("C.M")).IsSharperInferredReturnType);
        }

        [Fact]
        public void Infer_NotSupportedYet()
        {
            var source = """
                using System.Threading.Tasks;

                interface I
                {
                    infer M();
                    int N();
                }

                abstract class B
                {
                    public abstract infer Abstract();
                    public virtual int Virtual() => 1;
                }

                partial class C : B, I
                {
                    private infer BlockBody() { return 1; }
                    public infer Public() => 1;
                    internal infer Internal() => 1;
                    protected infer Protected() => 1;
                    private protected infer PrivateProtected() => 1;
                    public override infer Virtual() => 2;
                    infer I.N() => 1;
                    public infer M() => 1;
                    private partial infer Partial();
                    private partial infer Partial() => 1;
                    private async infer Async() => await Task.FromResult(1);
                    private int _field;
                    private ref infer Ref() => ref _field;
                    private static extern infer Extern();
                    private infer Property => 1;
                    private infer this[int i] => i;
                    public static infer operator +(C c) => c;
                    public static implicit operator infer(C c) => 1;
                    public override abstract infer Abstract();

                    void Locals()
                    {
                        infer LBlock() { return 1; }
                        async infer LAsync() => await Task.FromResult(1);
                        ref infer LRef() => ref _field;
                        static extern infer LExtern();
                    }
                }

                static class E
                {
                    extension(int i)
                    {
                        private infer Twice() => i * 2;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,5): error CS20016: 'I.M()' has no body to infer its return type from. Write the return type.
                //     infer M();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("I.M()").WithLocation(5, 5),
                // (11,21): error CS20016: 'B.Abstract()' has no body to infer its return type from. Write the return type.
                //     public abstract infer Abstract();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("B.Abstract()").WithLocation(11, 21),
                // (15,22): error CS0535: 'C' does not implement interface member 'I.N()'
                // partial class C : B, I
                Diagnostic(ErrorCode.ERR_UnimplementedInterfaceMember, "I").WithArguments("C", "I.N()").WithLocation(15, 22),
                // (17,13): error CS20015: An inferred return type ('infer') is not supported on a block body yet. Write the return type.
                //     private infer BlockBody() { return 1; }
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a block body").WithLocation(17, 13),
                // (18,12): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     public infer Public() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(18, 12),
                // (19,14): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     internal infer Internal() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(19, 14),
                // (20,15): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     protected infer Protected() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(20, 15),
                // (21,23): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     private protected infer PrivateProtected() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(21, 23),
                // (22,21): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     public override infer Virtual() => 2;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(22, 21),
                // (23,5): error CS20015: An inferred return type ('infer') is not supported on an explicit interface implementation yet. Write the return type.
                //     infer I.N() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an explicit interface implementation").WithLocation(23, 5),
                // (23,13): error CS9334: 'C.N()': return type must be 'int' to match implemented member 'I.N()'
                //     infer I.N() => 1;
                Diagnostic(ErrorCode.ERR_ExplicitInterfaceMemberReturnTypeMismatch, "N").WithArguments("C.N()", "int", "I.N()").WithLocation(23, 13),
                // (24,12): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     public infer M() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(24, 12),
                // (25,21): error CS20016: 'C.Partial()' has no body to infer its return type from. Write the return type.
                //     private partial infer Partial();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("C.Partial()").WithLocation(25, 21),
                // (26,21): error CS20015: An inferred return type ('infer') is not supported on a partial method yet. Write the return type.
                //     private partial infer Partial() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a partial method").WithLocation(26, 21),
                // (27,19): error CS20015: An inferred return type ('infer') is not supported on an async method yet. Write the return type.
                //     private async infer Async() => await Task.FromResult(1);
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an async method").WithLocation(27, 19),
                // (29,17): error CS20015: An inferred return type ('infer') is not supported on a ref return yet. Write the return type.
                //     private ref infer Ref() => ref _field;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a ref return").WithLocation(29, 17),
                // (30,27): error CS20016: 'C.Extern()' has no body to infer its return type from. Write the return type.
                //     private static extern infer Extern();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("C.Extern()").WithLocation(30, 27),
                // (30,33): warning CS0626: Method, operator, or accessor 'C.Extern()' is marked external and has no attributes on it. Consider adding a DllImport attribute to specify the external implementation.
                //     private static extern infer Extern();
                Diagnostic(ErrorCode.WRN_ExternMethodNoImplementation, "Extern").WithArguments("C.Extern()").WithLocation(30, 33),
                // (31,13): error CS20015: An inferred return type ('infer') is not supported on a property or indexer yet. Write the return type.
                //     private infer Property => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a property or indexer").WithLocation(31, 13),
                // (32,13): error CS20015: An inferred return type ('infer') is not supported on a property or indexer yet. Write the return type.
                //     private infer this[int i] => i;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a property or indexer").WithLocation(32, 13),
                // (33,19): error CS20015: An inferred return type ('infer') is not supported on an operator yet. Write the return type.
                //     public static infer operator +(C c) => c;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an operator").WithLocation(33, 19),
                // (34,37): error CS20015: An inferred return type ('infer') is not supported on an operator yet. Write the return type.
                //     public static implicit operator infer(C c) => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an operator").WithLocation(34, 37),
                // (35,30): error CS20016: 'C.Abstract()' has no body to infer its return type from. Write the return type.
                //     public override abstract infer Abstract();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("C.Abstract()").WithLocation(35, 30),
                // (35,36): error CS0513: 'C.Abstract()' is abstract but it is contained in non-abstract type 'C'
                //     public override abstract infer Abstract();
                Diagnostic(ErrorCode.ERR_AbstractInConcreteClass, "Abstract").WithArguments("C.Abstract()", "C").WithLocation(35, 36),
                // (39,9): error CS20015: An inferred return type ('infer') is not supported on a block body yet. Write the return type.
                //         infer LBlock() { return 1; }
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a block body").WithLocation(39, 9),
                // (39,15): warning CS8321: The local function 'LBlock' is declared but never used
                //         infer LBlock() { return 1; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "LBlock").WithArguments("LBlock").WithLocation(39, 15),
                // (40,15): error CS20015: An inferred return type ('infer') is not supported on an async method yet. Write the return type.
                //         async infer LAsync() => await Task.FromResult(1);
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an async method").WithLocation(40, 15),
                // (40,21): warning CS8321: The local function 'LAsync' is declared but never used
                //         async infer LAsync() => await Task.FromResult(1);
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "LAsync").WithArguments("LAsync").WithLocation(40, 21),
                // (41,9): error CS20015: An inferred return type ('infer') is not supported on a ref return yet. Write the return type.
                //         ref infer LRef() => ref _field;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "ref infer").WithArguments("on a ref return").WithLocation(41, 9),
                // (41,19): warning CS8321: The local function 'LRef' is declared but never used
                //         ref infer LRef() => ref _field;
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "LRef").WithArguments("LRef").WithLocation(41, 19),
                // (42,23): error CS20016: 'LExtern()' has no body to infer its return type from. Write the return type.
                //         static extern infer LExtern();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("LExtern()").WithLocation(42, 23),
                // (42,29): warning CS0626: Method, operator, or accessor 'LExtern()' is marked external and has no attributes on it. Consider adding a DllImport attribute to specify the external implementation.
                //         static extern infer LExtern();
                Diagnostic(ErrorCode.WRN_ExternMethodNoImplementation, "LExtern").WithArguments("LExtern()").WithLocation(42, 29),
                // (42,29): warning CS8321: The local function 'LExtern' is declared but never used
                //         static extern infer LExtern();
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "LExtern").WithArguments("LExtern").WithLocation(42, 29),
                // (50,17): error CS20015: An inferred return type ('infer') is not supported on an extension block member yet. Write the return type.
                //         private infer Twice() => i * 2;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an extension block member").WithLocation(50, 17));
        }

        [Fact]
        public void Infer_UsesInferredMethod()
        {
            // Inferring a return type from a body that uses another inferred return type is step R3.
            var source = """
                class C
                {
                    private static infer A() => B() + 1;
                    private static infer B() => 1;
                    private static infer D() => B;
                    private static int E() => A() + B();

                    static void Main()
                    {
                        infer L1() => 1;
                        infer L2() => L1() + B();
                        int L3() => L1() + L2();
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,20): error CS20017: Cannot infer the return type of 'C.A()', because its body uses 'C.B()', whose return type is inferred too. Write the return type of one of them.
                //     private static infer A() => B() + 1;
                Diagnostic(ErrorCode.ERR_SharperInferUsesInferredMethod, "infer").WithArguments("C.A()", "C.B()").WithLocation(3, 20),
                // (5,20): error CS20017: Cannot infer the return type of 'C.D()', because its body uses 'C.B()', whose return type is inferred too. Write the return type of one of them.
                //     private static infer D() => B;
                Diagnostic(ErrorCode.ERR_SharperInferUsesInferredMethod, "infer").WithArguments("C.D()", "C.B()").WithLocation(5, 20),
                // (11,9): error CS20017: Cannot infer the return type of 'L2()', because its body uses 'L1()', whose return type is inferred too. Write the return type of one of them.
                //         infer L2() => L1() + B();
                Diagnostic(ErrorCode.ERR_SharperInferUsesInferredMethod, "infer").WithArguments("L2()", "L1()").WithLocation(11, 9),
                // (12,13): warning CS8321: The local function 'L3' is declared but never used
                //         int L3() => L1() + L2();
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L3").WithArguments("L3").WithLocation(12, 13));
        }

        [Fact]
        public void Infer_UsesInferredMethod_OrderDoesNotMatter()
        {
            // Whichever method is inferred first, the method that uses the other reports the error.
            var source = """
                class C
                {
                    private static infer A() => B() + 1;
                    private static infer B() => 1;
                }
                """;
            foreach (var first in new[] { "C.A", "C.B" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<MethodSymbol>(first).ReturnType;
                comp.VerifyDiagnostics(
                    // (3,20): error CS20017: Cannot infer the return type of 'C.A()', because its body uses 'C.B()', whose return type is inferred too. Write the return type of one of them.
                    //     private static infer A() => B() + 1;
                    Diagnostic(ErrorCode.ERR_SharperInferUsesInferredMethod, "infer").WithArguments("C.A()", "C.B()").WithLocation(3, 20));
                Assert.Equal("infer", comp.GetMember<MethodSymbol>("C.A").ReturnType.ToTestDisplayString());
                Assert.Equal("System.Int32", comp.GetMember<MethodSymbol>("C.B").ReturnType.ToTestDisplayString());
            }
        }

        [Fact]
        public void Infer_Concurrent()
        {
            // The type is inferred outside of any lock; threads that race get the same type, and the errors are reported once.
            var builder = new System.Text.StringBuilder("class C\n{\n");
            for (int i = 0; i < 50; i++)
            {
                builder.AppendLine($"    private static infer M{i}(int x) => x > {i} ? \"a\" : null;");
                builder.AppendLine($"    private static infer N{i}() => null;");
            }

            builder.AppendLine("}");
            var comp = CreateCompilation(builder.ToString(), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var methods = comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToArray();
            System.Threading.Tasks.Parallel.For(0, 8, _ =>
            {
                foreach (var method in methods)
                {
                    Assert.Equal(method.Name.StartsWith("M") ? "System.String" : "infer", method.ReturnType.ToTestDisplayString());
                }
            });
            Assert.Equal(50, comp.GetDiagnostics().Count(d => d.Code == (int)ErrorCode.ERR_SharperInferNoType));
        }

        [Fact]
        public void Infer_UsesItself()
        {
            // A method that needs its own inferred return type must write it, as in F#.
            var source = """
                class C
                {
                    private static infer Fib(int i) => i < 2 ? 1 : Fib(i - 1) + Fib(i - 2);
                    private static infer Self() => Self;
                    private static int Caller() => Fib(3);

                    static void Main()
                    {
                        infer Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,20): error CS20018: Cannot infer the return type of 'C.Fib(int)', because its body uses 'C.Fib(int)' itself. Write the return type.
                //     private static infer Fib(int i) => i < 2 ? 1 : Fib(i - 1) + Fib(i - 2);
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("C.Fib(int)").WithLocation(3, 20),
                // (4,20): error CS20018: Cannot infer the return type of 'C.Self()', because its body uses 'C.Self()' itself. Write the return type.
                //     private static infer Self() => Self;
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("C.Self()").WithLocation(4, 20),
                // (9,9): error CS20018: Cannot infer the return type of 'Fact(int)', because its body uses 'Fact(int)' itself. Write the return type.
                //         infer Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("Fact(int)").WithLocation(9, 9));
        }

        [Fact]
        public void Infer_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    private static infer MaybeNull(string? s) => s;
                    private static string? MaybeNullCSharp(string? s) => s;
                    private static infer NotNull(string? s) => s ?? "";
                    private static string NotNullCSharp(string? s) => s ?? "";
                    private static infer Suppressed(string? s) => s!;
                    private static string SuppressedCSharp(string? s) => s!;
                    private static infer Generic<T>(T t) => t;
                    private static T GenericCSharp<T>(T t) => t;
                    private static infer MaybeDefault<T>(T t) => default(T);
                    private static T? MaybeDefaultCSharp<T>(T t) => default(T);
                    private static infer Checked(string? s) => s is null ? "" : s;
                    private static string CheckedCSharp(string? s) => s is null ? "" : s;
                    private static infer Nested(string? s) => new[] { s };
                    private static string?[] NestedCSharp(string? s) => new[] { s };
                #nullable disable
                    private static infer Oblivious(string s) => s;
                    private static string ObliviousCSharp(string s) => s;
                #nullable enable

                    static void Main()
                    {
                        _ = MaybeNull("a").Length;
                        _ = MaybeNullCSharp("a").Length;
                        _ = NotNull(null).Length;
                        _ = Oblivious(null).Length;
                        infer Local(string? s) => s;
                        _ = Local("a").Length;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (25,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = MaybeNull("a").Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, @"MaybeNull(""a"")").WithLocation(25, 13),
                // (26,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = MaybeNullCSharp("a").Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, @"MaybeNullCSharp(""a"")").WithLocation(26, 13),
                // (30,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = Local("a").Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, @"Local(""a"")").WithLocation(30, 13));
            foreach (var method in comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Where(m => m.Name != "Main" && !m.Name.EndsWith("CSharp") && m.MethodKind == MethodKind.Ordinary))
            {
                var twin = comp.GetMember<MethodSymbol>("C." + method.Name + "CSharp");
                Assert.Equal(twin.ReturnTypeWithAnnotations.ToTestDisplayString(includeNonNullable: true), method.ReturnTypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
            }
        }

        [Fact]
        public void Infer_VoidAndAnonymousType()
        {
            // A body that returns nothing infers void; a private method can return an anonymous type, which C# cannot write.
            var source = """
                using System;

                class C
                {
                    private static infer Log(string s) => Console.Write(s);
                    private static infer Point(int x) => new { X = x, Y = x * 2 };

                    static void Main()
                    {
                        Log("a");
                        var p = Point(3);
                        Console.Write(p.Y);
                        infer LocalLog() => Console.Write("b");
                        LocalLog();
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "a6b" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            Assert.True(comp.GetMember<MethodSymbol>("C.Log").ReturnsVoid);
            Assert.True(comp.GetMember<MethodSymbol>("C.Point").ReturnType.IsAnonymousType);
        }

        [Fact]
        public void Infer_FileType()
        {
            // The checks of a written return type apply to the inferred one.
            var source = """
                file class F { }

                class C
                {
                    private static infer M() => new F();
                    private static F MCSharp() => new F();
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,26): error CS9051: File-local type 'F' cannot be used in a member signature in non-file-local type 'C'.
                //     private static infer M() => new F();
                Diagnostic(ErrorCode.ERR_FileTypeDisallowedInSignature, "M").WithArguments("F", "C").WithLocation(5, 26),
                // (6,22): error CS9051: File-local type 'F' cannot be used in a member signature in non-file-local type 'C'.
                //     private static F MCSharp() => new F();
                Diagnostic(ErrorCode.ERR_FileTypeDisallowedInSignature, "MCSharp").WithArguments("F", "C").WithLocation(6, 22));
        }

        [Fact]
        public void Infer_SemanticModel()
        {
            var source = """
                #nullable enable
                class C
                {
                    private static infer M(string? s) => s;
                    private static string? MCSharp(string? s) => s;

                    static void Main()
                    {
                        infer L(int i) => i.ToString();
                        string LCSharp(int i) => i.ToString();
                        _ = L(1) + LCSharp(1) + M(null) + MCSharp(null);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
            var localFunctions = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToArray();
            verify(model.GetDeclaredSymbol(methods[0]), methods[0].ReturnType, model.GetDeclaredSymbol(methods[1]), methods[1].ReturnType);
            verify(model.GetDeclaredSymbol(localFunctions[0]), localFunctions[0].ReturnType, model.GetDeclaredSymbol(localFunctions[1]), localFunctions[1].ReturnType);

            void verify(IMethodSymbol method, TypeSyntax infer, IMethodSymbol twin, TypeSyntax twinType)
            {
                Assert.Equal(twin.ReturnType.ToTestDisplayString(includeNonNullable: true), method.ReturnType.ToTestDisplayString(includeNonNullable: true));
                Assert.Equal(twin.ReturnNullableAnnotation, method.ReturnNullableAnnotation);
                Assert.Equal(twin.ToTestDisplayString(), method.ToTestDisplayString().Replace(method.Name, twin.Name));

                // Like 'var', 'infer' keeps the top-level annotation, which GetTypeInfo drops for a written type.
                var typeInfo = model.GetTypeInfo(infer);
                var twinTypeInfo = model.GetTypeInfo(twinType);
                Assert.Equal(method.ReturnType.ToTestDisplayString(), typeInfo.Type.ToTestDisplayString());
                Assert.True(twinTypeInfo.Type.Equals(typeInfo.Type, SymbolEqualityComparer.Default));
                Assert.Equal(method.ReturnNullableAnnotation, typeInfo.Nullability.Annotation);
                Assert.True(twinTypeInfo.Type.Equals(model.GetSymbolInfo(infer).Symbol, SymbolEqualityComparer.Default));
            }
        }

        [Fact]
        public void Infer_IOperation()
        {
            // The body binds against the inferred type, as it would against the written type.
            var source = """
                class C
                {
                    private static infer M(int x) /*<bind>*/=> x * 2/*</bind>*/;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IBlockOperation (1 statements) (OperationKind.Block, Type: null) (Syntax: '=> x * 2')
                  IReturnOperation (OperationKind.Return, Type: null, IsImplicit) (Syntax: 'x * 2')
                    ReturnedValue:
                      IBinaryOperation (BinaryOperatorKind.Multiply) (OperationKind.Binary, Type: System.Int32) (Syntax: 'x * 2')
                        Left:
                          IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                        Right:
                          ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 2) (Syntax: '2')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<ArrowExpressionClauseSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void Infer_Metadata()
        {
            // The emitted method has the inferred return type, with the nullable annotation, as a C# compilation sees it.
            var source = """
                #nullable enable
                public class C
                {
                    private static infer M(string? s) => s;
                    private static infer N(int i) => i.ToString();
                    public static string? Call(string? s) => M(s) + N(1);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugDll.WithMetadataImportOptions(MetadataImportOptions.All));
            CompileAndVerify(comp, symbolValidator: module =>
            {
                Assert.Equal("System.String? C.M(System.String? s)", module.GlobalNamespace.GetMember<MethodSymbol>("C.M").ToTestDisplayString(includeNonNullable: true));
                Assert.Equal("System.String! C.N(System.Int32 i)", module.GlobalNamespace.GetMember<MethodSymbol>("C.N").ToTestDisplayString(includeNonNullable: true));
            }).VerifyDiagnostics();

            var reference = comp.EmitToImageReference();
            var csharp = CreateCompilation("", references: [reference], targetFramework: TargetFramework.Net90, options: TestOptions.DebugDll.WithMetadataImportOptions(MetadataImportOptions.All));
            Assert.Equal("System.String? C.M(System.String? s)", csharp.GetMember<MethodSymbol>("C.M").ToTestDisplayString(includeNonNullable: true));
        }

        [Fact]
        public void InferParameter_Basic()
        {
            // Each member with an inferred parameter type next to its C# twin, which writes the type out.
            var source = """
                using System;

                class C
                {
                    static string Fetch(string url, infer retries = 3, infer name = "n", infer ratio = 0.5, infer day = DayOfWeek.Monday) => $"{url}:{retries}:{name}:{ratio}:{day};";
                    static string FetchCSharp(string url, int retries = 3, string name = "n", double ratio = 0.5, DayOfWeek day = DayOfWeek.Monday) => $"{url}:{retries}:{name}:{ratio}:{day};";

                    static void Main()
                    {
                        Console.Write(Fetch("a"));
                        Console.Write(FetchCSharp("a"));
                        Console.Write(Fetch("b", 5, ratio: 1.5));
                        Console.Write(FetchCSharp("b", 5, ratio: 1.5));
                        string Local(infer x = 'c') => x.ToString() + x.GetType().Name;
                        string LocalCSharp(char x = 'c') => x.ToString() + x.GetType().Name;
                        Console.Write(Local() + LocalCSharp() + ";");
                        var lambda = (infer x = 10L) => x.GetType().Name;
                        var lambdaCSharp = (long x = 10L) => x.GetType().Name;
                        Console.Write(lambda() + lambdaCSharp() + ";");
                        Console.Write(ReadOnly() + ReadOnlyCSharp() + ";");
                    }

                    static string ReadOnly(readonly infer x = 1u) => x.GetType().Name;
                    static string ReadOnlyCSharp(readonly uint x = 1u) => x.GetType().Name;
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "a:3:n:0.5:Monday;a:3:n:0.5:Monday;b:5:n:1.5:Monday;b:5:n:1.5:Monday;cCharcChar;Int64Int64;UInt32UInt32;" : null);
            verifier.VerifyDiagnostics();
        }

        [Fact]
        public void InferParameter_Members()
        {
            // Every kind of member with a parameter list infers the same way.
            var source = """
                using System;

                delegate string D(infer x = 1);
                delegate string DCSharp(int x = 1);

                class P(infer x = 2)
                {
                    public string X => x.GetType().Name;
                }

                class PCSharp(int x = 2)
                {
                    public string X => x.GetType().Name;
                }

                record R(infer Name = "r");
                record RCSharp(string Name = "r");

                class C
                {
                    public C(infer x = 1.5f) => Value = x.GetType().Name;
                    public C(bool b, float x = 1.5f) => Value = x.GetType().Name;
                    public string Value;
                    public string this[int a, infer i = (short)1] => i.GetType().Name;
                    public string this[bool a, short i = (short)1] => i.GetType().Name;
                    public virtual string V(infer x = 1u) => x.GetType().Name;
                    public static string E() => "";

                    static void Main()
                    {
                        D d = x => x.GetType().Name;
                        DCSharp dCSharp = x => x.GetType().Name;
                        Console.Write(d() + dCSharp() + ";");
                        Console.Write(new P().X + new PCSharp().X + ";");
                        Console.Write(new R().Name.GetType().Name + new RCSharp().Name.GetType().Name + ";");
                        Console.Write(new C().Value + new C(true).Value + ";");
                        Console.Write(new C()[0] + new C()[true] + ";");
                        Console.Write(new Derived().V() + new Derived().V(2u) + ";");
                        Console.Write(5.Ext() + 5.ExtCSharp() + ";");
                    }
                }

                class Derived : C
                {
                    public override string V(infer x = 1u) => "D" + x;
                }

                static class Extensions
                {
                    public static string Ext(this int i, infer s = "e") => i + s;
                    public static string ExtCSharp(this int i, string s = "e") => i + s;
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Int32Int32;Int32Int32;StringString;SingleSingle;Int16Int16;D1D2;5e5e;" : null);
            verifier.VerifyDiagnostics();
        }

        [Fact]
        public void InferParameter_Types()
        {
            // The inferred type is the type 'var' would give a local initialized with the default value.
            var source = """
                #nullable enable
                struct S { }
                enum E { A }

                class C
                {
                    const long K = 1;
                    void M01(infer x = 1) { }
                    void M01CSharp(int x = 1) { }
                    void M02(infer x = -1L) { }
                    void M02CSharp(long x = -1L) { }
                    void M03(infer x = 1 + 2.0) { }
                    void M03CSharp(double x = 1 + 2.0) { }
                    void M04(infer x = K) { }
                    void M04CSharp(long x = K) { }
                    void M05(infer x = E.A) { }
                    void M05CSharp(E x = E.A) { }
                    void M06(infer x = (byte)1) { }
                    void M06CSharp(byte x = (byte)1) { }
                    void M07(infer x = 1m) { }
                    void M07CSharp(decimal x = 1m) { }
                    void M08(infer x = nameof(M08)) { }
                    void M08CSharp(string x = nameof(M08)) { }
                    void M09(infer x = default(int?)) { }
                    void M09CSharp(int? x = default(int?)) { }
                    void M10(infer x = default(string)) { }
                    void M10CSharp(string? x = default(string)) { }
                    void M11(infer x = (string?)null) { }
                    void M11CSharp(string? x = (string?)null) { }
                    void M12(infer x = new S()) { }
                    void M12CSharp(S x = new S()) { }
                    void M13(infer x = default(S)) { }
                    void M13CSharp(S x = default(S)) { }
                    void M14(infer x = "a" + "b") { }
                    void M14CSharp(string x = "a" + "b") { }
                    void M15<T>(infer x = default(T)) { }
                    void M15CSharp<T>(T? x = default(T)) { }
                    void M16(infer x = (object)null) { }
                    void M16CSharp(object? x = (object)null) { }
                    void M17(infer x = 'a') { }
                    void M17CSharp(char x = 'a') { }
                    void M18(infer x = true) { }
                    void M18CSharp(bool x = true) { }
                    void M19(in infer x = 1) { }
                    void M19CSharp(in int x = 1) { }
                    void M20(infer x = sizeof(int)) { }
                    void M20CSharp(int x = sizeof(int)) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (38,24): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //     void M16(infer x = (object)null) { }
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "(object)null").WithLocation(38, 24),
                // (39,32): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //     void M16CSharp(object? x = (object)null) { }
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "(object)null").WithLocation(39, 32));
            foreach (var method in comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Where(m => m.Name.StartsWith("M") && !m.Name.EndsWith("CSharp")))
            {
                var twin = comp.GetMember<MethodSymbol>("C." + method.Name + "CSharp");
                Assert.Equal(twin.Parameters[0].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true), method.Parameters[0].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
                Assert.Equal(twin.Parameters[0].RefKind, method.Parameters[0].RefKind);
                Assert.Equal(twin.Parameters[0].ExplicitDefaultConstantValue, method.Parameters[0].ExplicitDefaultConstantValue);
            }
        }

        [Fact]
        public void InferParameter_NoType()
        {
            // A default value with no type of its own gives nothing to infer from.
            var source = """
                class C
                {
                    void M1(infer x = null) { }
                    void M2(infer x = default) { }
                    void M3(infer x = new()) { }
                    void M4(infer x = [1, 2]) { }
                    void M5(infer x = y => y) { }
                    void M6(infer x = System.Console.Write) { }
                    void M7(infer x = System.Console.WriteLine()) { }
                    void M8(infer x = (1, null)) { }
                    void M9(infer x = Missing) { }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (<null>) has no type. Write the parameter type.
                //     void M1(infer x = null) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "null").WithArguments("x", "<null>").WithLocation(3, 23),
                // (4,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (default) has no type. Write the parameter type.
                //     void M2(infer x = default) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "default").WithArguments("x", "default").WithLocation(4, 23),
                // (5,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (new()) has no type. Write the parameter type.
                //     void M3(infer x = new()) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "new()").WithArguments("x", "new()").WithLocation(5, 23),
                // (6,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (collection expression) has no type. Write the parameter type.
                //     void M4(infer x = [1, 2]) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "[1, 2]").WithArguments("x", "collection expression").WithLocation(6, 23),
                // (7,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (lambda expression) has no type. Write the parameter type.
                //     void M5(infer x = y => y) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "y => y").WithArguments("x", "lambda expression").WithLocation(7, 23),
                // (8,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (method group) has no type. Write the parameter type.
                //     void M6(infer x = System.Console.Write) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "System.Console.Write").WithArguments("x", "method group").WithLocation(8, 23),
                // (9,23): error CS20024: Cannot infer the type of parameter 'x', because its default value (void) has no type. Write the parameter type.
                //     void M7(infer x = System.Console.WriteLine()) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "System.Console.WriteLine()").WithArguments("x", "void").WithLocation(9, 23),
                // (10,23): error CS20024: Cannot infer the type of parameter 'x', because its default value ((int, <null>)) has no type. Write the parameter type.
                //     void M8(infer x = (1, null)) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoType, "(1, null)").WithArguments("x", "(int, <null>)").WithLocation(10, 23),
                // (11,23): error CS0103: The name 'Missing' does not exist in the current context
                //     void M9(infer x = Missing) { }
                Diagnostic(ErrorCode.ERR_NameNotInContext, "Missing").WithArguments("Missing").WithLocation(11, 23));
        }

        [Fact]
        public void InferParameter_NotConstant()
        {
            // A value that is not a constant still gives its type, then reports the error of the C# twin.
            var source = """
                using System;

                class C
                {
                    void Fetch(string url, infer timeout = TimeSpan.FromSeconds(5), infer retries = 3) { }
                    void FetchCSharp(string url, TimeSpan timeout = TimeSpan.FromSeconds(5), int retries = 3) { }
                    void Lambda(infer f = () => 1) { }
                    void LambdaCSharp(Func<int> f = () => 1) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (5,44): error CS1736: Default parameter value for 'timeout' must be a compile-time constant
                //     void Fetch(string url, infer timeout = TimeSpan.FromSeconds(5), infer retries = 3) { }
                Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "TimeSpan.FromSeconds(5)").WithArguments("timeout").WithLocation(5, 44),
                // (6,53): error CS1736: Default parameter value for 'timeout' must be a compile-time constant
                //     void FetchCSharp(string url, TimeSpan timeout = TimeSpan.FromSeconds(5), int retries = 3) { }
                Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "TimeSpan.FromSeconds(5)").WithArguments("timeout").WithLocation(6, 53),
                // (7,27): error CS1736: Default parameter value for 'f' must be a compile-time constant
                //     void Lambda(infer f = () => 1) { }
                Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "() => 1").WithArguments("f").WithLocation(7, 27),
                // (8,37): error CS1736: Default parameter value for 'f' must be a compile-time constant
                //     void LambdaCSharp(Func<int> f = () => 1) { }
                Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "() => 1").WithArguments("f").WithLocation(8, 37));
            Assert.Equal(comp.GetMember<MethodSymbol>("C.FetchCSharp").ToTestDisplayString().Replace("FetchCSharp", "Fetch"), comp.GetMember<MethodSymbol>("C.Fetch").ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32>", comp.GetMember<MethodSymbol>("C.Lambda").Parameters[0].Type.ToTestDisplayString());
        }

        [Fact]
        public void InferParameter_NoDefault()
        {
            // Without a default value there is nothing to infer from; ref and out parameters cannot have one.
            var source = """
                class C
                {
                    void M1(infer x) { }
                    void M2(ref infer x) { }
                    void M3(out infer x) { x = default; }
                    void M4(params infer x) { }
                    void M5(ref infer x = 1) { }
                    void M5CSharp(ref int x = 1) { }
                    void M6(params infer x = 1) { }
                    void M6CSharp(params int x = 1) { }
                    void M7(this infer x = 1) { }
                    void M7CSharp(this int x = 1) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (1,7): error CS1106: Extension method must be defined in a non-generic static class
                // class C
                Diagnostic(ErrorCode.ERR_BadExtensionAgg, "C").WithLocation(1, 7),
                // (1,7): error CS1106: Extension method must be defined in a non-generic static class
                // class C
                Diagnostic(ErrorCode.ERR_BadExtensionAgg, "C").WithLocation(1, 7),
                // (3,13): error CS20025: Cannot infer the type of parameter 'x', because it has no default value. Write the parameter type.
                //     void M1(infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x").WithLocation(3, 13),
                // (4,17): error CS20025: Cannot infer the type of parameter 'x', because it has no default value. Write the parameter type.
                //     void M2(ref infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x").WithLocation(4, 17),
                // (5,17): error CS20025: Cannot infer the type of parameter 'x', because it has no default value. Write the parameter type.
                //     void M3(out infer x) { x = default; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x").WithLocation(5, 17),
                // (6,13): error CS0225: The params parameter must have a valid collection type
                //     void M4(params infer x) { }
                Diagnostic(ErrorCode.ERR_ParamsMustBeCollection, "params").WithLocation(6, 13),
                // (6,20): error CS20025: Cannot infer the type of parameter 'x', because it has no default value. Write the parameter type.
                //     void M4(params infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x").WithLocation(6, 20),
                // (7,13): error CS1741: A ref or out parameter cannot have a default value
                //     void M5(ref infer x = 1) { }
                Diagnostic(ErrorCode.ERR_RefOutDefaultValue, "ref").WithLocation(7, 13),
                // (8,19): error CS1741: A ref or out parameter cannot have a default value
                //     void M5CSharp(ref int x = 1) { }
                Diagnostic(ErrorCode.ERR_RefOutDefaultValue, "ref").WithLocation(8, 19),
                // (9,13): error CS1751: Cannot specify a default value for a parameter collection
                //     void M6(params infer x = 1) { }
                Diagnostic(ErrorCode.ERR_DefaultValueForParamsParameter, "params").WithLocation(9, 13),
                // (9,13): error CS0225: The params parameter must have a valid collection type
                //     void M6(params infer x = 1) { }
                Diagnostic(ErrorCode.ERR_ParamsMustBeCollection, "params").WithLocation(9, 13),
                // (10,19): error CS1751: Cannot specify a default value for a parameter collection
                //     void M6CSharp(params int x = 1) { }
                Diagnostic(ErrorCode.ERR_DefaultValueForParamsParameter, "params").WithLocation(10, 19),
                // (10,19): error CS0225: The params parameter must have a valid collection type
                //     void M6CSharp(params int x = 1) { }
                Diagnostic(ErrorCode.ERR_ParamsMustBeCollection, "params").WithLocation(10, 19),
                // (11,13): error CS1743: Cannot specify a default value for the 'this' parameter
                //     void M7(this infer x = 1) { }
                Diagnostic(ErrorCode.ERR_DefaultValueForExtensionParameter, "this").WithLocation(11, 13),
                // (12,19): error CS1743: Cannot specify a default value for the 'this' parameter
                //     void M7CSharp(this int x = 1) { }
                Diagnostic(ErrorCode.ERR_DefaultValueForExtensionParameter, "this").WithLocation(12, 19));
            Assert.Equal("System.Int32", comp.GetMember<MethodSymbol>("C.M5").Parameters[0].Type.ToTestDisplayString());
        }

        [Fact]
        public void InferParameter_FeatureDisabled()
        {
            var source = """
                class C
                {
                    void M(infer x = 1) { }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,12): error CS0246: The type or namespace name 'infer' could not be found (are you missing a using directive or an assembly reference?)
                //     void M(infer x = 1) { }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "infer").WithArguments("infer").WithLocation(3, 12),
                // (3,18): error CS1750: A value of type 'int' cannot be used as a default parameter because there are no standard conversions to type 'infer'
                //     void M(infer x = 1) { }
                Diagnostic(ErrorCode.ERR_NoConversionForDefaultParam, "x").WithArguments("int", "infer").WithLocation(3, 18));
        }

        [Fact]
        public void InferParameter_TypeNamedInfer()
        {
            // A type named 'infer' in scope wins, as it does for 'var'.
            var source = """
                class infer { }

                class C
                {
                    void M(infer x = null) { }
                    void N(infer x = 1) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (1,7): warning CS8981: The type name 'infer' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class infer { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "infer").WithArguments("infer").WithLocation(1, 7),
                // (6,18): error CS1750: A value of type 'int' cannot be used as a default parameter because there are no standard conversions to type 'infer'
                //     void N(infer x = 1) { }
                Diagnostic(ErrorCode.ERR_NoConversionForDefaultParam, "x").WithArguments("int", "infer").WithLocation(6, 18));
            Assert.Equal("infer", comp.GetMember<MethodSymbol>("C.M").Parameters[0].Type.ToTestDisplayString());
        }

        [Fact]
        public void InferParameter_Cycle()
        {
            // A default value that depends on the signature of its own member cannot give that signature a type.
            var source = """
                class C
                {
                    static int Self(infer x = Self()) => 0;
                    static int A(infer x = B()) => 0;
                    static int B(infer x = A()) => 0;
                    static int Uses(infer x = A()) => 0;
                    static string Name(infer x = nameof(Name)) => x;
                    const string K = nameof(Constant);
                    static string Constant(infer x = K) => x;

                    static void Main()
                    {
                        int Local(infer x = Local()) => 0;
                        Local();
                    }
                }
                """;
            foreach (var first in new[] { "C.Self", "C.A", "C.B", "C.Uses", "C.Constant" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<MethodSymbol>(first).Parameters;
                comp.VerifyDiagnostics(
                    // (3,21): error CS20026: Cannot infer the type of parameter 'x', because its default value depends on the type of 'x' itself. Write the parameter type.
                    //     static int Self(infer x = Self()) => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferParameterCycle, "infer").WithArguments("x").WithLocation(3, 21),
                    // (4,18): error CS20026: Cannot infer the type of parameter 'x', because its default value depends on the type of 'x' itself. Write the parameter type.
                    //     static int A(infer x = B()) => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferParameterCycle, "infer").WithArguments("x").WithLocation(4, 18),
                    // (5,18): error CS20026: Cannot infer the type of parameter 'x', because its default value depends on the type of 'x' itself. Write the parameter type.
                    //     static int B(infer x = A()) => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferParameterCycle, "infer").WithArguments("x").WithLocation(5, 18),
                    // (6,31): error CS1736: Default parameter value for 'x' must be a compile-time constant
                    //     static int Uses(infer x = A()) => 0;
                    Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "A()").WithArguments("x").WithLocation(6, 31),
                    // (13,19): error CS20026: Cannot infer the type of parameter 'x', because its default value depends on the type of 'x' itself. Write the parameter type.
                    //         int Local(infer x = Local()) => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferParameterCycle, "infer").WithArguments("x").WithLocation(13, 19));
                Assert.Equal("System.Int32", comp.GetMember<MethodSymbol>("C.Uses").Parameters[0].Type.ToTestDisplayString());
                Assert.Equal("System.String", comp.GetMember<MethodSymbol>("C.Name").Parameters[0].Type.ToTestDisplayString());
                Assert.Equal("System.String", comp.GetMember<MethodSymbol>("C.Constant").Parameters[0].Type.ToTestDisplayString());
            }
        }

        [Fact]
        public void InferParameter_Cycle_Concurrent()
        {
            // Members in a cycle are inferred outside of their locks, so two threads that start from either end neither
            // deadlock nor disagree.
            var source = """
                class C
                {
                    static int A(infer x = B()) => 0;
                    static int B(infer x = A()) => 0;
                }
                """;
            for (int i = 0; i < 20; i++)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                var methods = new[] { comp.GetMember<MethodSymbol>("C.A"), comp.GetMember<MethodSymbol>("C.B") };
                System.Threading.Tasks.Parallel.For(0, 4, t => Assert.Equal("infer", methods[t % 2].Parameters[0].Type.ToTestDisplayString()));
                Assert.Equal(2, comp.GetDiagnostics().Count(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterCycle));
            }
        }

        [Fact]
        public void InferParameter_CallerInfo()
        {
            // Caller information attributes apply to the inferred type, as to a written one.
            var source = """
                using System;
                using System.Runtime.CompilerServices;

                class C
                {
                    static string M([CallerMemberName] infer member = "", [CallerLineNumber] infer line = 0, [CallerArgumentExpression(nameof(member))] infer expression = "") => $"{member}:{line}:{expression};";
                    static void Bad([CallerMemberName] infer member = 0) { }
                    static void BadCSharp([CallerMemberName] int member = 0) { }

                    static void Main()
                    {
                        Console.Write(M());
                        Console.Write(M("x" + "y"));
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,22): error CS4019: CallerMemberNameAttribute cannot be applied because there are no standard conversions from type 'string' to type 'int'
                //     static void Bad([CallerMemberName] infer member = 0) { }
                Diagnostic(ErrorCode.ERR_NoConversionForCallerMemberNameParam, "CallerMemberName").WithArguments("string", "int").WithLocation(7, 22),
                // (8,28): error CS4019: CallerMemberNameAttribute cannot be applied because there are no standard conversions from type 'string' to type 'int'
                //     static void BadCSharp([CallerMemberName] int member = 0) { }
                Diagnostic(ErrorCode.ERR_NoConversionForCallerMemberNameParam, "CallerMemberName").WithArguments("string", "int").WithLocation(8, 28));

            var verifier = CompileAndVerify(source.Replace("static void Bad", "// "), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Main:12:;xy:13:\"x\" + \"y\";" : null);
            verifier.VerifyDiagnostics();
        }

        [Fact]
        public void InferParameter_Signatures()
        {
            // Overrides, implementations, partial methods and overloads compare the inferred types as written ones.
            var source = """
                interface I { void M(int x = 1); }

                class B
                {
                    public virtual void V(int x = 1) { }
                }

                partial class C : B, I
                {
                    public void M(infer x = 1) { }
                    public override void V(infer x = 1) { }
                    partial void P(infer x = 1);
                    partial void P(infer x = 1) { }
                    void O(int x) { }
                    void O(infer x = 2) { }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,26): warning CS1066: The default value specified for parameter 'x' will have no effect because it applies to a member that is used in contexts that do not allow optional arguments
                //     partial void P(infer x = 1) { }
                Diagnostic(ErrorCode.WRN_DefaultValueForUnconsumedLocation, "x").WithArguments("x").WithLocation(13, 26),
                // (15,10): error CS0111: Type 'C' already defines a member called 'O' with the same parameter types
                //     void O(infer x = 2) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "O").WithArguments("O", "C").WithLocation(15, 10));
        }

        [Fact]
        public void InferParameter_Nullable()
        {
            // The annotation is that of the default value: a null constant gives an annotated type.
            var source = """
                class C
                {
                #nullable enable
                    static void M1(infer s = "a") { }
                    static void M2(infer s = default(string)) { }
                    static void M3(infer s = "a")
                    {
                        s = null;
                    }
                #nullable disable
                    static void M4(infer s = "a") { }

                #nullable enable
                    static void Main()
                    {
                        M1(null);
                        M2(null);
                        M4(null);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (8,13): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         s = null;
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "null").WithLocation(8, 13),
                // (16,12): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         M1(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(16, 12));
            Assert.Equal("System.String!", comp.GetMember<MethodSymbol>("C.M1").Parameters[0].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
            Assert.Equal("System.String?", comp.GetMember<MethodSymbol>("C.M2").Parameters[0].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
            Assert.Equal("System.String", comp.GetMember<MethodSymbol>("C.M4").Parameters[0].TypeWithAnnotations.ToTestDisplayString(includeNonNullable: true));
        }

        [Fact]
        public void InferParameter_SemanticModel()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(infer x = 1, infer s = default(string), int xCSharp = 1, string? sCSharp = default(string))
                    {
                        var f = (infer y = 2L, long yCSharp = 2L) => y;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var parameters = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().ToArray();
            verify(parameters[0], parameters[2]);
            verify(parameters[1], parameters[3]);
            verify(parameters[4], parameters[5]);

            void verify(ParameterSyntax parameter, ParameterSyntax twin)
            {
                var symbol = model.GetDeclaredSymbol(parameter);
                var twinSymbol = model.GetDeclaredSymbol(twin);
                Assert.Equal(twinSymbol.Type.ToTestDisplayString(includeNonNullable: true), symbol.Type.ToTestDisplayString(includeNonNullable: true));
                Assert.Equal(twinSymbol.NullableAnnotation, symbol.NullableAnnotation);
                Assert.Equal(twinSymbol.ExplicitDefaultValue, symbol.ExplicitDefaultValue);

                var typeInfo = model.GetTypeInfo(parameter.Type);
                Assert.True(model.GetTypeInfo(twin.Type).Type.Equals(typeInfo.Type, SymbolEqualityComparer.Default));
                Assert.Equal(symbol.NullableAnnotation, typeInfo.Nullability.Annotation);
                Assert.True(symbol.Type.Equals(model.GetSymbolInfo(parameter.Type).Symbol, SymbolEqualityComparer.Default));
            }
        }

        [Fact]
        public void InferParameter_IOperation()
        {
            // The default value binds against the inferred type, as it would against the written type.
            var source = """
                class C
                {
                    void M(infer x /*<bind>*/= 1 + 2/*</bind>*/) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IParameterInitializerOperation (Parameter: [System.Int32 x = 3]) (OperationKind.ParameterInitializer, Type: null) (Syntax: '= 1 + 2')
                  IBinaryOperation (BinaryOperatorKind.Add) (OperationKind.Binary, Type: System.Int32, Constant: 3) (Syntax: '1 + 2')
                    Left:
                      ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                    Right:
                      ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 2) (Syntax: '2')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<EqualsValueClauseSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void InferParameter_Metadata()
        {
            // The emitted parameters have the inferred types and the default values, as a C# compilation sees them.
            var source = """
                #nullable enable
                public class C
                {
                    public static string M(infer count = 3, infer name = "n", infer other = default(string)) => name + count + other;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            CompileAndVerify(comp, symbolValidator: module =>
            {
                Assert.Equal("System.String! C.M([System.Int32 count = 3], [System.String! name = \"n\"], [System.String? other = null])", module.GlobalNamespace.GetMember<MethodSymbol>("C.M").ToTestDisplayString(includeNonNullable: true));
            }).VerifyDiagnostics();

            var csharp = """
                class D
                {
                    static void Main() => System.Console.Write(C.M() + C.M(4, "x"));
                }
                """;
            CompileAndVerify(csharp, references: [comp.EmitToImageReference()], targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "n3x4" : null).VerifyDiagnostics();
        }

        [Fact]
        public void InferParameter_Concurrent()
        {
            // The types are inferred outside of the method locks; threads that race get the same types, and the errors are reported once.
            var builder = new System.Text.StringBuilder("class C\n{\n");
            for (int i = 0; i < 50; i++)
            {
                builder.AppendLine($"    static int M{i}(infer x = {i}, infer y = N{(i + 1) % 50}()) => 0;");
                builder.AppendLine($"    static int N{i}(infer x = null) => 0;");
            }

            builder.AppendLine("}");
            var comp = CreateCompilation(builder.ToString(), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var methods = comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToArray();
            System.Threading.Tasks.Parallel.For(0, 8, _ =>
            {
                foreach (var method in methods)
                {
                    Assert.Equal(method.Name.StartsWith("M") ? "System.Int32" : "infer", method.Parameters[0].Type.ToTestDisplayString());
                }
            });
            Assert.Equal(50, comp.GetDiagnostics().Count(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterNoType));
        }

        [Fact]
        public void ImplicitIt_Basic()
        {
            // Each Sharper line next to its C# twin, which writes the lambda out.
            var source = """
                using System;
                using System.Linq;

                class C
                {
                    static string Process(int x, string a) => a + x;
                    static T Retry<T>(Func<T> load, int times, Func<T, bool> when)
                    {
                        T result = load();
                        for (int i = 1; i < times && when(result); i++) result = load();
                        return result;
                    }

                    static void Main()
                    {
                        int[] xs = [1, 2, 3];
                        string a = "n";
                        Console.Write(string.Join(",", xs.Select(Process(it, a))));
                        Console.Write(string.Join(",", xs.Select(x => Process(x, a))));
                        Console.Write(";");
                        Console.Write(string.Join(",", xs.Where(it > 1)));
                        Console.Write(string.Join(",", xs.Where(x => x > 1)));
                        Console.Write(";");
                        int calls = 0;
                        Console.Write(Retry(() => ++calls, times: 3, when: it < 3));
                        Console.Write(Retry(() => ++calls, times: 3, when: r => r < 3));
                        Console.Write(";");
                        Console.Write(string.Join(",", xs.Select(it * 2).Where(it > 2)));
                        Console.Write(string.Join(",", xs.Select(x => x * 2).Where(x => x > 2)));
                        Console.Write(";");
                        Console.Write(string.Join(",", xs.Select(it % 2 == 0 ? "even" : "odd")));
                        Console.Write(string.Join(",", xs.Select(x => x % 2 == 0 ? "even" : "odd")));
                        Console.Write(";");
                        Console.Write(string.Join(",", xs.Select(y => xs.Count(it > y))));
                        Console.Write(string.Join(",", xs.Select(y => xs.Count(x => x > y))));
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "n1,n2,n3n1,n2,n3;2,32,3;34;4,64,6;odd,even,oddodd,even,odd;2,1,02,1,0" : null);
            verifier.VerifyDiagnostics();
        }

        [Fact]
        public void ImplicitIt_Errors()
        {
            var source = """
                using System.Collections.Generic;
                using System.Linq;

                class C
                {
                    class Order { public List<Line> Lines = new(); public int Total; }
                    class Line { public int Qty; }
                    static int F(int x) => x;

                    void M(int[] xs, List<Order> orders)
                    {
                        var f = it * 2;
                        F(it);
                        orders.Where(it.Lines.Any(it.Qty > 5));
                        it = 1;
                        xs.Select(it++);
                        xs.Select(int.TryParse(it.ToString(), out var v) ? v : 0);
                        var q = from x in xs where F(it) > 0 select x;
                        orders.Select(it.Lines.Select(it.Qty).Sum());
                        xs.Select(F(xs.Count(it > 1)));
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,63): warning CS0649: Field 'C.Order.Total' is never assigned to, and will always have its default value 0
                //     class Order { public List<Line> Lines = new(); public int Total; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Total").WithArguments("C.Order.Total", "0").WithLocation(6, 63),
                // (7,29): warning CS0649: Field 'C.Line.Qty' is never assigned to, and will always have its default value 0
                //     class Line { public int Qty; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Qty").WithArguments("C.Line.Qty", "0").WithLocation(7, 29),
                // (12,17): error CS20019: 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'.
                //         var f = it * 2;
                Diagnostic(ErrorCode.ERR_SharperItNoLambda, "it").WithLocation(12, 17),
                // (13,11): error CS20019: 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'.
                //         F(it);
                Diagnostic(ErrorCode.ERR_SharperItNoLambda, "it").WithLocation(13, 11),
                // (14,35): error CS20020: 'it' has two meanings here: the parameter of an outer lambda and of this inner one. Name one of them, as in 'x => ...'.
                //         orders.Where(it.Lines.Any(it.Qty > 5));
                Diagnostic(ErrorCode.ERR_SharperItTwoMeanings, "it").WithLocation(14, 35),
                // (15,9): error CS20019: 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'.
                //         it = 1;
                Diagnostic(ErrorCode.ERR_SharperItNoLambda, "it").WithLocation(15, 9),
                // (16,19): error CS1656: Cannot assign to 'it' because it is a 'val variable'
                //         xs.Select(it++);
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "it").WithArguments("it", "val variable").WithLocation(16, 19),
                // (17,32): error CS20019: 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'.
                //         xs.Select(int.TryParse(it.ToString(), out var v) ? v : 0);
                Diagnostic(ErrorCode.ERR_SharperItNoLambda, "it").WithLocation(17, 32),
                // (17,55): error CS20022: Cannot declare 'v' in an implicit 'it' lambda. Write the lambda, as in 'x => ...'.
                //         xs.Select(int.TryParse(it.ToString(), out var v) ? v : 0);
                Diagnostic(ErrorCode.ERR_SharperItDeclaration, "v").WithArguments("v").WithLocation(17, 55),
                // (18,38): error CS20019: 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'.
                //         var q = from x in xs where F(it) > 0 select x;
                Diagnostic(ErrorCode.ERR_SharperItNoLambda, "it").WithLocation(18, 38),
                // (19,39): error CS20020: 'it' has two meanings here: the parameter of an outer lambda and of this inner one. Name one of them, as in 'x => ...'.
                //         orders.Select(it.Lines.Select(it.Qty).Sum());
                Diagnostic(ErrorCode.ERR_SharperItTwoMeanings, "it").WithLocation(19, 39),
                // (20,12): error CS0411: The type arguments for method 'Enumerable.Select<TSource, TResult>(IEnumerable<TSource>, Func<TSource, int, TResult>)' cannot be inferred from the usage. Try specifying the type arguments explicitly.
                //         xs.Select(F(xs.Count(it > 1)));
                Diagnostic(ErrorCode.ERR_CantInferMethTypeArgs, "Select").WithArguments("System.Linq.Enumerable.Select<TSource, TResult>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, int, TResult>)").WithLocation(20, 12));
        }

        [Fact]
        public void ImplicitIt_CSharpMeaningWins()
        {
            // Wherever C# finds something named 'it', the C# meaning wins: the same code compiles the same with
            // Sharper on and off.
            var source = """
                using System;
                using System.Linq;

                #pragma warning disable CS8981 // lowercase type name
                class it { public static int Value = 5; }
                #pragma warning restore CS8981

                class C
                {
                    static int F(int x) => x * 10;
                    int[] xs = [1, 2, 3];

                    void Local() { int it = 2; Console.Write(string.Join(",", xs.Select(x => x + it))); }
                    void Parameter(int it) => Console.Write(string.Join(",", xs.Select(x => F(it))));
                    void Explicit() => Console.Write(string.Join(",", xs.Select(it => F(it))));
                    void Pattern(object o) => Console.Write(F(o is int it ? it : 0));
                    void Type() => Console.Write(F(it.Value));

                    static void Main()
                    {
                        var c = new C();
                        c.Local(); Console.Write(";");
                        c.Parameter(1); Console.Write(";");
                        c.Explicit(); Console.Write(";");
                        c.Pattern(3); Console.Write(";");
                        c.Type();
                    }
                }
                """;
            var expectedOutput = ExecutionConditionUtil.IsCoreClr ? "3,4,5;10,10,10;10,20,30;30;50" : null;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: expectedOutput).VerifyDiagnostics();
            CompileAndVerify(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: expectedOutput).VerifyDiagnostics();
        }

        [Fact]
        public void ImplicitIt_FeatureOff()
        {
            var source = """
                using System.Linq;

                class C
                {
                    object M(int[] xs) => xs.Select(it * 2);
                    object N(int[] xs) => xs.Select(@it * 2);
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,37): error CS0103: The name 'it' does not exist in the current context
                //     object M(int[] xs) => xs.Select(it * 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "it").WithArguments("it").WithLocation(5, 37),
                // (6,37): error CS0103: The name 'it' does not exist in the current context
                //     object N(int[] xs) => xs.Select(@it * 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "@it").WithArguments("it").WithLocation(6, 37));

            // '@it' is the escape for the C# meaning, so it is never implicit.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,37): error CS0103: The name 'it' does not exist in the current context
                //     object N(int[] xs) => xs.Select(@it * 2);
                Diagnostic(ErrorCode.ERR_NameNotInContext, "@it").WithArguments("it").WithLocation(6, 37));
        }

        [Fact]
        public void ImplicitIt_InnermostFunction()
        {
            // 'it' belongs to the innermost argument that takes a function: explicit lambdas, thunks and calls that
            // take values are skipped.
            var source = """
                using System;
                using System.Linq;

                class User { public int Id; public string Name; }

                class C
                {
                    static string Load(int id) => "p" + id;
                    static string Render(User user, Func<string> load) => user.Name + ":" + load();
                    static string Format(int x) => "<" + x + ">";

                    static void Main()
                    {
                        User[] users = [new User { Id = 1, Name = "a" }, new User { Id = 2, Name = "b" }];
                        int[] xs = [1, 2, 3];
                        Console.Write(string.Join(",", users.Select(Render(it, () => Load(it.Id)))));
                        Console.Write(string.Join(",", users.Select(u => Render(u, () => Load(u.Id)))));
                        Console.Write(";");
                        Console.Write(Format(xs.First(it > 1)));
                        Console.Write(Format(xs.First(x => x > 1)));
                        Console.Write(";");
                        Console.Write(string.Join(",", users.Select(Format(it.Name.Count(char.IsLetter)))));
                        Console.Write(string.Join(",", users.Select(u => Format(u.Name.Count(char.IsLetter)))));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "a:p1,b:p2a:p1,b:p2;<2><2>;<1>,<1><1>,<1>" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ImplicitIt_ValueOrFunction()
        {
            // With an outer 'it', a method that takes a value or a function at the argument is ambiguous.
            var source = """
                using System.Linq;

                class Default { public bool Preferred; }
                class Order { public Default Preferred; }

                class C
                {
                    object M(Order[] orders, Default[] defaults) => orders.Select(defaults.FirstOrDefault(it.Preferred));
                    object N(Default[] defaults) => defaults.FirstOrDefault(it.Preferred);
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,29): warning CS0649: Field 'Default.Preferred' is never assigned to, and will always have its default value false
                // class Default { public bool Preferred; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Preferred").WithArguments("Default.Preferred", "false").WithLocation(3, 29),
                // (4,30): warning CS0649: Field 'Order.Preferred' is never assigned to, and will always have its default value null
                // class Order { public Default Preferred; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Preferred").WithArguments("Order.Preferred", "null").WithLocation(4, 30),
                // (8,91): error CS20021: 'it' is ambiguous here: 'FirstOrDefault' takes a value or a function at this argument, and the outer 'it' fits both. Write the lambda, as in 'x => ...', or name the outer value.
                //     object M(Order[] orders, Default[] defaults) => orders.Select(defaults.FirstOrDefault(it.Preferred));
                Diagnostic(ErrorCode.ERR_SharperItValueOrFunction, "it").WithArguments("FirstOrDefault").WithLocation(8, 91));
        }

        [Fact]
        public void ImplicitIt_ExpressionTree()
        {
            var source = """
                using System;
                using System.Linq;
                using System.Linq.Expressions;

                class C
                {
                    static void Main()
                    {
                        var xs = new[] { 1, 2, 3 }.AsQueryable();
                        Console.Write(string.Join(",", xs.Where(it > 1)));
                        Console.Write(string.Join(",", xs.Where(x => x > 1)));
                        Expression<Func<int, bool>> e = x => x > 1;
                        Console.Write(xs.Where(it > 1).Expression.ToString().Contains("it > 1"));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "2,32,3True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ImplicitIt_Nullable()
        {
            var source = """
                #nullable enable
                using System.Linq;

                class C
                {
                    object M(string?[] xs) => xs.Select(it.Length);
                    object MCSharp(string?[] xs) => xs.Select(x => x.Length);
                    object N(string?[] xs) => xs.Where(it != null).Select(it!.Length);
                }
                """;
            // The same warning as the C# twin.
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,41): warning CS8602: Dereference of a possibly null reference.
                //     object M(string?[] xs) => xs.Select(it.Length);
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "it").WithLocation(6, 41),
                // (7,52): warning CS8602: Dereference of a possibly null reference.
                //     object MCSharp(string?[] xs) => xs.Select(x => x.Length);
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "x").WithLocation(7, 52));
        }

        [Fact]
        public void ImplicitIt_SemanticModel()
        {
            var source = """
                using System.Linq;

                class C
                {
                    object M(int[] xs) => xs.Where(it > 1);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var it = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.Text == "it");
            var symbol = (IParameterSymbol)model.GetSymbolInfo(it).Symbol;
            Assert.Equal("System.Int32 it", symbol.ToTestDisplayString());
            Assert.Equal(MethodKind.AnonymousFunction, ((IMethodSymbol)symbol.ContainingSymbol).MethodKind);
            Assert.Equal("System.Int32", model.GetTypeInfo(it).Type.ToTestDisplayString());
            // PROTOTYPE(sharper): the semantic model finds a lambda's scope by its syntax, so lookup inside an implicit
            // lambda does not see 'it' yet (#123).
            Assert.DoesNotContain(model.LookupSymbols(it.SpanStart), s => s.Name == "it");

            var argument = (BinaryExpressionSyntax)it.Parent;
            // The lambda shares its syntax with its body, the argument expression, like a query clause's lambda.
            var operation = model.GetOperation(argument);
            Assert.Equal(OperationKind.Binary, operation.Kind);
            while (operation is not Microsoft.CodeAnalysis.Operations.IAnonymousFunctionOperation)
            {
                operation = operation.Parent;
            }

            Assert.True(operation.IsImplicit);
            Assert.Same(symbol.ContainingSymbol, ((Microsoft.CodeAnalysis.Operations.IAnonymousFunctionOperation)operation).Symbol);
        }

        [Fact]
        public void ImplicitIt_AwaitAndPatternVariable()
        {
            var source = """
                using System.Linq;
                using System.Threading.Tasks;

                class C
                {
                    async Task<object> M(int[] xs) => xs.Select(await Task.FromResult(it));
                    object N(object[] xs) => xs.Select(it is int n ? n : 0);
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,49): error CS20023: Cannot use 'await' in an implicit 'it' lambda. Write the lambda, as in 'async x => ...'.
                //     async Task<object> M(int[] xs) => xs.Select(await Task.FromResult(it));
                Diagnostic(ErrorCode.ERR_SharperItAwait, "await Task.FromResult(it)").WithLocation(6, 49),
                // (7,40): error CS20019: 'it' has no lambda here. 'it' is the parameter of an argument that takes a function with one parameter, such as 'xs.Select(it.Name)'.
                //     object N(object[] xs) => xs.Select(it is int n ? n : 0);
                Diagnostic(ErrorCode.ERR_SharperItNoLambda, "it").WithLocation(7, 40),
                // (7,50): error CS20022: Cannot declare 'n' in an implicit 'it' lambda. Write the lambda, as in 'x => ...'.
                //     object N(object[] xs) => xs.Select(it is int n ? n : 0);
                Diagnostic(ErrorCode.ERR_SharperItDeclaration, "n").WithArguments("n").WithLocation(7, 50));
        }
    }
}
