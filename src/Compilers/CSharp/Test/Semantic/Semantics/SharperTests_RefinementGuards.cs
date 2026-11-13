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
    /// Sharper's refined parameters, slice 1 step 5 (issue #33): the entry guard
    /// (<c>if (!(i is &gt;=0)) throw new ArgumentOutOfRangeException(nameof(i), i, "...")</c>) and the embedded
    /// <c>Sharper.SharperRefinementAttribute</c>, written and read back.
    /// </summary>
    public sealed partial class SharperTests
    {
        /// <summary>Prints what a call throws: the exception type, its parameter name, actual value and message.</summary>
        private const string RefinementGuardReport = """
            static class Report
            {
                public static void Run(string label, System.Action action)
                {
                    try
                    {
                        action();
                        System.Console.WriteLine(label + ": ok");
                    }
                    catch (System.ArgumentOutOfRangeException e)
                    {
                        System.Console.WriteLine(label + ": " + e.ParamName + " " + (e.ActualValue ?? "null") + " " + e.Message.Split('\n')[0].TrimEnd());
                    }
                }

                public static void Log(string s) => System.Console.WriteLine(s);

                // A value with no refinement, so that a call can pass a value outside the domain: the compiler cannot
                // prove it (CS20078, disabled here), and the guard throws.
                public static T V<T>(T value) => value;
            }
            """;

        [Fact]
        public void RefinementGuard_Basic()
        {
            // Each value outside the domain throws ArgumentOutOfRangeException with the parameter's name, the value and the
            // refinement; values inside it run as the C# twin does.
            var source = """
                #pragma warning disable CS20078 // Unproven callers, which the guard checks.
                using System;

                enum Color { Red, Green, Blue }

                class C
                {
                    static int Page(int i is >=0) => i;
                    static int PageCSharp(int i)
                    {
                        if (!(i is >=0)) throw new ArgumentOutOfRangeException(nameof(i), i, "The value does not satisfy the refinement 'is >= 0'.");
                        return i;
                    }

                    static void All(long l is >0 and <=100, double d is >=0.0, decimal m is >0m, char c is >='a' and <='z',
                        bool b is true, string s is not "", Color k is Color.Red or Color.Green, nint n is >=0, byte x is not 0) { }

                    static void Day(int d is >=1 and <=7, string name is "a" or "b" or null) { }

                    static void Main()
                    {
                        Report.Run("Page(1)", () => Page(1));
                        Report.Run("Page(0)", () => Page(0));
                        Report.Run("Page(-1)", () => Page(Report.V(-1)));
                        Report.Run("PageCSharp(-1)", () => PageCSharp(-1));
                        Report.Run("All", () => All(5, 0.5, 2m, 'q', true, "s", Color.Green, 7, 9));
                        Report.Run("l", () => All(Report.V(101), 0.5, 2m, 'q', true, "s", Color.Green, 7, 9));
                        Report.Run("d", () => All(5, Report.V(double.NaN), 2m, 'q', true, "s", Color.Green, 7, 9));
                        Report.Run("m", () => All(5, 0.5, Report.V(0m), 'q', true, "s", Color.Green, 7, 9));
                        Report.Run("c", () => All(5, 0.5, 2m, Report.V('Q'), true, "s", Color.Green, 7, 9));
                        Report.Run("b", () => All(5, 0.5, 2m, 'q', Report.V(false), "s", Color.Green, 7, 9));
                        Report.Run("s", () => All(5, 0.5, 2m, 'q', true, Report.V(""), Color.Green, 7, 9));
                        Report.Run("null", () => All(5, 0.5, 2m, 'q', true, null, Color.Green, 7, 9));
                        Report.Run("k", () => All(5, 0.5, 2m, 'q', true, "s", Report.V(Color.Blue), 7, 9));
                        Report.Run("n", () => All(5, 0.5, 2m, 'q', true, "s", Color.Green, Report.V(-7), 9));
                        Report.Run("x", () => All(5, 0.5, 2m, 'q', true, "s", Color.Green, 7, Report.V((byte)0)));

                        // Parameters are checked in order.
                        Report.Run("Day(0, \"c\")", () => Day(Report.V(0), Report.V("c")));
                        Report.Run("Day(1, \"c\")", () => Day(1, Report.V("c")));
                        Report.Run("Day(7, null)", () => Day(7, null));
                    }
                }
                """ + RefinementGuardReport;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                Page(1): ok
                Page(0): ok
                Page(-1): i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                PageCSharp(-1): i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                All: ok
                l: l 101 The value does not satisfy the refinement 'is > 0 and <= 100'. (Parameter 'l')
                d: d NaN The value does not satisfy the refinement 'is >= 0'. (Parameter 'd')
                m: m 0 The value does not satisfy the refinement 'is > 0m'. (Parameter 'm')
                c: c Q The value does not satisfy the refinement 'is >= 'a' and <= 'z''. (Parameter 'c')
                b: b False The value does not satisfy the refinement 'is true'. (Parameter 'b')
                s: s  The value does not satisfy the refinement 'is not ""'. (Parameter 's')
                null: ok
                k: k Blue The value does not satisfy the refinement 'is Color.Red or Color.Green'. (Parameter 'k')
                n: n -7 The value does not satisfy the refinement 'is >= 0'. (Parameter 'n')
                x: x 0 The value does not satisfy the refinement 'is not 0'. (Parameter 'x')
                Day(0, "c"): d 0 The value does not satisfy the refinement 'is >= 1 and <= 7'. (Parameter 'd')
                Day(1, "c"): name c The value does not satisfy the refinement 'is "a" or "b" or null'. (Parameter 'name')
                Day(7, null): ok
                """ : null).VerifyDiagnostics();
        }

        [Fact]
        public void RefinementGuard_Positions()
        {
            // Every member that can have a refinement checks it: methods of every accessibility, local functions (static or
            // capturing), constructors, primary constructors, indexer accessors, operators, conversions, generic methods and
            // extension block members.
            var source = """
                #pragma warning disable CS20078 // Unproven callers, which the guard checks.
                using System;

                public class C
                {
                    public C() { }
                    public C(int i is >0) { }
                    public int this[int i is >=0] { get => i; set { } }
                    public static C operator +(C c, int i is >=0) => c;
                    public static explicit operator C(int i is >=0) => new C();
                    private static T Generic<T>(T t, int i is >=0) => t;
                    internal static void Internal(int i is >=0) { }
                    protected static void Protected(int i is >=0) { }

                    public static void Main()
                    {
                        int captured = 1;
                        void Local(int i is >=0) => Console.Write(captured);
                        static void StaticLocal(int i is >=0) { }

                        Report.Run("ctor", () => new C(Report.V(0)));
                        Report.Run("primary ctor", () => new P(Report.V(-1)));
                        Report.Run("get", () => _ = new C()[Report.V(-1)]);
                        Report.Run("set", () => new C()[Report.V(-1)] = 0);
                        Report.Run("operator", () => _ = new C() + -1);
                        Report.Run("conversion", () => _ = (C)(-1));
                        Report.Run("generic", () => Generic("a", Report.V(-1)));
                        Report.Run("internal", () => Internal(Report.V(-1)));
                        Report.Run("protected", () => Protected(Report.V(-1)));
                        Report.Run("local", () => Local(Report.V(-1)));
                        Report.Run("static local", () => StaticLocal(Report.V(-1)));
                        Report.Run("extension", () => 1.Add(Report.V(-1)));
                        Report.Run("static extension", () => int.Make(Report.V(-1)));
                        Report.Run("delegate", () => ((Action<int>)Internal)(-1));
                        Report.Run("in range", () => { _ = new C(1)[0]; Local(0); StaticLocal(0); _ = 1.Add(0); });
                    }
                }

                public class P(int x is >=0)
                {
                    public int X => x;
                }

                public static class E
                {
                    extension(int n)
                    {
                        public int Add(int i is >=0) => n + i;
                        public static int Make(int i is >=0) => i;
                    }
                }
                """ + RefinementGuardReport;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                ctor: i 0 The value does not satisfy the refinement 'is > 0'. (Parameter 'i')
                primary ctor: x -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'x')
                get: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                set: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                operator: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                conversion: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                generic: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                internal: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                protected: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                local: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                static local: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                extension: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                static extension: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                delegate: i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                1in range: ok
                """ : null).VerifyDiagnostics();
        }

        [Fact]
        public void RefinementGuard_StateMachines()
        {
            // Iterators and async methods check in the kickoff method: the call throws, before anything is enumerated or
            // awaited, as the C# idiom of a wrapper method does.
            var source = """
                #pragma warning disable CS20078 // Unproven callers, which the guard checks.
                #pragma warning disable CS4014 // The calls are not awaited on purpose.
                using System.Collections.Generic;
                using System.Threading.Tasks;

                class C
                {
                    static IEnumerable<int> Count(int n is >=0)
                    {
                        Report.Log("enumerating");
                        for (int i = 0; i < n; i++) yield return i;
                    }

                    static async IAsyncEnumerable<int> CountAsync(int n is >=0)
                    {
                        Report.Log("enumerating");
                        await Task.Yield();
                        yield return n;
                    }

                    static async Task<int> Load(int id is >0)
                    {
                        Report.Log("loading");
                        await Task.Yield();
                        return id;
                    }

                    static async void Fire(int id is >0)
                    {
                        Report.Log("firing");
                        await Task.Yield();
                    }

                    static async Task Main()
                    {
                        IEnumerable<int> LocalCount(int n is >=0) { yield return n; }
                        async Task<int> LocalLoad(int id is >0) { await Task.Yield(); return id; }

                        Report.Run("iterator", () => Count(Report.V(-1)));
                        Report.Run("async iterator", () => CountAsync(Report.V(-1)));
                        Report.Run("async", () => Load(Report.V(0)));
                        Report.Run("async void", () => Fire(Report.V(0)));
                        Report.Run("local iterator", () => LocalCount(Report.V(-1)));
                        Report.Run("local async", () => LocalLoad(Report.V(0)));

                        foreach (var i in Count(2)) Report.Log(i.ToString());
                        await foreach (var i in CountAsync(3)) Report.Log(i.ToString());
                        Report.Log((await Load(4)).ToString());
                        Report.Log((await LocalLoad(5)).ToString());
                    }
                }
                """ + RefinementGuardReport;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                iterator: n -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'n')
                async iterator: n -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'n')
                async: id 0 The value does not satisfy the refinement 'is > 0'. (Parameter 'id')
                async void: id 0 The value does not satisfy the refinement 'is > 0'. (Parameter 'id')
                local iterator: n -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'n')
                local async: id 0 The value does not satisfy the refinement 'is > 0'. (Parameter 'id')
                enumerating
                0
                1
                enumerating
                3
                loading
                4
                5
                """ : null).VerifyDiagnostics();
        }

        [Fact]
        public void RefinementGuard_Constructors()
        {
            // A constructor checks before its field initializers, before a primary constructor stores captured parameters,
            // and before the base or 'this' constructor runs.
            var source = """
                #pragma warning disable CS20078 // Unproven callers, which the guard checks.
                class Base
                {
                    public Base(string s) => Report.Log("base " + s);
                }

                class C : Base
                {
                    int _f = Init("field");
                    static int Init(string s) { Report.Log(s); return 0; }

                    public C(int i is >=0) : base("C") => Report.Log("body");
                    public C(string s is "x") : this(1) => Report.Log("this " + s);
                }

                class P(int x is >=0) : Base("P")
                {
                    int _f = Init(x);
                    static int Init(int x) { Report.Log("field " + x); return x; }
                    public int X => x;
                }

                struct S(int x is >=0)
                {
                    public int F = x;
                }

                class Program
                {
                    static void Main()
                    {
                        Report.Run("C(-1)", () => new C(Report.V(-1)));
                        Report.Run("C(\"y\")", () => new C(Report.V("y")));
                        Report.Run("P(-1)", () => new P(Report.V(-1)));
                        Report.Run("S(-1)", () => new S(Report.V(-1)));
                        Report.Run("C(1)", () => new C(1));
                        Report.Run("C(\"x\")", () => new C("x"));
                        Report.Run("P(2)", () => Report.Log(new P(2).X.ToString()));
                        Report.Run("S(3)", () => Report.Log(new S(3).F.ToString()));
                    }
                }
                """ + RefinementGuardReport;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                C(-1): i -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                C("y"): s y The value does not satisfy the refinement 'is "x"'. (Parameter 's')
                P(-1): x -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'x')
                S(-1): x -1 The value does not satisfy the refinement 'is >= 0'. (Parameter 'x')
                field
                base C
                body
                C(1): ok
                field
                base C
                body
                this x
                C("x"): ok
                field 2
                base P
                2
                P(2): ok
                3
                S(3): ok
                """ : null).VerifyDiagnostics();
        }

        [Fact]
        public void RefinementGuard_RuntimeAsync()
        {
            // A runtime-async method has no kickoff method: the runtime puts every exception it throws into the returned
            // task, so a guard there would not throw at the call. A method with refined parameters keeps the compiler's state
            // machine instead, and checks in its kickoff method; other methods still use runtime async.
            var source = """
                #pragma warning disable CS20078 // Unproven callers, which the guard checks.
                using System;
                using System.Threading.Tasks;

                class C
                {
                    static async Task<int> Load(int id is >0)
                    {
                        Console.WriteLine("loading");
                        await Task.Yield();
                        return id;
                    }

                    static async Task<int> Plain(int id)
                    {
                        await Task.Yield();
                        return id;
                    }

                    static async Task Main()
                    {
                        Task<int> t = null;
                        int zero = 0;
                        try
                        {
                            t = Load(zero);
                            Console.WriteLine("returned " + t.Status);
                        }
                        catch (ArgumentOutOfRangeException e)
                        {
                            Console.WriteLine("threw " + e.ParamName);
                        }

                        if (t is not null)
                        {
                            try { await t; } catch (ArgumentOutOfRangeException e) { Console.WriteLine("awaited " + e.ParamName); }
                        }

                        Console.WriteLine(await Load(4));
                        Console.WriteLine(await Plain(5));
                    }
                }
                """;
            var comp = CreateRuntimeAsyncCompilation(source, TestOptions.ReleaseExe, parseOptions: WithRuntimeAsync(s_sharperOptions.WithLanguageVersion(LanguageVersion.Preview)));
            var verifier = CompileAndVerify(comp, expectedOutput: RuntimeAsyncTestHelpers.ExpectedOutput("""
                threw id
                loading
                4
                5
                """), verify: Verification.Fails);
            verifier.VerifyIL("C.Load", """
                {
                  // Code size       81 (0x51)
                  .maxstack  3
                  .locals init (C.<Load>d__0 V_0)
                  IL_0000:  ldarg.0
                  IL_0001:  ldc.i4.0
                  IL_0002:  bgt.s      IL_001a
                  IL_0004:  ldstr      "id"
                  IL_0009:  ldarg.0
                  IL_000a:  box        "int"
                  IL_000f:  ldstr      "The value does not satisfy the refinement 'is > 0'."
                  IL_0014:  newobj     "System.ArgumentOutOfRangeException..ctor(string, object, string)"
                  IL_0019:  throw
                  IL_001a:  ldloca.s   V_0
                  IL_001c:  call       "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int>.Create()"
                  IL_0021:  stfld      "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> C.<Load>d__0.<>t__builder"
                  IL_0026:  ldloca.s   V_0
                  IL_0028:  ldarg.0
                  IL_0029:  stfld      "int C.<Load>d__0.id"
                  IL_002e:  ldloca.s   V_0
                  IL_0030:  ldc.i4.m1
                  IL_0031:  stfld      "int C.<Load>d__0.<>1__state"
                  IL_0036:  ldloca.s   V_0
                  IL_0038:  ldflda     "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> C.<Load>d__0.<>t__builder"
                  IL_003d:  ldloca.s   V_0
                  IL_003f:  call       "void System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int>.Start<C.<Load>d__0>(ref C.<Load>d__0)"
                  IL_0044:  ldloca.s   V_0
                  IL_0046:  ldflda     "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> C.<Load>d__0.<>t__builder"
                  IL_004b:  call       "System.Threading.Tasks.Task<int> System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int>.Task.get"
                  IL_0050:  ret
                }
                """);
            verifier.VerifyIL("C.Plain", """
                {
                  // Code size       38 (0x26)
                  .maxstack  1
                  .locals init (System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter V_0,
                                System.Runtime.CompilerServices.YieldAwaitable V_1)
                  IL_0000:  call       "System.Runtime.CompilerServices.YieldAwaitable System.Threading.Tasks.Task.Yield()"
                  IL_0005:  stloc.1
                  IL_0006:  ldloca.s   V_1
                  IL_0008:  call       "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter System.Runtime.CompilerServices.YieldAwaitable.GetAwaiter()"
                  IL_000d:  stloc.0
                  IL_000e:  ldloca.s   V_0
                  IL_0010:  call       "bool System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter.IsCompleted.get"
                  IL_0015:  brtrue.s   IL_001d
                  IL_0017:  ldloc.0
                  IL_0018:  call       "void System.Runtime.CompilerServices.AsyncHelpers.UnsafeAwaitAwaiter<System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter>(System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter)"
                  IL_001d:  ldloca.s   V_0
                  IL_001f:  call       "void System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter.GetResult()"
                  IL_0024:  ldarg.0
                  IL_0025:  ret
                }
                """);
        }

        [Fact]
        public void RefinementGuard_CodeGen()
        {
            // The guard is the is-pattern lowering of 'i is not (...)' with a throw, first in the body and without sequence
            // points; in Debug and Release alike.
            var source = """
                class C
                {
                    static int Day(int d is >=1 and <=7) => d;
                    static int Plain(int d) => d;
                }
                """;
            foreach (var options in new[] { TestOptions.DebugDll, TestOptions.ReleaseDll })
            {
                var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, options: options);
                verifier.VerifyDiagnostics();
                verifier.VerifyIL("C.Day", """
                    {
                      // Code size       34 (0x22)
                      .maxstack  3
                      .locals init (int V_0)
                      IL_0000:  ldarg.0
                      IL_0001:  stloc.0
                      IL_0002:  ldloc.0
                      IL_0003:  ldc.i4.1
                      IL_0004:  blt.s      IL_000a
                      IL_0006:  ldloc.0
                      IL_0007:  ldc.i4.7
                      IL_0008:  ble.s      IL_0020
                      IL_000a:  ldstr      "d"
                      IL_000f:  ldarg.0
                      IL_0010:  box        "int"
                      IL_0015:  ldstr      "The value does not satisfy the refinement 'is >= 1 and <= 7'."
                      IL_001a:  newobj     "System.ArgumentOutOfRangeException..ctor(string, object, string)"
                      IL_001f:  throw
                      IL_0020:  ldarg.0
                      IL_0021:  ret
                    }
                    """);
                verifier.VerifyIL("C.Plain", """
                    {
                      // Code size        2 (0x2)
                      .maxstack  1
                      IL_0000:  ldarg.0
                      IL_0001:  ret
                    }
                    """);
            }
        }

        [Fact]
        public void RefinementGuard_CodeGen_Iterator()
        {
            // The kickoff method checks before it creates the state machine.
            var source = """
                class C
                {
                    static System.Collections.Generic.IEnumerable<int> Count(int n is >=0)
                    {
                        yield return n;
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, options: TestOptions.ReleaseDll);
            verifier.VerifyDiagnostics();
            verifier.VerifyIL("C.Count", """
                {
                  // Code size       41 (0x29)
                  .maxstack  3
                  IL_0000:  ldarg.0
                  IL_0001:  ldc.i4.0
                  IL_0002:  bge.s      IL_001a
                  IL_0004:  ldstr      "n"
                  IL_0009:  ldarg.0
                  IL_000a:  box        "int"
                  IL_000f:  ldstr      "The value does not satisfy the refinement 'is >= 0'."
                  IL_0014:  newobj     "System.ArgumentOutOfRangeException..ctor(string, object, string)"
                  IL_0019:  throw
                  IL_001a:  ldc.i4.s   -2
                  IL_001c:  newobj     "C.<Count>d__0..ctor(int)"
                  IL_0021:  dup
                  IL_0022:  ldarg.0
                  IL_0023:  stfld      "int C.<Count>d__0.<>3__n"
                  IL_0028:  ret
                }
                """);
        }

        [Fact]
        public void RefinementGuard_CSharpCaller()
        {
            // A C# caller sees a plain 'int' and meets the guard.
            var library = """
                public static class Pages
                {
                    public static int Page(int i is >=0) => i;
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            libraryComp.VerifyDiagnostics();

            var caller = """
                class Program
                {
                    static void Main()
                    {
                        Report.Run("Page(2)", () => System.Console.WriteLine(Pages.Page(2)));
                        Report.Run("Page(-2)", () => Pages.Page(-2));
                    }
                }
                """ + RefinementGuardReport;
            foreach (var reference in new[] { libraryComp.ToMetadataReference(), libraryComp.EmitToImageReference() })
            {
                var comp = CreateCompilation(caller, references: [reference], parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90, options: TestOptions.ReleaseExe);
                CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                    2
                    Page(2): ok
                    Page(-2): i -2 The value does not satisfy the refinement 'is >= 0'. (Parameter 'i')
                    """ : null).VerifyDiagnostics();

                var parameter = comp.GetMember<MethodSymbol>("Pages.Page").Parameters[0];
                Assert.Equal("System.Int32 i", parameter.ToTestDisplayString());
            }
        }

        [Fact]
        public void RefinementMetadata_Attribute()
        {
            // The parameter carries an embedded Sharper.SharperRefinementAttribute with the normalized pattern; the attribute
            // type is internal, embedded, and only emitted when a refinement needs it.
            var source = """
                public enum Color { Red, Green, Blue }

                public class C
                {
                    public void M(int i is >=0 and <10 or 42, string s is not ("" or " "), Color k is not Color.Red, int plain) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            CompileAndVerify(comp, symbolValidator: module =>
            {
                var attributeType = module.GlobalNamespace.GetMember<NamedTypeSymbol>("Sharper.SharperRefinementAttribute");
                Assert.Equal(Accessibility.Internal, attributeType.DeclaredAccessibility);
                Assert.Equal(
                    new[] { "Microsoft.CodeAnalysis.EmbeddedAttribute", "System.AttributeUsageAttribute(System.AttributeTargets.Parameter | System.AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)", "System.Runtime.CompilerServices.CompilerGeneratedAttribute" },
                    attributeType.GetAttributes().Select(a => a.ToString()!).OrderBy(s => s, System.StringComparer.Ordinal));
                Assert.Equal("Sharper.SharperRefinementAttribute..ctor(System.String pattern)", attributeType.InstanceConstructors.Single().ToTestDisplayString());

                var parameters = module.GlobalNamespace.GetMember<MethodSymbol>("C.M").Parameters;
                Assert.Equal(
                    new[] { "Sharper.SharperRefinementAttribute(\">= 0 and < 10 or 42\")", "Sharper.SharperRefinementAttribute(\"not (\\\"\\\" or \\\" \\\")\")", "Sharper.SharperRefinementAttribute(\"not Color.Red\")", "" },
                    parameters.Select(p => string.Join(", ", p.GetAttributes().Select(a => a.ToString()))));
            }).VerifyDiagnostics();

            // No refinement, no attribute type.
            CompileAndVerify("public class C { public void M(int i) { } }", parseOptions: s_sharperOptions, symbolValidator: module =>
                Assert.Null(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper"))).VerifyDiagnostics();

            // The attribute is written by the compiler only: its name does not bind in source.
            CreateCompilation("""
                class C
                {
                    void M([SharperRefinement(">= 0")] int i) { }
                }
                """, parseOptions: s_sharperOptions).VerifyDiagnostics(
                    // (3,13): error CS0246: The type or namespace name 'SharperRefinementAttribute' could not be found (are you missing a using directive or an assembly reference?)
                    //     void M([SharperRefinement(">= 0")] int i) { }
                    Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "SharperRefinement").WithArguments("SharperRefinementAttribute").WithLocation(3, 13),
                    // (3,13): error CS0246: The type or namespace name 'SharperRefinement' could not be found (are you missing a using directive or an assembly reference?)
                    //     void M([SharperRefinement(">= 0")] int i) { }
                    Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "SharperRefinement").WithArguments("SharperRefinement").WithLocation(3, 13));
        }

        [Fact]
        public void RefinementMetadata_RoundTrip()
        {
            // A Sharper compilation that references the assembly sees the refinement on the parameter: the same value set
            // and null rule as the source symbol, for every supported type and every constant the text can hold.
            var library = """
                public enum Color { Red, Green, Blue, @class = 7 }

                [System.Flags]
                public enum Flags : byte { None = 0, A = 1, B = 2 }

                public static class C
                {
                    public static void M(
                        int i is >=0 and <10 or 42,
                        long l is not (<-5 or >5),
                        sbyte sb is -128 or 127,
                        byte b is >0,
                        short sh is <0,
                        ushort us is >=1,
                        uint ui is <=4000000000,
                        ulong ul is >18000000000000000000,
                        nint n is >=-3,
                        nuint nu is <10,
                        double d is >=0.0 and <1E+20 and not double.NaN,
                        double nan is double.NaN or double.PositiveInfinity,
                        float f is >-1.5f and <float.PositiveInfinity,
                        decimal m is >0.01m,
                        char c is >='a' and <='z' or '\n',
                        bool t is true,
                        string s is "a\"b" or "\\" or null,
                        string ns is not "x",
                        Color k is Color.Red or Color.@class,
                        Flags fl is (Flags)3 or Flags.None) { }

                    public static void Plain(int i) { }
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            libraryComp.VerifyDiagnostics();
            var sourceParameters = libraryComp.GetMember<MethodSymbol>("C.M").Parameters;

            foreach (var reference in new[] { libraryComp.EmitToImageReference() })
            {
                var comp = CreateCompilation("class D { }", references: [reference], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                comp.VerifyDiagnostics();
                var parameters = comp.GetMember<MethodSymbol>("C.M").Parameters;
                Assert.Equal(sourceParameters.Length, parameters.Length);
                for (int i = 0; i < parameters.Length; i++)
                {
                    var expected = sourceParameters[i].SharperRefinement!;
                    var actual = parameters[i].SharperRefinement;
                    Assert.True(actual is not null, parameters[i].Name);
                    Assert.Equal(expected.Text, actual.Text);
                    Assert.Equal(expected.Values!.ToString(), actual.Values!.ToString());
                    Assert.Equal(expected.MatchesNull, actual.MatchesNull);
                }

                Assert.Null(comp.GetMember<MethodSymbol>("C.Plain").Parameters[0].SharperRefinement);
            }

            Assert.Equal(new[]
                {
                    ">= 0 and < 10 or 42",
                    "not (< -5 or > 5)",
                    "-128 or 127",
                    "> 0",
                    "< 0",
                    ">= 1",
                    "<= 4000000000",
                    "> 18000000000000000000",
                    ">= -3",
                    "< 10",
                    ">= 0 and < 1E+20 and not double.NaN",
                    "double.NaN or double.PositiveInfinity",
                    "> -1.5f and < float.PositiveInfinity",
                    "> 0.01m",
                    """>= 'a' and <= 'z' or '\n'""",
                    "true",
                    """ "a\"b" or "\\" or null""".Trim(),
                    """not "x" """.Trim(),
                    "Color.Red or Color.@class",
                    "(Flags)3 or Flags.None",
                },
                sourceParameters.Select(p => p.SharperRefinement!.Text));
        }

        [Fact]
        public void RefinementMetadata_Members()
        {
            // The refinement is read back on every member kind that can have one: constructors, indexers (from the accessor
            // parameters), operators, generic types, and the members of an extension block.
            var library = """
                public class C<T>
                {
                    public C(int i is >0) { }
                    public int this[int i is >=0] => i;
                    public static C<T> operator +(C<T> c, int i is >=1) => c;
                    public void M(int i is <=2) { }
                }

                public class P(int x is >=3) { public int X => x; }

                public static class E
                {
                    extension(int n)
                    {
                        public int Add(int i is >=4) => n + i;
                    }
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            libraryComp.VerifyDiagnostics();

            var comp = CreateCompilation("class D { }", references: [libraryComp.EmitToImageReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var c = comp.GetTypeByMetadataName("C`1")!;
            Assert.Equal("> 0", c.InstanceConstructors.Single().Parameters[0].SharperRefinement!.Text);
            Assert.Equal(">= 0", c.Indexers.Single().Parameters[0].SharperRefinement!.Text);
            Assert.Equal(">= 0", c.Indexers.Single().GetMethod.Parameters[0].SharperRefinement!.Text);
            Assert.Equal(">= 1", c.GetMember<MethodSymbol>("op_Addition").Parameters[1].SharperRefinement!.Text);
            Assert.Null(c.GetMember<MethodSymbol>("op_Addition").Parameters[0].SharperRefinement);

            // Through a constructed type.
            var constructed = c.Construct(comp.GetSpecialType(SpecialType.System_String));
            Assert.Equal("<= 2", constructed.GetMember<MethodSymbol>("M").Parameters[0].SharperRefinement!.Text);

            Assert.Equal(">= 3", comp.GetTypeByMetadataName("P")!.InstanceConstructors.Single().Parameters[0].SharperRefinement!.Text);

            // The extension member and its implementation method.
            var e = comp.GetTypeByMetadataName("E")!;
            Assert.Equal(">= 4", e.GetMembers("Add").OfType<MethodSymbol>().Single().Parameters[1].SharperRefinement!.Text);
            Assert.Equal(">= 4", e.GetTypeMembers().Single().GetMember<MethodSymbol>("Add").Parameters[0].SharperRefinement!.Text);
        }

        [Fact]
        public void RefinementMetadata_Untrusted()
        {
            // Text that does not parse, or does not fit the parameter type, gives no refinement; the attribute is matched by
            // namespace and name, so a declaration in another assembly counts.
            var library = """
                namespace Sharper
                {
                    [System.AttributeUsage(System.AttributeTargets.Parameter)]
                    public sealed class SharperRefinementAttribute(string pattern) : System.Attribute { public string Pattern => pattern; }
                }

                public static class C
                {
                    public static void Good([Sharper.SharperRefinement("> 0")] int i) { }
                    public static void Bad1([Sharper.SharperRefinement("> ")] int i) { }
                    public static void Bad2([Sharper.SharperRefinement("> \"a\"")] int i) { }
                    public static void Bad3([Sharper.SharperRefinement("int")] int i) { }
                    public static void Bad4([Sharper.SharperRefinement("> 0")] object o) { }
                    public static void Bad5([Sharper.SharperRefinement("> 1.5")] float f) { }
                    public static void Bad6([Sharper.SharperRefinement("Color.Purple")] Color c) { }
                    public static void Bad7([Sharper.SharperRefinement("> 0) or (1")] int i) { }
                }

                public enum Color { Red }
                """;
            var libraryComp = CreateCompilation(library, targetFramework: TargetFramework.Net90);
            libraryComp.VerifyDiagnostics();

            var comp = CreateCompilation("class D { }", references: [libraryComp.EmitToImageReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var c = comp.GetTypeByMetadataName("C")!;
            Assert.Equal("> 0", c.GetMember<MethodSymbol>("Good").Parameters[0].SharperRefinement!.Text);
            foreach (var name in new[] { "Bad1", "Bad2", "Bad3", "Bad4", "Bad5", "Bad6", "Bad7" })
            {
                Assert.True(c.GetMember<MethodSymbol>(name).Parameters[0].SharperRefinement is null, name);
            }
        }

        [Fact]
        public void RefinementGuard_MissingException()
        {
            // The guard's exception is looked up by name in the core library; without it the refinement is CS0656.
            var source = """
                class C
                {
                    static int Page(int i is >=0) => i;
                }
                """;
            CreateEmptyCompilation(source, references: [MinCorlibRef], parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (3,27): error CS0656: Missing compiler required member 'System.ArgumentOutOfRangeException..ctor'
                //     static int Page(int i is >=0) => i;
                Diagnostic(ErrorCode.ERR_MissingPredefinedMember, "is").WithArguments("System.ArgumentOutOfRangeException", ".ctor").WithLocation(3, 27));
        }

        [Fact]
        public void RefinementGuard_FlagOff()
        {
            // With the flag off a refinement is C#'s syntax error, and plain C# gets no guard and no attribute.
            var source = """
                class C
                {
                    static int Page(int i is >=0) => i;
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (3,27): error CS1003: Syntax error, ',' expected
                //     static int Page(int i is >=0) => i;
                Diagnostic(ErrorCode.ERR_SyntaxError, "is").WithArguments(",").WithLocation(3, 27));

            var plain = """
                class C
                {
                    static int Page(int i) => i;
                    static void Main() => System.Console.Write(Page(-1));
                }
                """;
            foreach (var options in new[] { TestOptions.Regular, s_sharperOptions })
            {
                var verifier = CompileAndVerify(plain, parseOptions: options, expectedOutput: "-1", symbolValidator: module =>
                    Assert.Null(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper")));
                verifier.VerifyDiagnostics();
                verifier.VerifyIL("C.Page", """
                    {
                      // Code size        2 (0x2)
                      .maxstack  1
                      IL_0000:  ldarg.0
                      IL_0001:  ret
                    }
                    """);
            }
        }
    }
}
