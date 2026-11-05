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
    /// Sharper's inferred parameter types, step P6 (issue #77): an <c>infer</c> parameter is pinned by the other uses in
    /// the body: an assignment to a typed local, parameter, field or property, <c>return x</c> from a member with a
    /// written return type, and <c>ref</c>, <c>out</c> and <c>in</c> arguments.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void InferFromUses_Basic()
        {
            // The issue's examples and every kind of use, next to the C# twin.
            var source = """
                using System;
                using System.Threading.Tasks;

                class Person
                {
                    public string Name { get; set; } = "";
                    public int Age;
                }

                class C
                {
                    string _field = "";
                    readonly int[] _items = new int[2];
                    public string Name { get; set; } = "";
                    public int this[int i] { get => _items[i]; set => _items[i] = value; }

                    static void SetOut(out int value) => value = 3;
                    static void Inc(ref long value) => value++;
                    static int Read(in int value) => value;

                    public void Rename(infer name) { this.Name = name; }
                    public void RenameCSharp(string name) { this.Name = name; }

                    public void SetField(infer value) { _field = value; Console.Write(_field); }
                    public void SetFieldCSharp(string value) { _field = value; Console.Write(_field); }

                    public static int Clamp(infer x) { return x; }
                    public static int ClampCSharp(int x) { return x; }

                    public static long Arrow(infer x) => x;
                    public static long ArrowCSharp(long x) => x;

                    public static double Local(infer x) { double d = x; return d * 2; }
                    public static double LocalCSharp(double x) { double d = x; return d * 2; }

                    public static int Assign(infer x) { int y; y = x; return y + 1; }
                    public static int AssignCSharp(int x) { int y; y = x; return y + 1; }

                    public static int Parameter(int p, infer x) { p = x; return p; }
                    public static int ParameterCSharp(int p, int x) { p = x; return p; }

                    public static int Out(infer x) { SetOut(out x); return 10 + x; }
                    public static int OutCSharp(int x) { SetOut(out x); return 10 + x; }

                    public static long Ref(infer x) { Inc(ref x); Inc(ref x); return x; }
                    public static long RefCSharp(long x) { Inc(ref x); Inc(ref x); return x; }

                    public static int In(infer x) => Read(in x);
                    public static int InCSharp(int x) => Read(in x);

                    public static ref int RefReturn(ref infer x) => ref x;
                    public static ref int RefReturnCSharp(ref int x) => ref x;

                    public static Person Make(infer name, infer age) => new Person { Name = name, Age = age };
                    public static Person MakeCSharp(string name, int age) => new Person { Name = name, Age = age };

                    public int Indexer(infer v) { this[1] = v; return this[1]; }
                    public int IndexerCSharp(int v) { this[1] = v; return this[1]; }

                    public static async Task<int> Async(infer x) { await Task.Yield(); return x; }
                    public static async Task<int> AsyncCSharp(int x) { await Task.Yield(); return x; }

                    public static int Lambda(infer x) { Func<int> f = int () => x; return f(); }
                    public static int LambdaCSharp(int x) { Func<int> f = int () => x; return f(); }

                    public static string LocalFunction(infer x) { string L() => x; return L(); }
                    public static string LocalFunctionCSharp(string x) { string L() => x; return L(); }

                    public static T Generic<T>(T t, infer x) { T y = x; return y; }
                    public static T GenericCSharp<T>(T t, T x) { T y = x; return y; }

                    public static infer Mixed(infer x) { int y = x; return x; }
                    public static int MixedCSharp(int x) { int y = x; return x; }
                }

                class Program
                {
                    static void Main()
                    {
                        var c = new C();
                        c.Rename("r"); Console.Write(c.Name); c.RenameCSharp("s"); Console.Write(c.Name + ";");
                        c.SetField("f"); c.SetFieldCSharp("g"); Console.Write(";");
                        Console.Write(C.Clamp(1) + C.ClampCSharp(1) + ";");
                        Console.Write(C.Arrow(2) + C.ArrowCSharp(2) + ";");
                        Console.Write(C.Local(1.5) + C.LocalCSharp(1.5) + ";");
                        Console.Write(C.Assign(3) + C.AssignCSharp(3) + ";");
                        Console.Write(C.Parameter(0, 4) + C.ParameterCSharp(0, 4) + ";");
                        Console.Write(C.Out(0) + C.OutCSharp(0) + ";");
                        Console.Write(C.Ref(5) + C.RefCSharp(5) + ";");
                        Console.Write(C.In(6) + C.InCSharp(6) + ";");
                        int i = 7, j = 7;
                        C.RefReturn(ref i) = 8; C.RefReturnCSharp(ref j) = 8;
                        Console.Write(i + j + ";");
                        var p = C.Make("p", 9); var q = C.MakeCSharp("q", 9);
                        Console.Write(p.Name + p.Age + q.Name + q.Age + ";");
                        Console.Write(c.Indexer(10) + c.IndexerCSharp(10) + ";");
                        Console.Write(C.Async(11).Result + C.AsyncCSharp(11).Result + ";");
                        Console.Write(C.Lambda(12) + C.LambdaCSharp(12) + ";");
                        Console.Write(C.LocalFunction("l") + C.LocalFunctionCSharp("l") + ";");
                        Console.Write(C.Generic("a", "b") + C.GenericCSharp("a", "b") + ";");
                        Console.Write(C.Mixed(13) + C.MixedCSharp(13));
                    }
                }
                """;
            // ILVerify rejects ref returns, in the C# twin as well.
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, verify: Verification.FailsILVerify,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "rs;fg;2;4;6;8;8;26;14;12;16;p9q9;20;22;24;ll;bb;26" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            foreach (var name in new[] { "Rename", "SetField", "Clamp", "Arrow", "Local", "Assign", "Parameter", "Out", "Ref", "In", "RefReturn", "Make", "Indexer", "Async", "Lambda", "LocalFunction", "Generic", "Mixed" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }

            // The semantic model sees the type, as for var.
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var infer = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Rename").ParameterList.Parameters[0].Type!;
            Assert.Equal("System.String", model.GetTypeInfo(infer).Type.ToTestDisplayString());
        }

        [Fact]
        public void InferFromUses_Disagree()
        {
            // Several uses must agree; CS20057 names two that differ and the type each pins.
            var source = """
                class C
                {
                    string Name { get; set; } = "";
                    int _count;
                    static void Take(int x) { }
                    static void TakeLong(long x) { }
                    static void Inc(ref long value) { }

                    void Both(infer x) { Take(x); TakeLong(x); }
                    int Returned(infer x) { Name = x; return x; }
                    void RefAndValue(infer x) { Take(x); Inc(ref x); }
                    void Locals(infer x) { int a = x; string b = x; }
                    void Lambda(infer x) { var f = long () => x; Take(x); }
                    void Field(infer x) { _count = x; Name = x; }
                    void LocalFunction(infer x) { string L() => x; Take(x); }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (9,15): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'C.Take(int)' and 'long' for 'C.TakeLong(long)'. Write the parameter type.
                //     void Both(infer x) { Take(x); TakeLong(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "C.Take(int)", "long", "C.TakeLong(long)").WithLocation(9, 15),
                // (10,18): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'string' for 'C.Name' and 'int' for 'the return of C.Returned'. Write the parameter type.
                //     int Returned(infer x) { Name = x; return x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "string", "C.Name", "int", "the return of C.Returned").WithLocation(10, 18),
                // (11,22): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'C.Take(int)' and 'long' for 'C.Inc(ref long)'. Write the parameter type.
                //     void RefAndValue(infer x) { Take(x); Inc(ref x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "C.Take(int)", "long", "C.Inc(ref long)").WithLocation(11, 22),
                // (12,17): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'a' and 'string' for 'b'. Write the parameter type.
                //     void Locals(infer x) { int a = x; string b = x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "a", "string", "b").WithLocation(12, 17),
                // (13,17): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'long' for 'the return of a lambda expression' and 'int' for 'C.Take(int)'. Write the parameter type.
                //     void Lambda(infer x) { var f = long () => x; Take(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "long", "the return of a lambda expression", "int", "C.Take(int)").WithLocation(13, 17),
                // (14,16): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'C._count' and 'string' for 'C.Name'. Write the parameter type.
                //     void Field(infer x) { _count = x; Name = x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "C._count", "string", "C.Name").WithLocation(14, 16),
                // (15,24): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'string' for 'the return of L' and 'int' for 'C.Take(int)'. Write the parameter type.
                //     void LocalFunction(infer x) { string L() => x; Take(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "string", "the return of L", "int", "C.Take(int)").WithLocation(15, 24),
                // (15,42): warning CS8321: The local function 'L' is declared but never used
                //     void LocalFunction(infer x) { string L() => x; Take(x); }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L").WithArguments("L").WithLocation(15, 42));

            var returned = comp.GetDiagnostics().Single(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterUsesDisagree && d.Location.SourceSpan.Start == source.IndexOf("infer x) { Name = x; return x;"));
            int start = source.IndexOf("Name = x; return x;");
            AssertEx.Equal([start + "Name = ".Length, start + "Name = x; return ".Length], returned.AdditionalLocations.Select(l => l.SourceSpan.Start));
        }

        [Fact]
        public void InferFromUses_PinsNothing()
        {
            // A var local, an inferred return type, a lambda without a written return type, an array element, an assignment
            // to the parameter itself and an argument whose ref kind its parameter does not take pin nothing.
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static void Inc(ref long value) { }
                    static void Take(long value) { }
                    static void Over(int x) { }
                    static void Over(string x) { }
                    static void OverRef(ref int x) { }
                    static void OverRef(ref string x) { }
                    static long[] s_array = new long[1];

                    static void Var(infer x) { var y = x; }
                    static infer Inferred(infer x) => x;
                    static void Lambda(infer x) { Func<int> f = () => x; }
                    static void Array(infer x) { s_array[0] = x; }
                    static void Itself(infer x) { x = 1; }
                    static void ByValueToRef(infer x) => Inc(x);
                    static void RefToByValue(infer x) => Take(ref x);
                    static void RefToOverloads(infer x) => Over(ref x);
                    static void RefCandidatesDisagree(infer x) => OverRef(ref x);
                    static void Placeholders(infer a, infer b) { a = b; }
                    static void LocalFunctionTypeParameter(infer x) { void L<U>(U u) { U v = x; } }
                    static IEnumerable<int> Iterator(infer x) { yield return x; }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (14,21): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Var(infer x) { var y = x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(14, 21),
                // (15,27): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static infer Inferred(infer x) => x;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(15, 27),
                // (16,24): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Lambda(infer x) { Func<int> f = () => x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(16, 24),
                // (17,23): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Array(infer x) { s_array[0] = x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(17, 23),
                // (18,24): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Itself(infer x) { x = 1; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(18, 24),
                // (19,30): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and an argument whose ref kind its parameter does not take pins nothing. Write the parameter type.
                //     static void ByValueToRef(infer x) => Inc(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "an argument whose ref kind its parameter does not take pins nothing").WithLocation(19, 30),
                // (20,30): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and an argument whose ref kind its parameter does not take pins nothing. Write the parameter type.
                //     static void RefToByValue(infer x) => Take(ref x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "an argument whose ref kind its parameter does not take pins nothing").WithLocation(20, 30),
                // (21,32): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and an argument whose ref kind its parameter does not take pins nothing. Write the parameter type.
                //     static void RefToOverloads(infer x) => Over(ref x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "an argument whose ref kind its parameter does not take pins nothing").WithLocation(21, 32),
                // (22,39): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the candidates of a call to 'OverRef' take different types there: 'C.OverRef(ref int)' takes 'int', 'C.OverRef(ref string)' takes 'string'. Write the parameter type.
                //     static void RefCandidatesDisagree(infer x) => OverRef(ref x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the candidates of a call to 'OverRef' take different types there: 'C.OverRef(ref int)' takes 'int', 'C.OverRef(ref string)' takes 'string'").WithLocation(22, 39),
                // (23,30): error CS20027: Cannot infer the type of parameter 'a', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Placeholders(infer a, infer b) { a = b; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("a", "no use passes it directly to a call with one candidate").WithLocation(23, 30),
                // (23,39): error CS20027: Cannot infer the type of parameter 'b', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Placeholders(infer a, infer b) { a = b; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("b", "no use passes it directly to a call with one candidate").WithLocation(23, 39),
                // (24,44): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void LocalFunctionTypeParameter(infer x) { void L<U>(U u) { U v = x; } }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(24, 44),
                // (24,60): warning CS8321: The local function 'L' is declared but never used
                //     static void LocalFunctionTypeParameter(infer x) { void L<U>(U u) { U v = x; } }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L").WithArguments("L").WithLocation(24, 60),
                // (25,38): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static IEnumerable<int> Iterator(infer x) { yield return x; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(25, 38));
        }

        [Fact]
        public void InferFromUses_Precedence()
        {
            // The member that is overridden or implemented (P2), then the default value (P1), then the body. A use that does
            // not accept the type of a higher source reports the C# error of the twin, which names both types.
            var source = """
                abstract class B
                {
                    public abstract string M(int x);
                }

                class C : B
                {
                    static void Inc(ref long value) { }

                    public override string M(infer x) => x;
                    static string Default(infer x = 1) => x;
                    static void Local(infer x = 1) { string s = x; }
                    static void RefArgument(infer x = 1) => Inc(ref x);

                    public string MCSharp(int x) => x;
                    static string DefaultCSharp(int x = 1) => x;
                    static void LocalCSharp(int x = 1) { string s = x; }
                    static void RefArgumentCSharp(int x = 1) => Inc(ref x);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (10,42): error CS0029: Cannot implicitly convert type 'int' to 'string'
                //     public override string M(infer x) => x;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "x").WithArguments("int", "string").WithLocation(10, 42),
                // (11,43): error CS0029: Cannot implicitly convert type 'int' to 'string'
                //     static string Default(infer x = 1) => x;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "x").WithArguments("int", "string").WithLocation(11, 43),
                // (12,49): error CS0029: Cannot implicitly convert type 'int' to 'string'
                //     static void Local(infer x = 1) { string s = x; }
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "x").WithArguments("int", "string").WithLocation(12, 49),
                // (13,53): error CS1503: Argument 1: cannot convert from 'ref int' to 'ref long'
                //     static void RefArgument(infer x = 1) => Inc(ref x);
                Diagnostic(ErrorCode.ERR_BadArgType, "x").WithArguments("1", "ref int", "ref long").WithLocation(13, 53),
                // (15,37): error CS0029: Cannot implicitly convert type 'int' to 'string'
                //     public string MCSharp(int x) => x;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "x").WithArguments("int", "string").WithLocation(15, 37),
                // (16,47): error CS0029: Cannot implicitly convert type 'int' to 'string'
                //     static string DefaultCSharp(int x = 1) => x;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "x").WithArguments("int", "string").WithLocation(16, 47),
                // (17,53): error CS0029: Cannot implicitly convert type 'int' to 'string'
                //     static void LocalCSharp(int x = 1) { string s = x; }
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "x").WithArguments("int", "string").WithLocation(17, 53),
                // (18,57): error CS1503: Argument 1: cannot convert from 'ref int' to 'ref long'
                //     static void RefArgumentCSharp(int x = 1) => Inc(ref x);
                Diagnostic(ErrorCode.ERR_BadArgType, "x").WithArguments("1", "ref int", "ref long").WithLocation(18, 57));
        }

        [Fact]
        public void InferFromUses_Nullable()
        {
            // Uses agree ignoring nullability; the type every use accepts wins, as for calls.
            var source = """
                #nullable enable
                class C
                {
                    string? _maybe;
                    string _sure = "";

                    void Maybe(infer s) { _maybe = s; }
                    void Merge(infer s) { _maybe = s; _sure = s; }
                    string? Returned(infer s) => s;

                    void MaybeCSharp(string? s) { _maybe = s; }
                    void MergeCSharp(string s) { _maybe = s; _sure = s; }
                    string? ReturnedCSharp(string? s) => s;

                    void Use()
                    {
                        Maybe(null);
                        Merge(null);
                        Returned(null);
                        MaybeCSharp(null);
                        MergeCSharp(null);
                        ReturnedCSharp(null);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (18,15): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         Merge(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(18, 15),
                // (21,21): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         MergeCSharp(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(21, 21));
            foreach (var name in new[] { "Maybe", "Merge", "Returned" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }
        }
    }
}
