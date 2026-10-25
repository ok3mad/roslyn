// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
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
        public void YieldReturnSpread_AsyncIterator_NotSupported()
        {
            // Spreads are not supported in async iterators, whether the source is synchronous or asynchronous.
            var source = """
                using System.Collections.Generic;

                class C
                {
                    static async IAsyncEnumerable<int> SyncSource(IEnumerable<int> xs)
                    {
                        yield return ..xs;
                    }

                    static async IAsyncEnumerable<int> AsyncSource(IAsyncEnumerable<int> xs)
                    {
                        yield return ..xs;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyEmitDiagnostics(
                // (7,22): error CS9402: A spread 'yield return ..' cannot be used in an async iterator.
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_SharperSpreadInAsyncIterator, "..xs").WithLocation(7, 22),
                // (12,22): error CS9402: A spread 'yield return ..' cannot be used in an async iterator.
                //         yield return ..xs;
                Diagnostic(ErrorCode.ERR_SharperSpreadInAsyncIterator, "..xs").WithLocation(12, 22));
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
            // A span's enumerator cannot live across the yields.
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
                // (8,9): error CS4007: Instance of type 'System.Span<int>.Enumerator' cannot be preserved across 'await' or 'yield' boundary.
                //         yield return ..xs.AsSpan();
                Diagnostic(ErrorCode.ERR_ByRefTypeAndAwait, "yield return ..xs.AsSpan();").WithArguments("System.Span<int>.Enumerator").WithLocation(8, 9));
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
                // (7,24): warning CS9403: '..d' is a range here, not a spread, because 'd' is dynamic. To yield each element, convert 'd' to a sequence type first.
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
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
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
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
        }

        [Fact]
        public void Val_StructField()
        {
            var source = """
                struct S
                {
                    public int F;
                    public int P { get; set; }
                    public void Mutate() { F++; }
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
                        v.Mutate();
                        w.Mutate();
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
        }

        [Fact]
        public void Val_StructMutatingMethod()
        {
            // Probe: does a mutating method on a val struct mutate the local, as for a foreach variable?
            var source = """
                using System;

                val v = new S();
                v.Mutate();
                Console.Write(v.F);
                foreach (var w in new[] { new S() })
                {
                    w.Mutate();
                    Console.Write(w.F);
                }

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "" : null).VerifyDiagnostics();
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
            CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
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
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
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
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
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
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
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
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics();
        }
    }
}
