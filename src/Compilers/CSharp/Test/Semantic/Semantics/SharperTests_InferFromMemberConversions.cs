// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's inferred parameter types, step P7b (issue #207): an <c>infer</c> parameter of a private method takes its
    /// type from the delegate types the method is converted to anywhere in its type, as a local function's does (P7).
    /// </summary>
    public sealed partial class SharperTests
    {
        /// <summary>The signature of each method named in <paramref name="expected"/>.</summary>
        private static void VerifyMethodSignatures(CSharpCompilation comp, params string[] expected)
        {
            foreach (var signature in expected)
            {
                string name = signature.Substring(0, signature.IndexOf('('));
                name = name.Substring(name.LastIndexOf(' ') + 1);
                Assert.Equal(signature, comp.GetMember<MethodSymbol>(name).ToTestDisplayString());
            }
        }

        [Fact]
        public void InferFromMemberConversions_Basic()
        {
            // The issue's example and every place and kind of conversion, next to the C# twin with the types written out.
            var source = """
                using System;
                using System.Linq;

                delegate void Handler(string message, int code);
                delegate void RefAction(ref int value);

                class C
                {
                    static infer Format(infer x) => $"<{x}>";
                    static infer FormatCSharp(int x) => $"<{x}>";
                    string[] Run(int[] xs) => xs.Select(Format).ToArray();
                    string[] RunCSharp(int[] xs) => xs.Select(FormatCSharp).ToArray();

                    // A field initializer, a property initializer and a cast.
                    static readonly Func<double, double> s_half = Half, s_halfCSharp = HalfCSharp;
                    static double Half(infer d) => d / 2;
                    static double HalfCSharp(double d) => d / 2;
                    static Action<string> Printer { get; } = (Action<string>)Print;
                    static void Print(infer s) => Console.Write(s);

                    // A custom delegate in a constructor, a function type in a property getter.
                    C() { Handler h = Log; h("h", 2); Handler hCSharp = LogCSharp; hCSharp("h", 2); }
                    void Log(infer message, infer code) => Console.Write(message + code);
                    void LogCSharp(string message, int code) => Console.Write(message + code);
                    long Doubled { get { (long) -> long f = Twice; return f(3); } }
                    static long Twice(infer v) => v * 2;

                    // A written parameter, ref, a lambda, and a call next to the conversion.
                    static string Pad(int n, infer s) => s.PadLeft(n);
                    static void Bump(ref infer value) => value++;
                    static int Square(infer x) => x * x;
                    static int UseAll()
                    {
                        Func<int, string, string> pad = Pad;
                        int i = 1;
                        RefAction bump = Bump;
                        bump(ref i);
                        Func<int> square = () => { Func<int, int> f = Square; return f(3) + Square(1); };
                        return pad(3, "p").Length + i + square();
                    }

                    // A nested type converts its outer type's private method.
                    class Nested
                    {
                        internal static Func<string, int> Length = Measure;
                    }

                    static int Measure(infer s) => s.Length;

                    static void Main()
                    {
                        var c = new C();
                        Console.Write(";" + string.Join("", c.Run([1, 2])) + string.Join("", c.RunCSharp([1, 2])));
                        Console.Write(";" + s_half(5) + s_halfCSharp(5) + ";");
                        Printer("p");
                        Console.Write(";" + c.Doubled + ";" + UseAll() + ";" + Nested.Length("abc"));
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "h2h2;<1><2><1><2>;2.52.5;p;6;15;3" : null);
            verifier.VerifyDiagnostics();
            VerifyMethodSignatures((CSharpCompilation)verifier.Compilation,
                "System.String C.Format(System.Int32 x)",
                "System.Double C.Half(System.Double d)",
                "void C.Print(System.String s)",
                "void C.Log(System.String message, System.Int32 code)",
                "System.Int64 C.Twice(System.Int64 v)",
                "System.String C.Pad(System.Int32 n, System.String s)",
                "void C.Bump(ref System.Int32 value)",
                "System.Int32 C.Square(System.Int32 x)",
                "System.Int32 C.Measure(System.String s)");

            // The semantic model sees the type on the keyword.
            var tree = verifier.Compilation.SyntaxTrees.Single();
            var model = verifier.Compilation.GetSemanticModel(tree);
            var infer = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Format").ParameterList.Parameters[0].Type!;
            Assert.Equal("System.Int32", model.GetTypeInfo(infer).Type.ToTestDisplayString());
        }

        [Fact]
        public void InferFromMemberConversions_PartialAndNested()
        {
            // Other partial declarations, a nested type in another file, and inferred return types around the conversion.
            var source1 = """
                using System;
                using System.Linq;

                partial class C
                {
                    static infer Show(infer x) => (x * 10).ToString();
                    static infer Run(int[] xs) => string.Join(",", xs.Select(Show));
                    static void Main() => Console.Write(Run([1, 2]) + ";" + new Inner().Get() + ";" + Twice(4));
                }
                """;
            var source2 = """
                using System;

                partial class C
                {
                    partial class Inner
                    {
                        internal infer Get() { Func<string, int> f = Length; return f("abcd"); }
                    }

                    static int Length(infer s) => s.Length;
                    static readonly Func<int, int> s_twice = Twice;
                }

                partial class C
                {
                    partial class Inner { }
                    static infer Twice(infer x) => x * 2;
                }
                """;
            var verifier = CompileAndVerify([source1, source2], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "10,20;4;8" : null);
            verifier.VerifyDiagnostics();
            VerifyMethodSignatures((CSharpCompilation)verifier.Compilation,
                "System.String C.Show(System.Int32 x)",
                "System.Int32 C.Length(System.String s)",
                "System.Int32 C.Twice(System.Int32 x)");
        }

        [Fact]
        public void InferFromMemberConversions_Disagree()
        {
            // Every conversion must agree, in any body of the type.
            var source = """
                using System;

                class C
                {
                    static void Print(infer s) => Console.Write(s);
                    static Action<string> s_print = Print;
                    void M() { Action<int> p = Print; }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,23): error CS20057: Cannot infer the type of parameter 's', because its uses need different types: 'int' for 'the conversion to System.Action<int>' and 'string' for 'the conversion to System.Action<string>'. Write the parameter type.
                //     static void Print(infer s) => Console.Write(s);
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("s", "int", "the conversion to System.Action<int>", "string", "the conversion to System.Action<string>").WithLocation(5, 23),
                // (6,37): error CS0123: No overload for 'Print' matches delegate 'Action<string>'
                //     static Action<string> s_print = Print;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "Print").WithArguments("Print", "System.Action<string>").WithLocation(6, 37),
                // (7,32): error CS0123: No overload for 'Print' matches delegate 'Action<int>'
                //     void M() { Action<int> p = Print; }
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "Print").WithArguments("Print", "System.Action<int>").WithLocation(7, 32));
        }

        [Fact]
        public void InferFromMemberConversions_PinsNothing()
        {
            // Other members, calls and natural types pin nothing; the body still can.
            var source = """
                using System;

                class C
                {
                    public static void Public(infer x) { }
                    internal static void Internal(infer x) { }
                    static void Called(infer x) { }
                    static void Natural(infer x) { }
                    static void FromBody(infer x) => Take(x);
                    static void Take(int x) { }

                    void M()
                    {
                        Action<int> a = Public, b = Internal;
                        Called(1);
                        var n = Natural;
                        Delegate d = Natural;
                        var f = FromBody;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (5,31): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     public static void Public(infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(5, 31),
                // (6,35): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     internal static void Internal(infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(6, 35),
                // (7,24): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     static void Called(infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(7, 24),
                // (8,25): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     static void Natural(infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(8, 25),
                // (14,25): error CS0123: No overload for 'Public' matches delegate 'Action<int>'
                //         Action<int> a = Public, b = Internal;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "Public").WithArguments("Public", "System.Action<int>").WithLocation(14, 25),
                // (14,37): error CS0123: No overload for 'Internal' matches delegate 'Action<int>'
                //         Action<int> a = Public, b = Internal;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "Internal").WithArguments("Internal", "System.Action<int>").WithLocation(14, 37),
                // (15,16): error CS1503: Argument 1: cannot convert from 'int' to 'infer'
                //         Called(1);
                Diagnostic(ErrorCode.ERR_BadArgType, "1").WithArguments("1", "int", "infer").WithLocation(15, 16));
            Assert.Equal("void C.FromBody(System.Int32 x)", comp.GetMember<MethodSymbol>("C.FromBody").ToTestDisplayString());
        }

        [Fact]
        public void InferFromMemberConversions_Precedence()
        {
            // A default value comes first, then the conversions, then the body.
            var source = """
                using System;

                class C
                {
                    static void WithDefault(infer x = 1) { }
                    static void ConversionFirst(infer x) => TakeInt(x);
                    static void TakeInt(int x) { }
                    static void Run()
                    {
                        Action<string> a = WithDefault;
                        Action<string> b = ConversionFirst;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (6,53): error CS1503: Argument 1: cannot convert from 'string' to 'int'
                //     static void ConversionFirst(infer x) => TakeInt(x);
                Diagnostic(ErrorCode.ERR_BadArgType, "x").WithArguments("1", "string", "int").WithLocation(6, 53),
                // (10,28): error CS0123: No overload for 'WithDefault' matches delegate 'Action<string>'
                //         Action<string> a = WithDefault;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "WithDefault").WithArguments("WithDefault", "System.Action<string>").WithLocation(10, 28));
            VerifyMethodSignatures(comp, "void C.WithDefault([System.Int32 x = 1])", "void C.ConversionFirst(System.String x)");
        }

        [Fact]
        public void InferFromMemberConversions_Nullable()
        {
            var source = """
                #nullable enable
                using System;

                class C
                {
                    static int Length(infer s) => s?.Length ?? 0;
                    static Func<string?, int> s_maybe = Length;
                    static Func<string, int> s_sure = Length;
                    static void Show(infer s) => Console.Write(s.Length);
                    static Action<string> s_show = Show;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            Assert.Equal("System.Int32 C.Length(System.String? s)", comp.GetMember<MethodSymbol>("C.Length").ToTestDisplayString());
            Assert.Equal("void C.Show(System.String! s)", comp.GetMember<MethodSymbol>("C.Show").ToTestDisplayString(includeNonNullable: true));
        }

        [Fact]
        public void InferFromMemberConversions_Cycles()
        {
            // A body that converts the method and needs its signature through a member whose return type is inferred is a
            // cycle, whichever member is asked for first. Another private method converted in the same body is not.
            var source = """
                using System;
                using System.Linq;

                class C
                {
                    static infer Format(infer x) => $"<{x}>";
                    static infer Header() => Format(0);
                    static void Run(int[] xs) => Console.Write(Header() + string.Join("", xs.Select(Format)) + Twice(1));
                    static infer Twice(infer y) => y * 2;
                    static Func<int, int> s_twice = Twice;
                }
                """;
            string[] orders = ["Format", "Header", "Run", "Twice"];
            string? expected = null;
            foreach (var first in orders)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<MethodSymbol>("C." + first).ReturnType;
                var signatures = string.Join("\n", comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Select(m => m.ToTestDisplayString()));
                var diagnostics = string.Join("\n", comp.GetDiagnostics().Select(d => d.ToString()).OrderBy(d => d));
                var actual = signatures + "\n" + diagnostics;
                expected ??= actual;
                AssertEx.Equal(expected, actual);
            }

            var compilation = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            compilation.VerifyDiagnostics(
                // (6,25): error CS20063: Cannot infer the type of parameter 'x' from the conversions of 'Format', because binding the members that convert 'Format' needs the signature of 'Format', directly or through other members whose types are inferred. Write the parameter type.
                //     static infer Format(infer x) => $"<{x}>";
                Diagnostic(ErrorCode.ERR_SharperInferParameterConversionCycle, "infer").WithArguments("x", "Format").WithLocation(6, 25),
                // (7,37): error CS1503: Argument 1: cannot convert from 'int' to 'infer'
                //     static infer Header() => Format(0);
                Diagnostic(ErrorCode.ERR_BadArgType, "0").WithArguments("1", "int", "infer").WithLocation(7, 37),
                // (8,78): error CS0411: The type arguments for method 'Enumerable.Select<TSource, TResult>(IEnumerable<TSource>, Func<TSource, int, TResult>)' cannot be inferred from the usage. Try specifying the type arguments explicitly.
                //     static void Run(int[] xs) => Console.Write(Header() + string.Join("", xs.Select(Format)) + Twice(1));
                Diagnostic(ErrorCode.ERR_CantInferMethTypeArgs, "Select").WithArguments("System.Linq.Enumerable.Select<TSource, TResult>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, int, TResult>)").WithLocation(8, 78));
            Assert.Equal("System.Int32 C.Twice(System.Int32 y)", compilation.GetMember<MethodSymbol>("C.Twice").ToTestDisplayString());
        }

        [Fact]
        public void InferFromMemberConversions_Concurrent()
        {
            // Threads that start anywhere neither deadlock nor disagree.
            var source = """
                using System;
                using System.Linq;

                class C
                {
                    static infer A(infer x) => x + 1;
                    static infer B(infer s) => s.Length;
                    static infer Format(infer x) => $"<{x}>";
                    static infer Header() => Format(0);
                    static int Run(int[] xs, string[] ss) => xs.Select(A).Concat(ss.Select(B)).Sum() + Header().Length + xs.Select(Format).Count();
                }
                """;
            string? expected = null;
            for (int i = 0; i < 20; i++)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                var methods = new[] { "A", "B", "Format", "Header", "Run" }.Select(n => comp.GetMember<MethodSymbol>("C." + n)).ToArray();
                System.Threading.Tasks.Parallel.For(0, 10, t => _ = methods[t % methods.Length].ReturnType);
                var actual = string.Join("\n", methods.Select(m => m.ToTestDisplayString())) + "\n" +
                    string.Join("\n", comp.GetDiagnostics().Select(d => d.ToString()).OrderBy(d => d));
                expected ??= actual;
                AssertEx.Equal(expected, actual);
            }

            Assert.Contains("System.Int32 C.A(System.Int32 x)", expected);
            Assert.Contains("System.Int32 C.B(System.String s)", expected);
        }
    }
}
