// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper: opaque patterns, slice 1 (#34): a relational or constant pattern whose operand is a pure expression that
    /// is not a constant.
    /// </summary>
    public sealed partial class SharperTests
    {
        private static CSharpCompilation CreateOpaqueCompilation(string source, CSharpParseOptions? parseOptions = null, CSharpCompilationOptions? options = null)
            => CreateCompilation(source, parseOptions: parseOptions ?? s_sharperOptions, targetFramework: TargetFramework.Net90, options: options ?? TestOptions.DebugExe);

        [Fact]
        public void Opaque_SwitchExpression()
        {
            var source = """
                using System;

                foreach (var amount in new[] { -5, 0, 1, 100, 101 })
                {
                    Console.WriteLine(Transfer.Check(amount, 100));
                }

                static class Transfer
                {
                    public static string Check(int amount, int max)
                    {
                        val limit = max;
                        return amount switch { > 0 and <= limit => "accept", _ => "reject" };
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "reject\nreject\naccept\naccept\nreject" : null);
        }

        [Fact]
        public void Opaque_CSharpTwin()
        {
            var source = """
                class C
                {
                    static string Check(int amount, int max)
                        => amount switch { > 0 and <= max => "accept", _ => "reject" };

                    static string Twin(int amount, int max)
                        => amount switch { > 0 and var v when v <= max => "accept", _ => "reject" };

                    static bool Is(int amount, int max) => amount is max;
                    static bool IsObject(object o, int max) => o is > max;
                }
                """;
            CreateOpaqueCompilation(source, parseOptions: TestOptions.Regular, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (4,39): error CS9135: A constant value of type 'int' is expected
                //         => amount switch { > 0 and <= max => "accept", _ => "reject" };
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "max").WithArguments("int").WithLocation(4, 39),
                // (9,54): error CS9135: A constant value of type 'int' is expected
                //     static bool Is(int amount, int max) => amount is max;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "max").WithArguments("int").WithLocation(9, 54),
                // (10,55): error CS0150: A constant value is expected
                //     static bool IsObject(object o, int max) => o is > max;
                Diagnostic(ErrorCode.ERR_ConstantExpected, "max").WithLocation(10, 55));

            // An input of type object needs a type pattern first, as it would for a constant of another type.
            CreateOpaqueCompilation(source, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (10,55): error CS0150: A constant value is expected
                //     static bool IsObject(object o, int max) => o is > max;
                Diagnostic(ErrorCode.ERR_ConstantExpected, "max").WithLocation(10, 55));
        }

        [Fact]
        public void Opaque_Contexts()
        {
            var source = """
                using System;

                int max = 3;
                foreach (var x in new[] { 1, 3, 5 })
                {
                    Console.Write(x is > max);
                    Console.Write(x is max);
                    Console.Write(x is not max);
                    Console.Write(x is < max or 5);
                    Console.Write(x is not (<= max));
                    Console.Write(new[] { x, 0 } is [>= max, ..]);
                    Console.Write("abc" is { Length: < max + 1 });
                    switch (x)
                    {
                        case < max: Console.Write("lt"); break;
                        case > max when x > 4: Console.Write("gt4"); break;
                        default: Console.Write("other"); break;
                    }
                    Console.WriteLine();
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                FalseFalseTrueTrueFalseFalseTruelt
                FalseTrueFalseFalseFalseTrueTrueother
                TrueFalseTrueTrueTrueTrueTruegt4
                """ : null);
        }

        [Fact]
        public void Opaque_Types()
        {
            var source = """
                using System;

                enum Color { Red, Green, Blue }

                static class P
                {
                    static void Main()
                    {
                        byte b = 7; byte bMax = 9;
                        long l = 10; long lMax = 10;
                        decimal m = 1.5m; decimal mMax = 2m;
                        char c = 'q'; char cMin = 'a';
                        nint n = 4; nint nMax = 3;
                        double d = 0.5; double dMax = 1.0;
                        uint u = 3; uint uMin = 2;
                        Color color = Color.Green; Color first = Color.Red;
                        string s = "abc"; string other = "ab" + "c".ToString();
                        bool t = true; bool same = true;
                        int? maybe = null; int? some = 4; int four = 4;
                        Console.WriteLine($"{b is < bMax} {l is <= lMax} {m is < mMax} {c is >= cMin} {n is > nMax} {d is < dMax} {u is > uMin}");
                        Console.WriteLine($"{color is > first} {color is first} {s is other} {t is same} {maybe is four} {some is four} {some is > four}");
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                True True True True True True True
                True False True True False True False
                """ : null);
        }

        [Fact]
        public void Opaque_NotOpaque()
        {
            var source = """
                class C
                {
                    void M(double d, double dMax, string s, string t, bool b, bool c, object o, int max, System.ReadOnlySpan<char> span)
                    {
                        _ = d is dMax;
                        _ = s is > t;
                        _ = b is > c;
                        _ = o is max;
                        _ = span is s;
                    }
                }
                """;
            // Equality of floating-point values (whose constant patterns match NaN), relational patterns on string and bool,
            // object inputs and span inputs keep the C# errors.
            CreateOpaqueCompilation(source, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (5,18): error CS9135: A constant value of type 'double' is expected
                //         _ = d is dMax;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "dMax").WithArguments("double").WithLocation(5, 18),
                // (6,20): error CS9135: A constant value of type 'string' is expected
                //         _ = s is > t;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "t").WithArguments("string").WithLocation(6, 20),
                // (7,20): error CS9135: A constant value of type 'bool' is expected
                //         _ = b is > c;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "c").WithArguments("bool").WithLocation(7, 20),
                // (8,18): error CS0150: A constant value is expected
                //         _ = o is max;
                Diagnostic(ErrorCode.ERR_ConstantExpected, "max").WithLocation(8, 18),
                // (9,21): error CS9135: A constant value of type 'ReadOnlySpan<char>' is expected
                //         _ = span is s;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "s").WithArguments("System.ReadOnlySpan<char>").WithLocation(9, 21));
        }

        [Fact]
        public void Opaque_Exhaustiveness()
        {
            var source = """
                class C
                {
                    static int M1(int x, int max) => x switch { <= max => 1, > max => 2 };
                    static int M2(int x, int max) => x switch { <= max => 1, not <= max => 2 };
                    static int M3(int x, int max) => x switch { <= max => 1, <= max => 2, _ => 3 };
                    static int M4(int x, int max) => x switch { > max => 1, > 5 => 2, _ => 3 };
                    static bool M5(int x, int max) => x is <= max or not <= max;
                    static int M6(int x, int max) => x switch { > 0 and <= max => 1, <= 0 => 2 };
                }
                """;
            // An opaque test narrows nothing, so only the same test on the same input (M2, M3, M5) is understood.
            CreateOpaqueCompilation(source, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (3,40): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
                //     static int M1(int x, int max) => x switch { <= max => 1, > max => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("_").WithLocation(3, 40),
                // (5,62): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //     static int M3(int x, int max) => x switch { <= max => 1, <= max => 2, _ => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "<= max").WithLocation(5, 62),
                // (7,39): warning CS8794: An expression of type 'int' always matches the provided pattern.
                //     static bool M5(int x, int max) => x is <= max or not <= max;
                Diagnostic(ErrorCode.WRN_IsPatternAlways, "x is <= max or not <= max").WithArguments("int").WithLocation(7, 39),
                // (8,40): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
                //     static int M6(int x, int max) => x switch { > 0 and <= max => 1, <= 0 => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("_").WithLocation(8, 40));
        }

        [Fact]
        public void Opaque_Purity()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                [Immutable] sealed record Limits(int Max);

                class C
                {
                    static int s_mutable = 3;
                    static readonly Limits s_limits = new Limits(3);
                    static readonly List<int> s_list = new();
                    int _field = 3;
                    int Counter { get; set; }

                    [Pure] static int Twice(int x) => 2 * x;
                    static int Impure(int x) => x;

                    void M(int x, Limits limits, int[] array)
                    {
                        int n = 3;
                        _ = x is > 1 and < s_limits.Max;
                        _ = x is < _field;
                        _ = x is < limits.Max;
                        _ = x is < Twice(n);
                        _ = x is < Math.Max(n, 2);
                        _ = x is < array[0];
                        _ = x is < array.Length;
                        _ = x is < Impure(n);
                        _ = x is < s_mutable;
                        _ = x is < s_list.Count;
                        _ = x is < n++;
                        _ = x is < (n = 4);
                        _ = x is < Counter;
                        _ = x is < new Random().Next();
                    }
                }
                """;
            // The rules of a [Pure] body, with no locals of its own: reads through anything and auto-property getters are
            // fine; writes, mutable statics and unmarked calls are not.
            CreateOpaqueCompilation(source, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (27,20): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 'Impure(n)' calls 'C.Impure(int)', which is not known to be pure.
                //         _ = x is < Impure(n);
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "Impure(n)").WithArguments("'Impure(n)' calls 'C.Impure(int)', which is not known to be pure").WithLocation(27, 20),
                // (28,20): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 's_mutable' reads static state that can change.
                //         _ = x is < s_mutable;
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "s_mutable").WithArguments("'s_mutable' reads static state that can change").WithLocation(28, 20),
                // (29,20): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 's_list.Count' calls 'System.Collections.Generic.List<int>.Count', which is not known to be pure.
                //         _ = x is < s_list.Count;
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "s_list.Count").WithArguments("'s_list.Count' calls 'System.Collections.Generic.List<int>.Count', which is not known to be pure").WithLocation(29, 20),
                // (30,20): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 'n++' writes state.
                //         _ = x is < n++;
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "n++").WithArguments("'n++' writes state").WithLocation(30, 20),
                // (31,21): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 'n = 4' writes state.
                //         _ = x is < (n = 4);
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "n = 4").WithArguments("'n = 4' writes state").WithLocation(31, 21),
                // (33,20): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 'new Random().Next()' calls 'System.Random.Next()', which is not known to be pure.
                //         _ = x is < new Random().Next();
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "new Random().Next()").WithArguments("'new Random().Next()' calls 'System.Random.Next()', which is not known to be pure").WithLocation(33, 20));
        }

        [Fact]
        public void Opaque_InPureMember()
        {
            var source = """
                using System;

                Console.WriteLine($"{Range.In(5, 1, 9)} {Range.In(0, 1, 9)}");

                static class Range
                {
                    [Pure] public static bool In(int x, int min, int max) => x is >= min and <= max;
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "True False" : null);
        }

        [Fact]
        public void Opaque_FlowAnalysis()
        {
            var source = """
                #nullable enable
                using System;

                class C
                {
                    static void M(int x, string? s, string t)
                    {
                        int max;
                        _ = x is > max;
                        Func<int, bool> f = y => y is > x;
                        _ = s is t;
                        _ = s.Length;
                        if (s is t)
                        {
                            _ = s.Length;
                        }
                    }
                }
                """;
            // An opaque test never matches null, so 's' is not null where 's is t' holds.
            CreateOpaqueCompilation(source, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (9,20): error CS0165: Use of unassigned local variable 'max'
                //         _ = x is > max;
                Diagnostic(ErrorCode.ERR_UseDefViolation, "max").WithArguments("max").WithLocation(9, 20),
                // (12,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = s.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(12, 13));
        }

        [Fact]
        public void Opaque_Lambda()
        {
            var source = """
                using System;
                using System.Linq;

                int max = 3;
                Func<int, bool> small = x => x is <= max;
                max = 4;
                Console.WriteLine(string.Join(",", new[] { 1, 4, 5 }.Where(small)));
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1,4" : null);
        }

        [Fact]
        public void Opaque_ExpressionTreeAndAwait()
        {
            var source = """
                using System;
                using System.Linq.Expressions;
                using System.Threading.Tasks;

                class C
                {
                    static async Task M(int x, int max)
                    {
                        Expression<Func<int, bool>> e = y => y is > max;
                        _ = x is > await Task.FromResult(max);
                    }
                }
                """;
            CreateOpaqueCompilation(source, options: TestOptions.DebugDll).VerifyDiagnostics(
                // (9,46): error CS8122: An expression tree may not contain an 'is' pattern-matching operator.
                //         Expression<Func<int, bool>> e = y => y is > max;
                Diagnostic(ErrorCode.ERR_ExpressionTreeContainsIsMatch, "y is > max").WithLocation(9, 46),
                // (10,20): error CS20058: A pattern operand that is not a constant must be pure, because the order of pattern tests is not specified: 'await Task.FromResult(max)' is not allowed in a pattern.
                //         _ = x is > await Task.FromResult(max);
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "await Task.FromResult(max)").WithArguments("'await Task.FromResult(max)' is not allowed in a pattern").WithLocation(10, 20));
        }

        [Fact]
        public void Opaque_IOperation()
        {
            var source = """
                class C
                {
                    static bool M(int x, int max) => /*<bind>*/x is > 0 and <= max/*</bind>*/;
                }
                """;
            var comp = CreateOpaqueCompilation(source, options: TestOptions.DebugDll);
            var expectedOperationTree = """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'x is > 0 and <= max')
                  Value:
                    IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                  Pattern:
                    IBinaryPatternOperation (BinaryOperatorKind.And) (OperationKind.BinaryPattern, Type: null) (Syntax: '> 0 and <= max') (InputType: System.Int32, NarrowedType: System.Int32)
                      LeftPattern:
                        IRelationalPatternOperation (BinaryOperatorKind.GreaterThan) (OperationKind.RelationalPattern, Type: null) (Syntax: '> 0') (InputType: System.Int32, NarrowedType: System.Int32)
                          Value:
                            ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 0) (Syntax: '0')
                      RightPattern:
                        IRelationalPatternOperation (BinaryOperatorKind.LessThanOrEqual) (OperationKind.RelationalPattern, Type: null) (Syntax: '<= max') (InputType: System.Int32, NarrowedType: System.Int32)
                          Value:
                            IParameterReferenceOperation: max (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'max')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<Syntax.IsPatternExpressionSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void Opaque_IOperation_Constant()
        {
            var source = """
                class C
                {
                    static bool M(int x, int max) => /*<bind>*/x is max/*</bind>*/;
                }
                """;
            var comp = CreateOpaqueCompilation(source, options: TestOptions.DebugDll);
            var expectedOperationTree = """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'x is max')
                  Value:
                    IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                  Pattern:
                    IConstantPatternOperation (OperationKind.ConstantPattern, Type: null, IsImplicit) (Syntax: 'max') (InputType: System.Int32, NarrowedType: System.Int32)
                      Value:
                        IParameterReferenceOperation: max (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'max')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<Syntax.BinaryExpressionSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void Opaque_CaseLabel()
        {
            var source = """
                using System;

                foreach (var x in new[] { 1, 2, 3 })
                {
                    Console.Write(Label(x, 2));
                }

                static string Label(int x, int two)
                {
                    switch (x)
                    {
                        case 1: return "one ";
                        case two: return "two ";
                        case two: return "again ";
                        default: return "other ";
                    }
                }
                """;
            CreateOpaqueCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (13,14): error CS9135: A constant value of type 'int' is expected
                //         case two: return "two ";
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "two").WithArguments("int").WithLocation(13, 14),
                // (14,14): error CS9135: A constant value of type 'int' is expected
                //         case two: return "again ";
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "two").WithArguments("int").WithLocation(14, 14));
            // The same opaque test is shared, so the second 'case two:' cannot match.
            CreateOpaqueCompilation(source).VerifyDiagnostics(
                // (14,14): error CS8120: The switch case is unreachable. It has already been handled by a previous case or it is impossible to match.
                //         case two: return "again ";
                Diagnostic(ErrorCode.ERR_SwitchCaseSubsumed, "two").WithLocation(14, 14));

            var comp = CreateOpaqueCompilation(source.Replace("case two: return \"again \";", ""));
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "one two other " : null);
        }

        [Fact]
        public void Opaque_NestedOperand()
        {
            var source = """
                using System;

                foreach (var (x, y) in new[] { (1, 1), (2, 5), (3, 2) })
                {
                    Console.Write(x is < (y switch { 1 => 2, _ => 3 }));
                    Console.Write(x is (y is > 4 ? 2 : 3));
                    Console.Write(y switch { < (x is 1 ? 2 : 0) => "a", > x => "b", _ => "c" });
                    Console.WriteLine();
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                TrueFalsea
                TrueTrueb
                FalseTruec
                """ : null);
        }
    }
}
