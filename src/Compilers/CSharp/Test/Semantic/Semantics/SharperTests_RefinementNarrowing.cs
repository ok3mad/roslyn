// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's refined types, slice 2 (issue #33): switch expression arms narrow a refined or readonly input, and
    /// '+', '-' and unary '-' of refined integers have a refinement (interval arithmetic).
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void RefinementNarrowing_Arms()
        {
            // Each arm sees the values its pattern matches among those the earlier arms leave. The C# twin has no
            // refinements, so none of its calls is checked.
            var source = """
                class C
                {
                    static void Page(int i is >=0) { }
                    static void Positive(int i is >=1) { }
                    static void Small(int i is >=0 and <10) { }

                    static void M(int k is >=0, readonly int r, int plain)
                    {
                        _ = k switch { 0 => 0, _ => F(() => Positive(k)) };
                        _ = k switch { <10 => F(() => Small(k)), _ => F(() => Positive(k)) };
                        _ = k switch { 0 => 0, 1 => 1, var other => F(() => Positive(k)) };
                        _ = k switch { int when k > 3 => 0, 0 => 1, _ => F(() => Positive(k)) };
                        _ = k switch { 0 => F(() => Positive(k)), _ => 0 };
                        _ = k switch { 0 or 1 when F(() => Positive(k)) == 0 => 0, _ => 1 };
                        _ = r switch { >=0 => F(() => Page(r)), _ => F(() => Page(r)) };  // the second call always throws
                        _ = plain switch { >=0 => F(() => Page(plain)), _ => 0 };
                        _ = k switch { >0 => k switch { <10 => F(() => Small(k - 1)), _ => 0 }, _ => 0 };
                        val v = k;
                        _ = v switch { >=1 => F(() => Positive(v)), _ => 0 };
                    }

                    static void M_CSharp(int k, int r)
                    {
                        _ = k switch { 0 => 0, _ => F(() => Positive_CSharp(k)) };
                        _ = r switch { >=0 => F(() => Page_CSharp(r)), _ => F(() => Page_CSharp(r)) };
                    }

                    static void Page_CSharp(int i) { }
                    static void Positive_CSharp(int i) { }
                    static int F(System.Action a) => 0;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (13,46): error CS20075: The argument 'k' never satisfies the refinement 'is >= 1' of parameter 'i': its values, 'is 0', are all outside it.
                //         _ = k switch { 0 => F(() => Positive(k)), _ => 0 };
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "k").WithArguments("k", "i", ">= 1", "0").WithLocation(13, 46),
                // (14,53): warning CS20077: The argument 'k' may not satisfy the refinement 'is >= 1' of parameter 'i': its refinement 'is 0 or 1' allows 0.
                //         _ = k switch { 0 or 1 when F(() => Positive(k)) == 0 => 0, _ => 1 };
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "k").WithArguments("k", "i", ">= 1", "0 or 1", "0").WithLocation(14, 53),
                // (15,67): error CS20075: The argument 'r' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is <= -1', are all outside it.
                //         _ = r switch { >=0 => F(() => Page(r)), _ => F(() => Page(r)) };  // the second call always throws
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "r").WithArguments("r", "i", ">= 0", "<= -1").WithLocation(15, 67),
                // (16,48): warning CS20078: The compiler cannot prove that the argument 'plain' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         _ = plain switch { >=0 => F(() => Page(plain)), _ => 0 };
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "plain").WithArguments("plain", "i", ">= 0").WithLocation(16, 48));
        }

        [Fact]
        public void RefinementNarrowing_Types()
        {
            // Narrowing works for every type with a value set: strings (also those that exclude a few), enums, chars,
            // booleans, doubles with NaN; nint has no written form, so it is not narrowed.
            var source = """
                enum Color { Red, Green, Blue }

                class C
                {
                    static int Letter(string s is "b" or "c") => 0;
                    static int NotEmpty(string s is not ("" or null)) => 0;
                    static int Warm(Color c is Color.Red or Color.Green) => 0;
                    static int Lower(char c is >='a' and <='z') => 0;
                    static int Yes(bool b is true) => 0;
                    static int Number(double d is not double.NaN) => 0;
                    static int Native(nint n is >=0) => 0;

                    static void M(string s is "a" or "b" or "c", readonly string t, Color c is Color.Red or Color.Green or Color.Blue, readonly char ch,
                        bool b is true or false, readonly double d, readonly nint n)
                    {
                        _ = s switch { "a" => 0, _ => Letter(s) };
                        _ = t switch { "" or null => 0, _ => NotEmpty(t) };
                        _ = c switch { Color.Blue => 0, _ => Warm(c) };
                        _ = ch switch { >='a' and <='z' => Lower(ch), _ => 0 };
                        _ = b switch { false => 0, _ => Yes(b) };
                        _ = d switch { double.NaN => 0, _ => Number(d) };
                        _ = n switch { <0 => 0, _ => Native(n) };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (22,45): warning CS20078: The compiler cannot prove that the argument 'n' satisfies the refinement 'is >= 0' of parameter 'n': the argument has no refinement. The method checks it when it is called.
                //         _ = n switch { <0 => 0, _ => Native(n) };
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "n").WithArguments("n", "n", ">= 0").WithLocation(22, 45));
        }

        [Fact]
        public void RefinementNarrowing_Severity()
        {
            // A narrowed refinement keeps its variable's kind: one that depends on a call's inferred return is still an
            // error when it is not proven, others warn; an arm whose values are all outside the domain is disproven.
            var source = """
                class C
                {
                    static void Positive(int i is >=1) { }
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };

                    static void M(int x, int k is >=0)
                    {
                        val s = Sign(x);
                        _ = s switch { >0 => F(() => Positive(s)), _ => 0 };
                        _ = s switch { >=0 => F(() => Positive(s)), _ => 0 };
                        _ = s switch { <=0 => F(() => Positive(s)), _ => 0 };
                        _ = k switch { <=5 => F(() => Positive(k)), _ => 0 };
                        _ = k switch { 0 => F(() => Positive(k)), _ => 0 };
                    }

                    static int F(System.Action a) => 0;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (10,48): error CS20076: The argument 's' may not satisfy the refinement 'is >= 1' of parameter 'i': its inferred refinement 'is 0 or 1' allows 0.
                //         _ = s switch { >=0 => F(() => Positive(s)), _ => 0 };
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "s").WithArguments("s", "i", ">= 1", "0 or 1", "0").WithLocation(10, 48),
                // (11,48): error CS20075: The argument 's' never satisfies the refinement 'is >= 1' of parameter 'i': its values, 'is -1 or 0', are all outside it.
                //         _ = s switch { <=0 => F(() => Positive(s)), _ => 0 };
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "s").WithArguments("s", "i", ">= 1", "-1 or 0").WithLocation(11, 48),
                // (12,48): warning CS20077: The argument 'k' may not satisfy the refinement 'is >= 1' of parameter 'i': its refinement 'is >= 0 and <= 5' allows 0.
                //         _ = k switch { <=5 => F(() => Positive(k)), _ => 0 };
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "k").WithArguments("k", "i", ">= 1", ">= 0 and <= 5", "0").WithLocation(12, 48),
                // (13,46): error CS20075: The argument 'k' never satisfies the refinement 'is >= 1' of parameter 'i': its values, 'is 0', are all outside it.
                //         _ = k switch { 0 => F(() => Positive(k)), _ => 0 };
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "k").WithArguments("k", "i", ">= 1", "0").WithLocation(13, 46));
        }

        [Fact]
        public void RefinementArithmetic_Arguments()
        {
            // '+', '-' and unary '-' of refined int, uint, long and ulong values. Unchecked arithmetic that can overflow
            // wraps around and gives no refinement; checked arithmetic drops the values that would throw. Mixed types
            // convert an operand, which keeps no refinement in slice 2.
            var source = """
                class C
                {
                    static void Page(int i is >=0) { }
                    static void Positive(int i is >=1) { }
                    static void PageU(uint u is >=1) { }
                    static void PageL(long l is >=0) { }
                    static void PageUL(ulong u is >=1) { }

                    static void M(int k is >=0 and <100, int all is >=0, int j is >=1 and <=5, uint u is >=0 and <=9, long l is >=1 and <=9,
                        ulong ul is >=0 and <=9)
                    {
                        Positive(k + 1);
                        Page(k - 1);
                        Page(k + j);
                        Page(k - j);
                        Page(-k);
                        Page(-(-k));
                        Page(all + 1);
                        Page(checked(all + 1));
                        PageU(u + 1);
                        PageU(u - 1);
                        PageU(checked(u - 1));
                        PageL(l - 1);
                        PageL(l + k);
                        PageUL(ul + 1);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (13,14): warning CS20077: The argument 'k - 1' may not satisfy the refinement 'is >= 0' of parameter 'i': its refinement 'is >= -1 and <= 98' allows -1.
                //         Page(k - 1);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "k - 1").WithArguments("k - 1", "i", ">= 0", ">= -1 and <= 98", "-1").WithLocation(13, 14),
                // (15,14): warning CS20077: The argument 'k - j' may not satisfy the refinement 'is >= 0' of parameter 'i': its refinement 'is >= -5 and <= 98' allows -1.
                //         Page(k - j);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "k - j").WithArguments("k - j", "i", ">= 0", ">= -5 and <= 98", "-1").WithLocation(15, 14),
                // (16,14): warning CS20077: The argument '-k' may not satisfy the refinement 'is >= 0' of parameter 'i': its refinement 'is >= -99 and <= 0' allows -1.
                //         Page(-k);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "-k").WithArguments("-k", "i", ">= 0", ">= -99 and <= 0", "-1").WithLocation(16, 14),
                // (18,14): warning CS20078: The compiler cannot prove that the argument 'all + 1' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         Page(all + 1);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "all + 1").WithArguments("all + 1", "i", ">= 0").WithLocation(18, 14),
                // (21,15): warning CS20078: The compiler cannot prove that the argument 'u - 1' satisfies the refinement 'is >= 1' of parameter 'u': the argument has no refinement. The method checks it when it is called.
                //         PageU(u - 1);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "u - 1").WithArguments("u - 1", "u", ">= 1").WithLocation(21, 15),
                // (22,23): warning CS20077: The argument 'u - 1' may not satisfy the refinement 'is >= 1' of parameter 'u': its refinement 'is <= 8' allows 0.
                //         PageU(checked(u - 1));
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "u - 1").WithArguments("u - 1", "u", ">= 1", "<= 8", "0").WithLocation(22, 23),
                // (24,15): warning CS20078: The compiler cannot prove that the argument 'l + k' satisfies the refinement 'is >= 0' of parameter 'l': the argument has no refinement. The method checks it when it is called.
                //         PageL(l + k);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "l + k").WithArguments("l + k", "l", ">= 0").WithLocation(24, 15));
        }

        [Fact]
        public void RefinementArithmetic_Returns()
        {
            // Inferred return refinements and 'val' locals follow arithmetic too, and the text is written to metadata.
            var source = """
                class C
                {
                    public static infer Next(int i is >=0 and <10) => i + 1;
                    static infer Negate(int i is >=0 and <10) => -i;
                    static infer Distance(int a is >=0 and <=10, int b is >=0 and <=10) => a - b;
                    static infer Unbounded(int i is >=0) => i + 1;
                    static infer Checked(int i is >=0) => checked(i + 1);
                    static infer Holes(int i is 1 or 5) => i + 10;
                    static infer Plain(int i) => i + 1;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "Next: >= 1 and <= 10",
                    "Negate: >= -9 and <= 0",
                    "Distance: >= -10 and <= 10",
                    "Unbounded: <none>",
                    "Checked: >= 1",
                    "Holes: 11 or 15",
                    "Plain: <none>",
                },
                ReturnRefinements(comp));

            CompileAndVerify(comp, symbolValidator: module =>
            {
                var next = module.GlobalNamespace.GetMember<MethodSymbol>("C.Next");
                Assert.Equal(">= 1 and <= 10", next.SharperReturnRefinement!.Text);
            }).VerifyDiagnostics();
        }

        [Fact]
        public void RefinementNarrowing_FlagOff()
        {
            // Without the flag, nothing is narrowed and arithmetic is plain C#.
            var source = """
                class C
                {
                    static int M(int k) => k switch { 0 => 0, _ => N(k - 1) };
                    static int N(int i) => i;
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics();
        }
    }
}
