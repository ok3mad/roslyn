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
    /// Sharper: purity, slice 1 (<c>[Pure]</c> on members, declared only, #173).
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void Pure_Fib()
        {
            var source = """
                using System;

                Console.WriteLine(MathEx.Fib(10));
                Console.WriteLine(MathEx.FibLoop(10));

                static class MathEx
                {
                    [Pure] public static int Fib(int i)
                    {
                        if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
                        return i < 2 ? i : Fib(i - 1) + Fib(i - 2);
                    }

                    [Pure] public static int FibLoop(int i)
                    {
                        if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
                        int a = 0, b = 1;
                        for (int k = 0; k < i; k++)
                        {
                            (a, b) = (b, a + b);
                        }

                        return a;
                    }
                }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "55\n55" : null);
        }

        [Fact]
        public void Pure_CSharpTwin()
        {
            var source = """
                static class C
                {
                    [Pure] public static int Twice(int x) => 2 * x;
                }
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (3,6): error CS0246: The type or namespace name 'PureAttribute' could not be found (are you missing a using directive or an assembly reference?)
                //     [Pure] public static int Twice(int x) => 2 * x;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Pure").WithArguments("PureAttribute").WithLocation(3, 6),
                // (3,6): error CS0246: The type or namespace name 'Pure' could not be found (are you missing a using directive or an assembly reference?)
                //     [Pure] public static int Twice(int x) => 2 * x;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Pure").WithArguments("Pure").WithLocation(3, 6));
        }

        [Fact]
        public void Pure_Allowed()
        {
            var source = """
                using System;
                using System.Collections.Immutable;

                [Immutable] sealed record Money(decimal Amount, string Currency)
                {
                    public decimal Doubled => Amount * 2;
                }

                class Counter
                {
                    private int _count;
                    private readonly string _name = "";
                    private int[] _items = new int[1];
                    public int Auto { get; set; }
                    private const int Limit = 10;
                    private static readonly string s_prefix = "c";
                    private static readonly ImmutableArray<int> s_primes = [2, 3, 5];

                    [Pure] public static int Twice(int x) => 2 * x;
                    [Pure] public int Next() => _count + 1;
                    [Pure] public string Label() => s_prefix + _name.ToUpperInvariant();
                    [Pure] public int Read() => Auto + _items.Length + Limit + s_primes[0];
                    [Pure] public int Count { get => _count; set => _count = value; }
                    public int Other { [Pure] get => _count; }
                    [Pure] public int CallsPure() => Next() + Twice(Count) + Other;

                    [Pure] public static int Sum(int[] xs)
                    {
                        int sum = 0;
                        foreach (var x in xs) sum += x;
                        for (int i = 0; i < xs.Length; i++) sum += xs[i];
                        return sum;
                    }

                    [Pure] public static int Letters(string s)
                    {
                        int n = 0;
                        foreach (char c in s)
                        {
                            switch (c)
                            {
                                case ' ': continue;
                                default: n++; break;
                            }
                        }

                        return n + s.IndexOf('x') + s.Substring(1).Length + (s.Contains("ab") ? 1 : 0);
                    }

                    [Pure] public static bool TryHalf(int x, out int half)
                    {
                        if (x % 2 != 0)
                        {
                            half = 0;
                            return false;
                        }

                        half = x / 2;
                        return true;
                    }

                    [Pure] public static int UsesTry(int x)
                    {
                        _ = TryHalf(x, out var h);
                        TryHalf(h, out int h2);
                        TryHalf(h2, out _);
                        try
                        {
                            return Math.Max(Fail(x), h2);
                        }
                        catch (InvalidOperationException)
                        {
                            return -1;
                        }
                        finally
                        {
                            h = 0;
                        }
                    }

                    [Pure] public static int Fail(int x) => x > 0 ? x : throw new InvalidOperationException("negative");

                    [Pure] public static Money Make(decimal amount) => new Money(amount, "EUR");

                    [Pure] public static Counter Fresh() => new Counter(1);

                    [Pure] public Counter(int count) { _count = count; }

                    [Pure] public static decimal Observe(Money m) => m.Doubled + m.Amount;

                    [Pure] public static string Text(string a, char b) => $"{a}{b}" + a + b;

                    public static int Outer(int x)
                    {
                        [Pure] int Local(int y) => y * x;
                        [Pure] static int Static(int y) => Twice(y);
                        return Local(x) + Static(x);
                    }

                    [Pure] public static object Box(int x) => x;
                }

                readonly struct Point(int x, int y)
                {
                    [Pure] public int Sum() => x + y;
                }

                struct Mutable
                {
                    public int X;
                    [Pure] public readonly int Doubled() => X * 2;
                    [Pure] public int Auto { get; set; }
                    [Pure] public Mutable(int x) { X = x; Auto = x; }
                    [Pure] public static Mutable With(Mutable m, int x) { m.X = x; return m; }
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics();
        }

        [Fact]
        public void Pure_Writes()
        {
            var source = """
                class Box { public int X; }
                class C
                {
                    private int _x;
                    private static int s_x;
                    private int[] _xs = [];

                    [Pure] int WriteThis() => _x = 1;
                    [Pure] int Increment() => _x++;
                    [Pure] static int WriteArgument(Box b) => b.X = 1;
                    [Pure] static int WriteElement(int[] xs) => xs[0] = 1;
                    [Pure] static int WriteRef(ref int x) => x = 1;
                    [Pure] static int WriteStatic() => s_x = 1;
                    [Pure] int WriteRefLocal() { ref int r = ref _x; r = 1; return r; }
                    [Pure] static bool TryGet(out int x) { x = 1; return true; }
                    [Pure] bool OutToField() => TryGet(out _x);
                    [Pure] bool OutToElement() => TryGet(out _xs[0]);
                    int Captured()
                    {
                        int count = 0;
                        [Pure] int Local() => count++;
                        return Local();
                    }
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (5,24): warning CS0414: The field 'C.s_x' is assigned but its value is never used
                //     private static int s_x;
                Diagnostic(ErrorCode.WRN_UnreferencedFieldAssg, "s_x").WithArguments("C.s_x").WithLocation(5, 24),
                // (8,31): error CS20046: 'C.WriteThis()' is marked [Pure], but it is not pure: '_x = 1' writes state outside the member.
                //     [Pure] int WriteThis() => _x = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_x = 1").WithArguments("C.WriteThis()", "'_x = 1' writes state outside the member").WithLocation(8, 31),
                // (9,31): error CS20046: 'C.Increment()' is marked [Pure], but it is not pure: '_x++' writes state outside the member.
                //     [Pure] int Increment() => _x++;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_x++").WithArguments("C.Increment()", "'_x++' writes state outside the member").WithLocation(9, 31),
                // (10,47): error CS20046: 'C.WriteArgument(Box)' is marked [Pure], but it is not pure: 'b.X = 1' writes state outside the member.
                //     [Pure] static int WriteArgument(Box b) => b.X = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "b.X = 1").WithArguments("C.WriteArgument(Box)", "'b.X = 1' writes state outside the member").WithLocation(10, 47),
                // (11,49): error CS20046: 'C.WriteElement(int[])' is marked [Pure], but it is not pure: 'xs[0] = 1' writes state outside the member.
                //     [Pure] static int WriteElement(int[] xs) => xs[0] = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "xs[0] = 1").WithArguments("C.WriteElement(int[])", "'xs[0] = 1' writes state outside the member").WithLocation(11, 49),
                // (12,46): error CS20046: 'C.WriteRef(ref int)' is marked [Pure], but it is not pure: 'x = 1' writes state outside the member.
                //     [Pure] static int WriteRef(ref int x) => x = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x = 1").WithArguments("C.WriteRef(ref int)", "'x = 1' writes state outside the member").WithLocation(12, 46),
                // (13,40): error CS20046: 'C.WriteStatic()' is marked [Pure], but it is not pure: 's_x = 1' writes state outside the member.
                //     [Pure] static int WriteStatic() => s_x = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_x = 1").WithArguments("C.WriteStatic()", "'s_x = 1' writes state outside the member").WithLocation(13, 40),
                // (14,54): error CS20046: 'C.WriteRefLocal()' is marked [Pure], but it is not pure: 'r = 1' writes state outside the member.
                //     [Pure] int WriteRefLocal() { ref int r = ref _x; r = 1; return r; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "r = 1").WithArguments("C.WriteRefLocal()", "'r = 1' writes state outside the member").WithLocation(14, 54),
                // (16,44): warning CS20049: The out argument '_x' of the [Pure] member 'C.TryGet(out int)' is not a fresh local, so 'C.TryGet(out int)' may observe its own write through another argument.
                //     [Pure] bool OutToField() => TryGet(out _x);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_x").WithArguments("C.TryGet(out int)", "_x").WithLocation(16, 44),
                // (16,44): error CS20046: 'C.OutToField()' is marked [Pure], but it is not pure: '_x' writes state outside the member.
                //     [Pure] bool OutToField() => TryGet(out _x);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_x").WithArguments("C.OutToField()", "'_x' writes state outside the member").WithLocation(16, 44),
                // (17,46): warning CS20049: The out argument '_xs[0]' of the [Pure] member 'C.TryGet(out int)' is not a fresh local, so 'C.TryGet(out int)' may observe its own write through another argument.
                //     [Pure] bool OutToElement() => TryGet(out _xs[0]);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_xs[0]").WithArguments("C.TryGet(out int)", "_xs[0]").WithLocation(17, 46),
                // (17,46): error CS20046: 'C.OutToElement()' is marked [Pure], but it is not pure: '_xs[0]' writes state outside the member.
                //     [Pure] bool OutToElement() => TryGet(out _xs[0]);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_xs[0]").WithArguments("C.OutToElement()", "'_xs[0]' writes state outside the member").WithLocation(17, 46),
                // (21,31): error CS20046: 'Local()' is marked [Pure], but it is not pure: 'count++' writes state outside the member.
                //         [Pure] int Local() => count++;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "count++").WithArguments("Local()", "'count++' writes state outside the member").WithLocation(21, 31));
        }

        [Fact]
        public void Pure_Reads()
        {
            var source = """
                using System.Collections.Generic;
                class C
                {
                    private static int s_mutable;
                    private static readonly List<int> s_list = [];
                    private static readonly int[] s_array = [1];
                    private static int Static { get; set; }
                    private static int StaticGetOnly { get; } = 1;
                    private static List<int> StaticList { get; } = [];

                    [Pure] static int ReadStatic() => s_mutable;
                    [Pure] static List<int> ReadList() => s_list;
                    [Pure] static int ReadArray() => s_array[0];
                    [Pure] static int ReadStaticProperty() => Static;
                    [Pure] static int ReadStaticGetOnly() => StaticGetOnly;
                    [Pure] static List<int> ReadStaticList() => StaticList;
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (4,24): warning CS0649: Field 'C.s_mutable' is never assigned to, and will always have its default value 0
                //     private static int s_mutable;
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "s_mutable").WithArguments("C.s_mutable", "0").WithLocation(4, 24),
                // (11,39): error CS20046: 'C.ReadStatic()' is marked [Pure], but it is not pure: 's_mutable' reads static state that can change.
                //     [Pure] static int ReadStatic() => s_mutable;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_mutable").WithArguments("C.ReadStatic()", "'s_mutable' reads static state that can change").WithLocation(11, 39),
                // (12,43): error CS20046: 'C.ReadList()' is marked [Pure], but it is not pure: 's_list' reads static state that can change.
                //     [Pure] static List<int> ReadList() => s_list;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_list").WithArguments("C.ReadList()", "'s_list' reads static state that can change").WithLocation(12, 43),
                // (13,38): error CS20046: 'C.ReadArray()' is marked [Pure], but it is not pure: 's_array' reads static state that can change.
                //     [Pure] static int ReadArray() => s_array[0];
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_array").WithArguments("C.ReadArray()", "'s_array' reads static state that can change").WithLocation(13, 38),
                // (14,47): error CS20046: 'C.ReadStaticProperty()' is marked [Pure], but it is not pure: 'Static' calls 'C.Static', which is not known to be pure.
                //     [Pure] static int ReadStaticProperty() => Static;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Static").WithArguments("C.ReadStaticProperty()", "'Static' calls 'C.Static', which is not known to be pure").WithLocation(14, 47),
                // (16,49): error CS20046: 'C.ReadStaticList()' is marked [Pure], but it is not pure: 'StaticList' calls 'C.StaticList', which is not known to be pure.
                //     [Pure] static List<int> ReadStaticList() => StaticList;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "StaticList").WithArguments("C.ReadStaticList()", "'StaticList' calls 'C.StaticList', which is not known to be pure").WithLocation(16, 49));
        }

        [Fact]
        public void Pure_Calls()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                class Base { public virtual int V() => 1; }
                class C : Base
                {
                    int Unmarked() => 1;
                    C() { }
                    C(int x) { }
                    [Pure] int CallsUnmarked() => Unmarked();
                    [Pure] int CallsVirtual(Base b) => b.V();
                    [Pure] static int Invoke(Func<int> f) => f();
                    [Pure] static Func<int> Lambda() => () => 1;
                    [Pure] static string Format(int x) => x.ToString();
                    [Pure] static string Interpolate(int x) => $"{x}";
                    [Pure] static string Concat(string s, int x) => s + x;
                    [Pure] static bool StartsWith(string s) => s.StartsWith("a");
                    [Pure] static bool Identity(object a, object b) => ReferenceEquals(a, b);
                    [Pure] static bool ReferenceEqual(C a, C b) => a == b;
                    [Pure] static void Print() => Console.WriteLine();
                    [Pure] static List<int> NewList() => new List<int>();
                    [Pure] static C NewUnmarked() => new C(1);
                    [Pure] static C WithInitializer() => new C { };
                    [Pure] static DateTime Now() => DateTime.Now;
                    [Pure] static int Lock(object o) { lock (o) { return 1; } }
                    [Pure] static async System.Threading.Tasks.Task<int> Await(System.Threading.Tasks.Task<int> t) => await t;
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (9,35): error CS20046: 'C.CallsUnmarked()' is marked [Pure], but it is not pure: 'Unmarked()' calls 'C.Unmarked()', which is not known to be pure.
                //     [Pure] int CallsUnmarked() => Unmarked();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Unmarked()").WithArguments("C.CallsUnmarked()", "'Unmarked()' calls 'C.Unmarked()', which is not known to be pure").WithLocation(9, 35),
                // (10,40): error CS20046: 'C.CallsVirtual(Base)' is marked [Pure], but it is not pure: 'b.V()' calls 'Base.V()', which is not known to be pure.
                //     [Pure] int CallsVirtual(Base b) => b.V();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "b.V()").WithArguments("C.CallsVirtual(Base)", "'b.V()' calls 'Base.V()', which is not known to be pure").WithLocation(10, 40),
                // (11,46): error CS20046: 'C.Invoke(Func<int>)' is marked [Pure], but it is not pure: 'f()' calls 'System.Func<int>.Invoke()', which is not known to be pure.
                //     [Pure] static int Invoke(Func<int> f) => f();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "f()").WithArguments("C.Invoke(System.Func<int>)", "'f()' calls 'System.Func<int>.Invoke()', which is not known to be pure").WithLocation(11, 46),
                // (12,41): error CS20046: 'C.Lambda()' is marked [Pure], but it is not pure: '() => 1' is not allowed in a pure member.
                //     [Pure] static Func<int> Lambda() => () => 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "() => 1").WithArguments("C.Lambda()", "'() => 1' is not allowed in a pure member").WithLocation(12, 41),
                // (13,43): error CS20046: 'C.Format(int)' is marked [Pure], but it is not pure: 'x.ToString()' calls 'int.ToString()', which is not known to be pure.
                //     [Pure] static string Format(int x) => x.ToString();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x.ToString()").WithArguments("C.Format(int)", "'x.ToString()' calls 'int.ToString()', which is not known to be pure").WithLocation(13, 43),
                // (14,51): error CS20046: 'C.Interpolate(int)' is marked [Pure], but it is not pure: 'x' formats with the current culture.
                //     [Pure] static string Interpolate(int x) => $"{x}";
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x").WithArguments("C.Interpolate(int)", "'x' formats with the current culture").WithLocation(14, 51),
                // (15,53): error CS20046: 'C.Concat(string, int)' is marked [Pure], but it is not pure: 's + x' formats with the current culture.
                //     [Pure] static string Concat(string s, int x) => s + x;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s + x").WithArguments("C.Concat(string, int)", "'s + x' formats with the current culture").WithLocation(15, 53),
                // (16,48): error CS20046: 'C.StartsWith(string)' is marked [Pure], but it is not pure: 's.StartsWith("a")' calls 'string.StartsWith(string)', which is not known to be pure.
                //     [Pure] static bool StartsWith(string s) => s.StartsWith("a");
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, @"s.StartsWith(""a"")").WithArguments("C.StartsWith(string)", @"'s.StartsWith(""a"")' calls 'string.StartsWith(string)', which is not known to be pure").WithLocation(16, 48),
                // (17,56): error CS20046: 'C.Identity(object, object)' is marked [Pure], but it is not pure: 'ReferenceEquals(a, b)' observes object identity.
                //     [Pure] static bool Identity(object a, object b) => ReferenceEquals(a, b);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "ReferenceEquals(a, b)").WithArguments("C.Identity(object, object)", "'ReferenceEquals(a, b)' observes object identity").WithLocation(17, 56),
                // (18,52): error CS20046: 'C.ReferenceEqual(C, C)' is marked [Pure], but it is not pure: 'a == b' observes object identity.
                //     [Pure] static bool ReferenceEqual(C a, C b) => a == b;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a == b").WithArguments("C.ReferenceEqual(C, C)", "'a == b' observes object identity").WithLocation(18, 52),
                // (19,35): error CS20046: 'C.Print()' is marked [Pure], but it is not pure: 'Console.WriteLine()' calls 'System.Console.WriteLine()', which is not known to be pure.
                //     [Pure] static void Print() => Console.WriteLine();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Console.WriteLine()").WithArguments("C.Print()", "'Console.WriteLine()' calls 'System.Console.WriteLine()', which is not known to be pure").WithLocation(19, 35),
                // (20,42): error CS20046: 'C.NewList()' is marked [Pure], but it is not pure: 'new List<int>()' calls 'System.Collections.Generic.List<int>.List()', which is not known to be pure.
                //     [Pure] static List<int> NewList() => new List<int>();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "new List<int>()").WithArguments("C.NewList()", "'new List<int>()' calls 'System.Collections.Generic.List<int>.List()', which is not known to be pure").WithLocation(20, 42),
                // (21,38): error CS20046: 'C.NewUnmarked()' is marked [Pure], but it is not pure: 'new C(1)' calls 'C.C(int)', which is not known to be pure.
                //     [Pure] static C NewUnmarked() => new C(1);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "new C(1)").WithArguments("C.NewUnmarked()", "'new C(1)' calls 'C.C(int)', which is not known to be pure").WithLocation(21, 38),
                // (22,42): error CS20046: 'C.WithInitializer()' is marked [Pure], but it is not pure: 'new C { }' is not allowed in a pure member.
                //     [Pure] static C WithInitializer() => new C { };
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "new C { }").WithArguments("C.WithInitializer()", "'new C { }' is not allowed in a pure member").WithLocation(22, 42),
                // (23,37): error CS20046: 'C.Now()' is marked [Pure], but it is not pure: 'DateTime.Now' calls 'System.DateTime.Now', which is not known to be pure.
                //     [Pure] static DateTime Now() => DateTime.Now;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "DateTime.Now").WithArguments("C.Now()", "'DateTime.Now' calls 'System.DateTime.Now', which is not known to be pure").WithLocation(23, 37),
                // (24,40): error CS20046: 'C.Lock(object)' is marked [Pure], but it is not pure: 'lock (o) { return 1; }' is not allowed in a pure member.
                //     [Pure] static int Lock(object o) { lock (o) { return 1; } }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "lock (o) { return 1; }").WithArguments("C.Lock(object)", "'lock (o) { return 1; }' is not allowed in a pure member").WithLocation(24, 40),
                // (25,103): error CS20046: 'C.Await(Task<int>)' is marked [Pure], but it is not pure: 'await t' is not allowed in a pure member.
                //     [Pure] static async System.Threading.Tasks.Task<int> Await(System.Threading.Tasks.Task<int> t) => await t;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "await t").WithArguments("C.Await(System.Threading.Tasks.Task<int>)", "'await t' is not allowed in a pure member").WithLocation(25, 103));
        }

        [Fact]
        public void Pure_Declarations()
        {
            var source = """
                abstract class Base
                {
                    [Pure] public virtual int V() => 1;
                    [Pure] public abstract int A();
                    public virtual int O() => 1;
                }

                class Derived : Base
                {
                    [Pure] public override int A() => 1;
                    [Pure] public sealed override int O() => 1;
                }

                interface I
                {
                    [Pure] int M();
                    [Pure] static int S() => 1;
                }

                struct S
                {
                    int _x;
                    [Pure] public int Get() => _x;
                    [Pure] public int Property => _x;
                    [Pure] public readonly int ReadOnlyGet() => _x;
                    [Pure] public int Auto { get; }
                    [Pure] public static int Static() => 1;
                    [Pure] public S(int x) { _x = x; Auto = x; }
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (3,31): error CS20048: 'Base.V()' cannot be marked [Pure] yet: [Pure] is not supported on virtual, abstract and override members.
                //     [Pure] public virtual int V() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureVirtual, "V").WithArguments("Base.V()").WithLocation(3, 31),
                // (4,32): error CS20048: 'Base.A()' cannot be marked [Pure] yet: [Pure] is not supported on virtual, abstract and override members.
                //     [Pure] public abstract int A();
                Diagnostic(ErrorCode.ERR_SharperPureVirtual, "A").WithArguments("Base.A()").WithLocation(4, 32),
                // (10,32): error CS20048: 'Derived.A()' cannot be marked [Pure] yet: [Pure] is not supported on virtual, abstract and override members.
                //     [Pure] public override int A() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureVirtual, "A").WithArguments("Derived.A()").WithLocation(10, 32),
                // (11,39): error CS20048: 'Derived.O()' cannot be marked [Pure] yet: [Pure] is not supported on virtual, abstract and override members.
                //     [Pure] public sealed override int O() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureVirtual, "O").WithArguments("Derived.O()").WithLocation(11, 39),
                // (16,16): error CS20048: 'I.M()' cannot be marked [Pure] yet: [Pure] is not supported on virtual, abstract and override members.
                //     [Pure] int M();
                Diagnostic(ErrorCode.ERR_SharperPureVirtual, "M").WithArguments("I.M()").WithLocation(16, 16),
                // (23,23): error CS20047: 'S.Get()' is marked [Pure], so it must be readonly: declare it 'readonly', or make the struct 'readonly'.
                //     [Pure] public int Get() => _x;
                Diagnostic(ErrorCode.ERR_SharperPureStructMemberNotReadOnly, "Get").WithArguments("S.Get()").WithLocation(23, 23),
                // (24,23): error CS20047: 'S.Property' is marked [Pure], so it must be readonly: declare it 'readonly', or make the struct 'readonly'.
                //     [Pure] public int Property => _x;
                Diagnostic(ErrorCode.ERR_SharperPureStructMemberNotReadOnly, "Property").WithArguments("S.Property").WithLocation(24, 23));
        }

        [Fact]
        public void Pure_OutArgumentWarning()
        {
            var source = """
                class Box { public int X; }
                class C
                {
                    int _field;
                    [Pure] static bool TryGet(Box b, out int x) { x = b.X; return true; }
                    static bool TryUnmarked(Box b, out int x) { x = b.X; return true; }

                    void M(Box box, int[] xs, int p, out int o)
                    {
                        int local;
                        TryGet(box, out local);
                        TryGet(box, out var declared);
                        TryGet(box, out _);
                        TryGet(box, out p);
                        TryGet(box, out box.X);
                        TryGet(box, out _field);
                        TryGet(box, out xs[0]);
                        ref int r = ref box.X;
                        TryGet(box, out r);
                        TryGet(box, out o);
                        TryUnmarked(box, out box.X);
                    }

                    [Pure] static bool Wrapper(Box b, out int x) => TryGet(b, out x);
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (15,25): warning CS20049: The out argument 'box.X' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out box.X);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "box.X").WithArguments("C.TryGet(Box, out int)", "box.X").WithLocation(15, 25),
                // (16,25): warning CS20049: The out argument '_field' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out _field);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_field").WithArguments("C.TryGet(Box, out int)", "_field").WithLocation(16, 25),
                // (17,25): warning CS20049: The out argument 'xs[0]' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out xs[0]);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "xs[0]").WithArguments("C.TryGet(Box, out int)", "xs[0]").WithLocation(17, 25),
                // (19,25): warning CS20049: The out argument 'r' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out r);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "r").WithArguments("C.TryGet(Box, out int)", "r").WithLocation(19, 25),
                // (20,25): warning CS20049: The out argument 'o' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out o);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "o").WithArguments("C.TryGet(Box, out int)", "o").WithLocation(20, 25));
        }

        [Fact]
        public void Pure_OutArgumentWarning_CSharpTwin()
        {
            var library = """
                namespace Sharper { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class PureAttribute : System.Attribute { } }
                public static class Lib
                {
                    [Sharper.Pure] public static bool TryGet(out int x) { x = 1; return true; }
                }
                """;
            var libraryComp = CreateImmutableCompilation(library);
            libraryComp.VerifyDiagnostics();

            var source = """
                class C
                {
                    int _field;
                    void M() => Lib.TryGet(out _field);
                }
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular, references: [libraryComp.EmitToImageReference()]).VerifyDiagnostics();
            CreateImmutableCompilation(source, references: [libraryComp.EmitToImageReference()]).VerifyDiagnostics(
                // (4,32): warning CS20049: The out argument '_field' of the [Pure] member 'Lib.TryGet(out int)' is not a fresh local, so 'Lib.TryGet(out int)' may observe its own write through another argument.
                //     void M() => Lib.TryGet(out _field);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_field").WithArguments("Lib.TryGet(out int)", "_field").WithLocation(4, 32));
        }

        [Fact]
        public void Pure_Constructors()
        {
            var source = """
                using System;
                class Base
                {
                    public Base() { }
                    [Pure] public Base(int x) { }
                }

                class C : Base
                {
                    private static int s_count;
                    private readonly int _x;
                    private readonly int _y = Compute();

                    static int Compute() => 1;

                    [Pure] C(int x) : base(x) { _x = x; }
                    [Pure] C(string s) : base() { _x = s.Length; }
                    [Pure] C(long x) : this((int)x) { s_count++; }
                }

                class Initializers
                {
                    private readonly int _x = Twice(1);
                    [Pure] static int Twice(int x) => x * 2;
                    [Pure] Initializers() { }
                }

                class MyException(string message) : Exception(message)
                {
                    [Pure] public static MyException Make() => new MyException("x");
                }

                [method: Pure] class Primary(int x)
                {
                    private readonly int _x = x;
                    private readonly int _y = Impure();
                    static int Impure() => 1;
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (12,31): error CS20046: 'C.C(int)' is marked [Pure], but it is not pure: 'Compute()' calls 'C.Compute()', which is not known to be pure.
                //     private readonly int _y = Compute();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Compute()").WithArguments("C.C(int)", "'Compute()' calls 'C.Compute()', which is not known to be pure").WithLocation(12, 31),
                // (17,24): error CS20046: 'C.C(string)' is marked [Pure], but it is not pure: ': base()' calls 'Base.Base()', which is not known to be pure.
                //     [Pure] C(string s) : base() { _x = s.Length; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, ": base()").WithArguments("C.C(string)", "': base()' calls 'Base.Base()', which is not known to be pure").WithLocation(17, 24),
                // (18,39): error CS20046: 'C.C(long)' is marked [Pure], but it is not pure: 's_count++' writes state outside the member.
                //     [Pure] C(long x) : this((int)x) { s_count++; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_count++").WithArguments("C.C(long)", "'s_count++' writes state outside the member").WithLocation(18, 39),
                // (36,31): error CS20046: 'Primary.Primary(int)' is marked [Pure], but it is not pure: 'Impure()' calls 'Primary.Impure()', which is not known to be pure.
                //     private readonly int _y = Impure();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Impure()").WithArguments("Primary.Primary(int)", "'Impure()' calls 'Primary.Impure()', which is not known to be pure").WithLocation(36, 31));
        }

        [Fact]
        public void Pure_Metadata()
        {
            var library = """
                public static class Lib
                {
                    [Pure] public static int Twice(int x) => 2 * x;
                    public static int Unmarked(int x) => x;
                }
                """;
            var libraryComp = CreateImmutableCompilation(library);
            libraryComp.VerifyDiagnostics();

            var source = """
                static class C
                {
                    [Pure] static int Calls(int x) => Lib.Twice(x);
                    [Pure] static int CallsUnmarked(int x) => Lib.Unmarked(x);
                }
                """;
            CreateImmutableCompilation(source, references: [libraryComp.EmitToImageReference()]).VerifyDiagnostics(
                // (4,47): error CS20046: 'C.CallsUnmarked(int)' is marked [Pure], but it is not pure: 'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure.
                //     [Pure] static int CallsUnmarked(int x) => Lib.Unmarked(x);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Lib.Unmarked(x)").WithArguments("C.CallsUnmarked(int)", "'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure").WithLocation(4, 47));
            CreateImmutableCompilation(source, references: [libraryComp.ToMetadataReference()]).VerifyDiagnostics(
                // (4,47): error CS20046: 'C.CallsUnmarked(int)' is marked [Pure], but it is not pure: 'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure.
                //     [Pure] static int CallsUnmarked(int x) => Lib.Unmarked(x);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Lib.Unmarked(x)").WithArguments("C.CallsUnmarked(int)", "'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure").WithLocation(4, 47));

            CompileAndVerify(libraryComp, symbolValidator: module =>
            {
                var attribute = module.ContainingAssembly.GetTypeByMetadataName("Sharper.PureAttribute");
                Assert.NotNull(attribute);
                var twice = module.GlobalNamespace.GetMember<MethodSymbol>("Lib.Twice");
                Assert.True(SharperImmutability.IsDeclaredPure(twice));
            });
        }
    }
}
