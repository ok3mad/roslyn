// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper: predicate patterns, slice 2 of patterns on pure functions (implementation plan Feature 13, #35): a method
    /// group naming a pure <c>bool M(T)</c> is a pattern that matches when the method returns true. Each test sits next to
    /// its C# twin, a <c>when</c> guard.
    /// </summary>
    public sealed partial class SharperTests
    {
        private const string PredicateChecks = """
            static class Checks
            {
                [Pure] public static bool IsEven(int x) => x % 2 == 0;
                [Pure] public static bool IsSmall(int x) => x < 3;
                [Pure] public static bool IsEmpty(string s) => s.Length == 0;
            }
            """;

        [Fact]
        public void Predicate_IsAndSwitch()
        {
            var source = """
                using System;
                using static Checks;

                for (int i = 0; i < 5; i++)
                {
                    Console.Write(i is IsEven ? "e" : "o");
                    Console.Write(i is var a && IsEven(a) ? "e" : "o");
                    Console.Write(i is Checks.IsEven ? "e" : "o");
                    Console.Write(i switch { IsEven => "e", _ => "o" });
                    Console.Write(i switch { var b when IsEven(b) => "e", _ => "o" });
                    switch (i)
                    {
                        case IsEven: Console.Write("e"); break;
                        default: Console.Write("o"); break;
                    }
                    Console.Write(" ");
                }
                """ + PredicateChecks;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "eeeeee oooooo eeeeee oooooo eeeeee " : null);
        }

        [Fact]
        public void Predicate_FlagOff()
        {
            var source = """
                using static Checks;
                class C
                {
                    static bool M(int x) => x is IsEven;
                    static bool N(int x) => x is Checks.IsEven;
                    static int O(int x) => x switch { IsEven => 1, _ => 0 };
                    static bool P(int x) => x is not IsEven;
                }
                static class Checks
                {
                    public static bool IsEven(int x) => x % 2 == 0;
                }
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (4,34): error CS0428: Cannot convert method group 'IsEven' to non-delegate type 'int'. Did you intend to invoke the method?
                //     static bool M(int x) => x is IsEven;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "IsEven").WithArguments("IsEven", "int").WithLocation(4, 34),
                // (5,34): error CS0428: Cannot convert method group 'IsEven' to non-delegate type 'int'. Did you intend to invoke the method?
                //     static bool N(int x) => x is Checks.IsEven;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Checks.IsEven").WithArguments("IsEven", "int").WithLocation(5, 34),
                // (6,39): error CS0428: Cannot convert method group 'IsEven' to non-delegate type 'int'. Did you intend to invoke the method?
                //     static int O(int x) => x switch { IsEven => 1, _ => 0 };
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "IsEven").WithArguments("IsEven", "int").WithLocation(6, 39),
                // (7,38): error CS0428: Cannot convert method group 'IsEven' to non-delegate type 'int'. Did you intend to invoke the method?
                //     static bool P(int x) => x is not IsEven;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "IsEven").WithArguments("IsEven", "int").WithLocation(7, 38));
        }

        [Fact]
        public void Predicate_AndOrNot()
        {
            var source = """
                using System;
                using static Checks;

                for (int i = 0; i < 6; i++)
                {
                    Console.Write(i is IsEven and IsSmall ? "a" : "-");
                    Console.Write(i is var a && IsEven(a) && IsSmall(a) ? "a" : "-");
                    Console.Write(i is IsEven or 5 ? "o" : "-");
                    Console.Write(i is var b && (IsEven(b) || b == 5) ? "o" : "-");
                    Console.Write(i is not IsEven ? "n" : "-");
                    Console.Write(i is var c && !IsEven(c) ? "n" : "-");
                    Console.Write(i is not (IsEven or IsSmall) ? "x" : "-");
                    Console.Write(i is > 1 and IsEven ? "r" : "-");
                    Console.Write(" ");
                }
                """ + PredicateChecks;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "aaoo---- ----nn-- aaoo---r ----nnx- --oo---r --oonnx- " : null);
        }

        [Fact]
        public void Predicate_Nested()
        {
            var source = """
                using System;
                using static Checks;

                var items = new object[] { new Box(2, ""), new Box(3, "a"), new[] { 1, 4 }, new[] { 2 }, (4, "x"), 6, 7 };
                foreach (var item in items)
                {
                    Console.Write(item switch
                    {
                        Box { Value: IsEven, Name: IsEmpty } => "b",
                        int[] and [_, IsEven] => "l",
                        ValueTuple<int, string> (IsEven, not IsEmpty) => "t",
                        int and IsEven => "i",
                        _ => "-",
                    });
                    Console.Write(item switch
                    {
                        Box { Value: var v, Name: var n } when IsEven(v) && IsEmpty(n) => "b",
                        int[] and [_, var e] when IsEven(e) => "l",
                        (int t, string s) when IsEven(t) && !IsEmpty(s) => "t",
                        int i when IsEven(i) => "i",
                        _ => "-",
                    });
                    Console.Write(" ");
                }

                record Box(int Value, string Name);
                """ + PredicateChecks;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "bb -- ll -- tt ii -- " : null);
        }

        [Fact]
        public void Predicate_TypeOrConstantWins()
        {
            var source = """
                using System;

                class IsEven { }

                static class Program
                {
                    const int IsOdd = 3;
                    [Pure] static bool IsEven(int x) => x % 2 == 0;
                    [Pure] static bool IsOdd2(int x) => x % 2 == 1;

                    static void Main()
                    {
                        object o = new IsEven();
                        Console.Write(o is IsEven ? "t" : "f");
                        Console.Write(3 is IsOdd ? "t" : "f");
                        Console.Write(4 is IsOdd ? "t" : "f");
                    }
                }
                """;
            // C#'s meaning, with its warnings: a type pattern and a constant pattern.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics(
                // (15,23): warning CS8520: The given expression always matches the provided constant.
                //         Console.Write(3 is IsOdd ? "t" : "f");
                Diagnostic(ErrorCode.WRN_GivenExpressionAlwaysMatchesConstant, "3 is IsOdd").WithLocation(15, 23),
                // (16,23): warning CS8519: The given expression never matches the provided pattern.
                //         Console.Write(4 is IsOdd ? "t" : "f");
                Diagnostic(ErrorCode.WRN_GivenExpressionNeverMatchesPattern, "4 is IsOdd").WithLocation(16, 23));
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ttf" : null);
        }

        [Fact]
        public void Predicate_Impure()
        {
            var source = """
                class C
                {
                    int _count;
                    static bool IsEven(int x) => x % 2 == 0;
                    bool Counts(int x) => ++_count > x;
                    [Pure] bool Within(int x) => x < _count;
                    [AssumePure] static bool Assumed(int x) => x > 0;

                    bool M1(int x) => x is IsEven;
                    bool M2(int x) => x is Counts;
                    bool M3(int x) => x is Within;
                    bool M4(int x) => x is Assumed;
                    bool M5(string s) => s is string.IsNullOrEmpty;
                    bool M6(char c) => c is char.IsDigit;
                }
                """;
            // The rules of a [Pure] body: the callee is [Pure], [AssumePure] or attested.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (9,28): error CS20069: The method of a predicate pattern must be pure, but it is not: 'IsEven' calls 'C.IsEven(int)', which is not known to be pure.
                //     bool M1(int x) => x is IsEven;
                Diagnostic(ErrorCode.ERR_SharperPredicatePatternNotPure, "IsEven").WithArguments("'IsEven' calls 'C.IsEven(int)', which is not known to be pure").WithLocation(9, 28),
                // (10,28): error CS20069: The method of a predicate pattern must be pure, but it is not: 'Counts' calls 'C.Counts(int)', which is not known to be pure.
                //     bool M2(int x) => x is Counts;
                Diagnostic(ErrorCode.ERR_SharperPredicatePatternNotPure, "Counts").WithArguments("'Counts' calls 'C.Counts(int)', which is not known to be pure").WithLocation(10, 28),
                // (14,29): error CS20069: The method of a predicate pattern must be pure, but it is not: 'char.IsDigit' calls 'char.IsDigit(char)', which is not known to be pure.
                //     bool M6(char c) => c is char.IsDigit;
                Diagnostic(ErrorCode.ERR_SharperPredicatePatternNotPure, "char.IsDigit").WithArguments("'char.IsDigit' calls 'char.IsDigit(char)', which is not known to be pure").WithLocation(14, 29));
        }

        [Fact]
        public void Predicate_PureVirtual()
        {
            var source = """
                using System;

                abstract class Rule
                {
                    [Pure] public abstract bool Accepts(int x);
                    public virtual bool Loose(int x) => true;
                    public bool Check(int x) => x is Accepts;
                    public bool CheckLoose(int x) => x is Loose;
                }

                interface IRule
                {
                    [Pure] bool Accepts(int x);
                }

                sealed class Positive : Rule, IRule
                {
                    [Pure] public override bool Accepts(int x) => x > 0;
                }

                static class Program
                {
                    static bool Through(int x, Rule r) => x is r.Accepts;
                    static bool ThroughInterface(int x, IRule r) => x is r.Accepts;

                    static void Main()
                    {
                        var p = new Positive();
                        Console.Write($"{p.Check(1)} {p.Check(-1)} {Through(2, p)} {ThroughInterface(-2, p)}");
                    }
                }
                """;
            // A call through a [Pure] slot is pure (#225), on 'this' and on another receiver; an unmarked virtual is not.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics(
                // (8,43): error CS20069: The method of a predicate pattern must be pure, but it is not: 'Loose' calls 'Rule.Loose(int)', which is not known to be pure.
                //     public bool CheckLoose(int x) => x is Loose;
                Diagnostic(ErrorCode.ERR_SharperPredicatePatternNotPure, "Loose").WithArguments("'Loose' calls 'Rule.Loose(int)', which is not known to be pure").WithLocation(8, 43));
            source = source.Replace("public bool CheckLoose(int x) => x is Loose;", "");
            CompileAndVerify(CreateOpaqueCompilation(source), expectedOutput: ExecutionConditionUtil.IsCoreClr ? "True False True False" : null);
        }

        [Fact]
        public void Predicate_Overloads()
        {
            var source = """
                using System;

                static class Program
                {
                    [Pure] static bool Is(int x) => true;
                    [Pure] static bool Is(string s) => false;
                    [Pure] static bool Wide(long x) => x > 0;
                    [Pure] static bool Ref(object o) => o is string;
                    [Pure] static bool Gen<T>(T value) => value is int;
                    [Pure] static int NotBool(int x) => x;
                    [Pure] static bool Two(int x, int y) => x == y;
                    [Pure] static bool Opt(int x, int y = 0) => x == y;
                    [Pure] static bool Many(params int[] xs) => xs.Length == 1;
                    [Pure] static bool Amb(long x) => true;
                    [Pure] static bool Amb(int? x) => true;
                    [Pure] static bool In(in int x) => x > 0;
                    [Pure] static bool IsNull(int? x) => x is null;

                    static void Main()
                    {
                        Console.Write(1 is Is ? "t" : "f");
                        Console.Write("" is Is ? "t" : "f");
                        Console.Write(2 is Wide ? "t" : "f");
                        Console.Write("" is Ref ? "t" : "f");
                        Console.Write(3 is Gen ? "t" : "f");
                        Console.Write("" is Gen<string> ? "t" : "f");
                        Console.Write(4 is In ? "t" : "f");
                        Console.Write(5 is IsNull ? "t" : "f");
                    }

                    static void Errors(int x, object o, int? n)
                    {
                        _ = x is NotBool;
                        _ = x is Two;
                        _ = x is Opt;
                        _ = x is Many;
                        _ = o is Wide;
                        _ = n is Wide;
                        _ = x is Amb;
                    }
                }
                """;
            // The input converts as an argument would (int to long, string to object, int to int?), and a generic method
            // infers its type argument from it. A method that is not 'bool M(T)' with the input as its only argument
            // keeps C#'s error; an ambiguity among applicable methods is reported.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics(
                // (33,18): error CS0428: Cannot convert method group 'NotBool' to non-delegate type 'int'. Did you intend to invoke the method?
                //         _ = x is NotBool;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "NotBool").WithArguments("NotBool", "int").WithLocation(33, 18),
                // (34,18): error CS0428: Cannot convert method group 'Two' to non-delegate type 'int'. Did you intend to invoke the method?
                //         _ = x is Two;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Two").WithArguments("Two", "int").WithLocation(34, 18),
                // (35,18): error CS0428: Cannot convert method group 'Opt' to non-delegate type 'int'. Did you intend to invoke the method?
                //         _ = x is Opt;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Opt").WithArguments("Opt", "int").WithLocation(35, 18),
                // (36,18): error CS0428: Cannot convert method group 'Many' to non-delegate type 'int'. Did you intend to invoke the method?
                //         _ = x is Many;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Many").WithArguments("Many", "int").WithLocation(36, 18),
                // (37,18): warning CS8974: Converting method group 'Wide' to non-delegate type 'object'. Did you intend to invoke the method?
                //         _ = o is Wide;
                Diagnostic(ErrorCode.WRN_MethGrpToNonDel, "Wide").WithArguments("Wide", "object").WithLocation(37, 18),
                // (37,18): error CS0150: A constant value is expected
                //         _ = o is Wide;
                Diagnostic(ErrorCode.ERR_ConstantExpected, "Wide").WithLocation(37, 18),
                // (38,18): error CS0428: Cannot convert method group 'Wide' to non-delegate type 'int?'. Did you intend to invoke the method?
                //         _ = n is Wide;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Wide").WithArguments("Wide", "int?").WithLocation(38, 18),
                // (39,18): error CS0121: The call is ambiguous between the following methods or properties: 'Program.Amb(long)' and 'Program.Amb(int?)'
                //         _ = x is Amb;
                Diagnostic(ErrorCode.ERR_AmbigCall, "Amb").WithArguments("Program.Amb(long)", "Program.Amb(int?)").WithLocation(39, 18));
            comp = CreateOpaqueCompilation(source.Substring(0, source.IndexOf("static void Errors")) + "}");
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "tftttftf" : null);
        }

        [Fact]
        public void Predicate_InstanceAndExtension()
        {
            var source = """
                using System;

                class Range
                {
                    public readonly int Lo, Hi;
                    public Range(int lo, int hi) { Lo = lo; Hi = hi; }
                    [Pure] public bool Contains(int x) => x >= Lo && x < Hi;
                    public bool M(int x) => x is Contains;
                    public static bool S(int x) => x is Contains;
                }

                static class Ext
                {
                    [Pure] public static bool Above(this Range r, int x) => x >= r.Hi;
                }

                static class Program
                {
                    static void Main()
                    {
                        var r = new Range(1, 3);
                        Console.Write(2 is r.Contains ? "t" : "f");
                        Console.Write(r.M(5) ? "t" : "f");
                        Console.Write(4 is r.Above ? "t" : "f");
                        Console.Write(4 is Range.Contains ? "t" : "f");
                    }
                }
                """;
            // An instance method with 'this' or another receiver, and an extension method called on a receiver. The input is
            // an argument, never the receiver. An instance method without an instance keeps C#'s error.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics(
                // (9,41): error CS0428: Cannot convert method group 'Contains' to non-delegate type 'int'. Did you intend to invoke the method?
                //     public static bool S(int x) => x is Contains;
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Contains").WithArguments("Contains", "int").WithLocation(9, 41),
                // (25,23): warning CS8519: The given expression never matches the provided pattern.
                //         Console.Write(4 is Range.Contains ? "t" : "f");
                Diagnostic(ErrorCode.WRN_GivenExpressionNeverMatchesPattern, "4 is Range.Contains").WithLocation(25, 23),
                // (25,28): error CS0428: Cannot convert method group 'Contains' to non-delegate type 'int'. Did you intend to invoke the method?
                //         Console.Write(4 is Range.Contains ? "t" : "f");
                Diagnostic(ErrorCode.ERR_MethGrpToNonDel, "Range.Contains").WithArguments("Contains", "int").WithLocation(25, 28));
            source = source.Replace("public static bool S(int x) => x is Contains;", "").Replace("""Console.Write(4 is Range.Contains ? "t" : "f");""", "");
            CompileAndVerify(CreateOpaqueCompilation(source), expectedOutput: ExecutionConditionUtil.IsCoreClr ? "tft" : null);
        }

        [Fact]
        public void Predicate_Exhaustiveness()
        {
            var source = """
                using static Checks;
                class C
                {
                    static int M(int x) => x switch { IsEven => 1, not IsEven => 2 };
                    static int Twin(int x) => x switch { var a when IsEven(a) => 1, var a when !IsEven(a) => 2 };
                    static int N(int x) => x switch { IsEven => 1, IsSmall => 2 };
                    static int Same(int x) => x switch { IsEven => 1, IsEven => 2, _ => 3 };
                    static int Sub(int x) => x switch { IsEven => 1, 2 => 2, _ => 3 };
                    static int S1(string s) => s switch { IsEmpty => 1, not IsEmpty => 2 };
                    static int Other(int x, Checks2 c) => x switch { c.IsOdd => 1, not c.IsOdd => 2 };
                    static int S2(string s) => s switch { string.IsNullOrEmpty => 1, not string.IsNullOrEmpty => 2 };
                    static int S3(int x) => x switch { Checks.IsEven => 1, not Checks.IsEven => 2 };
                }
                class Checks2
                {
                    [Pure] public bool IsOdd(int x) => x % 2 == 1;
                }
                """ + PredicateChecks;
            // Nothing is known of a predicate's outcome but that the same call on the same value has the same one, also
            // through a type or an instance.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (5,33): warning CS8846: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered. However, a pattern with a 'when' clause might successfully match this value.
                //     static int Twin(int x) => x switch { var a when IsEven(a) => 1, var a when !IsEven(a) => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustiveWithWhen, "switch").WithArguments("_").WithLocation(5, 33),
                // (6,30): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
                //     static int N(int x) => x switch { IsEven => 1, IsSmall => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("_").WithLocation(6, 30),
                // (7,55): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //     static int Same(int x) => x switch { IsEven => 1, IsEven => 2, _ => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "IsEven").WithLocation(7, 55));
        }

        [Fact]
        public void Predicate_SharedTest()
        {
            var source = """
                using System;

                static class Program
                {
                    static int calls;
                    [AssumePure] static bool IsEven(int x) { calls++; return x % 2 == 0; }

                    static int M((int, int) t) => t switch
                    {
                        (IsEven, 1) => 1,
                        (IsEven, 2) => 2,
                        _ => 3,
                    };

                    static int Twin((int, int) t) => t switch
                    {
                        (var a, var b) when IsEven(a) && b == 1 => 1,
                        (var a, var b) when IsEven(a) && b == 2 => 2,
                        _ => 3,
                    };

                    static void Main()
                    {
                        Console.WriteLine($"{M((2, 2))} {calls}");
                        calls = 0;
                        Console.WriteLine($"{Twin((2, 2))} {calls}");
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
        public void Predicate_RefinementRejects()
        {
            var source = """
                using static Checks;
                class C
                {
                    static int M(int i is IsEven) => i;
                    static bool Impure(int x) => true;
                    static int N(int i is Impure) => i;
                }
                """ + PredicateChecks;
            // A refinement must stay closed over constants; the callee's purity is not checked there.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (4,27): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'IsEven' is not.
                //     static int M(int i is IsEven) => i;
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "IsEven").WithArguments("IsEven").WithLocation(4, 27),
                // (6,27): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'Impure' is not.
                //     static int N(int i is Impure) => i;
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "Impure").WithArguments("Impure").WithLocation(6, 27));
        }

        [Fact]
        public void Predicate_Nullable()
        {
            var source = """
                #nullable enable
                static class Program
                {
                    [Pure] static bool IsEmpty(string s) => s.Length == 0;
                    [Pure] static bool IsNullOrEmpty(string? s) => s is null or "";

                    static bool M1(string? s) => s is IsEmpty;
                    static bool M2(string? s) => s is not null and IsEmpty;
                    static bool M3(string? s) => s is IsNullOrEmpty;
                    static bool M4(string? s) => s is { } and IsEmpty;
                    static bool Twin1(string? s) => s is var a && IsEmpty(a);
                    static bool Twin2(string? s) => s is not null and var a && IsEmpty(a);
                }
                """;
            // The argument has the input's state where the DAG calls the method, as in the twins.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (7,39): warning CS8604: Possible null reference argument for parameter 's' in 'bool Program.IsEmpty(string s)'.
                //     static bool M1(string? s) => s is IsEmpty;
                Diagnostic(ErrorCode.WRN_NullReferenceArgument, "IsEmpty").WithArguments("s", "bool Program.IsEmpty(string s)").WithLocation(7, 39),
                // (11,59): warning CS8604: Possible null reference argument for parameter 's' in 'bool Program.IsEmpty(string s)'.
                //     static bool Twin1(string? s) => s is var a && IsEmpty(a);
                Diagnostic(ErrorCode.WRN_NullReferenceArgument, "a").WithArguments("s", "bool Program.IsEmpty(string s)").WithLocation(11, 59));
        }

        [Fact]
        public void Predicate_Null()
        {
            var source = """
                using System;

                foreach (var s in new[] { null, "", "a" })
                {
                    Console.Write(s is string.IsNullOrEmpty ? "e" : "-");
                    Console.Write(string.IsNullOrEmpty(s) ? "e" : "-");
                    Console.Write(s is { } and string.IsNullOrEmpty ? "n" : "-");
                    Console.Write(s switch { string.IsNullOrEmpty => 1, not string.IsNullOrEmpty => 2 });
                    Console.Write(" ");
                }
                """;
            // The method is called with the input as it is, null included: no null test comes first.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ee-1 een1 ---2 " : null);
        }

        [Fact]
        public void Predicate_DefiniteAssignment()
        {
            var source = """
                using static Checks;
                class C
                {
                    static bool M((int, int) t) => t is (var a, IsEven) && a > 0;
                    static void N(int x)
                    {
                        if (x is IsEven and var y) { _ = y; }
                    }
                }
                """ + PredicateChecks;
            CreateImmutableCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Predicate_IOperation()
        {
            var source = """
                using static Checks;
                class C
                {
                    bool M(int x)
                    {
                        return /*<bind>*/x is IsEven or Checks.IsSmall/*</bind>*/;
                    }
                }
                """ + PredicateChecks;
            // A constant pattern whose value is a reference to the method the pattern calls with its input.
            var comp = CreateImmutableCompilation(source);
            VerifyOperationTreeAndDiagnosticsForTest<IsPatternExpressionSyntax>(comp, """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'x is IsEven ... cks.IsSmall')
                  Value:
                    IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                  Pattern:
                    IBinaryPatternOperation (BinaryOperatorKind.Or) (OperationKind.BinaryPattern, Type: null) (Syntax: 'IsEven or Checks.IsSmall') (InputType: System.Int32, NarrowedType: System.Int32)
                      LeftPattern:
                        IConstantPatternOperation (OperationKind.ConstantPattern, Type: null) (Syntax: 'IsEven') (InputType: System.Int32, NarrowedType: System.Int32)
                          Value:
                            IMethodReferenceOperation: System.Boolean Checks.IsEven(System.Int32 x) (Static) (OperationKind.MethodReference, Type: null) (Syntax: 'IsEven')
                              Instance Receiver:
                                null
                      RightPattern:
                        IConstantPatternOperation (OperationKind.ConstantPattern, Type: null) (Syntax: 'Checks.IsSmall') (InputType: System.Int32, NarrowedType: System.Int32)
                          Value:
                            IMethodReferenceOperation: System.Boolean Checks.IsSmall(System.Int32 x) (Static) (OperationKind.MethodReference, Type: null) (Syntax: 'Checks.IsSmall')
                              Instance Receiver:
                                null
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Predicate_IOperation_IsOperator()
        {
            var source = """
                using static Checks;
                class C
                {
                    bool M(long x)
                    {
                        return /*<bind>*/x is IsEven/*</bind>*/;
                    }
                }
                static class Checks
                {
                    [Pure] public static bool IsEven(long x) => x % 2 == 0;
                }
                """;
            var comp = CreateImmutableCompilation(source);
            VerifyOperationTreeAndDiagnosticsForTest<BinaryExpressionSyntax>(comp, """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'x is IsEven')
                  Value:
                    IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int64) (Syntax: 'x')
                  Pattern:
                    IConstantPatternOperation (OperationKind.ConstantPattern, Type: null, IsImplicit) (Syntax: 'IsEven') (InputType: System.Int64, NarrowedType: System.Int64)
                      Value:
                        IMethodReferenceOperation: System.Boolean Checks.IsEven(System.Int64 x) (Static) (OperationKind.MethodReference, Type: null) (Syntax: 'IsEven')
                          Instance Receiver:
                            null
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Predicate_SemanticModel()
        {
            var source = """
                using static Checks;
                class C
                {
                    bool M(int x) => x is IsEven or Checks.IsSmall;
                }
                """ + PredicateChecks;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var names = tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>().Where(n => n.Identifier.Text is "IsEven" or "IsSmall").ToArray();
            Assert.Equal(2, names.Length);
            Assert.Equal("System.Boolean Checks.IsEven(System.Int32 x)", model.GetSymbolInfo(names[0]).Symbol.ToTestDisplayString());
            Assert.Equal("System.Boolean Checks.IsSmall(System.Int32 x)", model.GetSymbolInfo(names[1]).Symbol.ToTestDisplayString());
        }

        [Fact]
        public void Predicate_FlowGraph()
        {
            var source = """
                using static Checks;
                class C
                {
                    void M(int x, bool b)
                    /*<bind>*/{
                        b = x is IsEven and not IsSmall;
                    }/*</bind>*/
                }
                """ + PredicateChecks;
            var comp = CreateImmutableCompilation(source);
            VerifyFlowGraphAndDiagnosticsForTest<BlockSyntax>(comp, """
                Block[B0] - Entry
                    Statements (0)
                    Next (Regular) Block[B1]
                Block[B1] - Block
                    Predecessors: [B0]
                    Statements (1)
                        IExpressionStatementOperation (OperationKind.ExpressionStatement, Type: null) (Syntax: 'b = x is Is ... ot IsSmall;')
                          Expression:
                            ISimpleAssignmentOperation (OperationKind.SimpleAssignment, Type: System.Boolean) (Syntax: 'b = x is Is ... not IsSmall')
                              Left:
                                IParameterReferenceOperation: b (OperationKind.ParameterReference, Type: System.Boolean) (Syntax: 'b')
                              Right:
                                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 'x is IsEven ... not IsSmall')
                                  Value:
                                    IParameterReferenceOperation: x (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'x')
                                  Pattern:
                                    IBinaryPatternOperation (BinaryOperatorKind.And) (OperationKind.BinaryPattern, Type: null) (Syntax: 'IsEven and not IsSmall') (InputType: System.Int32, NarrowedType: System.Int32)
                                      LeftPattern:
                                        IConstantPatternOperation (OperationKind.ConstantPattern, Type: null) (Syntax: 'IsEven') (InputType: System.Int32, NarrowedType: System.Int32)
                                          Value:
                                            IMethodReferenceOperation: System.Boolean Checks.IsEven(System.Int32 x) (Static) (OperationKind.MethodReference, Type: null) (Syntax: 'IsEven')
                                              Instance Receiver:
                                                null
                                      RightPattern:
                                        INegatedPatternOperation (OperationKind.NegatedPattern, Type: null) (Syntax: 'not IsSmall') (InputType: System.Int32, NarrowedType: System.Int32)
                                          Pattern:
                                            IConstantPatternOperation (OperationKind.ConstantPattern, Type: null) (Syntax: 'IsSmall') (InputType: System.Int32, NarrowedType: System.Int32)
                                              Value:
                                                IMethodReferenceOperation: System.Boolean Checks.IsSmall(System.Int32 x) (Static) (OperationKind.MethodReference, Type: null) (Syntax: 'IsSmall')
                                                  Instance Receiver:
                                                    null
                    Next (Regular) Block[B2]
                Block[B2] - Exit
                    Predecessors: [B1]
                    Statements (0)
                """, DiagnosticDescription.None);
        }
    }
}
