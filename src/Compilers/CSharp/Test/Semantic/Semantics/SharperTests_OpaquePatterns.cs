// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper: opaque patterns, slice 1 of patterns on pure functions (implementation plan Feature 13, #34): a relational
    /// or constant pattern with a pure operand that is not a constant. Each test sits next to its C# twin, a <c>when</c> guard.
    /// </summary>
    public sealed partial class SharperTests
    {
        private static CSharpCompilation CreateOpaqueCompilation(string source, CSharpParseOptions? parseOptions = null)
            => CreateCompilation(source, parseOptions: parseOptions ?? s_sharperOptions, options: TestOptions.ReleaseExe, targetFramework: TargetFramework.Net90);

        [Fact]
        public void Opaque_SwitchExpression()
        {
            var source = """
                using System;

                int max = 10;
                foreach (var amount in new[] { -1, 0, 1, 10, 11 })
                {
                    Console.Write(amount switch { > 0 and <= max => "A", _ => "R" });
                    Console.Write(amount switch { var a when a > 0 && a <= max => "A", _ => "R" });
                    Console.Write(" ");
                }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "RR RR AA AA RR " : null);
        }

        [Fact]
        public void Opaque_FlagOff()
        {
            var source = """
                class C
                {
                    static string M(int amount, int max) => amount switch { > 0 and <= max => "A", _ => "R" };
                    static bool N(int x, int max) => x is max;
                    static bool O(object o, int max) => o is < max;
                }
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (3,72): error CS9135: A constant value of type 'int' is expected
                //     static string M(int amount, int max) => amount switch { > 0 and <= max => "A", _ => "R" };
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "max").WithArguments("int").WithLocation(3, 72),
                // (4,43): error CS9135: A constant value of type 'int' is expected
                //     static bool N(int x, int max) => x is max;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "max").WithArguments("int").WithLocation(4, 43),
                // (5,48): error CS0150: A constant value is expected
                //     static bool O(object o, int max) => o is < max;
                Diagnostic(ErrorCode.ERR_ConstantExpected, "max").WithLocation(5, 48));
        }

        [Fact]
        public void Opaque_AndOrNot()
        {
            var source = """
                using System;

                int lo = 2, hi = 5;
                for (int i = 0; i <= 7; i++)
                {
                    Console.Write(i is >= lo and < hi ? "a" : "-");
                    Console.Write(i is < lo or hi ? "o" : "-");
                    Console.Write(i is not (< lo or > hi) ? "n" : "-");
                    Console.Write(i is not lo ? "x" : "-");
                    Console.Write(" ");
                }
                Console.WriteLine();
                for (int i = 0; i <= 7; i++)
                {
                    Console.Write(i >= lo && i < hi ? "a" : "-");
                    Console.Write(i < lo || i == hi ? "o" : "-");
                    Console.Write(!(i < lo || i > hi) ? "n" : "-");
                    Console.Write(i != lo ? "x" : "-");
                    Console.Write(" ");
                }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            var line = "-o-x -o-x a-n- a-nx a-nx -onx ---x ---x ";
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? line + "\n" + line : null);
        }

        [Fact]
        public void Opaque_Nested()
        {
            var source = """
                using System;

                record Order(string Name, int Amount, int[] Lines);

                static class Program
                {
                    static string Classify(Order o, int max, int first) => o switch
                    {
                        { Amount: > max } => "big",
                        { Lines: [first, ..] } => "first",
                        { Lines: [_, > max, ..] } => "second",
                        { Amount: var a } when a == max => "max",
                        _ => "other",
                    };

                    static string Twin(Order o, int max, int first) => o switch
                    {
                        { Amount: var a } when a > max => "big",
                        { Lines: [var l, ..] } when l == first => "first",
                        { Lines: [_, var l, ..] } when l > max => "second",
                        { Amount: var a } when a == max => "max",
                        _ => "other",
                    };

                    static void Main()
                    {
                        var orders = new[]
                        {
                            new Order("a", 20, []),
                            new Order("b", 5, [3, 1]),
                            new Order("c", 5, [1, 30]),
                            new Order("d", 10, [1, 2]),
                            new Order("e", 1, []),
                        };
                        foreach (var o in orders) Console.Write(Classify(o, 10, 3) + " ");
                        Console.WriteLine();
                        foreach (var o in orders) Console.Write(Twin(o, 10, 3) + " ");
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            var line = "big first second max other ";
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? line + "\n" + line : null);
        }

        [Fact]
        public void Opaque_SwitchStatementAndTypes()
        {
            var source = """
                using System;

                enum Color { Red, Green, Blue }

                static class Program
                {
                    static string M(object o, int i, string s, Color c, byte b, long l, double d, decimal m)
                    {
                        switch (o)
                        {
                            case < i: return "lt";
                            case s: return "s";
                            case c: return "c";
                            case int: return "int";
                            default: return "other";
                        }
                    }

                    static string N(int? n, int i) => n switch { null => "null", > i => "gt", _ => "le" };
                    static bool B(byte x, byte b) => x is > b;
                    static bool L(long x, long l, int i) => x is >= l and <= i;
                    static bool D(double x, double d) => x is < d;
                    static bool Dm(decimal x, decimal m) => x is > m;
                    static bool C(Color x, Color c) => x is >= c;
                    static bool S(string x, string s) => x is s;
                    static bool T<T>(T x, int i) => x is > i;

                    static void Main()
                    {
                        Console.WriteLine($"{M(1, 2, "x", Color.Blue, 0, 0, 0, 0)} {M("x", 2, "x", Color.Blue, 0, 0, 0, 0)} {M(Color.Blue, 2, "x", Color.Blue, 0, 0, 0, 0)} {M(5, 2, "x", Color.Blue, 0, 0, 0, 0)} {M(1.0, 2, "x", Color.Blue, 0, 0, 0, 0)}");
                        Console.WriteLine($"{N(null, 1)} {N(2, 1)} {N(1, 1)}");
                        Console.WriteLine($"{B(3, 2)} {L(5, 5, 6)} {L(7, 5, 6)} {D(1, double.NaN)} {Dm(2m, 1m)} {C(Color.Blue, Color.Green)} {S("a", "a")} {S("a", null)} {T(3, 2)} {T("3", 2)}");
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                lt s c int other
                null gt le
                True True False False True True True False True False
                """ : null);
        }

        [Fact]
        public void Opaque_Exhaustiveness()
        {
            var source = """
                class C
                {
                    static int M(int x, int max) => x switch { > max => 1, <= max => 2 };
                    static int Twin(int x, int max) => x switch { var a when a > max => 1, var a when a <= max => 2 };
                    static int N(int x, int max) => x switch { > max => 1, not > max => 2 };
                    static int O(int x, int max) => x switch { > max => 1, _ => 2 };
                }
                """;
            // An arm with an opaque test helps exhaustiveness no more than a guarded arm does, but the same test on the same
            // value has the same outcome, so `not > max` covers what `> max` does not.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (3,39): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
                //     static int M(int x, int max) => x switch { > max => 1, <= max => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("_").WithLocation(3, 39),
                // (4,42): warning CS8846: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered. However, a pattern with a 'when' clause might successfully match this value.
                //     static int Twin(int x, int max) => x switch { var a when a > max => 1, var a when a <= max => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustiveWithWhen, "switch").WithArguments("_").WithLocation(4, 42));
        }

        [Fact]
        public void Opaque_Subsumption()
        {
            var source = """
                class C
                {
                    static int M(int x, int max) => x switch { > max => 1, > 5 => 2, _ => 3 };
                    static int Twin(int x, int max) => x switch { var a when a > max => 1, > 5 => 2, _ => 3 };
                    static int N(int x, int max) => x switch { > 5 => 1, > max => 2, _ => 3 };
                    static int Same(int x, int max) => x switch { > max => 1, > max => 2, _ => 3 };
                    static int SameTwin(int x, int max) => x switch { var a when a > max => 1, var a when a > max => 2, _ => 3 };
                }
                """;
            // An opaque test subsumes nothing on its own; only the same test again is decided.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (6,63): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //     static int Same(int x, int max) => x switch { > max => 1, > max => 2, _ => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "> max").WithLocation(6, 63));
        }

        [Fact]
        public void Opaque_SharedTest()
        {
            // The same comparison on the same value is evaluated once; a when guard is evaluated for each arm.
            var source = """
                using System;

                static class Program
                {
                    static int calls;
                    [AssumePure] static int Limit() { calls++; return 0; }

                    static int M((int, int) t) => t switch
                    {
                        (> Limit(), 1) => 1,
                        (> Limit(), 2) => 2,
                        _ => 3,
                    };

                    static int Twin((int, int) t) => t switch
                    {
                        (var a, var b) when a > Limit() && b == 1 => 1,
                        (var a, var b) when a > Limit() && b == 2 => 2,
                        _ => 3,
                    };

                    static void Main()
                    {
                        Console.WriteLine($"{M((1, 2))} {calls}");
                        calls = 0;
                        Console.WriteLine($"{Twin((1, 2))} {calls}");
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                2 1
                2 2
                """ : null);
        }

        [Fact]
        public void Opaque_Impure()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static int s_max;
                    static readonly List<int> s_list = new();
                    static readonly int s_limit = 3;
                    const int Const = 3;
                    int _field;
                    int Next() => ++_field;
                    [Pure] static int Twice(int x) => 2 * x;
                    [Pure] int Field() => _field;

                    bool M1(int x) => x is > Next();
                    bool M2(int x) => x is > s_max;
                    bool M3(int x) => x is > s_list.Count;
                    bool M4(int x) => x is > s_limit and < Const;
                    bool M5(int x) => x is > _field and < Field();
                    bool M6(int x) => x is > Twice(_field);
                    bool M7(int x) => x is > Math.Max(_field, 3);
                    bool M8(int x, int[] a) => x is > a[0];
                    bool M9(int x) => x is > DateTime.Now.Second;
                }
                """;
            // The rules of a [Pure] body: mutable statics and calls of members not known to be pure are rejected; instance
            // fields, static readonly fields of immutable types, [Pure] and attested calls are read.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (6,16): warning CS0649: Field 'C.s_max' is never assigned to, and will always have its default value 0
                //     static int s_max;
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "s_max").WithArguments("C.s_max", "0").WithLocation(6, 16),
                // (15,30): error CS20062: The operand of a pattern that is not a constant must be pure, but it is not: 'Next()' calls 'C.Next()', which is not known to be pure.
                //     bool M1(int x) => x is > Next();
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "Next()").WithArguments("'Next()' calls 'C.Next()', which is not known to be pure").WithLocation(15, 30),
                // (16,30): error CS20062: The operand of a pattern that is not a constant must be pure, but it is not: 's_max' reads static state that can change.
                //     bool M2(int x) => x is > s_max;
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "s_max").WithArguments("'s_max' reads static state that can change").WithLocation(16, 30),
                // (17,30): error CS20062: The operand of a pattern that is not a constant must be pure, but it is not: 's_list.Count' calls 'System.Collections.Generic.List<int>.Count', which is not known to be pure.
                //     bool M3(int x) => x is > s_list.Count;
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "s_list.Count").WithArguments("'s_list.Count' calls 'System.Collections.Generic.List<int>.Count', which is not known to be pure").WithLocation(17, 30),
                // (23,30): error CS20062: The operand of a pattern that is not a constant must be pure, but it is not: 'DateTime.Now' calls 'System.DateTime.Now', which is not known to be pure.
                //     bool M9(int x) => x is > DateTime.Now.Second;
                Diagnostic(ErrorCode.ERR_SharperOpaquePatternNotPure, "DateTime.Now").WithArguments("'DateTime.Now' calls 'System.DateTime.Now', which is not known to be pure").WithLocation(23, 30));
        }

        [Fact]
        public void Opaque_PureCall()
        {
            var source = """
                using System;

                static class Program
                {
                    [Pure] static int Limit(int scale) => scale * 10;
                    [Pure] static bool InRange(int x, int lo, int hi) => x is >= lo and < hi;
                    static readonly int s_max = 15;
                    static readonly bool s_initialized = 20 is > s_max;

                    static void Main()
                    {
                        Console.WriteLine($"{25 is > Limit(2)} {15 is > Limit(2)} {InRange(3, 1, 4)} {InRange(4, 1, 4)} {s_initialized}");
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "True False True False True" : null);
        }

        [Fact]
        public void Opaque_CapturedAndAsync()
        {
            var source = """
                using System;
                using System.Linq;
                using System.Threading.Tasks;

                static class Program
                {
                    static async Task<string> M(int[] xs, int max)
                    {
                        await Task.Yield();
                        var big = xs.Where(x => x is > max).ToArray();
                        await Task.Yield();
                        return string.Join(",", big) + (xs is [.., > max] ? " last" : "");
                    }

                    static void Main()
                    {
                        Console.WriteLine(M([1, 5, 9, 12], 4).Result);
                    }
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "5,9,12 last" : null);
        }

        [Fact]
        public void Opaque_Unsupported()
        {
            // Operands that C# could not compare as a constant either keep C#'s error.
            var source = """
                using System;

                class C
                {
                    static bool M1(int x, long l) => x is > l;
                    static bool M2(object o, object p) => o is p;
                    static bool M3(string s, string t) => s is > t;
                    static bool M4(ReadOnlySpan<char> s, string t) => s is t;
                    static bool M5(bool b, bool c) => b is < c;
                    static bool M6(C c, C d) => c is d;
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (5,45): error CS0266: Cannot implicitly convert type 'long' to 'int'. An explicit conversion exists (are you missing a cast?)
                //     static bool M1(int x, long l) => x is > l;
                Diagnostic(ErrorCode.ERR_NoImplicitConvCast, "l").WithArguments("long", "int").WithLocation(5, 45),
                // (6,48): error CS0150: A constant value is expected
                //     static bool M2(object o, object p) => o is p;
                Diagnostic(ErrorCode.ERR_ConstantExpected, "p").WithLocation(6, 48),
                // (7,50): error CS9135: A constant value of type 'string' is expected
                //     static bool M3(string s, string t) => s is > t;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "t").WithArguments("string").WithLocation(7, 50),
                // (8,60): error CS9135: A constant value of type 'ReadOnlySpan<char>' is expected
                //     static bool M4(ReadOnlySpan<char> s, string t) => s is t;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "t").WithArguments("System.ReadOnlySpan<char>").WithLocation(8, 60),
                // (9,46): error CS9135: A constant value of type 'bool' is expected
                //     static bool M5(bool b, bool c) => b is < c;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "c").WithArguments("bool").WithLocation(9, 46),
                // (10,38): error CS9135: A constant value of type 'C' is expected
                //     static bool M6(C c, C d) => c is d;
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "d").WithArguments("C").WithLocation(10, 38));
        }

        [Fact]
        public void Opaque_RefinementRejects()
        {
            // A refinement is a declared domain, which must stay closed over constants (Feature 11): an operand that would
            // make an opaque pattern elsewhere is still CS20058 there, pure or not.
            var source = """
                class C
                {
                    static readonly int s_max = 10;
                    static int s_mutable;
                    [Pure] static int Limit() => 10;
                    static int Next() => ++s_mutable;
                    void M1(int i is > s_max) { }
                    void M2(int i is >= 0 and < Limit()) { }
                    void M3(int i is not s_max) { }
                    void M4(int i is > Next()) { }
                    bool N(int i) => i is > s_max and < Limit();
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (7,22): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '> s_max' is not.
                //     void M1(int i is > s_max) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "> s_max").WithArguments("> s_max").WithLocation(7, 22),
                // (8,31): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '< Limit()' is not.
                //     void M2(int i is >= 0 and < Limit()) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "< Limit()").WithArguments("< Limit()").WithLocation(8, 31),
                // (9,26): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 's_max' is not.
                //     void M3(int i is not s_max) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "s_max").WithArguments("s_max").WithLocation(9, 26),
                // (10,22): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '> Next()' is not.
                //     void M4(int i is > Next()) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "> Next()").WithArguments("> Next()").WithLocation(10, 22));
        }

        [Fact]
        public void Opaque_DefiniteAssignment()
        {
            var source = """
                class C
                {
                    static bool M((int, int) t) => t is (var a, > a);
                    static bool N(int x)
                    {
                        int max;
                        return x is > max;
                    }
                }
                """;
            // An operand is read before the match assigns the pattern's own variables.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (3,51): error CS0165: Use of unassigned local variable 'a'
                //     static bool M((int, int) t) => t is (var a, > a);
                Diagnostic(ErrorCode.ERR_UseDefViolation, "a").WithArguments("a").WithLocation(3, 51),
                // (7,23): error CS0165: Use of unassigned local variable 'max'
                //         return x is > max;
                Diagnostic(ErrorCode.ERR_UseDefViolation, "max").WithArguments("max").WithLocation(7, 23));
        }

        [Fact]
        public void Opaque_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    static bool M(int x, string? s) => x is > s.Length;
                    static bool N(int x, string? s) => x switch { > s.Length => true, _ => false };
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (4,47): warning CS8602: Dereference of a possibly null reference.
                //     static bool M(int x, string? s) => x is > s.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(4, 47),
                // (5,53): warning CS8602: Dereference of a possibly null reference.
                //     static bool N(int x, string? s) => x switch { > s.Length => true, _ => false };
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(5, 53));
        }

        [Fact]
        public void Opaque_IOperation()
        {
            var source = """
                class C
                {
                    bool M(int x, int max, string s, string t)
                    {
                        return /*<bind>*/x is > 0 and <= max/*</bind>*/;
                    }
                }
                """;
            var comp = CreateImmutableCompilation(source);
            VerifyOperationTreeAndDiagnosticsForTest<IsPatternExpressionSyntax>(comp, """
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
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Opaque_IOperation_Constant()
        {
            var source = """
                class C
                {
                    bool M(object o, string s)
                    {
                        return /*<bind>*/o is s/*</bind>*/;
                    }
                }
                """;
            var comp = CreateImmutableCompilation(source);
            VerifyOperationTreeAndDiagnosticsForTest<BinaryExpressionSyntax>(comp, """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'o is s')
                  Value:
                    IParameterReferenceOperation: o (OperationKind.ParameterReference, Type: System.Object) (Syntax: 'o')
                  Pattern:
                    IConstantPatternOperation (OperationKind.ConstantPattern, Type: null, IsImplicit) (Syntax: 's') (InputType: System.Object, NarrowedType: System.String)
                      Value:
                        IParameterReferenceOperation: s (OperationKind.ParameterReference, Type: System.String) (Syntax: 's')
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Opaque_FlowGraph()
        {
            var source = """
                class C
                {
                    int M(int x, int max)
                    /*<bind>*/{
                        return x switch { > max => 1, _ => 2 };
                    }/*</bind>*/
                }
                """;
            var comp = CreateImmutableCompilation(source);
            VerifyFlowGraphAndDiagnosticsForTest<BlockSyntax>(comp, """
                Block[B0] - Entry
                    Statements (0)
                    Next (Regular) Block[B1]
                        Entering: {R1} {R2}
                .locals {R1}
                {
                    CaptureIds: [0]
                    .locals {R2}
                    {
                        CaptureIds: [1]
                        Block[B1] - Block
                            Predecessors: [B0]
                            Statements (1)
                                IFlowCaptureOperation: 1 (OperationKind.FlowCapture, Type: null, IsImplicit) (Syntax: 'x')
                                  Value:
                                    IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                            Jump if False (Regular) to Block[B3]
                                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: '> max => 1')
                                  Value:
                                    IFlowCaptureReferenceOperation: 1 (OperationKind.FlowCaptureReference, Type: System.Int32, IsImplicit) (Syntax: 'x')
                                  Pattern:
                                    IRelationalPatternOperation (BinaryOperatorKind.GreaterThan) (OperationKind.RelationalPattern, Type: null) (Syntax: '> max') (InputType: System.Int32, NarrowedType: System.Int32)
                                      Value:
                                        IParameterReferenceOperation: max (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'max')
                            Next (Regular) Block[B2]
                        Block[B2] - Block
                            Predecessors: [B1]
                            Statements (1)
                                IFlowCaptureOperation: 0 (OperationKind.FlowCapture, Type: null, IsImplicit) (Syntax: '1')
                                  Value:
                                    ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                            Next (Regular) Block[B6]
                                Leaving: {R2}
                        Block[B3] - Block
                            Predecessors: [B1]
                            Statements (0)
                            Jump if False (Regular) to Block[B5]
                                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: '_ => 2')
                                  Value:
                                    IFlowCaptureReferenceOperation: 1 (OperationKind.FlowCaptureReference, Type: System.Int32, IsImplicit) (Syntax: 'x')
                                  Pattern:
                                    IDiscardPatternOperation (OperationKind.DiscardPattern, Type: null) (Syntax: '_') (InputType: System.Int32, NarrowedType: System.Int32)
                                Leaving: {R2}
                            Next (Regular) Block[B4]
                        Block[B4] - Block
                            Predecessors: [B3]
                            Statements (1)
                                IFlowCaptureOperation: 0 (OperationKind.FlowCapture, Type: null, IsImplicit) (Syntax: '2')
                                  Value:
                                    ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 2) (Syntax: '2')
                            Next (Regular) Block[B6]
                                Leaving: {R2}
                    }
                    Block[B5] - Block
                        Predecessors: [B3]
                        Statements (0)
                        Next (Throw) Block[null]
                            IObjectCreationOperation (Constructor: System.Runtime.CompilerServices.SwitchExpressionException..ctor()) (OperationKind.ObjectCreation, Type: System.Runtime.CompilerServices.SwitchExpressionException, IsImplicit) (Syntax: 'x switch {  ... 1, _ => 2 }')
                              Arguments(0)
                              Initializer:
                                null
                    Block[B6] - Block
                        Predecessors: [B2] [B4]
                        Statements (0)
                        Next (Return) Block[B7]
                            IFlowCaptureReferenceOperation: 0 (OperationKind.FlowCaptureReference, Type: System.Int32, IsImplicit) (Syntax: 'x switch {  ... 1, _ => 2 }')
                            Leaving: {R1}
                }
                Block[B7] - Exit
                    Predecessors: [B6]
                    Statements (0)
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Opaque_DataFlow()
        {
            var source = """
                class C
                {
                    bool M(int x, int max, int min) => x is > max and < min;
                }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var isPattern = tree.GetRoot().DescendantNodes().OfType<IsPatternExpressionSyntax>().Single();
            var dataFlow = model.AnalyzeDataFlow(isPattern);
            Assert.True(dataFlow.Succeeded);
            AssertEx.Equal("x, max, min", string.Join(", ", dataFlow.ReadInside.Select(s => s.Name)));
            AssertEx.Equal("x, max, min", string.Join(", ", dataFlow.DataFlowsIn.Select(s => s.Name)));

            var max = isPattern.DescendantNodes().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.Text == "max");
            Assert.Equal("System.Int32 max", model.GetSymbolInfo(max).Symbol!.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetTypeInfo(max).Type!.ToTestDisplayString());
        }
    }
}
