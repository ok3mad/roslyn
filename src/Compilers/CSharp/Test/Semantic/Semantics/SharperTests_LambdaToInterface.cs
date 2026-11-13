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
    /// Sharper's lambda to single-method interface (issue #29): a lambda or method group converts to an interface with
    /// one abstract method, where C# reports an error.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void LambdaToInterface_Basic()
        {
            var source = """
                using System;

                record Profile(string Plan);
                interface IProfiles { Profile Load(int id); }

                class C
                {
                    static string Render(int user, IProfiles profiles) => profiles.Load(user).Plan;
                    static Profile LoadProfile(int id) => new Profile("m" + id);

                    // The C# it stands for.
                    sealed class Profiles(Func<int, Profile> load) : IProfiles
                    {
                        public Profile Load(int id) => load(id);
                    }

                    static void Main()
                    {
                        string plan = "Pro";
                        IProfiles local = id => new Profile(plan + id);                   // a variable, capturing
                        Console.Write(local.Load(1).Plan);
                        Console.Write(" " + Render(2, id => new Profile("Free" + id)));   // an argument
                        Console.Write(" " + Render(3, LoadProfile));                      // a method group
                        Console.Write(" " + Render(4, new Profiles(id => new Profile("Pro" + id))));
                        Console.Write(" " + Make().Load(5).Plan);
                    }

                    static IProfiles Make() => id => new Profile("r" + id);               // a return
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Pro1 Free2 m3 Pro4 r5" : null).VerifyDiagnostics();

            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (20,30): error CS1660: Cannot convert lambda expression to type 'IProfiles' because it is not a delegate type
                //         IProfiles local = id => new Profile(plan + id);                   // a variable, capturing
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IProfiles").WithLocation(20, 30),
                // (22,42): error CS1660: Cannot convert lambda expression to type 'IProfiles' because it is not a delegate type
                //         Console.Write(" " + Render(2, id => new Profile("Free" + id)));   // an argument
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IProfiles").WithLocation(22, 42),
                // (23,39): error CS1503: Argument 2: cannot convert from 'method group' to 'IProfiles'
                //         Console.Write(" " + Render(3, LoadProfile));                      // a method group
                Diagnostic(ErrorCode.ERR_BadArgType, "LoadProfile").WithArguments("2", "method group", "IProfiles").WithLocation(23, 39),
                // (28,35): error CS1660: Cannot convert lambda expression to type 'IProfiles' because it is not a delegate type
                //     static IProfiles Make() => id => new Profile("r" + id);               // a return
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IProfiles").WithLocation(28, 35));
        }

        [Fact]
        public void LambdaToInterface_DelegateOverloadWins()
        {
            // Design rule 2: where C# binds, Sharper does not change the meaning.
            var source = """
                using System;

                interface IF { int Apply(int x); }

                static class E
                {
                    public static string Ext(this string s, Func<int, int> f) => "ext delegate";
                }

                class C
                {
                    static string M(IF f) => "interface";
                    static string M(Func<int, int> f) => "delegate";
                    static string N(object o) => "object";
                    static string N(IF f) => "interface";
                    static string O(ICloneable c) => "cloneable";
                    string Ext(IF f) => "instance interface";

                    void Run()
                    {
                        Console.Write(M(x => x));
                        Console.Write(" " + N((int x) => x));
                        Console.Write(" " + O((int x) => x));
                        Console.Write(" " + "s".Ext(x => x));
                        Console.Write(" " + N(x => x));
                    }

                    static void Main() => new C().Run();
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "delegate object cloneable ext delegate interface" : null).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_Bcl()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static void Main()
                    {
                        // A constructor whose overloads take IComparer<T> or IEnumerable<T>.
                        var set = new SortedSet<string>((a, b) => b.CompareTo(a)) { "a", "c", "b" };
                        Console.Write(string.Join("", set));

                        IComparer<int> byAbs = (x, y) => Math.Abs(x).CompareTo(Math.Abs(y));
                        var numbers = new[] { 3, -1, 2 };
                        Array.Sort(numbers, byAbs);
                        Console.Write(" " + string.Join(",", numbers));

                        using (IDisposable d = () => Console.Write(" disposed"))
                        {
                            Console.Write(" body");
                        }
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "cba -1,2,3 body disposed" : null).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_Ineligible()
        {
            // Interfaces that are not one abstract instance method keep the C# errors.
            var source = """
                using System;
                using System.Collections.Generic;

                interface ITwo { int A(int x); int B(int x); }
                interface IProperty { int Value { get; } }
                interface IGenericMethod { T Id<T>(T x); }
                interface IStatic { int Apply(int x); static abstract int Make(); }
                interface IEmpty { }

                class C
                {
                    static void Main()
                    {
                        ITwo two = x => x;
                        IProperty property = () => 1;
                        IGenericMethod generic = x => x;
                        IEqualityComparer<int> comparer = (x, y) => x == y;
                        IEmpty empty = () => { };
                        ICloneable cloneable = () => 1;                 // C#: the delegate itself is ICloneable
                        IComparer<int> wrongArity = x => 0;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (14,22): error CS1660: Cannot convert lambda expression to type 'ITwo' because it is not a delegate type
                //         ITwo two = x => x;
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "ITwo").WithLocation(14, 22),
                // (15,33): error CS1660: Cannot convert lambda expression to type 'IProperty' because it is not a delegate type
                //         IProperty property = () => 1;
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IProperty").WithLocation(15, 33),
                // (16,36): error CS1660: Cannot convert lambda expression to type 'IGenericMethod' because it is not a delegate type
                //         IGenericMethod generic = x => x;
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IGenericMethod").WithLocation(16, 36),
                // (17,50): error CS1660: Cannot convert lambda expression to type 'IEqualityComparer<int>' because it is not a delegate type
                //         IEqualityComparer<int> comparer = (x, y) => x == y;
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "System.Collections.Generic.IEqualityComparer<int>").WithLocation(17, 50),
                // (18,27): error CS1660: Cannot convert lambda expression to type 'IEmpty' because it is not a delegate type
                //         IEmpty empty = () => { };
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IEmpty").WithLocation(18, 27),
                // (20,39): error CS1660: Cannot convert lambda expression to type 'IComparer<int>' because it is not a delegate type
                //         IComparer<int> wrongArity = x => 0;
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "System.Collections.Generic.IComparer<int>").WithLocation(20, 39));
        }

        [Fact]
        public void LambdaToInterface_DefaultAndInheritedMembers()
        {
            var source = """
                using System;

                interface IBase { int Get(); int Twice() => 2 * Get(); }
                interface IDerived : IBase { string Name => "derived"; }
                interface IOther { int First(); int Second(); }
                interface IOneLeft : IOther { int IOther.First() => 10 + Second(); }

                class C
                {
                    static void Main()
                    {
                        IDerived d = () => 21;
                        Console.Write(d.Twice() + " " + d.Name);
                        IOneLeft one = () => 5;
                        Console.Write(" " + ((IOther)one).First());
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "42 derived 15" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_Generics()
        {
            var source = """
                using System;

                interface IMap<T> { T Map(T x); }
                interface IParse { bool TryParse(string s, out int value); }

                class Outer<U>
                {
                    private interface IGet { U Get(); }

                    public static U Run(U value)
                    {
                        IGet get = () => value;
                        return get.Get();
                    }
                }

                class C
                {
                    static T Apply<T>(T x, IMap<T> map) => map.Map(x);

                    static T Twice<T>(T x, Func<T, T, T> add)
                    {
                        IMap<T> map = y => add(y, y);
                        return map.Map(x);
                    }

                    static void Main()
                    {
                        Console.Write(Apply(3, x => x + 1));
                        Console.Write(" " + Apply("a", x => x + "b"));
                        Console.Write(" " + Twice(4, (a, b) => a + b));
                        Console.Write(" " + Outer<string>.Run("outer"));
                        IParse parse = (string s, out int value) => int.TryParse(s, out value);
                        Console.Write(" " + (parse.TryParse("12", out var v) ? v : -1));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "4 ab 8 outer 12" : null).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_AsyncItAndPipe()
        {
            var source = """
                using System;
                using System.Threading.Tasks;

                interface ILoader { Task<int> LoadAsync(int id); }
                interface IFormat { string Format(int x); }

                class C
                {
                    static string Show(int x, IFormat format) => format.Format(x);
                    static string Hex(int x) => x.ToString("x");

                    static async Task Main()
                    {
                        ILoader loader = async id => { await Task.Yield(); return id * 2; };
                        Console.Write(await loader.LoadAsync(21));
                        Console.Write(" " + Show(10, "<" + it + ">"));
                        Console.Write(" " + (255 |> Show(it, Hex)));
                        Console.Write(" " + (7 |> Show(it, x => "#" + x)));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "42 <10> ff #7" : null).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_GeneratedClass()
        {
            // One sealed class per interface and type, implementing the method under its own name.
            var source = """
                using System;
                using System.Linq;
                using System.Reflection;

                interface IProfiles { string Load(int id); }
                namespace N { interface IProfiles { string Load(int id); } }

                class C
                {
                    static void Main()
                    {
                        IProfiles a = id => "a";
                        IProfiles b = id => throw new InvalidOperationException();
                        N.IProfiles c = id => "c";
                        var type = a.GetType();
                        Console.Write(type.Name + " " + type.IsSealed + " " + (type == b.GetType()) + " " + c.GetType().Name);
                        Console.Write(" " + string.Join(",", type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(m => m.Name)));
                        // The lambda's own frame is in a stack trace; the class's forwarding frame is often inlined.
                        try { b.Load(1); }
                        catch (InvalidOperationException e) { Console.Write(" " + e.StackTrace!.Contains("<Main>b__")); }
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "<IProfiles>Lambda True True <IProfiles>Lambda_1 Load True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_NullableAndExpressionTree()
        {
            var source = """
                #nullable enable
                using System;
                using System.Linq.Expressions;

                interface IName { string Name(string? prefix); }

                class C
                {
                    static void Main()
                    {
                        IName bad = prefix => prefix;
                        IName good = prefix => prefix ?? "none";
                        Expression<Func<IName>> tree = () => p => "x";
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (11,31): warning CS8603: Possible null reference return.
                //         IName bad = prefix => prefix;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "prefix").WithLocation(11, 31),
                // (13,46): error CS20070: An expression tree cannot contain a lambda or method group converted to the interface 'IName'.
                //         Expression<Func<IName>> tree = () => p => "x";
                Diagnostic(ErrorCode.ERR_SharperFunctionToInterfaceInExpressionTree, @"p => ""x""").WithArguments("IName").WithLocation(13, 46));
        }

        [Fact]
        public void LambdaToInterface_Contexts()
        {
            var source = """
                using System;

                interface IF { int Apply(int x); }

                class Base
                {
                    protected readonly IF F;
                    public Base(IF f) { F = f; }
                }

                class Derived : Base
                {
                    public Derived(int k) : base(x => x * k) { }
                    public int Run(int x) => F.Apply(x);
                }

                struct S
                {
                    public int K;
                    public IF Make() { var k = K; return x => x + k; }
                }

                interface IWithDefault
                {
                    IF Offset(int k) => x => x + k;
                }

                class Impl : IWithDefault { }

                class C
                {
                    static IF field = x => x - 1;
                    int factor = 3;

                    static int Parse(int x) => x * 10;
                    static int Parse(string s) => s.Length;
                    static int Use(IF f) => f.Apply(2);

                    IF Scale() => x => x * factor;

                    static void Main()
                    {
                        Console.Write(new Derived(4).Run(5));
                        Console.Write(" " + new S { K = 1 }.Make().Apply(1));
                        Console.Write(" " + ((IWithDefault)new Impl()).Offset(7).Apply(1));
                        Console.Write(" " + field.Apply(1));
                        Console.Write(" " + Use(Parse));
                        Console.Write(" " + new C().Scale().Apply(5));
                        int outer = 100;
                        Func<int, int> nested = y => Use(x => x + y + outer);
                        Console.Write(" " + nested(1));
                        Console.Write(" " + Use(static x => -x));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "20 2 8 0 20 15 103 -2" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_Errors()
        {
            var source = """
                interface IA { int Apply(int x); }
                interface IB { int Apply(int x); }

                class C
                {
                    static void M(IA a) { }
                    static void M(IB b) { }
                    static void N(IA a, string s) { }

                    static void Main()
                    {
                        M(x => x);                  // ambiguous between two interfaces
                        N(x => x, 1);               // another argument fails: C#'s errors
                        IA body = x => undefined;   // an error in the body
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (12,9): error CS0121: The call is ambiguous between the following methods or properties: 'C.M(IA)' and 'C.M(IB)'
                //         M(x => x);                  // ambiguous between two interfaces
                Diagnostic(ErrorCode.ERR_AmbigCall, "M").WithArguments("C.M(IA)", "C.M(IB)").WithLocation(12, 9),
                // (13,13): error CS1660: Cannot convert lambda expression to type 'IA' because it is not a delegate type
                //         N(x => x, 1);               // another argument fails: C#'s errors
                Diagnostic(ErrorCode.ERR_AnonMethToNonDel, "=>").WithArguments("lambda expression", "IA").WithLocation(13, 13),
                // (13,19): error CS1503: Argument 2: cannot convert from 'int' to 'string'
                //         N(x => x, 1);               // another argument fails: C#'s errors
                Diagnostic(ErrorCode.ERR_BadArgType, "1").WithArguments("2", "int", "string").WithLocation(13, 19),
                // (14,24): error CS0103: The name 'undefined' does not exist in the current context
                //         IA body = x => undefined;   // an error in the body
                Diagnostic(ErrorCode.ERR_NameNotInContext, "undefined").WithArguments("undefined").WithLocation(14, 24));
        }

        [Fact]
        public void LambdaToInterface_SemanticModel()
        {
            var source = """
                interface IF { int Apply(int x); }

                class C
                {
                    static int Use(IF f) => f.Apply(1);
                    static void Main()
                    {
                        IF f = x => x;
                        Use(y => y + 1);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var lambdas = tree.GetRoot().DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().ToArray();

            var info = model.GetTypeInfo(lambdas[0]);
            Assert.Null(info.Type);
            Assert.Equal("IF", info.ConvertedType.ToTestDisplayString());
            Assert.True(model.GetConversion(lambdas[0]).IsImplicit);
            Assert.Equal("System.Int32 x", model.GetDeclaredSymbol(lambdas[0].Parameter)!.ToTestDisplayString());

            var parameter = model.GetDeclaredSymbol(lambdas[1].Parameter)!;
            Assert.Equal("System.Int32 y", parameter.ToTestDisplayString());
            var call = (InvocationExpressionSyntax)lambdas[1].Parent!.Parent!.Parent!;
            Assert.Equal("System.Int32 C.Use(IF f)", model.GetSymbolInfo(call).Symbol.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetTypeInfo(((BinaryExpressionSyntax)lambdas[1].Body).Left).Type.ToTestDisplayString());
        }

        [Fact]
        public void LambdaToInterface_TypeInference()
        {
            // Step b: a lambda or method group infers type arguments through the interface's method, as for its delegate.
            var source = """
                using System;
                using System.Threading.Tasks;

                interface IGet<T> { T Get(); }
                interface IParser<T> { T Parse(string s); }
                interface IMap<TIn, TOut> { TOut Map(TIn x); }
                interface ILoad<T> { Task<T> Load(); }

                static class E
                {
                    public static R Then<T, R>(this T x, IMap<T, R> f) => f.Map(x);
                }

                class C
                {
                    static T Make<T>(IGet<T> g) => g.Get();
                    static T Parse<T>(string s, IParser<T> p) => p.Parse(s);
                    static Task<T> Run<T>(ILoad<T> l) => l.Load();
                    static string Kind<T>(T value) => typeof(T).Name;
                    static int Number() => 7;

                    static string Pick<T>(Func<T> f) => "delegate " + Kind(f());
                    static string Pick<T>(IGet<T> g) => "interface " + Kind(g.Get());

                    static async Task Main()
                    {
                        Console.Write(Kind(Make(() => 42)));                          // from the return type
                        Console.Write(" " + Kind(Parse("abc", (string s) => s.Length)));  // explicit parameter types
                        Console.Write(" " + Kind(5.Then(x => x * 2.0)));               // T from 5, then R from the body
                        Console.Write(" " + Kind(Make(Number)));                       // a method group
                        Console.Write(" " + Kind(await Run(async () => { await Task.Yield(); return "s"; })));
                        Console.Write(" " + Pick(() => 1L));                            // the delegate overload still wins
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Int32 Int32 Double Int32 String delegate Int64" : null).VerifyDiagnostics();
        }

        [Fact]
        public void LambdaToInterface_TypeInference_Errors()
        {
            var source = """
                interface IMap<T> { T Map(T x); }

                class C
                {
                    static T Apply<T>(IMap<T> m) => default!;
                    static T ApplyF<T>(System.Func<T, T> f) => default!;

                    static void Main()
                    {
                        Apply(x => x);      // nothing fixes T, as for the delegate
                        ApplyF(x => x);
                        Apply((int x) => x);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (10,9): error CS0411: The type arguments for method 'C.Apply<T>(IMap<T>)' cannot be inferred from the usage. Try specifying the type arguments explicitly.
                //         Apply(x => x);      // nothing fixes T, as for the delegate
                Diagnostic(ErrorCode.ERR_CantInferMethTypeArgs, "Apply").WithArguments("C.Apply<T>(IMap<T>)").WithLocation(10, 9),
                // (11,9): error CS0411: The type arguments for method 'C.ApplyF<T>(Func<T, T>)' cannot be inferred from the usage. Try specifying the type arguments explicitly.
                //         ApplyF(x => x);
                Diagnostic(ErrorCode.ERR_CantInferMethTypeArgs, "ApplyF").WithArguments("C.ApplyF<T>(System.Func<T, T>)").WithLocation(11, 9));
        }

        [Fact]
        public void LambdaToInterface_TypeInference_Nullable()
        {
            var source = """
                #nullable enable
                interface IGet<T> { T Get(); }

                class C
                {
                    static T Make<T>(IGet<T> g) => g.Get();
                    static T MakeF<T>(System.Func<T> f) => f();
                    static void Use(IGet<string> g) { }

                    static void Main()
                    {
                        int a = Make(() => (string?)null).Length;   // T is string?
                        int b = Make(() => "s").Length;
                        int c = MakeF(() => (string?)null).Length;
                        string? n = null;
                        int d = Make(() => n).Length;
                        int e = MakeF(() => n).Length;
                        n = "x";
                        int f = Make(() => n).Length;
                        int g = MakeF(() => n).Length;
                        Use(() => null);
                    }
                }
                """;
            // The interface lines warn where their Func twins do: the lambda's flow state decides T.
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (12,17): warning CS8602: Dereference of a possibly null reference.
                //         int a = Make(() => (string?)null).Length;   // T is string?
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "Make(() => (string?)null)").WithLocation(12, 17),
                // (14,17): warning CS8602: Dereference of a possibly null reference.
                //         int c = MakeF(() => (string?)null).Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "MakeF(() => (string?)null)").WithLocation(14, 17),
                // (16,17): warning CS8602: Dereference of a possibly null reference.
                //         int d = Make(() => n).Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "Make(() => n)").WithLocation(16, 17),
                // (17,17): warning CS8602: Dereference of a possibly null reference.
                //         int e = MakeF(() => n).Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "MakeF(() => n)").WithLocation(17, 17),
                // (21,19): warning CS8603: Possible null reference return.
                //         Use(() => null);
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "null").WithLocation(21, 19));
        }
    }
}
