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
    /// Sharper's <c>val x</c> on method parameters (issue #178): the shorthand for <c>readonly infer x</c>, as
    /// <c>(val x) =&gt; ...</c> is for <c>(readonly x) =&gt; ...</c>. A type named <c>val</c> in scope keeps its C# meaning.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void ValParameter_Sources()
        {
            // Every source of an inferred parameter type, next to its twin with the type written out.
            var source = """
                using System;

                var c = new C();
                Console.Write(c.Default() + ";");
                Console.Write(c.FromBody(4) + ";");
                Console.Write(c.FromAssignment("a") + ";");
                Console.Write(new Square(2).Area(1.5) + ";");
                Console.Write(c.Conversions() + ";");

                abstract class Shape { public abstract double Area(double scale); }

                class Square(double side) : Shape
                {
                    public override infer Area(val scale) => side * side * scale;
                }

                class C
                {
                    string _s = "";
                    static int Twice(int i) => i * 2;

                    public long Default(val x = 10L) => x + 1;
                    public int FromBody(val x) => Twice(x);
                    public string FromAssignment(val s) { _s = s; return _s; }
                    public int Conversions()
                    {
                        Func<int, int> f = Local;
                        Func<string, int> g = Private;
                        return f(3) + g("ab");

                        int Local(val i) => i + 1;
                    }

                    private int Private(val s) => s.Length;
                }

                class CCSharp
                {
                    string _s = "";
                    static int Twice(int i) => i * 2;

                    public long Default(readonly long x = 10L) => x + 1;
                    public int FromBody(readonly int x) => Twice(x);
                    public string FromAssignment(readonly string s) { _s = s; return _s; }
                    private int Private(readonly string s) => s.Length;
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "11;8;a;6;6;" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameSignature(comp, "C.Default", "CCSharp.Default");
            VerifySameSignature(comp, "C.FromBody", "CCSharp.FromBody");
            VerifySameSignature(comp, "C.FromAssignment", "CCSharp.FromAssignment");
            VerifySameSignature(comp, "C.Private", "CCSharp.Private");
            Assert.Equal("System.Double", comp.GetMember<MethodSymbol>("Square.Area").Parameters[0].Type.ToTestDisplayString());
            foreach (var name in new[] { "C.Default", "C.FromBody", "C.FromAssignment", "C.Private", "Square.Area" })
            {
                var parameter = (SourceComplexParameterSymbolBase)comp.GetMember<MethodSymbol>(name).Parameters[0];
                Assert.True(parameter.IsSharperVal);
                Assert.True(parameter.IsSharperReadOnly);
            }
        }

        [Fact]
        public void ValParameter_Writes()
        {
            // Writes report the readonly errors with the cause 'val variable', as for a val local or a (val x) lambda
            // parameter, also from lambdas and local functions; readonly val is val.
            var source = """
                class C
                {
                    static void Ref(ref int i) { }

                    void M(val x = 1, readonly val y = 2, val p = default(P))
                    {
                        x = 2;
                        x++;
                        Ref(ref x);
                        y = 3;
                        p.F = 1;
                        System.Action a = () => x = 3;
                        void Local() { x = 4; }
                    }
                }

                struct P { public int F; }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,9): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(7, 9),
                // (8,9): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(8, 9),
                // (9,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'val variable'
                //         Ref(ref x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(9, 17),
                // (10,9): error CS1656: Cannot assign to 'y' because it is a 'val variable'
                //         y = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "y").WithArguments("y", "val variable").WithLocation(10, 9),
                // (11,9): error CS1654: Cannot modify members of 'p' because it is a 'val variable'
                //         p.F = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "p.F").WithArguments("p", "val variable").WithLocation(11, 9),
                // (12,33): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         System.Action a = () => x = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(12, 33),
                // (13,14): warning CS8321: The local function 'Local' is declared but never used
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "Local").WithArguments("Local").WithLocation(13, 14),
                // (13,24): error CS1656: Cannot assign to 'x' because it is a 'val variable'
                //         void Local() { x = 4; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "val variable").WithLocation(13, 24));
        }

        [Fact]
        public void ValParameter_Ref()
        {
            // ref, in, out and scoped val, like (ref val x) in a lambda and readonly ref infer x: writes through the ref keep
            // their C# rules, and ref reassignment is CS20006. An override copies the ref kind it does not write.
            var source = """
                using System;

                int i = 1;
                new D().M(ref i, 5, out int o, ref i);
                Console.Write(i + ";" + o + ";");
                new E().M(ref i, 6, out o, ref i);
                Console.Write(i + ";" + o);

                class B { public virtual void M(ref int r, in int n, out int o, scoped ref int s) { o = 0; } }

                class D : B
                {
                    public override void M(ref val r, in val n, out val o, scoped ref val s) { r = n; o = 2; s++; }
                }

                class E : B
                {
                    public override void M(val r, val n, val o, val s) { r = n; o = 3; }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "6;2;6;3" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameSignature(comp, "D.M", "B.M");
            VerifySameSignature(comp, "E.M", "B.M");

            source = """
                class B { public virtual void M(ref int r, in int n, out int o) { o = 0; } }

                class D : B
                {
                    int _j;

                    public override void M(ref val r, in val n, out val o)
                    {
                        r = ref _j;
                        n = ref _j;
                        o = 1;
                        o = ref _j;
                        n = 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (9,9): error CS20006: Cannot ref-assign 'r' because it is a ref val: it always refers to the same variable. Writes through 'r' are allowed.
                //         r = ref _j;
                Diagnostic(ErrorCode.ERR_SharperValRefReassignment, "r").WithArguments("r").WithLocation(9, 9),
                // (10,9): error CS20006: Cannot ref-assign 'n' because it is a ref val: it always refers to the same variable. Writes through 'n' are allowed.
                //         n = ref _j;
                Diagnostic(ErrorCode.ERR_SharperValRefReassignment, "n").WithArguments("n").WithLocation(10, 9),
                // (12,9): error CS20006: Cannot ref-assign 'o' because it is a ref val: it always refers to the same variable. Writes through 'o' are allowed.
                //         o = ref _j;
                Diagnostic(ErrorCode.ERR_SharperValRefReassignment, "o").WithArguments("o").WithLocation(12, 9),
                // (13,9): error CS8331: Cannot assign to variable 'n' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         n = 1;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "n").WithArguments("variable", "n").WithLocation(13, 9));
        }

        [Fact]
        public void ValParameter_StructMemberCallOnCopy()
        {
            // A non-readonly member of a struct val parameter runs on a copy and warns (CS20003), as for a val local.
            var source = """
                using System;

                new C().M();

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                }

                class C
                {
                    public void M(val s = default(S))
                    {
                        s.Mutate();
                        Console.Write(s.F);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0" : null).VerifyDiagnostics(
                // (15,9): warning CS20003: Call to non-readonly member 'S.Mutate()' on val variable 's' runs on a copy, so 's' is not changed.
                //         s.Mutate();
                Diagnostic(ErrorCode.WRN_SharperValMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s", "val variable").WithLocation(15, 9));
        }

        [Fact]
        public void ValParameter_NoSource()
        {
            // Without a source, val x is CS20027, the same error as readonly infer x.
            var source = """
                class C
                {
                    public void Log(val message) { }
                    public void LogInfer(readonly infer message) { }
                    int Len(val s) => s.Length;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,21): error CS20027: Cannot infer the type of parameter 'message', because it has no default value, and the body does not use it. Write the parameter type.
                //     public void Log(val message) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "val").WithArguments("message", "the body does not use it").WithLocation(3, 21),
                // (4,35): error CS20027: Cannot infer the type of parameter 'message', because it has no default value, and the body does not use it. Write the parameter type.
                //     public void LogInfer(readonly infer message) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("message", "the body does not use it").WithLocation(4, 35),
                // (5,13): error CS20027: Cannot infer the type of parameter 's', because it has no default value, and a member access pins nothing. Write the parameter type.
                //     int Len(val s) => s.Length;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "val").WithArguments("s", "a member access pins nothing").WithLocation(5, 13));
        }

        [Fact]
        public void ValParameter_TypeNamedVal()
        {
            // C# wins: with a type named val in scope, val x is a parameter of that type, which can be reassigned.
            var source = """
                class val { }

                class C
                {
                    void M(val x) { x = new val(); }
                    void N(val x = null) { x = null; }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (1,7): warning CS8981: The type name 'val' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // class val { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "val").WithArguments("val").WithLocation(1, 7));
            foreach (var name in new[] { "C.M", "C.N" })
            {
                var parameter = comp.GetMember<MethodSymbol>(name).Parameters[0];
                Assert.Equal("val", parameter.Type.ToTestDisplayString());
                Assert.False(parameter is SourceComplexParameterSymbolBase { IsSharperVal: true });
            }
        }

        [Fact]
        public void ValParameter_OtherPositions()
        {
            // Anonymous methods and extension block receivers keep val a type name, as C# does; lambdas keep (val x).
            var source = """
                static class E
                {
                    extension(val v) { }
                }

                class C
                {
                    void M()
                    {
                        System.Action<int> a = delegate (val x) { };
                        System.Action<int> l = (val x) => { };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,15): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //     extension(val v) { }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(3, 15),
                // (10,42): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         System.Action<int> a = delegate (val x) { };
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(10, 42));
        }

        [Fact]
        public void ValParameter_SemanticModel()
        {
            // Like var and infer, the val of a parameter stands for the inferred type.
            var source = """
                class C
                {
                    static int Twice(int i) => i * 2;
                    long M(val x = 1L) => x;
                    int N(val y) => Twice(y);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var valSyntaxes = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().Select(p => p.Type!).Where(t => t.ToString() == "val").ToArray();
            AssertEx.Equal(["System.Int64", "System.Int32"], valSyntaxes.Select(t => model.GetTypeInfo(t).Type.ToTestDisplayString()));
            AssertEx.Equal(["System.Int64", "System.Int32"], valSyntaxes.Select(t => model.GetSymbolInfo(t).Symbol.ToTestDisplayString()));
        }

        [Fact]
        public void ValParameter_FeatureDisabled()
        {
            // Without Sharper, val is a type name, as in C#.
            var source = """
                class C
                {
                    void M(val x = 1) { }
                }
                """;
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,12): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //     void M(val x = 1) { }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(3, 12),
                // (3,16): error CS1750: A value of type 'int' cannot be used as a default parameter because there are no standard conversions to type 'val'
                //     void M(val x = 1) { }
                Diagnostic(ErrorCode.ERR_NoConversionForDefaultParam, "x").WithArguments("int", "val").WithLocation(3, 16));
        }
    }
}
