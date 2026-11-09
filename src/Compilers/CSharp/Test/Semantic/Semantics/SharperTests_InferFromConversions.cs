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
    /// Sharper's inferred parameter types, step P7 (issue #78): an <c>infer</c> parameter of a local function takes its
    /// type from the delegate types the local function is converted to, as a lambda's parameter would.
    /// </summary>
    public sealed partial class SharperTests
    {
        /// <summary>The parameter types of each local function named in <paramref name="expected"/>, as the semantic model sees them.</summary>
        private static void VerifyLocalFunctionParameterTypes(Compilation comp, params (string Name, string Types)[] expected)
        {
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            foreach (var (name, types) in expected)
            {
                var declaration = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(l => l.Identifier.ValueText == name);
                var symbol = (IMethodSymbol)model.GetDeclaredSymbol(declaration)!;
                Assert.Equal(types, string.Join(", ", symbol.Parameters.Select(p => (p.RefKind == RefKind.Ref ? "ref " : "") + p.Type.ToTestDisplayString(includeNonNullable: false))));
            }
        }

        [Fact]
        public void InferFromConversions_Basic()
        {
            // The issue's example and every kind of conversion, next to the C# twin with the types written out.
            var source = """
                using System;
                using System.Linq;

                delegate void Handler(string message, int code);
                delegate void RefAction(ref int value);

                class C
                {
                    static void Run(Func<int, string> f) => Console.Write(f(1));
                    static void Apply<T>(Func<T, string> f, T value) => Console.Write(f(value));
                    static void Handle(Handler h) => h("h", 2);
                    static void Arrow((long) -> long f) => Console.Write(f(3));

                    static void Main()
                    {
                        int[] xs = [1, 2];

                        // TResult comes from the inferred return type of Format, once x is int.
                        infer Format(infer x) => $"<{x}>";
                        infer FormatCSharp(int x) => $"<{x}>";
                        Console.Write(string.Join("", xs.Select(Format)) + string.Join("", xs.Select(FormatCSharp)) + ";");

                        // A conversion before the declaration.
                        Run(Before); Run(BeforeCSharp); Console.Write(";");
                        string Before(infer x) => (x + 1).ToString();
                        string BeforeCSharp(int x) => (x + 1).ToString();

                        Func<double, double> half = Half, halfCSharp = HalfCSharp;
                        double Half(infer d) => d / 2;
                        double HalfCSharp(double d) => d / 2;
                        Console.Write(half(5) + halfCSharp(5) + ";");

                        Apply(Upper, "u"); Apply(UpperCSharp, "v"); Console.Write(";");
                        string Upper(infer s) => s.ToUpper();
                        string UpperCSharp(string s) => s.ToUpper();

                        Handle(Log); Handle(LogCSharp); Console.Write(";");
                        void Log(infer message, infer code) => Console.Write(message + code);
                        void LogCSharp(string message, int code) => Console.Write(message + code);

                        Arrow(Twice); Arrow(TwiceCSharp); Console.Write(";");
                        long Twice(infer v) => v * 2;
                        long TwiceCSharp(long v) => v * 2;

                        Func<int, string, string> pad = Pad, padCSharp = PadCSharp;
                        string Pad(int n, infer s) => s.PadLeft(n);
                        string PadCSharp(int n, string s) => s.PadLeft(n);
                        Console.Write(pad(2, "p") + padCSharp(2, "q") + ";");

                        int i = 1;
                        RefAction bump = Bump, bumpCSharp = BumpCSharp;
                        void Bump(ref infer value) => value++;
                        void BumpCSharp(ref int value) => value++;
                        bump(ref i); bumpCSharp(ref i);
                        Console.Write(i + ";");

                        // Several conversions that agree, one of them cast.
                        Action<string> print = Print;
                        var printAgain = (Action<string>)Print;
                        void Print(infer s) => Console.Write(s);
                        print("a"); printAgain("b"); Console.Write(";");

                        // In a lambda, a local function and an accessor.
                        Func<int> outer = () => { Func<int, int> f = Square; return f(3); int Square(infer x) => x * x; };
                        Console.Write(outer() + ";" + Nested() + ";" + new C().Property);
                        int Nested() { Func<int, int> f = Inc; return f(4); int Inc(infer x) => x + 1; }
                    }

                    int Property { get { Func<int, int> f = Dec; return f(10); int Dec(infer x) => x - 1; } }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "<1><2><1><2>;22;5;UV;h2h2;66; p q;3;ab;9;5;9" : null);
            verifier.VerifyDiagnostics();
            VerifyLocalFunctionParameterTypes(verifier.Compilation,
                ("Format", "System.Int32"), ("Before", "System.Int32"), ("Half", "System.Double"), ("Upper", "System.String"),
                ("Log", "System.String, System.Int32"), ("Twice", "System.Int64"), ("Pad", "System.Int32, System.String"),
                ("Bump", "ref System.Int32"), ("Print", "System.String"), ("Square", "System.Int32"), ("Inc", "System.Int32"),
                ("Dec", "System.Int32"));

            // The semantic model sees the type on the keyword, as for var.
            var tree = verifier.Compilation.SyntaxTrees.Single();
            var model = verifier.Compilation.GetSemanticModel(tree);
            var infer = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(l => l.Identifier.ValueText == "Format").ParameterList.Parameters[0].Type!;
            Assert.Equal("System.Int32", model.GetTypeInfo(infer).Type.ToTestDisplayString());
        }

        [Fact]
        public void InferFromConversions_TopLevelStatements()
        {
            var source = """
                using System;
                using System.Linq;

                int[] xs = [1, 2];
                Console.Write(string.Join(",", xs.Select(Show)));
                Func<string, int> length = Length;
                Console.Write(";" + length("abc"));

                string Show(infer x) => (x * 10).ToString();
                int Length(infer s) => s.Length;
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "10,20;3" : null);
            verifier.VerifyDiagnostics();
            VerifyLocalFunctionParameterTypes(verifier.Compilation, ("Show", "System.Int32"), ("Length", "System.String"));
        }

        [Fact]
        public void InferFromConversions_Disagree()
        {
            // Every conversion must agree; CS20057 names two delegate types that differ, also those of an overloaded call.
            var source = """
                using System;

                class C
                {
                    static void Run(Func<int, string> f) { }
                    static void Run(Func<long, string> f) { }

                    void Two()
                    {
                        Func<int, string> f = F;
                        Func<long, string> g = F;
                        string F(infer x) => "";
                    }

                    void Overloads()
                    {
                        Run(G);
                        string G(infer x) => "";
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (10,31): error CS0123: No overload for 'F' matches delegate 'Func<int, string>'
                //         Func<int, string> f = F;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "F").WithArguments("F", "System.Func<int, string>").WithLocation(10, 31),
                // (11,32): error CS0123: No overload for 'F' matches delegate 'Func<long, string>'
                //         Func<long, string> g = F;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "F").WithArguments("F", "System.Func<long, string>").WithLocation(11, 32),
                // (12,18): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'the conversion to System.Func<int, string>' and 'long' for 'the conversion to System.Func<long, string>'. Write the parameter type.
                //         string F(infer x) => "";
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "the conversion to System.Func<int, string>", "long", "the conversion to System.Func<long, string>").WithLocation(12, 18),
                // (17,13): error CS1503: Argument 1: cannot convert from 'method group' to 'System.Func<int, string>'
                //         Run(G);
                Diagnostic(ErrorCode.ERR_BadArgType, "G").WithArguments("1", "method group", "System.Func<int, string>").WithLocation(17, 13),
                // (18,18): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'the conversion to System.Func<int, string>' and 'long' for 'the conversion to System.Func<long, string>'. Write the parameter type.
                //         string G(infer x) => "";
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "the conversion to System.Func<int, string>", "long", "the conversion to System.Func<long, string>").WithLocation(18, 18));
        }

        [Fact]
        public void InferFromConversions_PinsNothing()
        {
            // A conversion to a type with no known parameter types, a call, a conversion that does not fit the signature, and
            // members (inference from callers) pin nothing.
            var source = """
                using System;

                class C
                {
                    static void Apply<T>(Func<T, string> f) { }

                    void Natural() { var f = F; string F(infer x) => ""; }
                    void ToDelegate() { Delegate d = F; string F(infer x) => ""; }
                    void Called() { F(1); string F(infer x) => ""; }
                    void Count() { Func<int, int, string> f = F; string F(infer x) => ""; }
                    void RefKind() { Func<int, string> f = F; string F(ref infer x) => ""; }
                    void Written() { Func<int, string, string> f = F; string F(string s, infer x) => ""; }
                    void Unfixed() { Apply(F); string F(infer x) => ""; }
                    void Lambda() { Func<int, string> f = (infer x) => ""; }

                    static string Member(infer x) => "";
                    void UsesMember() { Func<int, string> f = Member; }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (7,42): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void Natural() { var f = F; string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(7, 42),
                // (8,50): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void ToDelegate() { Delegate d = F; string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(8, 50),
                // (9,23): error CS1503: Argument 1: cannot convert from 'int' to 'infer'
                //     void Called() { F(1); string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_BadArgType, "1").WithArguments("1", "int", "infer").WithLocation(9, 23),
                // (9,36): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void Called() { F(1); string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(9, 36),
                // (10,47): error CS0123: No overload for 'F' matches delegate 'Func<int, int, string>'
                //     void Count() { Func<int, int, string> f = F; string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "F").WithArguments("F", "System.Func<int, int, string>").WithLocation(10, 47),
                // (10,59): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void Count() { Func<int, int, string> f = F; string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(10, 59),
                // (11,44): error CS0123: No overload for 'F' matches delegate 'Func<int, string>'
                //     void RefKind() { Func<int, string> f = F; string F(ref infer x) => ""; }
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "F").WithArguments("F", "System.Func<int, string>").WithLocation(11, 44),
                // (11,60): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void RefKind() { Func<int, string> f = F; string F(ref infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(11, 60),
                // (12,52): error CS0123: No overload for 'F' matches delegate 'Func<int, string, string>'
                //     void Written() { Func<int, string, string> f = F; string F(string s, infer x) => ""; }
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "F").WithArguments("F", "System.Func<int, string, string>").WithLocation(12, 52),
                // (12,74): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void Written() { Func<int, string, string> f = F; string F(string s, infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(12, 74),
                // (13,22): error CS0411: The type arguments for method 'C.Apply<T>(Func<T, string>)' cannot be inferred from the usage. Try specifying the type arguments explicitly.
                //     void Unfixed() { Apply(F); string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_CantInferMethTypeArgs, "Apply").WithArguments("C.Apply<T>(System.Func<T, string>)").WithLocation(13, 22),
                // (13,41): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void Unfixed() { Apply(F); string F(infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(13, 41),
                // (14,44): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     void Lambda() { Func<int, string> f = (infer x) => ""; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(14, 44),
                // (16,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     static string Member(infer x) => "";
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(16, 26),
                // (17,47): error CS0123: No overload for 'Member' matches delegate 'Func<int, string>'
                //     void UsesMember() { Func<int, string> f = Member; }
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "Member").WithArguments("Member", "System.Func<int, string>").WithLocation(17, 47));
        }

        [Fact]
        public void InferFromConversions_Precedence()
        {
            // A default value comes first (P1), and a conversion that disagrees with it reports the C# error of the twin.
            // A local function in a body that infers the member's own parameter types (P4) is inferred as well.
            var source = """
                using System;

                class C
                {
                    static void Take(int x) { }

                    void Default()
                    {
                        Func<long, string> f = F;
                        string F(infer x = 1) => "";
                    }

                    void Outer(infer y)
                    {
                        Take(y);
                        Func<string, int> f = L;
                        int L(infer s) => s.Length;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (9,32): error CS0123: No overload for 'F' matches delegate 'Func<long, string>'
                //         Func<long, string> f = F;
                Diagnostic(ErrorCode.ERR_MethDelegateMismatch, "F").WithArguments("F", "System.Func<long, string>").WithLocation(9, 32));
            VerifyLocalFunctionParameterTypes(comp, ("F", "System.Int32"), ("L", "System.String"));
            Assert.Equal("System.Int32", comp.GetMember<MethodSymbol>("C.Outer").Parameters[0].Type.ToTestDisplayString());
        }

        [Fact]
        public void InferFromConversions_Nullable()
        {
            // The delegate's arguments flow into the parameter: a maybe-null one makes it annotated, as for a lambda.
            var source = """
                #nullable enable
                using System;

                class C
                {
                    void M()
                    {
                        Func<string?, int> f = L;
                        Func<string, int> g = L;
                        int L(infer s) => s.Length;

                        Func<string, int> h = N;
                        int N(infer s) => s.Length;
                    }

                    void CSharp()
                    {
                        Func<string?, int> f = L;
                        Func<string, int> g = L;
                        int L(string? s) => s.Length;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (10,27): warning CS8602: Dereference of a possibly null reference.
                //         int L(infer s) => s.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(10, 27),
                // (20,29): warning CS8602: Dereference of a possibly null reference.
                //         int L(string? s) => s.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(20, 29));
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var functions = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToArray();
            Assert.Equal("System.String?", ((IMethodSymbol)model.GetDeclaredSymbol(functions[0])!).Parameters[0].Type.ToTestDisplayString(includeNonNullable: true));
            Assert.Equal("System.String!", ((IMethodSymbol)model.GetDeclaredSymbol(functions[1])!).Parameters[0].Type.ToTestDisplayString(includeNonNullable: true));
        }
    }
}
