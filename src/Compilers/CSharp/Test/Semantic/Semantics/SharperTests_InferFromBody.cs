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
    /// Sharper's inferred parameter types, step P4 (issue #75): an <c>infer</c> parameter passed directly as an argument
    /// to a call with exactly one candidate gets that candidate's parameter type.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void InferFromBody_Basic()
        {
            // The issue's example and every kind of call next to its C# twin, which writes the types out.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;
                using System.Threading.Tasks;

                record ProfileResult(string Name);
                interface IProfiles { ProfileResult Load(int userId); }
                class Profiles : IProfiles { public ProfileResult Load(int userId) => new("p" + userId); }
                record Point(long X, long Y);
                class Base(string name) { public string Name => name; }
                static class Ext
                {
                    public static int Twice(int x) => 2 * x;
                    public static string Pad(this IProfiles p, int width) => p.Load(width).Name.PadLeft(width);
                }

                class C(IProfiles profiles)
                {
                    public infer Hello(infer id) => profiles.Load(id);
                    public ProfileResult HelloCSharp(int id) => profiles.Load(id);

                    public Point Make(infer x, infer y) => new Point(x, Y: y);
                    public Point MakeCSharp(long x, long y) => new Point(x, Y: y);

                    public int Call(Func<string, int> f, infer s) => f(s);
                    public int CallCSharp(Func<string, int> f, string s) => f(s);

                    public string Extensions(infer n, infer w) => Ext.Twice(n) + profiles.Pad(w);
                    public string ExtensionsCSharp(int n, int w) => Ext.Twice(n) + profiles.Pad(w);

                    public IEnumerable<string> Lambdas(int[] xs, infer id, infer k)
                    {
                        int Local() => Ext.Twice(k);
                        return xs.Select(x => profiles.Load(id).Name + Local());
                    }

                    public IEnumerable<string> LambdasCSharp(int[] xs, int id, int k)
                    {
                        int Local() => Ext.Twice(k);
                        return xs.Select(x => profiles.Load(id).Name + Local());
                    }

                    public async Task<string> Async(infer id) { await Task.Yield(); return profiles.Load(id).Name; }
                    public async Task<string> AsyncCSharp(int id) { await Task.Yield(); return profiles.Load(id).Name; }

                    public IEnumerable<int> Iterator(infer x) { yield return Ext.Twice(x); }
                    public IEnumerable<int> IteratorCSharp(int x) { yield return Ext.Twice(x); }

                    public static infer operator +(C c, infer id) => c.Hello(id);
                    public static explicit operator C(infer p) => Wrap(p);
                    static C Wrap(Profiles p) => new C(p);
                }

                class D : Base
                {
                    public D(infer name) : base(name) { }
                }

                class DCSharp : Base
                {
                    public DCSharp(string name) : base(name) { }
                }

                class Program
                {
                    static async Task Main()
                    {
                        var c = new C(new Profiles());
                        Console.Write(c.Hello(1).Name + c.HelloCSharp(1).Name + ";");
                        Console.Write(c.Make(1, 2) == c.MakeCSharp(1, 2));
                        Console.Write(c.Call(s => s.Length, "abc") + c.CallCSharp(s => s.Length, "abc") + ";");
                        Console.Write(c.Extensions(2, 3) + c.ExtensionsCSharp(2, 3) + ";");
                        Console.Write(string.Join(",", c.Lambdas([1, 2], 7, 1)) + string.Join(",", c.LambdasCSharp([1, 2], 7, 1)) + ";");
                        Console.Write(await c.Async(4) + await c.AsyncCSharp(4) + ";");
                        Console.Write(c.Iterator(5).Single() + c.IteratorCSharp(5).Single() + ";");
                        Console.Write((c + 6).Name + ((C)new Profiles()).Hello(8).Name + ";");
                        Console.Write(new D("d").Name + new DCSharp("d").Name);
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "p1p1;True6;4 p34 p3;p72,p72p72,p72;p4p4;20;p6p8;dd" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            foreach (var name in new[] { "Hello", "Make", "Call", "Extensions", "Lambdas", "Async", "Iterator" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }

            Assert.Equal("ProfileResult C.op_Addition(C c, System.Int32 id)", comp.GetMember<MethodSymbol>("C.op_Addition").ToTestDisplayString());
            Assert.Equal("C C.op_Explicit(Profiles p)", comp.GetMember<MethodSymbol>("C.op_Explicit").ToTestDisplayString());
            Assert.Equal("D..ctor(System.String name)", comp.GetMember<NamedTypeSymbol>("D").InstanceConstructors.Single().ToTestDisplayString());
        }

        [Fact]
        public void InferFromBody_PinsNothing()
        {
            // Uses that are not a direct argument of a call with one candidate pin nothing: the error asks for the type.
            var source = """
                using System;
                using System.Linq;

                static class Ext { public static int Twice(this int x) => 2 * x; }

                class C
                {
                    static void Take(int x) { }
                    static void Over(int x) { }
                    static void Over(string x) { }
                    static void Generic<T>(T t) { }
                    static void Params(params int[] xs) { }
                    static void Ref(ref int x) { }
                    static void Out(out int x) { x = 0; }

                    static infer Member(infer s) => s.Length;
                    static infer Operator(infer x) => x + 1;
                    static infer Is(infer x) => x is int;
                    static infer Interpolation(infer x) => $"{x}";
                    static void Overloads(infer x) => Over(x);
                    static void Inferred(infer x) => Generic(x);
                    static void InParams(infer x) => Params(x);
                    static void RefArgument(infer x) => Ref(ref x);
                    static void OutArgument(infer x) => Out(out x);
                    static infer Receiver(infer x) => x.Twice();
                    static void Expression(infer x) => Take((int)x);
                    static void Shadowed(infer x) { Func<int, int> f = x => { Take(x); return x; }; }
                    static void Unused(infer x) { }
                    static void Lambda(infer x) { Func<int, int> f = (infer y) => y; }
                    static void LocalFunction(infer x) { void L(infer y) => Take(y); Take(x); }
                    static void Abstract(infer x);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (2,1): hidden CS8019: Unnecessary using directive.
                // using System.Linq;
                Diagnostic(ErrorCode.HDN_UnusedUsingDirective, "using System.Linq;").WithLocation(2, 1),
                // (16,25): error CS20027: Cannot infer the type of parameter 's', because it has no default value, and a member access pins nothing. Write the parameter type.
                //     static infer Member(infer s) => s.Length;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("s", "a member access pins nothing").WithLocation(16, 25),
                // (17,27): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and an operator pins nothing. Write the parameter type.
                //     static infer Operator(infer x) => x + 1;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "an operator pins nothing").WithLocation(17, 27),
                // (18,21): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a type test pins nothing. Write the parameter type.
                //     static infer Is(infer x) => x is int;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a type test pins nothing").WithLocation(18, 21),
                // (19,32): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and an interpolation pins nothing. Write the parameter type.
                //     static infer Interpolation(infer x) => $"{x}";
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "an interpolation pins nothing").WithLocation(19, 32),
                // (20,27): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a call to 'Over', which has more than one candidate, pins nothing yet. Write the parameter type.
                //     static void Overloads(infer x) => Over(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a call to 'Over', which has more than one candidate, pins nothing yet").WithLocation(20, 27),
                // (21,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and 'Generic' takes a type parameter there, which pins nothing yet. Write the parameter type.
                //     static void Inferred(infer x) => Generic(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "'Generic' takes a type parameter there, which pins nothing yet").WithLocation(21, 26),
                // (22,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a params parameter pins nothing yet. Write the parameter type.
                //     static void InParams(infer x) => Params(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a params parameter pins nothing yet").WithLocation(22, 26),
                // (23,29): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a ref or out argument pins nothing yet. Write the parameter type.
                //     static void RefArgument(infer x) => Ref(ref x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a ref or out argument pins nothing yet").WithLocation(23, 29),
                // (24,29): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a ref or out argument pins nothing yet. Write the parameter type.
                //     static void OutArgument(infer x) => Out(out x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a ref or out argument pins nothing yet").WithLocation(24, 29),
                // (25,27): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a member access pins nothing. Write the parameter type.
                //     static infer Receiver(infer x) => x.Twice();
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a member access pins nothing").WithLocation(25, 27),
                // (26,28): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Expression(infer x) => Take((int)x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(26, 28),
                // (27,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     static void Shadowed(infer x) { Func<int, int> f = x => { Take(x); return x; }; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(27, 26),
                // (28,24): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     static void Unused(infer x) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(28, 24),
                // (29,24): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the body does not use it. Write the parameter type.
                //     static void Lambda(infer x) { Func<int, int> f = (infer y) => y; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the body does not use it").WithLocation(29, 24),
                // (29,55): error CS20027: Cannot infer the type of parameter 'y', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     static void Lambda(infer x) { Func<int, int> f = (infer y) => y; }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("y", "nothing else gives its type").WithLocation(29, 55),
                // (30,47): warning CS8321: The local function 'L' is declared but never used
                //     static void LocalFunction(infer x) { void L(infer y) => Take(y); Take(x); }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L").WithArguments("L").WithLocation(30, 47),
                // (30,49): error CS20027: Cannot infer the type of parameter 'y', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     static void LocalFunction(infer x) { void L(infer y) => Take(y); Take(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("y", "nothing else gives its type").WithLocation(30, 49),
                // (31,17): error CS0501: 'C.Abstract(infer)' must declare a body because it is not marked abstract, extern, or partial
                //     static void Abstract(infer x);
                Diagnostic(ErrorCode.ERR_ConcreteMissingBody, "Abstract").WithArguments("C.Abstract(infer)").WithLocation(31, 17),
                // (31,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and nothing else gives its type. Write the parameter type.
                //     static void Abstract(infer x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "nothing else gives its type").WithLocation(31, 26));

            // The error points at the use that pins nothing.
            var member = comp.GetDiagnostics().First(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterNoDefault);
            Assert.Equal(source.IndexOf("s.Length"), member.AdditionalLocations.Single().SourceSpan.Start);
        }

        [Fact]
        public void InferFromBody_Uses()
        {
            // Several uses must agree; nullability and tuple names do not count, and the type every use accepts wins.
            var source = """
                #nullable enable
                class C
                {
                    static void Take(int x) { }
                    static void TakeLong(long x) { }
                    static void TakeString(string s) { }
                    static void TakeMaybe(string? s) { }
                    static void TakeTuple((int a, int b) t) { }
                    static void TakeOtherTuple((int x, int y) t) { }

                    static void Agree(infer x) { Take(x); Take(x); }
                    static void Both(infer x) { Take(x); TakeLong(x); }
                    static void Three(infer x) { Take(x); System.Console.WriteLine(x); TakeString(x); }
                    static void Maybe(infer s) => TakeMaybe(s);
                    static void Merge(infer s) { TakeMaybe(s); TakeString(s); }
                    static void Tuples(infer t) { TakeTuple(t); TakeOtherTuple(t); }

                    static void AgreeCSharp(int x) { Take(x); Take(x); }
                    static void MaybeCSharp(string? s) => TakeMaybe(s);
                    static void MergeCSharp(string s) { TakeMaybe(s); TakeString(s); }
                    static void TuplesCSharp((int, int) t) { TakeTuple(t); TakeOtherTuple(t); }

                    static void Use()
                    {
                        Maybe(null);
                        Merge(null);
                        MaybeCSharp(null);
                        MergeCSharp(null);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (12,22): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'C.Take(int)' and 'long' for 'C.TakeLong(long)'. Write the parameter type.
                //     static void Both(infer x) { Take(x); TakeLong(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "C.Take(int)", "long", "C.TakeLong(long)").WithLocation(12, 22),
                // (13,23): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'int' for 'C.Take(int)' and 'string' for 'C.TakeString(string)'. Write the parameter type.
                //     static void Three(infer x) { Take(x); System.Console.WriteLine(x); TakeString(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "int", "C.Take(int)", "string", "C.TakeString(string)").WithLocation(13, 23),
                // (26,15): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         Merge(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(26, 15),
                // (28,21): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         MergeCSharp(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(28, 21));
            foreach (var name in new[] { "Agree", "Maybe", "Merge", "Tuples" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }

            var both = comp.GetDiagnostics().Single(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterUsesDisagree && d.Location.SourceSpan.Start == source.IndexOf("infer x) { Take(x); TakeLong"));
            int bothStart = source.IndexOf("Take(x); TakeLong(x)");
            AssertEx.Equal([bothStart + "Take(".Length, bothStart + "Take(x); TakeLong(".Length], both.AdditionalLocations.Select(l => l.SourceSpan.Start));
        }

        [Fact]
        public void InferFromBody_Precedence()
        {
            // The member that is overridden or implemented (P2), then the default value (P1), then the body. A body use
            // that does not accept the type of a higher source reports the C# error of the twin.
            var source = """
                interface I { void M(int x); }
                class C : I
                {
                    static void Take(long x) { }
                    static void TakeString(string s) { }
                    public void M(infer x) => Take(x);
                    void Default(infer x = 1) => Take(x);
                    void DefaultMismatch(infer x = 1) => TakeString(x);
                    void DefaultMismatchCSharp(int x = 1) => TakeString(x);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (8,53): error CS1503: Argument 1: cannot convert from 'int' to 'string'
                //     void DefaultMismatch(infer x = 1) => TakeString(x);
                Diagnostic(ErrorCode.ERR_BadArgType, "x").WithArguments("1", "int", "string").WithLocation(8, 53),
                // (9,57): error CS1503: Argument 1: cannot convert from 'int' to 'string'
                //     void DefaultMismatchCSharp(int x = 1) => TakeString(x);
                Diagnostic(ErrorCode.ERR_BadArgType, "x").WithArguments("1", "int", "string").WithLocation(9, 57));
            Assert.Equal("void C.M(System.Int32 x)", comp.GetMember<MethodSymbol>("C.M").ToTestDisplayString());
            Assert.Equal("void C.Default([System.Int32 x = 1])", comp.GetMember<MethodSymbol>("C.Default").ToTestDisplayString());
        }

        [Fact]
        public void InferFromBody_Generics()
        {
            // The candidate's containing type is substituted; its own type parameters pin only when their type arguments
            // are written. The method's own type parameters are in scope; a local function's are not.
            var source = """
                interface IRepo<T> { void Put(T item); }
                class C
                {
                    static void Put(IRepo<string> r, infer x) => r.Put(x);
                    static void Owner<T>(IRepo<T> r, infer x) => r.Put(x);
                    static void Generic<T>(T t, int i) { }
                    static void Written(infer x) => Generic<long>(x, 1);
                    static void OtherPosition(infer x, infer i) => Generic(x, i);
                    static void Local(infer x) { void L<U>(IRepo<U> r) => r.Put(x); }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (8,31): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and 'Generic' takes a type parameter there, which pins nothing yet. Write the parameter type.
                //     static void OtherPosition(infer x, infer i) => Generic(x, i);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "'Generic' takes a type parameter there, which pins nothing yet").WithLocation(8, 31),
                // (9,23): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and 'Put' takes a type parameter there, which pins nothing yet. Write the parameter type.
                //     static void Local(infer x) { void L<U>(IRepo<U> r) => r.Put(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "'Put' takes a type parameter there, which pins nothing yet").WithLocation(9, 23),
                // (9,39): warning CS8321: The local function 'L' is declared but never used
                //     static void Local(infer x) { void L<U>(IRepo<U> r) => r.Put(x); }
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "L").WithArguments("L").WithLocation(9, 39));
            Assert.Equal("void C.Put(IRepo<System.String> r, System.String x)", comp.GetMember<MethodSymbol>("C.Put").ToTestDisplayString());
            Assert.Equal("void C.Owner<T>(IRepo<T> r, T x)", comp.GetMember<MethodSymbol>("C.Owner").ToTestDisplayString());
            Assert.Equal("void C.Written(System.Int64 x)", comp.GetMember<MethodSymbol>("C.Written").ToTestDisplayString());
            Assert.Equal("void C.OtherPosition(infer x, System.Int32 i)", comp.GetMember<MethodSymbol>("C.OtherPosition").ToTestDisplayString());
        }

        [Fact]
        public void InferFromBody_Recursion()
        {
            // A call to the method itself, or to an overload of it, reads the parameters of the body's placeholder, so
            // recursion is no cycle. The placeholder's own types pin nothing; its written ones pin as any other.
            var source = """
                using System;
                class C
                {
                    static string Take(string s) => s;
                    static string Recursive(infer x, int n) => n == 0 ? Take(x) : Recursive(x, n - 1);
                    static string RecursiveCSharp(string x, int n) => n == 0 ? Take(x) : RecursiveCSharp(x, n - 1);
                    static int Swap(int a, infer b) => b == 0 ? a : Swap(b, a - 1);
                    static int SwapCSharp(int a, int b) => b == 0 ? a : SwapCSharp(b, a - 1);
                    static void Only(infer x) => Only(x);
                    static void Overload(infer x) => Overload(x, 0);
                    static void Overload(string x, int level) { }
                    C(string a) { }
                    C(infer a, long b, string c) : this(c) { Take(a); }

                    static void Main() => Console.Write(Recursive("r", 2) + Swap(3, 1));
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            comp.VerifyDiagnostics(
                // (9,22): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no use passes it directly to a call with one candidate. Write the parameter type.
                //     static void Only(infer x) => Only(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no use passes it directly to a call with one candidate").WithLocation(9, 22),
                // (10,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a call to 'Overload', which has more than one candidate, pins nothing yet. Write the parameter type.
                //     static void Overload(infer x) => Overload(x, 0);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a call to 'Overload', which has more than one candidate, pins nothing yet").WithLocation(10, 26));
            VerifySameSignature(comp, "C.Recursive", "C.RecursiveCSharp");
            VerifySameSignature(comp, "C.Swap", "C.SwapCSharp");
            Assert.Equal("C..ctor(System.String a, System.Int64 b, System.String c)", comp.GetMember<NamedTypeSymbol>("C").InstanceConstructors.Last().ToTestDisplayString());
        }

        [Fact]
        public void InferFromBody_Cycles()
        {
            // A body that needs its own member's signature through other members whose types are inferred is a cycle,
            // whichever member is asked for first. Overload resolution reads every candidate's signature, so two
            // constructors whose initializers call the other overload are a cycle too.
            var source = """
                class C
                {
                    static void P(infer x) => Q(x);
                    static void Q(infer y) => P(y);
                    static infer A(infer id) => B(id);
                    static infer B(int x) => A(x);
                    static infer R(infer x) => S(x);
                    static int S(int y) => T(y);
                    static infer T(int z) => R(z) + 1;
                    C(infer a, int b) : this(a, "") { }
                    C(infer a, string c) : this(1) { }
                    C(long a) { }
                }
                """;
            string[] orders = ["P", "Q", "A", "B", "R", "S", "T", ".ctor"];
            string? expected = null;
            foreach (var first in orders)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMembers("C." + first).OfType<MethodSymbol>().Select(m => m.Parameters).ToArray();
                var signatures = string.Join("\n", comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Select(m => m.ToTestDisplayString()));
                var diagnostics = string.Join("\n", comp.GetDiagnostics().Select(d => d.ToString()).OrderBy(d => d));
                var actual = signatures + "\n" + diagnostics;
                expected ??= actual;
                AssertEx.Equal(expected, actual);
            }

            var compilation = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            compilation.VerifyDiagnostics(
                // (3,19): error CS20056: Cannot infer the type of parameter 'x' from its uses, because binding the body of 'P' needs the signature of 'P', directly or through other members whose types are inferred. Write the parameter type.
                //     static void P(infer x) => Q(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterBodyCycle, "infer").WithArguments("x", "P").WithLocation(3, 19),
                // (4,19): error CS20056: Cannot infer the type of parameter 'y' from its uses, because binding the body of 'Q' needs the signature of 'Q', directly or through other members whose types are inferred. Write the parameter type.
                //     static void Q(infer y) => P(y);
                Diagnostic(ErrorCode.ERR_SharperInferParameterBodyCycle, "infer").WithArguments("y", "Q").WithLocation(4, 19),
                // (5,12): error CS20033: Cannot infer the return type of 'C.A(infer)', because it depends on itself: 'C.A(infer)' uses 'C.B(int)', which uses 'C.A(infer)'. Write the return type of one of these methods.
                //     static infer A(infer id) => B(id);
                Diagnostic(ErrorCode.ERR_SharperInferCycle, "infer").WithArguments("C.A(infer)", "'C.A(infer)' uses 'C.B(int)', which uses 'C.A(infer)'").WithLocation(5, 12),
                // (5,20): error CS20056: Cannot infer the type of parameter 'id' from its uses, because binding the body of 'A' needs the signature of 'A', directly or through other members whose types are inferred. Write the parameter type.
                //     static infer A(infer id) => B(id);
                Diagnostic(ErrorCode.ERR_SharperInferParameterBodyCycle, "infer").WithArguments("id", "A").WithLocation(5, 20),
                // (6,12): error CS20033: Cannot infer the return type of 'C.B(int)', because it depends on itself: 'C.B(int)' uses 'C.A(infer)', which uses 'C.B(int)'. Write the return type of one of these methods.
                //     static infer B(int x) => A(x);
                Diagnostic(ErrorCode.ERR_SharperInferCycle, "infer").WithArguments("C.B(int)", "'C.B(int)' uses 'C.A(infer)', which uses 'C.B(int)'").WithLocation(6, 12),
                // (6,32): error CS1503: Argument 1: cannot convert from 'int' to 'infer'
                //     static infer B(int x) => A(x);
                Diagnostic(ErrorCode.ERR_BadArgType, "x").WithArguments("1", "int", "infer").WithLocation(6, 32),
                // (10,7): error CS20056: Cannot infer the type of parameter 'a' from its uses, because binding the body of 'C' needs the signature of 'C', directly or through other members whose types are inferred. Write the parameter type.
                //     C(infer a, int b) : this(a, "") { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterBodyCycle, "infer").WithArguments("a", "C").WithLocation(10, 7),
                // (11,7): error CS20056: Cannot infer the type of parameter 'a' from its uses, because binding the body of 'C' needs the signature of 'C', directly or through other members whose types are inferred. Write the parameter type.
                //     C(infer a, string c) : this(1) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterBodyCycle, "infer").WithArguments("a", "C").WithLocation(11, 7));
            Assert.Equal("System.Int32 C.R(System.Int32 x)", compilation.GetMember<MethodSymbol>("C.R").ToTestDisplayString());
        }

        [Fact]
        public void InferFromBody_Concurrent()
        {
            // Members are inferred outside of their locks: threads that start anywhere in a chain or a cycle neither
            // deadlock nor disagree.
            var source = """
                class C
                {
                    static void Take(int x) { }
                    static void A(infer x) => B(x);
                    static void B(infer x) => C1(x);
                    static void C1(infer x) => Take(x);
                    static void P(infer x) => Q(x);
                    static void Q(infer x) => P(x);
                }
                """;
            for (int i = 0; i < 20; i++)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                var methods = new[] { "A", "B", "C1", "P", "Q" }.Select(n => comp.GetMember<MethodSymbol>("C." + n)).ToArray();
                System.Threading.Tasks.Parallel.For(0, 10, t =>
                {
                    var method = methods[t % methods.Length];
                    Assert.Equal(method.Name is "P" or "Q" ? "infer" : "System.Int32", method.Parameters[0].Type.ToTestDisplayString());
                });
                Assert.Equal(2, comp.GetDiagnostics().Count(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterBodyCycle));
            }
        }

        [Fact]
        public void InferFromBody_SemanticModelAndOperations()
        {
            var source = """
                interface IProfiles { string Load(int userId); }
                class C(IProfiles profiles)
                {
                    public infer Hello(infer id) => /*<bind>*/profiles.Load(id)/*</bind>*/;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var parameter = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().Single(p => p.Identifier.ValueText == "id");
            Assert.Equal("System.Int32", model.GetTypeInfo(parameter.Type!).Type.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetSymbolInfo(parameter.Type!).Symbol.ToTestDisplayString());
            Assert.Equal("System.Int32 id", model.GetDeclaredSymbol(parameter).ToTestDisplayString());
            var use = tree.GetRoot().DescendantNodes().OfType<ArgumentSyntax>().Single().Expression;
            Assert.Equal("System.Int32 id", model.GetSymbolInfo(use).Symbol.ToTestDisplayString());

            VerifyOperationTreeAndDiagnosticsForTest<InvocationExpressionSyntax>(comp, """
                IInvocationOperation (virtual System.String IProfiles.Load(System.Int32 userId)) (OperationKind.Invocation, Type: System.String) (Syntax: 'profiles.Load(id)')
                  Instance Receiver:
                    IParameterReferenceOperation: profiles (OperationKind.ParameterReference, Type: IProfiles) (Syntax: 'profiles')
                  Arguments(1):
                      IArgumentOperation (ArgumentKind.Explicit, Matching Parameter: userId) (OperationKind.Argument, Type: null) (Syntax: 'id')
                        IParameterReferenceOperation: id (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'id')
                        InConversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                        OutConversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                """, []);
        }

        [Fact]
        public void InferFromBody_Metadata()
        {
            // A C# compilation sees the inferred types as written ones.
            var library = CreateCompilation("""
                public interface IProfiles { string Load(int userId); }
                public class C(IProfiles profiles)
                {
                    public infer Hello(infer id) => profiles.Load(id);
                    public C(infer name, IProfiles p) : this(p) { Name = Pass(name); }
                    public string Name { get; } = "";
                    static string Pass(string s) => s;
                }
                """, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            library.VerifyDiagnostics();
            foreach (var reference in new[] { library.ToMetadataReference(), library.EmitToImageReference() })
            {
                var comp = CreateCompilation("""
                    class D
                    {
                        static string M(C c) => c.Hello(1) + new C("n", null).Name;
                    }
                    """, references: [reference], targetFramework: TargetFramework.Net90);
                comp.VerifyDiagnostics();
                Assert.Equal("System.String C.Hello(System.Int32 id)", comp.GetMember<MethodSymbol>("C.Hello").ToTestDisplayString());
            }
        }

        [Fact]
        public void InferFromBody_DefaultValuesInBody()
        {
            // The default values of lambdas and local functions in a body bound with placeholders keep their own types.
            var source = """
                using System;
                class C
                {
                    static int Take(int x) => x;
                    static int M(infer x)
                    {
                        Func<int, int> f = (infer y = 2) => y + Take(x);
                        int L(infer z = 3) => z * Take(x);
                        return f(1) + L();
                    }

                    static void Main() => Console.Write(M(10));
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "41" : null);
            verifier.VerifyDiagnostics(
                // (7,35): warning CS9099: Parameter 1 has default value '2' in lambda but '<missing>' in the target delegate type.
                //         Func<int, int> f = (infer y = 2) => y + Take(x);
                Diagnostic(ErrorCode.WRN_OptionalParamValueMismatch, "y").WithArguments("1", "2", "<missing>").WithLocation(7, 35));
            Assert.Equal("System.Int32 C.M(System.Int32 x)", ((CSharpCompilation)verifier.Compilation).GetMember<MethodSymbol>("C.M").ToTestDisplayString());
        }

        [Fact]
        public void InferFromBody_FlagOff()
        {
            var source = """
                class C
                {
                    static void Take(int x) { }
                    static void M(infer x) => Take(x);
                }
                """;
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (4,19): error CS0246: The type or namespace name 'infer' could not be found (are you missing a using directive or an assembly reference?)
                //     static void M(infer x) => Take(x);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "infer").WithArguments("infer").WithLocation(4, 19));
        }
    }
}
