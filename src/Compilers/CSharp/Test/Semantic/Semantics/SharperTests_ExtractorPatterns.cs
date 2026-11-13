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
    /// Sharper: extractor patterns, slice 3 of patterns on pure functions (implementation plan Feature 13, #35): a
    /// positional pattern whose name is no type but a pure method, <c>s is TryParse(var n)</c> or <c>s is Parse(&gt; 0)</c>,
    /// calls the method and matches its sub-patterns on the <c>out</c> values or the result. Each test sits next to its C#
    /// twin, a <c>Try</c> call or a call in a <c>when</c> guard.
    /// </summary>
    public sealed partial class SharperTests
    {
        private const string ExtractorChecks = """
            static class Ex
            {
                [AssumePure] public static bool TryParse(string s, out int value) => int.TryParse(s, out value);
                [Pure] public static bool TrySplit(string s, out string head, out string tail)
                {
                    int i = s.IndexOf(',');
                    head = i < 0 ? s : s.Substring(0, i);
                    tail = i < 0 ? "" : s.Substring(i + 1);
                    return i >= 0;
                }
                [Pure] public static int Length(string s) => s.Length;
                [Pure] public static int Halve(int x) => x / 2;
                [Pure] public static bool IsEven(int x) => x % 2 == 0;
            }
            """;

        [Fact]
        public void Extractor_TryShape()
        {
            var source = """
                using System;
                using static Ex;

                foreach (var s in new[] { "12", "-3", "x", "" })
                {
                    Console.Write(s is TryParse(var n) ? $"{n} " : "- ");
                    Console.Write(TryParse(s, out var m) ? $"{m} " : "- ");
                    Console.Write(s is TryParse(> 0) ? "p " : "- ");
                    Console.Write(s is Ex.TryParse(var a and > 0) ? $"{a} " : "- ");
                    Console.Write(s switch { TryParse(> 10) => "big", TryParse(_) => "num", _ => "nan" });
                    Console.Write(s switch { _ when TryParse(s, out var b) && b > 10 => "big", _ when TryParse(s, out _) => "num", _ => "nan" });
                    Console.Write("|");
                }
                Console.Write(" ");
                Console.Write("a,b" is TrySplit(var h, var t) ? $"{h}+{t}" : "-");
                Console.Write("a,b" is TrySplit("a", not "c") ? "y" : "n");
                Console.Write("ab" is TrySplit(_, _) ? "y" : "n");
                """ + ExtractorChecks;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "12 12 p 12 bigbig|-3 -3 - - numnum|- - - - nannan|- - - - nannan| a+byn" : null);
        }

        [Fact]
        public void Extractor_ValueShape()
        {
            var source = """
                using System;
                using static Ex;

                foreach (var s in new[] { "", "a", "abcd" })
                {
                    Console.Write(s is Length(0) ? "e" : "-");
                    Console.Write(Length(s) is 0 ? "e" : "-");
                    Console.Write(s is Length(> 2 and var n) ? $"{n}" : "-");
                    Console.Write(s is Length(Halve(2)) ? "h" : "-");
                    Console.Write(s is Length(IsEven(true)) ? "v" : "-");
                    Console.Write(s switch { Length(0) => "zero", Length(1) => "one", _ => "many" });
                    Console.Write(" ");
                }
                """ + ExtractorChecks;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ee--vzero -----one --4hvmany " : null);
        }

        [Fact]
        public void Extractor_FlagOff()
        {
            var source = """
                using static Ex;
                class C
                {
                    static bool A(string s) => s is TryParse(var n);
                    static bool B(string s) => s is Ex.Length(> 0);
                    static bool D(string s, C c) => s is c.Inst(var n);
                    bool Inst(string s, out int n) { n = 0; return true; }
                }
                static class Ex
                {
                    public static bool TryParse(string s, out int value) => int.TryParse(s, out value);
                    public static int Length(string s) => s.Length;
                }
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (1,1): hidden CS8019: Unnecessary using directive.
                // using static Ex;
                Diagnostic(ErrorCode.HDN_UnusedUsingDirective, "using static Ex;").WithLocation(1, 1),
                // (4,37): error CS0246: The type or namespace name 'TryParse' could not be found (are you missing a using directive or an assembly reference?)
                //     static bool A(string s) => s is TryParse(var n);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "TryParse").WithArguments("TryParse").WithLocation(4, 37),
                // (5,40): error CS0426: The type name 'Length' does not exist in the type 'Ex'
                //     static bool B(string s) => s is Ex.Length(> 0);
                Diagnostic(ErrorCode.ERR_DottedTypeNameNotFoundInAgg, "Length").WithArguments("Length", "Ex").WithLocation(5, 40),
                // (6,42): error CS0118: 'c' is a variable but is used like a type
                //     static bool D(string s, C c) => s is c.Inst(var n);
                Diagnostic(ErrorCode.ERR_BadSKknown, "c").WithArguments("c", "variable", "type").WithLocation(6, 42));
        }

        [Fact]
        public void Extractor_AndOrNot()
        {
            var source = """
                using System;
                using static Ex;

                foreach (var s in new[] { "4", "7", "x", "a,b" })
                {
                    Console.Write(s is TryParse(var n) and Length(1) ? $"{n}" : "-");
                    Console.Write(TryParse(s, out var m) && Length(s) == 1 ? $"{m}" : "-");
                    Console.Write(s is TryParse(IsEven) or TrySplit(_, _) ? "o" : "-");
                    Console.Write(s is not TryParse(_) ? "n" : "-");
                    Console.Write(!TryParse(s, out _) ? "n" : "-");
                    Console.Write(s is not (TryParse(> 5) or Length(> 2)) ? "x" : "-");
                    Console.Write(" ");
                }
                """ + ExtractorChecks;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "44o--x 77---- ---nnx --onn- " : null);
        }

        [Fact]
        public void Extractor_Nested()
        {
            var source = """
                using System;
                using static Ex;

                Order[] orders = [new("12", ["1", "x"]), new("x", []), new("3", ["5"])];
                foreach (var o in orders)
                {
                    Console.Write(o is { Id: TryParse(> 10) } ? "p" : "-");
                    Console.Write(o is { Id: var id } && TryParse(id, out var v) && v > 10 ? "p" : "-");
                    Console.Write(o is { Lines: [TryParse(var first), ..] } ? $"{first}" : "-");
                    Console.Write(o is { Lines: [.., not TryParse(_)] } ? "l" : "-");
                    Console.Write(o is (TryParse(var a), [Length(1)]) ? $"{a}" : "-");
                    Console.Write(o is { Id: TryParse(TrySmall(var b)) } ? $"{b}" : "-");
                    Console.Write(" ");
                }

                record Order(string Id, string[] Lines);
                static class More
                {
                    [Pure] public static bool TrySmall(int x, out int y) { y = x * 10; return x < 5; }
                }
                """ + ExtractorChecks.Replace("static class Ex", "static partial class Ex") + """
                static partial class Ex
                {
                    [Pure] public static bool TrySmall(int x, out int y) => More.TrySmall(x, out y);
                }
                """;
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "pp1l-- ------ --5-330 " : null);
        }

        [Fact]
        public void Extractor_TypeWins()
        {
            var source = """
                using System;
                using static Ex;

                Console.Write(new Parse(3) is Parse(var n) ? $"{n}" : "-");
                Console.Write(4 is Halve(var h) ? $"{h}" : "-");
                Console.Write(4 is Ex.Halve(var g) ? $"{g}" : "-");

                class Parse
                {
                    public Parse(int value) => Value = value;
                    public int Value { get; }
                    public void Deconstruct(out int value) => value = Value;
                }
                static class Ex
                {
                    [Pure] public static int Parse(Parse p) => 0;
                    [Pure] public static int Halve(int x) => x / 2;
                }
                """;
            // A type of that name always wins: 'Parse(var n)' deconstructs the class, and 'Halve(var h)' is the type
            // 'System.Half'. 'Ex.Halve' names no type, so it is an extractor.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
        }

        [Fact]
        public void Extractor_DefiniteAssignment()
        {
            var source = """
                using static Ex;
                class C
                {
                    static int M1(string s) => s is TryParse(var n) ? n : 0;
                    static int M2(string s) => s is TryParse(var n) and Length(1) ? n : 0;
                    static int M3(string s) => s is not TryParse(var n) ? 0 : n;
                    static int M4(string s) => s is TrySplit(var h, TryParse(var n)) ? n + h.Length : 0;
                    static int M5(string s, int k) => s switch { TryParse(var n) => n + k, _ => k };
                    static int M6((string, int) t) => t is (TryParse(var n), > n) ? n : 0;
                    static void M7(string s)
                    {
                        if (!(s is Length(var len))) return;
                        _ = len;
                    }
                }
                """ + ExtractorChecks;
            // The call is made before the pattern's variables are assigned, as an opaque operand is read: '> n' reads 'n'
            // before the match assigns it.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (9,64): error CS0165: Use of unassigned local variable 'n'
                //     static int M6((string, int) t) => t is (TryParse(var n), > n) ? n : 0;
                Diagnostic(ErrorCode.ERR_UseDefViolation, "n").WithArguments("n").WithLocation(9, 64));
        }

        [Fact]
        public void Extractor_SharedCall()
        {
            var source = """
                using System;

                static class Program
                {
                    static int calls;
                    [AssumePure] static bool TryParse(string s, out int n) { calls++; return int.TryParse(s, out n); }

                    static int M(string s) => s switch
                    {
                        TryParse(> 10) => 1,
                        TryParse(> 0) => 2,
                        TryParse(_) => 3,
                        _ => 4,
                    };

                    static int Twin(string s) => s switch
                    {
                        _ when TryParse(s, out var n) && n > 10 => 1,
                        _ when TryParse(s, out var n) && n > 0 => 2,
                        _ when TryParse(s, out _) => 3,
                        _ => 4,
                    };

                    static void Main()
                    {
                        Console.WriteLine($"{M("-1")} {calls}");
                        calls = 0;
                        Console.WriteLine($"{Twin("-1")} {calls}");
                    }
                }
                """;
            // The DAG makes one call for the three arms; the twin calls three times.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                3 1
                3 3
                """ : null);
        }

        [Fact]
        public void Extractor_NotHoisted()
        {
            var source = """
                using System;

                static class Program
                {
                    static int calls;
                    [AssumePure] static bool TryParse(string s, out int n) { calls++; return int.TryParse(s, out n); }

                    static int M(string s, bool b) => (s, b) switch
                    {
                        (_, true) => 0,
                        (TryParse(var n), _) => n,
                        _ => -1,
                    };

                    static void Main()
                    {
                        Console.WriteLine($"{M("5", true)} {calls}");
                        Console.WriteLine($"{M("5", false)} {calls}");
                    }
                }
                """;
            // The call is made only on the path of the arm that needs it.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                0 0
                5 1
                """ : null);
        }

        [Fact]
        public void Extractor_Impure()
        {
            var source = """
                class C
                {
                    int _count;
                    static bool TryParse(string s, out int n) { n = 0; return true; }
                    int Counts(string s) => ++_count;
                    [Pure] int Within(string s) => s.Length + _count;
                    [AssumePure] static bool Assumed(string s, out int n) { n = 1; return true; }

                    bool M1(string s) => s is TryParse(var n);
                    bool M2(string s) => s is Counts(> 0);
                    bool M3(string s) => s is Within(> 0);
                    bool M4(string s) => s is Assumed(var n);
                    bool M5(string s) => s is System.Int32.TryParse(var n);
                }
                """;
            // The rules of a [Pure] body: the callee is [Pure], [AssumePure] or attested.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (9,31): error CS20075: The method of an extractor pattern must be pure, but it is not: 'TryParse' calls 'C.TryParse(string, out int)', which is not known to be pure.
                //     bool M1(string s) => s is TryParse(var n);
                Diagnostic(ErrorCode.ERR_SharperExtractorPatternNotPure, "TryParse").WithArguments("'TryParse' calls 'C.TryParse(string, out int)', which is not known to be pure").WithLocation(9, 31),
                // (10,31): error CS20075: The method of an extractor pattern must be pure, but it is not: 'Counts' calls 'C.Counts(string)', which is not known to be pure.
                //     bool M2(string s) => s is Counts(> 0);
                Diagnostic(ErrorCode.ERR_SharperExtractorPatternNotPure, "Counts").WithArguments("'Counts' calls 'C.Counts(string)', which is not known to be pure").WithLocation(10, 31),
                // (13,31): error CS20075: The method of an extractor pattern must be pure, but it is not: 'System.Int32.TryParse' calls 'int.TryParse(string?, out int)', which is not known to be pure.
                //     bool M5(string s) => s is System.Int32.TryParse(var n);
                Diagnostic(ErrorCode.ERR_SharperExtractorPatternNotPure, "System.Int32.TryParse").WithArguments("'System.Int32.TryParse' calls 'int.TryParse(string?, out int)', which is not known to be pure").WithLocation(13, 31));
        }

        [Fact]
        public void Extractor_PureOutArgumentsAreFresh()
        {
            var source = """
                class C
                {
                    int _x;
                    [Pure] static bool TryGet(C c, out int r) { r = 1; return true; }
                    bool M(C c) => c is TryGet(> 0);
                    bool Twin(C c) => TryGet(c, out c._x);
                }
                """;
            // A pattern's out temporaries are fresh, so the extractor never gets CS20053 (#177); its twin does.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (6,37): warning CS20053: The out argument 'c._x' of the [Pure] member 'C.TryGet(C, out int)' is not a fresh local, so 'C.TryGet(C, out int)' may observe its own write through another argument.
                //     bool Twin(C c) => TryGet(c, out c._x);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "c._x").WithArguments("C.TryGet(C, out int)", "c._x").WithLocation(6, 37));
        }

        [Fact]
        public void Extractor_PureVirtual()
        {
            var source = """
                using System;

                abstract class Parser
                {
                    [Pure] public abstract bool TryParse(string s, out int n);
                    public virtual int Loose(string s) => 0;
                    public bool Check(string s) => s is TryParse(> 0);
                    public bool CheckLoose(string s) => s is Loose(0);
                }

                interface IParser
                {
                    [Pure] bool TryParse(string s, out int n);
                }

                sealed class Digits : Parser, IParser
                {
                    [Pure] public override bool TryParse(string s, out int n) { n = s.Length; return s.Length > 0 && s[0] >= '0' && s[0] <= '9'; }
                }

                static class Program
                {
                    static bool Through(string s, Parser p) => s is p.TryParse(var n) && n > 1;
                    static bool ThroughInterface(string s, IParser p) => s is p.TryParse(_);

                    static void Main()
                    {
                        var d = new Digits();
                        Console.Write($"{d.Check("1")} {d.Check("x")} {Through("12", d)} {ThroughInterface("y", d)}");
                    }
                }
                """;
            // A call through a [Pure] slot is pure (#225), on 'this' and on another receiver; an unmarked virtual is not.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics(
                // (8,46): error CS20075: The method of an extractor pattern must be pure, but it is not: 'Loose' calls 'Parser.Loose(string)', which is not known to be pure.
                //     public bool CheckLoose(string s) => s is Loose(0);
                Diagnostic(ErrorCode.ERR_SharperExtractorPatternNotPure, "Loose").WithArguments("'Loose' calls 'Parser.Loose(string)', which is not known to be pure").WithLocation(8, 46));
            source = source.Replace("public bool CheckLoose(string s) => s is Loose(0);", "");
            CompileAndVerify(CreateOpaqueCompilation(source), expectedOutput: ExecutionConditionUtil.IsCoreClr ? "True False True False" : null);
        }

        [Fact]
        public void Extractor_Shapes()
        {
            var source = """
                using System;

                static class Program
                {
                    // Both shapes under one name: the Try shape is tried first.
                    [Pure] static bool Get(string s, out int n) { n = 1; return true; }
                    [Pure] static int Get(string s) => 2;

                    // Only the value shape applies to one sub-pattern without an out overload.
                    [Pure] static bool Small(int x) => x < 3;

                    // A generic method infers from the input; the out type comes from the method.
                    [Pure] static T Id<T>(T t) => t;
                    [Pure] static bool TryFirst<T>(T[] a, out T first) { first = a.Length > 0 ? a[0] : default!; return a.Length > 0; }

                    static void Main()
                    {
                        Console.Write("s" is Get(var g) ? $"{g} " : "- ");
                        Console.Write(2 is Small(true) ? "s " : "- ");
                        Console.Write(5 is Small(false) ? "b " : "- ");
                        Console.Write(7 is Id(7) ? "i " : "- ");
                        Console.Write("x" is Id<object>(string) ? "o " : "- ");
                        Console.Write(new[] { 4, 5 } is TryFirst(var f) ? $"{f} " : "- ");
                        Console.Write(new int[0] is TryFirst(_) ? "f " : "- ");
                        Console.Write(2 is Small() ? "z" : "-");
                    }
                }
                """;
            // With no sub-pattern, 'Small()' is the Try shape with no out values: a 'bool M(T)' that returns true.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1 s b i o 4 - z" : null);
        }

        [Fact]
        public void Extractor_NotAShape()
        {
            var source = """
                class C
                {
                    [Pure] static void V(string s, out int n) { n = 0; }
                    [Pure] static int TwoOuts(string s, out int a, out int b) { a = b = 0; return 0; }
                    [Pure] static bool Defaults(string s, out int n, int k = 0) { n = k; return true; }
                    [Pure] static int Two(string s, int k) => k;
                    [Pure] static ref int RefReturn(string s) => throw null!;
                    [Pure] static bool Ambiguous(string s, out int n) { n = 0; return true; }
                    [Pure] static bool Ambiguous(string s, out long n) { n = 0; return true; }
                    [Pure] static bool TryNamed(string s, out int value) { value = 0; return true; }
                    [Pure] static int Value(string s) => 0;

                    bool M1(string s) => s is V(var n);
                    bool M2(string s) => s is TwoOuts(var a, var b);
                    bool M3(string s) => s is Defaults(var n);
                    bool M4(string s) => s is Two(var k);
                    bool M5(string s) => s is RefReturn(var r);
                    bool M6(string s) => s is Ambiguous(var n);
                    bool M7(string s) => s is TryNamed(value: var n);
                    bool M8(string s) => s is TryNamed(other: var n);
                    bool M9(string s) => s is Value(result: var n);
                    bool M10(string s) => s is Value(var a, var b);
                    bool M11(object o) => o is Value(var n);
                    bool M12(string s) => s is Value(var n) { Length: 0 };
                    bool M13(string s) => s is Value(var n) v;
                }
                """;
            // Another shape keeps C#'s error; an applicable call with an error, such as an ambiguity, reports it.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (13,31): error CS0246: The type or namespace name 'V' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M1(string s) => s is V(var n);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "V").WithArguments("V").WithLocation(13, 31),
                // (14,31): error CS0246: The type or namespace name 'TwoOuts' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M2(string s) => s is TwoOuts(var a, var b);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "TwoOuts").WithArguments("TwoOuts").WithLocation(14, 31),
                // (15,31): error CS0246: The type or namespace name 'Defaults' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M3(string s) => s is Defaults(var n);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Defaults").WithArguments("Defaults").WithLocation(15, 31),
                // (16,31): error CS0246: The type or namespace name 'Two' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M4(string s) => s is Two(var k);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Two").WithArguments("Two").WithLocation(16, 31),
                // (17,31): error CS0246: The type or namespace name 'RefReturn' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M5(string s) => s is RefReturn(var r);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "RefReturn").WithArguments("RefReturn").WithLocation(17, 31),
                // (18,31): error CS0121: The call is ambiguous between the following methods or properties: 'C.Ambiguous(string, out int)' and 'C.Ambiguous(string, out long)'
                //     bool M6(string s) => s is Ambiguous(var n);
                Diagnostic(ErrorCode.ERR_AmbigCall, "Ambiguous").WithArguments("C.Ambiguous(string, out int)", "C.Ambiguous(string, out long)").WithLocation(18, 31),
                // (20,40): error CS8517: The name 'other' does not match the corresponding 'Deconstruct' parameter 'value'.
                //     bool M8(string s) => s is TryNamed(other: var n);
                Diagnostic(ErrorCode.ERR_DeconstructParameterNameMismatch, "other").WithArguments("other", "value").WithLocation(20, 40),
                // (21,31): error CS0246: The type or namespace name 'Value' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M9(string s) => s is Value(result: var n);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Value").WithArguments("Value").WithLocation(21, 31),
                // (22,32): error CS0246: The type or namespace name 'Value' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M10(string s) => s is Value(var a, var b);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Value").WithArguments("Value").WithLocation(22, 32),
                // (23,32): error CS0246: The type or namespace name 'Value' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M11(object o) => o is Value(var n);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Value").WithArguments("Value").WithLocation(23, 32),
                // (24,32): error CS0246: The type or namespace name 'Value' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M12(string s) => s is Value(var n) { Length: 0 };
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Value").WithArguments("Value").WithLocation(24, 32),
                // (25,32): error CS0246: The type or namespace name 'Value' could not be found (are you missing a using directive or an assembly reference?)
                //     bool M13(string s) => s is Value(var n) v;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Value").WithArguments("Value").WithLocation(25, 32));
        }

        [Fact]
        public void Extractor_InstanceAndExtension()
        {
            var source = """
                using System;

                var r = new Range(1, 9);
                Console.Write("5" is r.TryParseWithin(var n) ? $"{n} " : "- ");
                Console.Write("50" is r.TryParseWithin(_) ? "w " : "- ");
                Console.Write(r.Check("3"));
                Console.Write(r.Check("0"));
                Console.Write(4 is r.Clamp(4) ? " c" : " -");
                Console.Write(40 is r.Clamp(9) ? "c" : "-");

                class Range
                {
                    public readonly int Lo, Hi;
                    public Range(int lo, int hi) { Lo = lo; Hi = hi; }
                    [AssumePure] public bool TryParseWithin(string s, out int n) => int.TryParse(s, out n) && n >= Lo && n <= Hi;
                    public bool Check(string s) => s is TryParseWithin(_);
                }

                static class Ext
                {
                    [Pure] public static int Clamp(this Range r, int x) => x < r.Lo ? r.Lo : x > r.Hi ? r.Hi : x;
                }
                """;
            // An instance method on 'this' or on another receiver; an extension method called on a receiver takes the
            // input as its argument, not as the receiver.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "5 - TrueFalse cc" : null);
        }

        [Fact]
        public void Extractor_Exhaustiveness()
        {
            var source = """
                using static Ex;
                class C
                {
                    static int M1(string s) => s switch { TryParse(_) => 1, not TryParse(_) => 2 };
                    static int M2(string s) => s switch { TryParse(> 0) => 1, TryParse(<= 0) => 2 };
                    static int M3(string s) => s switch { TryParse(var n) => n, Length(_) => 2 };
                    static int M4(string s) => s switch { Length(> 0) => 1, Length(0) => 2, Length(< 0) => 3 };
                    static int M5(string s) => s switch { TryParse(_) => 1, TryParse(_) => 2, _ => 3 };
                    static int M6(string s) => s switch { TryParse(> 0) => 1, TryParse(> 5) => 2, _ => 3 };
                    static int Twin(string s) => s switch { _ when TryParse(s, out _) => 1, _ when !TryParse(s, out _) => 2 };
                }
                """ + ExtractorChecks;
            // The call's results are values the DAG knows: an identical call has the same ones, and its out values and
            // result narrow as any value does ('TryParse(> 5)' after 'TryParse(> 0)'). Nothing is learned about the input,
            // and 'TryParse(> 0), TryParse(<= 0)' leaves out the inputs for which TryParse returns false.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (5,34): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
                //     static int M2(string s) => s switch { TryParse(> 0) => 1, TryParse(<= 0) => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("_").WithLocation(5, 34),
                // (8,61): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //     static int M5(string s) => s switch { TryParse(_) => 1, TryParse(_) => 2, _ => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "TryParse(_)").WithLocation(8, 61),
                // (9,63): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //     static int M6(string s) => s switch { TryParse(> 0) => 1, TryParse(> 5) => 2, _ => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "TryParse(> 5)").WithLocation(9, 63),
                // (10,36): warning CS8846: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered. However, a pattern with a 'when' clause might successfully match this value.
                //     static int Twin(string s) => s switch { _ when TryParse(s, out _) => 1, _ when !TryParse(s, out _) => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustiveWithWhen, "switch").WithArguments("_").WithLocation(10, 36));
        }

        [Fact]
        public void Extractor_RefinementRejects()
        {
            var source = """
                using static Ex;
                class C
                {
                    static string M(string s is TryParse(_)) => s;
                    static int Impure(string s) => 0;
                    static string N(string s is Impure(0)) => s;
                }
                """ + ExtractorChecks;
            // A refinement must stay closed over constants; the callee's purity is not checked there.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (1,1): hidden CS8019: Unnecessary using directive.
                // using static Ex;
                Diagnostic(ErrorCode.HDN_UnusedUsingDirective, "using static Ex;").WithLocation(1, 1),
                // (4,33): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'TryParse(_)' is not.
                //     static string M(string s is TryParse(_)) => s;
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "TryParse(_)").WithArguments("TryParse(_)").WithLocation(4, 33),
                // (6,33): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'Impure(0)' is not.
                //     static string N(string s is Impure(0)) => s;
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "Impure(0)").WithArguments("Impure(0)").WithLocation(6, 33));
        }

        [Fact]
        public void Extractor_Nullable()
        {
            var source = """
                #nullable enable
                using System.Diagnostics.CodeAnalysis;
                static class Program
                {
                    [Pure] static string? Find(string s) => s.Length > 0 ? s : null;
                    [Pure] static bool TryFind(string s, [NotNullWhen(true)] out string? found) { found = s.Length > 0 ? s : null; return found is not null; }
                    [Pure] static bool TryLoose(string s, out string? found) { found = null; return true; }
                    [Pure] static int Length(string s) => s.Length;

                    static int M1(string s) => s is Find({ } f) ? f.Length : 0;
                    static int M2(string s) => s is Find(var f) ? f.Length : 0;
                    static int M3(string s) => s is TryFind(var f) ? f.Length : 0;
                    static int M4(string s) => s is TryLoose(var f) ? f.Length : 0;
                    static int M5(string? s) => s is Length(> 0) ? 1 : 0;
                    static int M6(string? s) => s is not null and Length(> 0) ? 1 : 0;
                    static int M7(string s) => s is Find(null) ? 1 : 0;
                    static int Twin1(string s) => Find(s) is var f ? f.Length : 0;
                    static int Twin2(string? s) => s is var a && Length(a) > 0 ? 1 : 0;
                }
                """;
            // The result and the out values have the method's annotations; an out value gets the annotations for 'true'.
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (11,51): warning CS8602: Dereference of a possibly null reference.
                //     static int M2(string s) => s is Find(var f) ? f.Length : 0;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "f").WithLocation(11, 51),
                // (13,55): warning CS8602: Dereference of a possibly null reference.
                //     static int M4(string s) => s is TryLoose(var f) ? f.Length : 0;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "f").WithLocation(13, 55),
                // (14,38): warning CS8604: Possible null reference argument for parameter 's' in 'int Program.Length(string s)'.
                //     static int M5(string? s) => s is Length(> 0) ? 1 : 0;
                Diagnostic(ErrorCode.WRN_NullReferenceArgument, "Length").WithArguments("s", "int Program.Length(string s)").WithLocation(14, 38),
                // (17,54): warning CS8602: Dereference of a possibly null reference.
                //     static int Twin1(string s) => Find(s) is var f ? f.Length : 0;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "f").WithLocation(17, 54),
                // (18,57): warning CS8604: Possible null reference argument for parameter 's' in 'int Program.Length(string s)'.
                //     static int Twin2(string? s) => s is var a && Length(a) > 0 ? 1 : 0;
                Diagnostic(ErrorCode.WRN_NullReferenceArgument, "a").WithArguments("s", "int Program.Length(string s)").WithLocation(18, 57));
        }

        [Fact]
        public void Extractor_Null()
        {
            var source = """
                #nullable enable
                using System;

                foreach (var s in new[] { null, "", "a" })
                {
                    Console.Write(s is Describe(var d) ? d : "-");
                    Console.Write(Describe(s) is var e ? e : "-");
                    Console.Write(s is Describe("null") ? "N" : "-");
                    Console.Write(" ");
                }

                static partial class Program
                {
                    [AssumePure] static string Describe(string? s) => s is null ? "null" : s.Length.ToString();
                }
                """;
            // The method is called with the input as it is, null included; 'null' has no special meaning.
            var comp = CreateOpaqueCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "nullnullN 00- 11- " : null);
        }

        [Fact]
        public void Extractor_ConditionalAccess()
        {
            var source = """
                #nullable enable
                class C
                {
                    string? S = null;
                    [Pure] static bool TryParse(string? s, out int n) { n = 0; return s is null; }
                    static void M(C? c)
                    {
                        if (c?.S is TryParse(var n)) { _ = n; }
                        int x;
                        if (c?.Get(out x) is IsNull()) { _ = x; }
                        int y;
                        if (IsNull(c?.Get(out y))) { _ = y; }
                        int z;
                        if (c?.Get(out z) is not null) { _ = z; }
                    }
                    string? Get(out int x) { x = 0; return null; }
                    [Pure] static bool IsNull(string? s) => s is null;
                }
                """;
            // The method is called with null too, so a match says nothing about the access: 'x' may be unassigned, as 'y'
            // is in the twin. A pattern that never matches null tells that the access was made ('z').
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (10,46): error CS0165: Use of unassigned local variable 'x'
                //         if (c?.Get(out x) is IsNull()) { _ = x; }
                Diagnostic(ErrorCode.ERR_UseDefViolation, "x").WithArguments("x").WithLocation(10, 46),
                // (12,42): error CS0165: Use of unassigned local variable 'y'
                //         if (IsNull(c?.Get(out y))) { _ = y; }
                Diagnostic(ErrorCode.ERR_UseDefViolation, "y").WithArguments("y").WithLocation(12, 42));
        }

        [Fact]
        public void Extractor_IOperation()
        {
            var source = """
                using static Ex;
                class C
                {
                    bool M(string s)
                    {
                        return /*<bind>*/s is TryParse(var n and > 0) and not Ex.Length(0)/*</bind>*/;
                    }
                }
                """ + ExtractorChecks;
            // A positional pattern whose deconstruct symbol is the method the pattern calls with its input.
            var comp = CreateImmutableCompilation(source);
            VerifyOperationTreeAndDiagnosticsForTest<IsPatternExpressionSyntax>(comp, """
                IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 's is TryPar ... x.Length(0)')
                  Value:
                    IParameterReferenceOperation: s (OperationKind.ParameterReference, Type: System.String) (Syntax: 's')
                  Pattern:
                    IBinaryPatternOperation (BinaryOperatorKind.And) (OperationKind.BinaryPattern, Type: null) (Syntax: 'TryParse(va ... x.Length(0)') (InputType: System.String, NarrowedType: System.String)
                      LeftPattern:
                        IRecursivePatternOperation (OperationKind.RecursivePattern, Type: null) (Syntax: 'TryParse(var n and > 0)') (InputType: System.String, NarrowedType: System.String, DeclaredSymbol: null, MatchedType: System.String, DeconstructSymbol: System.Boolean Ex.TryParse(System.String s, out System.Int32 value))
                          DeconstructionSubpatterns (1):
                              IBinaryPatternOperation (BinaryOperatorKind.And) (OperationKind.BinaryPattern, Type: null) (Syntax: 'var n and > 0') (InputType: System.Int32, NarrowedType: System.Int32)
                                LeftPattern:
                                  IDeclarationPatternOperation (OperationKind.DeclarationPattern, Type: null) (Syntax: 'var n') (InputType: System.Int32, NarrowedType: System.Int32, DeclaredSymbol: System.Int32 n, MatchesNull: True)
                                RightPattern:
                                  IRelationalPatternOperation (BinaryOperatorKind.GreaterThan) (OperationKind.RelationalPattern, Type: null) (Syntax: '> 0') (InputType: System.Int32, NarrowedType: System.Int32)
                                    Value:
                                      ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 0) (Syntax: '0')
                          PropertySubpatterns (0)
                      RightPattern:
                        INegatedPatternOperation (OperationKind.NegatedPattern, Type: null) (Syntax: 'not Ex.Length(0)') (InputType: System.String, NarrowedType: System.String)
                          Pattern:
                            IRecursivePatternOperation (OperationKind.RecursivePattern, Type: null) (Syntax: 'Ex.Length(0)') (InputType: System.String, NarrowedType: System.String, DeclaredSymbol: null, MatchedType: System.String, DeconstructSymbol: System.Int32 Ex.Length(System.String s))
                              DeconstructionSubpatterns (1):
                                  IConstantPatternOperation (OperationKind.ConstantPattern, Type: null) (Syntax: '0') (InputType: System.Int32, NarrowedType: System.Int32)
                                    Value:
                                      ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 0) (Syntax: '0')
                              PropertySubpatterns (0)
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Extractor_FlowGraph()
        {
            var source = """
                using static Ex;
                class C
                {
                    void M(string s, int r)
                    /*<bind>*/{
                        r = s is TryParse(var n) ? n : 0;
                    }/*</bind>*/
                }
                """ + ExtractorChecks;
            var comp = CreateImmutableCompilation(source);
            VerifyFlowGraphAndDiagnosticsForTest<BlockSyntax>(comp, """
                Block[B0] - Entry
                    Statements (0)
                    Next (Regular) Block[B1]
                        Entering: {R1}
                .locals {R1}
                {
                    Locals: [System.Int32 n]
                    CaptureIds: [0] [1]
                    Block[B1] - Block
                        Predecessors: [B0]
                        Statements (1)
                            IFlowCaptureOperation: 0 (OperationKind.FlowCapture, Type: null, IsImplicit) (Syntax: 'r')
                              Value:
                                IParameterReferenceOperation: r (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'r')
                        Jump if False (Regular) to Block[B3]
                            IIsPatternOperation (OperationKind.IsPattern, Type: System.Boolean) (Syntax: 's is TryParse(var n)')
                              Value:
                                IParameterReferenceOperation: s (OperationKind.ParameterReference, Type: System.String) (Syntax: 's')
                              Pattern:
                                IRecursivePatternOperation (OperationKind.RecursivePattern, Type: null) (Syntax: 'TryParse(var n)') (InputType: System.String, NarrowedType: System.String, DeclaredSymbol: null, MatchedType: System.String, DeconstructSymbol: System.Boolean Ex.TryParse(System.String s, out System.Int32 value))
                                  DeconstructionSubpatterns (1):
                                      IDeclarationPatternOperation (OperationKind.DeclarationPattern, Type: null) (Syntax: 'var n') (InputType: System.Int32, NarrowedType: System.Int32, DeclaredSymbol: System.Int32 n, MatchesNull: True)
                                  PropertySubpatterns (0)
                        Next (Regular) Block[B2]
                    Block[B2] - Block
                        Predecessors: [B1]
                        Statements (1)
                            IFlowCaptureOperation: 1 (OperationKind.FlowCapture, Type: null, IsImplicit) (Syntax: 'n')
                              Value:
                                ILocalReferenceOperation: n (OperationKind.LocalReference, Type: System.Int32) (Syntax: 'n')
                        Next (Regular) Block[B4]
                    Block[B3] - Block
                        Predecessors: [B1]
                        Statements (1)
                            IFlowCaptureOperation: 1 (OperationKind.FlowCapture, Type: null, IsImplicit) (Syntax: '0')
                              Value:
                                ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 0) (Syntax: '0')
                        Next (Regular) Block[B4]
                    Block[B4] - Block
                        Predecessors: [B2] [B3]
                        Statements (1)
                            IExpressionStatementOperation (OperationKind.ExpressionStatement, Type: null) (Syntax: 'r = s is Tr ... n) ? n : 0;')
                              Expression:
                                ISimpleAssignmentOperation (OperationKind.SimpleAssignment, Type: System.Int32) (Syntax: 'r = s is Tr ...  n) ? n : 0')
                                  Left:
                                    IFlowCaptureReferenceOperation: 0 (OperationKind.FlowCaptureReference, Type: System.Int32, IsImplicit) (Syntax: 'r')
                                  Right:
                                    IFlowCaptureReferenceOperation: 1 (OperationKind.FlowCaptureReference, Type: System.Int32, IsImplicit) (Syntax: 's is TryPar ...  n) ? n : 0')
                        Next (Regular) Block[B5]
                            Leaving: {R1}
                }
                Block[B5] - Exit
                    Predecessors: [B4]
                    Statements (0)
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Extractor_SemanticModel()
        {
            var source = """
                using static Ex;
                class C
                {
                    bool M(string s) => s is TryParse(var n) and Ex.Length(1);
                }
                """ + ExtractorChecks;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var names = tree.GetRoot().DescendantNodes().OfType<RecursivePatternSyntax>().Select(p => p.Type!).ToArray();
            Assert.Equal(2, names.Length);
            Assert.Equal("System.Boolean Ex.TryParse(System.String s, out System.Int32 value)", model.GetSymbolInfo(names[0]).Symbol.ToTestDisplayString());
            Assert.Equal("System.Int32 Ex.Length(System.String s)", model.GetSymbolInfo(names[1]).Symbol.ToTestDisplayString());
            var n = tree.GetRoot().DescendantNodes().OfType<SingleVariableDesignationSyntax>().Single();
            Assert.Equal("System.Int32 n", model.GetDeclaredSymbol(n)!.ToTestDisplayString());
        }
    }
}
