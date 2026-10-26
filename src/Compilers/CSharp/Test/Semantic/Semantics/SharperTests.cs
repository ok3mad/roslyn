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
                // (6,1): warning CS9404: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(6, 1),
                // (8,5): warning CS9404: Call to non-readonly member 'S.Getter.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v.Getter;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Getter.get", "v").WithLocation(8, 5),
                // (10,5): warning CS9404: Call to non-readonly member 'S.this[int].get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[0];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.this[int].get", "v").WithLocation(10, 5),
                // (12,19): warning CS9404: Call to non-readonly member 'S.GetEnumerator()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // foreach (var x in v) { }
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.GetEnumerator()", "v").WithLocation(12, 19),
                // (14,14): warning CS9404: Call to non-readonly member 'S.Deconstruct(out int, out int)' on val variable 'v' runs on a copy, so 'v' is not changed.
                // var (a, b) = v;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Deconstruct(out int, out int)", "v").WithLocation(14, 14),
                // (16,1): warning CS9404: Call to non-readonly member 'S.E.add' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.E += () => { };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.E.add", "v").WithLocation(16, 1),
                // (21,20): warning CS9404: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // Action f = () => { v.Mutate(); Console.Write(v.F); };
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(21, 20),
                // (24,1): warning CS9404: Call to non-readonly member 'S.Mutate()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // v.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Mutate()", "v").WithLocation(24, 1),
                // (40,5): warning CS9404: Call to non-readonly member 'IMutable.Mutate()' on val variable 'g' runs on a copy, so 'g' is not changed.
                //     g.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "g").WithArguments("IMutable.Mutate()", "g").WithLocation(40, 5),
                // (41,19): warning CS9404: Call to non-readonly member 'IMutable.Get()' on val variable 'g' runs on a copy, so 'g' is not changed.
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
                // (6,7): warning CS9404: Call to non-readonly member 'S.GetAwaiter()' on val variable 'v' runs on a copy, so 'v' is not changed.
                // await v;
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.GetAwaiter()", "v").WithLocation(6, 7),
                // (8,5): warning CS9404: Call to non-readonly member 'S.Length.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[^1];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Length.get", "v").WithLocation(8, 5),
                // (8,5): warning CS9404: Call to non-readonly member 'S.this[int].get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[^1];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.this[int].get", "v").WithLocation(8, 5),
                // (10,5): warning CS9404: Call to non-readonly member 'S.Length.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[1..];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Length.get", "v").WithLocation(10, 5),
                // (10,5): warning CS9404: Call to non-readonly member 'S.Slice(int, int)' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v[1..];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Slice(int, int)", "v").WithLocation(10, 5),
                // (12,5): warning CS9404: Call to non-readonly member 'S.Length.get' on val variable 'v' runs on a copy, so 'v' is not changed.
                // _ = v is [_, ..];
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "v").WithArguments("S.Length.get", "v").WithLocation(12, 5),
                // (14,5): warning CS9404: Call to non-readonly member 'S.P.get' on val variable 'v' runs on a copy, so 'v' is not changed.
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
                // (12,9): error CS1656: Cannot assign to 'r' because it is a 'val variable'
                //         r = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r").WithArguments("r", "val variable").WithLocation(12, 9),
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
                // (11,9): error CS1656: Cannot assign to 'd' because it is a 'using variable'
                //         d = null;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "d").WithArguments("d", "using variable").WithLocation(11, 9),
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
                // (12,42): error CS1656: Cannot assign to 'x' because it is a 'foreach iteration variable'
                //         foreach (val x in new[] { 1 }) { x = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "foreach iteration variable").WithLocation(12, 42),
                // (13,43): error CS1656: Cannot assign to 'x2' because it is a 'foreach iteration variable'
                //         foreach (var x2 in new[] { 1 }) { x2 = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x2").WithArguments("x2", "foreach iteration variable").WithLocation(13, 43),
                // (14,41): error CS1656: Cannot assign to 'k' because it is a 'foreach iteration variable'
                //         foreach (val (k, v) in pairs) { k = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "k").WithArguments("k", "foreach iteration variable").WithLocation(14, 41),
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
                // A type named val keeps its C# meaning, with the same error var gets for `var (...)` when a type named var exists.
                comp.VerifyDiagnostics(sharper ? [
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7),
                // (6,13): error CS8136: Deconstruction 'var (...)' form disallows a specific type for 'var'.
                //         val (a, b) = (1, 2);
                Diagnostic(ErrorCode.ERR_DeconstructionVarFormDisallowsSpecificType, "(a, b)").WithLocation(6, 13),
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
                // (5,5): warning CS9404: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                //     s.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(5, 5),
                // (9,1): warning CS9404: Call to non-readonly member 'S.Mutate()' on val variable 't' runs on a copy, so 't' is not changed.
                // t.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "t").WithArguments("S.Mutate()", "t").WithLocation(9, 1),
                // (12,1): warning CS9404: Call to non-readonly member 'S.Mutate()' on val variable 'o' runs on a copy, so 'o' is not changed.
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
    }
}
