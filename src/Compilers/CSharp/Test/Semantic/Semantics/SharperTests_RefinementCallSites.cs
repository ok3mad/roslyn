// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's refined types, slice 1 step 4 (issue #33): call sites and switches. An argument for a refined parameter
    /// is proven (silent), disproven (CS20075) or not proven: an error when its value has an inferred refinement (CS20076),
    /// a warning when it has a written one (CS20077) or none (CS20078). A switch on a refined value seeds its decision dag
    /// with the refinement's values: missing values are named inside the domain, an inferred refinement that is not
    /// covered is an error (CS20079), and an arm excluded only by the refinement is a warning (CS20080).
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void RefinementCallSite_Constants()
        {
            // A constant is proven or disproven; the C# twin, with no refinement, accepts every call.
            var source = """
                enum Color { Red, Green, Blue }

                class C
                {
                    static void Page(int i is >=0) { }
                    static void Page_CSharp(int i) { }
                    static void All(long l is >0 and <=100, double d is >=0.0, decimal m is >0m, char c is >='a' and <='z',
                        bool b is true, string s is not "", Color k is Color.Red or Color.Green, nint n is >=0, byte x is not 0) { }
                    static void Name(string s is "a" or "b" or null) { }
                    static void NotX(string s is not "x") { }
                    static void NotNull(string s is "a" or "b") { }
                    const int Limit = 10;

                    static void M()
                    {
                        Page(0);
                        Page(Limit * 2 + 1);
                        Page(-1);
                        Page(-Limit);
                        Page_CSharp(-1);
                        All(5, 0.5, 2m, 'q', true, "s", Color.Green, 7, 9);
                        All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                        Name(null);
                        Name("a");
                        Name("c");
                        NotX(null);
                        NotX("x");
                        NotNull(null);
                        NotNull(default);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (18,14): error CS20075: The argument '-1' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -1', are all outside it.
                //         Page(-1);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-1").WithArguments("-1", "i", ">= 0", "-1").WithLocation(18, 14),
                // (19,14): error CS20075: The argument '-Limit' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -10', are all outside it.
                //         Page(-Limit);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-Limit").WithArguments("-Limit", "i", ">= 0", "-10").WithLocation(19, 14),
                // (22,13): error CS20075: The argument '101' never satisfies the refinement 'is > 0 and <= 100' of parameter 'l': its values, 'is 101', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "101").WithArguments("101", "l", "> 0 and <= 100", "101").WithLocation(22, 13),
                // (22,18): error CS20075: The argument 'double.NaN' never satisfies the refinement 'is >= 0' of parameter 'd': its values, 'is double.NaN', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "double.NaN").WithArguments("double.NaN", "d", ">= 0", "double.NaN").WithLocation(22, 18),
                // (22,30): error CS20075: The argument '0m' never satisfies the refinement 'is > 0m' of parameter 'm': its values, 'is 0m', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "0m").WithArguments("0m", "m", "> 0m", "0m").WithLocation(22, 30),
                // (22,34): error CS20075: The argument ''Q'' never satisfies the refinement 'is >= 'a' and <= 'z'' of parameter 'c': its values, 'is 'Q'', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "'Q'").WithArguments("'Q'", "c", ">= 'a' and <= 'z'", "'Q'").WithLocation(22, 34),
                // (22,39): error CS20075: The argument 'false' never satisfies the refinement 'is true' of parameter 'b': its values, 'is false', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "false").WithArguments("false", "b", "true", "false").WithLocation(22, 39),
                // (22,46): error CS20075: The argument '""' never satisfies the refinement 'is not ""' of parameter 's': its values, 'is ""', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, @"""""").WithArguments(@"""""", "s", @"not """"", @"""""").WithLocation(22, 46),
                // (22,50): error CS20075: The argument 'Color.Blue' never satisfies the refinement 'is Color.Red or Color.Green' of parameter 'k': its values, 'is Color.Blue', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "Color.Blue").WithArguments("Color.Blue", "k", "Color.Red or Color.Green", "Color.Blue").WithLocation(22, 50),
                // (22,62): error CS20075: The argument '-7' never satisfies the refinement 'is >= 0' of parameter 'n': its values, 'is -7', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-7").WithArguments("-7", "n", ">= 0", "-7").WithLocation(22, 62),
                // (22,66): error CS20075: The argument '0' never satisfies the refinement 'is not 0' of parameter 'x': its values, 'is 0', are all outside it.
                //         All(101, double.NaN, 0m, 'Q', false, "", Color.Blue, -7, 0);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "0").WithArguments("0", "x", "not 0", "0").WithLocation(22, 66),
                // (25,14): error CS20075: The argument '"c"' never satisfies the refinement 'is "a" or "b" or null' of parameter 's': its values, 'is "c"', are all outside it.
                //         Name("c");
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, @"""c""").WithArguments(@"""c""", "s", @"""a"" or ""b"" or null", @"""c""").WithLocation(25, 14),
                // (27,14): error CS20075: The argument '"x"' never satisfies the refinement 'is not "x"' of parameter 's': its values, 'is "x"', are all outside it.
                //         NotX("x");
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, @"""x""").WithArguments(@"""x""", "s", @"not ""x""", @"""x""").WithLocation(27, 14),
                // (28,17): error CS20075: The argument 'null' never satisfies the refinement 'is "a" or "b"' of parameter 's': its values, 'is null', are all outside it.
                //         NotNull(null);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "null").WithArguments("null", "s", @"""a"" or ""b""", "null").WithLocation(28, 17),
                // (29,17): error CS20075: The argument 'default' never satisfies the refinement 'is "a" or "b"' of parameter 's': its values, 'is null', are all outside it.
                //         NotNull(default);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "default").WithArguments("default", "s", @"""a"" or ""b""", "null").WithLocation(29, 17));
        }

        [Fact]
        public void RefinementCallSite_Values()
        {
            // The sources of an argument's refinement are those of step 3. A written refinement that is not proven is a
            // warning, since slices 2 and 3 narrow such values after tests; an inferred one is an error.
            var source = """
                class C
                {
                    static void Page(int i is >=0) { }
                    static void Positive(int i is >=1) { }
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    static infer Abs(int x) => x switch { <0 => 1, _ => 0 };
                    static infer Same(int i is >=0) => i;
                    static int Plain(int x) => x;

                    static void M(int x, bool c, int j is >=1, int k is >=0, int n is <0)
                    {
                        Page(j);
                        Page(k);
                        Page(n);
                        Positive(k);
                        val v = k;
                        Positive(v);
                        Page(Abs(x));
                        Page(Sign(x));
                        val s = Sign(x);
                        Page(s);
                        Positive(Same(k));
                        Page(c ? 1 : 2);
                        Page(c ? 1 : -1);
                        Page(c ? j : n);
                        Page(x switch { 0 => j, _ => 5 });
                        Page(x);
                        Page(Plain(x));
                        Page(x + 1);
                        Page((int)k);
                        Page(checked(k));
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (14,14): error CS20075: The argument 'n' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is < 0', are all outside it.
                //         Page(n);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "n").WithArguments("n", "i", ">= 0", "< 0").WithLocation(14, 14),
                // (15,18): warning CS20077: The argument 'k' may not satisfy the refinement 'is >= 1' of parameter 'i': its refinement 'is >= 0' allows 0.
                //         Positive(k);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "k").WithArguments("k", "i", ">= 1", ">= 0", "0").WithLocation(15, 18),
                // (17,18): warning CS20077: The argument 'v' may not satisfy the refinement 'is >= 1' of parameter 'i': its refinement 'is >= 0' allows 0.
                //         Positive(v);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentNotProven, "v").WithArguments("v", "i", ">= 1", ">= 0", "0").WithLocation(17, 18),
                // (19,14): error CS20076: The argument 'Sign(x)' may not satisfy the refinement 'is >= 0' of parameter 'i': its inferred refinement 'is -1 or 0 or 1' allows -1.
                //         Page(Sign(x));
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "Sign(x)").WithArguments("Sign(x)", "i", ">= 0", "-1 or 0 or 1", "-1").WithLocation(19, 14),
                // (21,14): error CS20076: The argument 's' may not satisfy the refinement 'is >= 0' of parameter 'i': its inferred refinement 'is -1 or 0 or 1' allows -1.
                //         Page(s);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "s").WithArguments("s", "i", ">= 0", "-1 or 0 or 1", "-1").WithLocation(21, 14),
                // (22,18): error CS20076: The argument 'Same(k)' may not satisfy the refinement 'is >= 1' of parameter 'i': its inferred refinement 'is >= 0' allows 0.
                //         Positive(Same(k));
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "Same(k)").WithArguments("Same(k)", "i", ">= 1", ">= 0", "0").WithLocation(22, 18),
                // (24,14): error CS20076: The argument 'c ? 1 : -1' may not satisfy the refinement 'is >= 0' of parameter 'i': its inferred refinement 'is -1 or 1' allows -1.
                //         Page(c ? 1 : -1);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "c ? 1 : -1").WithArguments("c ? 1 : -1", "i", ">= 0", "-1 or 1", "-1").WithLocation(24, 14),
                // (25,14): error CS20076: The argument 'c ? j : n' may not satisfy the refinement 'is >= 0' of parameter 'i': its inferred refinement 'is >= 1 or < 0' allows -1.
                //         Page(c ? j : n);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "c ? j : n").WithArguments("c ? j : n", "i", ">= 0", ">= 1 or < 0", "-1").WithLocation(25, 14),
                // (27,14): warning CS20078: The compiler cannot prove that the argument 'x' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         Page(x);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "x").WithArguments("x", "i", ">= 0").WithLocation(27, 14),
                // (28,14): warning CS20078: The compiler cannot prove that the argument 'Plain(x)' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         Page(Plain(x));
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "Plain(x)").WithArguments("Plain(x)", "i", ">= 0").WithLocation(28, 14),
                // (29,14): warning CS20078: The compiler cannot prove that the argument 'x + 1' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         Page(x + 1);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "x + 1").WithArguments("x + 1", "i", ">= 0").WithLocation(29, 14));
        }

        [Fact]
        public void RefinementCallSite_WidenedReturnBreaksCaller()
        {
            // The caller compiles against the first body; widening it makes the call an error, as a changed return type
            // would. Narrowing it keeps the call proven.
            var caller = """
                class Caller
                {
                    static void Page(int i is >=0) { }
                    static void M(int x) => Page(Lib.Kind(x));
                }
                """;
            string lib(string body) => "static class Lib { internal static infer Kind(int x) => " + body + "; }";

            CreateCompilation([caller, lib("x switch { >0 => 1, _ => 0 }")], parseOptions: s_sharperOptions).VerifyDiagnostics();
            CreateCompilation([caller, lib("x switch { >0 => 1, _ => -1 }")], parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (4,34): error CS20076: The argument 'Lib.Kind(x)' may not satisfy the refinement 'is >= 0' of parameter 'i': its inferred refinement 'is -1 or 1' allows -1.
                //     static void M(int x) => Page(Lib.Kind(x));
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "Lib.Kind(x)").WithArguments("Lib.Kind(x)", "i", ">= 0", "-1 or 1", "-1").WithLocation(4, 34));
            CreateCompilation([caller, lib("x switch { _ => 1 }")], parseOptions: s_sharperOptions).VerifyDiagnostics();
        }

        [Fact]
        public void RefinementCallSite_Routes()
        {
            // Every call the binder resolves is checked: constructors and their initializers, primary constructors,
            // indexers, named and optional arguments, local functions, extension methods and extension block members,
            // attributes. Method group conversions and dynamic calls are not: the guard checks them.
            var source = """
                using System;

                class C
                {
                    public C(int i is >=0) { }
                    public C() : this(-1) { }
                    public int this[int i is >=0] { get => i; set { } }
                    public static void Named(int a is >=0, int b is <0 = -1) { }
                }

                class P(int x is >=0) { public int X => x; }
                class D() : P(-2) { }
                class E : C { public E() : base(-3) { } }

                class A : Attribute { public A(int i is >=0) { } }

                static class Ext
                {
                    public static void Old(this int i is >=0) { }
                    extension(string s)
                    {
                        public void New(int i is >=0) { }
                        public static void StaticNew(int i is >=0) { }
                    }
                }

                class M
                {
                    [A(-4)]
                    static void Run(dynamic d)
                    {
                        _ = new C(-5);
                        C c = new(-6);
                        _ = c[-7];
                        c[-8] = 0;
                        C.Named(b: 1, a: -9);
                        C.Named(0);
                        Local(-10);
                        (-11).Old();
                        Ext.Old(-12);
                        "".New(-13);
                        string.StaticNew(-14);
                        Ext.New("", -15);
                        Action<int> f = Local;
                        Func<int, int> g = static i => i;
                        C.Named(d);
                        _ = new P(d);

                        void Local(int i is >=0) { }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,23): error CS20075: The argument '-1' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -1', are all outside it.
                //     public C() : this(-1) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-1").WithArguments("-1", "i", ">= 0", "-1").WithLocation(6, 23),
                // (12,15): error CS20075: The argument '-2' never satisfies the refinement 'is >= 0' of parameter 'x': its values, 'is -2', are all outside it.
                // class D() : P(-2) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-2").WithArguments("-2", "x", ">= 0", "-2").WithLocation(12, 15),
                // (13,33): error CS20075: The argument '-3' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -3', are all outside it.
                // class E : C { public E() : base(-3) { } }
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-3").WithArguments("-3", "i", ">= 0", "-3").WithLocation(13, 33),
                // (29,8): error CS20075: The argument '-4' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -4', are all outside it.
                //     [A(-4)]
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-4").WithArguments("-4", "i", ">= 0", "-4").WithLocation(29, 8),
                // (32,19): error CS20075: The argument '-5' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -5', are all outside it.
                //         _ = new C(-5);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-5").WithArguments("-5", "i", ">= 0", "-5").WithLocation(32, 19),
                // (33,19): error CS20075: The argument '-6' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -6', are all outside it.
                //         C c = new(-6);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-6").WithArguments("-6", "i", ">= 0", "-6").WithLocation(33, 19),
                // (34,15): error CS20075: The argument '-7' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -7', are all outside it.
                //         _ = c[-7];
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-7").WithArguments("-7", "i", ">= 0", "-7").WithLocation(34, 15),
                // (35,11): error CS20075: The argument '-8' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -8', are all outside it.
                //         c[-8] = 0;
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-8").WithArguments("-8", "i", ">= 0", "-8").WithLocation(35, 11),
                // (36,20): error CS20075: The argument '1' never satisfies the refinement 'is < 0' of parameter 'b': its values, 'is 1', are all outside it.
                //         C.Named(b: 1, a: -9);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "1").WithArguments("1", "b", "< 0", "1").WithLocation(36, 20),
                // (36,26): error CS20075: The argument '-9' never satisfies the refinement 'is >= 0' of parameter 'a': its values, 'is -9', are all outside it.
                //         C.Named(b: 1, a: -9);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-9").WithArguments("-9", "a", ">= 0", "-9").WithLocation(36, 26),
                // (38,15): error CS20075: The argument '-10' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -10', are all outside it.
                //         Local(-10);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-10").WithArguments("-10", "i", ">= 0", "-10").WithLocation(38, 15),
                // (39,10): error CS20075: The argument '-11' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -11', are all outside it.
                //         (-11).Old();
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-11").WithArguments("-11", "i", ">= 0", "-11").WithLocation(39, 10),
                // (40,17): error CS20075: The argument '-12' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -12', are all outside it.
                //         Ext.Old(-12);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-12").WithArguments("-12", "i", ">= 0", "-12").WithLocation(40, 17),
                // (41,16): error CS20075: The argument '-13' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -13', are all outside it.
                //         "".New(-13);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-13").WithArguments("-13", "i", ">= 0", "-13").WithLocation(41, 16),
                // (42,26): error CS20075: The argument '-14' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -14', are all outside it.
                //         string.StaticNew(-14);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-14").WithArguments("-14", "i", ">= 0", "-14").WithLocation(42, 26),
                // (43,21): error CS20075: The argument '-15' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -15', are all outside it.
                //         Ext.New("", -15);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-15").WithArguments("-15", "i", ">= 0", "-15").WithLocation(43, 21));
        }

        [Fact]
        public void RefinementCallSite_Metadata()
        {
            // A Sharper caller checks a metadata method against its SharperRefinementAttribute, and its inferred return;
            // a C# caller, or a Sharper caller with the flag off, sees the plain types and meets the guard.
            var library = """
                public static class Lib
                {
                    public static void Page(int i is >=0) { }
                    public static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                }
                """;
            var caller = """
                class C
                {
                    static void M(int x)
                    {
                        Lib.Page(1);
                        Lib.Page(-1);
                        Lib.Page(x);
                        Lib.Page(Lib.Sign(x));
                        _ = Lib.Sign(x) switch { 0 => 1, 1 => 2 };
                    }
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions);
            libraryComp.VerifyDiagnostics();

            foreach (var reference in new[] { libraryComp.ToMetadataReference(), libraryComp.EmitToImageReference() })
            {
                CreateCompilation(caller, references: [reference], parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (6,18): error CS20075: The argument '-1' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -1', are all outside it.
                //         Lib.Page(-1);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-1").WithArguments("-1", "i", ">= 0", "-1").WithLocation(6, 18),
                // (7,18): warning CS20078: The compiler cannot prove that the argument 'x' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         Lib.Page(x);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "x").WithArguments("x", "i", ">= 0").WithLocation(7, 18),
                // (8,18): error CS20076: The argument 'Lib.Sign(x)' may not satisfy the refinement 'is >= 0' of parameter 'i': its inferred refinement 'is -1 or 0 or 1' allows -1.
                //         Lib.Page(Lib.Sign(x));
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentNotProven, "Lib.Sign(x)").WithArguments("Lib.Sign(x)", "i", ">= 0", "-1 or 0 or 1", "-1").WithLocation(8, 18),
                // (9,25): error CS20079: The switch expression does not handle all values of its input's inferred refinement 'is -1 or 0 or 1'. For example, the pattern '-1' is not covered.
                //         _ = Lib.Sign(x) switch { 0 => 1, 1 => 2 };
                Diagnostic(ErrorCode.ERR_SharperSwitchNotExhaustiveForRefinement, "switch").WithArguments("-1 or 0 or 1", "-1").WithLocation(9, 25));
                CreateCompilation(caller, references: [reference], parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (9,25): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '2' is not covered.
                //         _ = Lib.Sign(x) switch { 0 => 1, 1 => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("2").WithLocation(9, 25));
            }
        }

        [Fact]
        public void RefinementCallSite_MixedCompilation()
        {
            // Only Sharper files are checked: a C# file of the same compilation calls a refined method as C# does.
            var sharper = """
                class Lib
                {
                    public static void Page(int i is >=0) { }
                    static void M() => Page(-1);
                }
                """;
            var csharp = """
                class CSharpCaller
                {
                    static void M(int x) { Lib.Page(-1); Lib.Page(x); }
                }
                """;
            CreateCompilation([
                SyntaxFactory.ParseSyntaxTree(sharper, s_sharperOptions),
                SyntaxFactory.ParseSyntaxTree(csharp, TestOptions.Regular)]).VerifyDiagnostics(
                // (4,29): error CS20075: The argument '-1' never satisfies the refinement 'is >= 0' of parameter 'i': its values, 'is -1', are all outside it.
                //     static void M() => Page(-1);
                Diagnostic(ErrorCode.ERR_SharperRefinementArgumentDisproven, "-1").WithArguments("-1", "i", ">= 0", "-1").WithLocation(4, 29));
        }

        [Fact]
        public void RefinementCallSite_Runtime()
        {
            // Proven and unproven calls run as the C# twin does; an unproven one meets the guard.
            var source = """
                using System;

                class C
                {
                    static int Page(int i is >=0) => i * 10;
                    static int Page_CSharp(int i) => i * 10;
                    static infer Kind(int x) => x switch { >0 => 1, _ => 0 };

                    static void Main()
                    {
                        Console.WriteLine(Page(3) + " " + Page_CSharp(3));
                        Console.WriteLine(Page(Kind(-5)) + " " + Page_CSharp(Kind(-5)));
                        int x = -1;
                        try { Page(x); } catch (ArgumentOutOfRangeException e) { Console.WriteLine(e.ParamName); }
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                30 30
                0 0
                i
                """ : null).VerifyDiagnostics(
                // (14,20): warning CS20078: The compiler cannot prove that the argument 'x' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //         try { Page(x); } catch (ArgumentOutOfRangeException e) { Console.WriteLine(e.ParamName); }
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "x").WithArguments("x", "i", ">= 0").WithLocation(14, 20));
        }

        [Fact]
        public void RefinementSwitch_Fib()
        {
            // Fib's switch needs no '_' arm: the refinement makes it exhaustive. The C# twin warns (CS8509) and its
            // recursive calls are not checked; Sharper's unproven arguments warn until slice 2 narrows the arm.
            var source = """
                using System;

                class C
                {
                    static int Fib(int i is >=0) => i switch { 0 or 1 => 1, >=2 => Fib(i - 1) + Fib(i - 2) };
                    static int Fib_CSharp(int i) => i switch { 0 or 1 => 1, >=2 => Fib_CSharp(i - 1) + Fib_CSharp(i - 2) };

                    static int Statement(int i is >=0)
                    {
                        switch (i)
                        {
                            case 0: return 1;
                            case >0: return 2;
                        }
                    }

                    static void Main() => Console.WriteLine(Fib(10) + " " + Fib_CSharp(10) + " " + Statement(4));
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "89 89 2" : null);
            verifier.VerifyDiagnostics(
                // (5,72): warning CS20078: The compiler cannot prove that the argument 'i - 1' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //     static int Fib(int i is >=0) => i switch { 0 or 1 => 1, >=2 => Fib(i - 1) + Fib(i - 2) };
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "i - 1").WithArguments("i - 1", "i", ">= 0").WithLocation(5, 72),
                // (5,85): warning CS20078: The compiler cannot prove that the argument 'i - 2' satisfies the refinement 'is >= 0' of parameter 'i': the argument has no refinement. The method checks it when it is called.
                //     static int Fib(int i is >=0) => i switch { 0 or 1 => 1, >=2 => Fib(i - 1) + Fib(i - 2) };
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "i - 2").WithArguments("i - 2", "i", ">= 0").WithLocation(5, 85),
                // (6,39): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '-1' is not covered.
                //     static int Fib_CSharp(int i) => i switch { 0 or 1 => 1, >=2 => Fib_CSharp(i - 1) + Fib_CSharp(i - 2) };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("-1").WithLocation(6, 39));

            // The lowered switch still tests the values the refinement excludes and throws for them, as the C# twin does,
            // so a broken promise (metadata, Edit and Continue) cannot pick a wrong arm.
            Assert.Contains("ThrowSwitchExpressionException", verifier.VisualizeIL("C.Fib"));
            verifier.VerifyIL("C.Statement", """
                {
                  // Code size       51 (0x33)
                  .maxstack  3
                  .locals init (int V_0)
                  IL_0000:  ldarg.0
                  IL_0001:  ldc.i4.0
                  IL_0002:  bge.s      IL_001a
                  IL_0004:  ldstr      "i"
                  IL_0009:  ldarg.0
                  IL_000a:  box        "int"
                  IL_000f:  ldstr      "The value does not satisfy the refinement 'is >= 0'."
                  IL_0014:  newobj     "System.ArgumentOutOfRangeException..ctor(string, object, string)"
                  IL_0019:  throw
                  IL_001a:  ldarg.0
                  IL_001b:  stloc.0
                  IL_001c:  ldloc.0
                  IL_001d:  ldc.i4.0
                  IL_001e:  bgt.s      IL_0025
                  IL_0020:  ldloc.0
                  IL_0021:  brtrue.s   IL_0027
                  IL_0023:  ldc.i4.1
                  IL_0024:  ret
                  IL_0025:  ldc.i4.2
                  IL_0026:  ret
                  IL_0027:  ldloc.0
                  IL_0028:  box        "int"
                  IL_002d:  newobj     "System.Runtime.CompilerServices.SwitchExpressionException..ctor(object)"
                  IL_0032:  throw
                }
                """);
        }

        [Fact]
        public void RefinementSwitch_Exhaustiveness()
        {
            // A missing value is named inside the domain. A written refinement keeps C#'s warning (CS8509); an inferred one
            // that a switch does not cover is an error, so that a widened return breaks the switch.
            var source = """
                enum Color { Red, Green, Blue }

                class C
                {
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };

                    static void M(int x, int i is >=0, Color k is Color.Red or Color.Green, string s is "a" or "b", string t is "a" or null)
                    {
                        _ = i switch { 0 => 1, 1 => 2 };
                        _ = x switch { 0 => 1, 1 => 2 };
                        _ = k switch { Color.Red => 1 };
                        _ = k switch { Color.Red => 1, Color.Green => 2 };
                        _ = s switch { "a" => 1, "b" => 2 };
                        _ = t switch { "a" => 1 };
                        _ = t switch { "a" => 1, null => 2 };
                        _ = Sign(x) switch { 0 => 1, 1 => 2 };
                        _ = Sign(x) switch { -1 => 0, 0 => 1, 1 => 2 };
                        val v = Sign(x);
                        _ = v switch { >=0 => 1 };
                        _ = (x > 0 ? 1 : 2) switch { 1 => 0 };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (9,15): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '2' is not covered.
                //         _ = i switch { 0 => 1, 1 => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("2").WithLocation(9, 15),
                // (10,15): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '2' is not covered.
                //         _ = x switch { 0 => 1, 1 => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("2").WithLocation(10, 15),
                // (11,15): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern 'Color.Green' is not covered.
                //         _ = k switch { Color.Red => 1 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("Color.Green").WithLocation(11, 15),
                // (14,15): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '""' is not covered.
                //         _ = t switch { "a" => 1 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments(@"""""").WithLocation(14, 15),
                // (15,15): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '""' is not covered.
                //         _ = t switch { "a" => 1, null => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments(@"""""").WithLocation(15, 15),
                // (16,21): error CS20079: The switch expression does not handle all values of its input's inferred refinement 'is -1 or 0 or 1'. For example, the pattern '-1' is not covered.
                //         _ = Sign(x) switch { 0 => 1, 1 => 2 };
                Diagnostic(ErrorCode.ERR_SharperSwitchNotExhaustiveForRefinement, "switch").WithArguments("-1 or 0 or 1", "-1").WithLocation(16, 21),
                // (19,15): error CS20079: The switch expression does not handle all values of its input's inferred refinement 'is -1 or 0 or 1'. For example, the pattern '-1' is not covered.
                //         _ = v switch { >=0 => 1 };
                Diagnostic(ErrorCode.ERR_SharperSwitchNotExhaustiveForRefinement, "switch").WithArguments("-1 or 0 or 1", "-1").WithLocation(19, 15),
                // (20,29): error CS20079: The switch expression does not handle all values of its input's inferred refinement 'is 1 or 2'. For example, the pattern '2' is not covered.
                //         _ = (x > 0 ? 1 : 2) switch { 1 => 0 };
                Diagnostic(ErrorCode.ERR_SharperSwitchNotExhaustiveForRefinement, "switch").WithArguments("1 or 2", "2").WithLocation(20, 29));
        }

        [Fact]
        public void RefinementSwitch_UnreachableArms()
        {
            // An arm excluded only by the refinement is a warning; an arm that earlier arms subsume is still C#'s error.
            // A narrowed inferred refinement only warns.
            var source = """
                class C
                {
                    static infer Kind(int x) => x switch { >0 => 1, _ => 1 };

                    static void M(int x, int i is >=0)
                    {
                        _ = i switch { <0 => 0, 0 => 1, _ => 2 };
                        _ = i switch { 0 => 1, 0 => 2, _ => 3 };
                        _ = x switch { <0 => 0, 0 => 1, _ => 2 };
                        _ = Kind(x) switch { 0 => 0, 1 => 1 };
                        _ = i switch { _ => 1 };
                        switch (i)
                        {
                            case -1: break;
                            case < -5: break;
                            case 0: break;
                            case 0: break;
                            default: break;
                        }
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (7,24): warning CS20080: The pattern is unreachable: the input's refinement 'is >= 0' excludes every value it matches.
                //         _ = i switch { <0 => 0, 0 => 1, _ => 2 };
                Diagnostic(ErrorCode.WRN_SharperSwitchArmExcludedByRefinement, "<0").WithArguments(">= 0").WithLocation(7, 24),
                // (8,32): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //         _ = i switch { 0 => 1, 0 => 2, _ => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "0").WithLocation(8, 32),
                // (10,30): warning CS20080: The pattern is unreachable: the input's refinement 'is 1' excludes every value it matches.
                //         _ = Kind(x) switch { 0 => 0, 1 => 1 };
                Diagnostic(ErrorCode.WRN_SharperSwitchArmExcludedByRefinement, "0").WithArguments("1").WithLocation(10, 30),
                // (14,18): warning CS20080: The pattern is unreachable: the input's refinement 'is >= 0' excludes every value it matches.
                //             case -1: break;
                Diagnostic(ErrorCode.WRN_SharperSwitchArmExcludedByRefinement, "-1").WithArguments(">= 0").WithLocation(14, 18),
                // (14,22): warning CS0162: Unreachable code detected
                //             case -1: break;
                Diagnostic(ErrorCode.WRN_UnreachableCode, "break").WithLocation(14, 22),
                // (15,18): warning CS20080: The pattern is unreachable: the input's refinement 'is >= 0' excludes every value it matches.
                //             case < -5: break;
                Diagnostic(ErrorCode.WRN_SharperSwitchArmExcludedByRefinement, "< -5").WithArguments(">= 0").WithLocation(15, 18),
                // (15,24): warning CS0162: Unreachable code detected
                //             case < -5: break;
                Diagnostic(ErrorCode.WRN_UnreachableCode, "break").WithLocation(15, 24),
                // (17,13): error CS0152: The switch statement contains multiple cases with the label value '0'
                //             case 0: break;
                Diagnostic(ErrorCode.ERR_DuplicateCaseLabel, "case 0:").WithArguments("0").WithLocation(17, 13));
        }

        [Fact]
        public void RefinementCallSite_FlagOff()
        {
            // Without the flag, nothing is refined and nothing is checked; refinements are a syntax error (step 1).
            var source = """
                class C
                {
                    static int Page(int i) => i switch { 0 or 1 => 1, >=2 => 2 };
                    static void M() { Page(-1); _ = -1 switch { <0 => 0, _ => 1 }; }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (3,33): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '-1' is not covered.
                //     static int Page(int i) => i switch { 0 or 1 => 1, >=2 => 2 };
                Diagnostic(ErrorCode.WRN_SwitchExpressionNotExhaustive, "switch").WithArguments("-1").WithLocation(3, 33));
        }

        [Fact]
        public void RefinementCallSite_Nullable()
        {
            // Null state and refinements are separate: a refinement that matches null accepts a null constant, and the
            // nullable warnings stay those of the C# twin.
            var source = """
                #nullable enable
                class C
                {
                    static void NotX(string s is not "x") { }
                    static void NotX_CSharp(string s) { }
                    static void Maybe(string? s is not "x") { }

                    static void M(string? t)
                    {
                        NotX(null);
                        NotX_CSharp(null);
                        Maybe(null);
                        Maybe(t);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (10,14): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         NotX(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(10, 14),
                // (11,21): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         NotX_CSharp(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(11, 21),
                // (13,15): warning CS20078: The compiler cannot prove that the argument 't' satisfies the refinement 'is not "x"' of parameter 's': the argument has no refinement. The method checks it when it is called.
                //         Maybe(t);
                Diagnostic(ErrorCode.WRN_SharperRefinementArgumentUnknown, "t").WithArguments("t", "s", @"not ""x""").WithLocation(13, 15));
        }
    }
}
