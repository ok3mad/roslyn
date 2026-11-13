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
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ee---vzero -----one --4hvmany " : null);
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
    }
}
