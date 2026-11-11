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
                // (8,31): error CS20050: 'C.WriteThis()' is marked [Pure], but it is not pure: '_x = 1' writes state outside the member.
                //     [Pure] int WriteThis() => _x = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_x = 1").WithArguments("C.WriteThis()", "'_x = 1' writes state outside the member").WithLocation(8, 31),
                // (9,31): error CS20050: 'C.Increment()' is marked [Pure], but it is not pure: '_x++' writes state outside the member.
                //     [Pure] int Increment() => _x++;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_x++").WithArguments("C.Increment()", "'_x++' writes state outside the member").WithLocation(9, 31),
                // (10,47): error CS20050: 'C.WriteArgument(Box)' is marked [Pure], but it is not pure: 'b.X = 1' writes state outside the member.
                //     [Pure] static int WriteArgument(Box b) => b.X = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "b.X = 1").WithArguments("C.WriteArgument(Box)", "'b.X = 1' writes state outside the member").WithLocation(10, 47),
                // (11,49): error CS20050: 'C.WriteElement(int[])' is marked [Pure], but it is not pure: 'xs[0] = 1' writes state outside the member.
                //     [Pure] static int WriteElement(int[] xs) => xs[0] = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "xs[0] = 1").WithArguments("C.WriteElement(int[])", "'xs[0] = 1' writes state outside the member").WithLocation(11, 49),
                // (12,46): error CS20050: 'C.WriteRef(ref int)' is marked [Pure], but it is not pure: 'x = 1' writes state outside the member.
                //     [Pure] static int WriteRef(ref int x) => x = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x = 1").WithArguments("C.WriteRef(ref int)", "'x = 1' writes state outside the member").WithLocation(12, 46),
                // (13,40): error CS20050: 'C.WriteStatic()' is marked [Pure], but it is not pure: 's_x = 1' writes state outside the member.
                //     [Pure] static int WriteStatic() => s_x = 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_x = 1").WithArguments("C.WriteStatic()", "'s_x = 1' writes state outside the member").WithLocation(13, 40),
                // (14,54): error CS20050: 'C.WriteRefLocal()' is marked [Pure], but it is not pure: 'r = 1' writes state outside the member.
                //     [Pure] int WriteRefLocal() { ref int r = ref _x; r = 1; return r; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "r = 1").WithArguments("C.WriteRefLocal()", "'r = 1' writes state outside the member").WithLocation(14, 54),
                // (16,44): warning CS20053: The out argument '_x' of the [Pure] member 'C.TryGet(out int)' is not a fresh local, so 'C.TryGet(out int)' may observe its own write through another argument.
                //     [Pure] bool OutToField() => TryGet(out _x);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_x").WithArguments("C.TryGet(out int)", "_x").WithLocation(16, 44),
                // (16,44): error CS20050: 'C.OutToField()' is marked [Pure], but it is not pure: '_x' writes state outside the member.
                //     [Pure] bool OutToField() => TryGet(out _x);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_x").WithArguments("C.OutToField()", "'_x' writes state outside the member").WithLocation(16, 44),
                // (17,46): warning CS20053: The out argument '_xs[0]' of the [Pure] member 'C.TryGet(out int)' is not a fresh local, so 'C.TryGet(out int)' may observe its own write through another argument.
                //     [Pure] bool OutToElement() => TryGet(out _xs[0]);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_xs[0]").WithArguments("C.TryGet(out int)", "_xs[0]").WithLocation(17, 46),
                // (17,46): error CS20050: 'C.OutToElement()' is marked [Pure], but it is not pure: '_xs[0]' writes state outside the member.
                //     [Pure] bool OutToElement() => TryGet(out _xs[0]);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "_xs[0]").WithArguments("C.OutToElement()", "'_xs[0]' writes state outside the member").WithLocation(17, 46),
                // (21,31): error CS20050: 'Local()' is marked [Pure], but it is not pure: 'count++' writes state outside the member.
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
                // (11,39): error CS20050: 'C.ReadStatic()' is marked [Pure], but it is not pure: 's_mutable' reads static state that can change.
                //     [Pure] static int ReadStatic() => s_mutable;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_mutable").WithArguments("C.ReadStatic()", "'s_mutable' reads static state that can change").WithLocation(11, 39),
                // (12,43): error CS20050: 'C.ReadList()' is marked [Pure], but it is not pure: 's_list' reads static state that can change.
                //     [Pure] static List<int> ReadList() => s_list;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_list").WithArguments("C.ReadList()", "'s_list' reads static state that can change").WithLocation(12, 43),
                // (13,38): error CS20050: 'C.ReadArray()' is marked [Pure], but it is not pure: 's_array' reads static state that can change.
                //     [Pure] static int ReadArray() => s_array[0];
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_array").WithArguments("C.ReadArray()", "'s_array' reads static state that can change").WithLocation(13, 38),
                // (14,47): error CS20050: 'C.ReadStaticProperty()' is marked [Pure], but it is not pure: 'Static' calls 'C.Static', which is not known to be pure.
                //     [Pure] static int ReadStaticProperty() => Static;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Static").WithArguments("C.ReadStaticProperty()", "'Static' calls 'C.Static', which is not known to be pure").WithLocation(14, 47),
                // (16,49): error CS20050: 'C.ReadStaticList()' is marked [Pure], but it is not pure: 'StaticList' calls 'C.StaticList', which is not known to be pure.
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
                // (9,35): error CS20050: 'C.CallsUnmarked()' is marked [Pure], but it is not pure: 'Unmarked()' calls 'C.Unmarked()', which is not known to be pure.
                //     [Pure] int CallsUnmarked() => Unmarked();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Unmarked()").WithArguments("C.CallsUnmarked()", "'Unmarked()' calls 'C.Unmarked()', which is not known to be pure").WithLocation(9, 35),
                // (10,40): error CS20050: 'C.CallsVirtual(Base)' is marked [Pure], but it is not pure: 'b.V()' calls 'Base.V()', which is not known to be pure.
                //     [Pure] int CallsVirtual(Base b) => b.V();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "b.V()").WithArguments("C.CallsVirtual(Base)", "'b.V()' calls 'Base.V()', which is not known to be pure").WithLocation(10, 40),
                // (11,46): error CS20050: 'C.Invoke(Func<int>)' is marked [Pure], but it is not pure: 'f()' calls 'System.Func<int>.Invoke()', which is not known to be pure.
                //     [Pure] static int Invoke(Func<int> f) => f();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "f()").WithArguments("C.Invoke(System.Func<int>)", "'f()' calls 'System.Func<int>.Invoke()', which is not known to be pure").WithLocation(11, 46),
                // (12,41): error CS20050: 'C.Lambda()' is marked [Pure], but it is not pure: '() => 1' is not allowed in a pure member.
                //     [Pure] static Func<int> Lambda() => () => 1;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "() => 1").WithArguments("C.Lambda()", "'() => 1' is not allowed in a pure member").WithLocation(12, 41),
                // (13,43): error CS20050: 'C.Format(int)' is marked [Pure], but it is not pure: 'x.ToString()' calls 'int.ToString()', which is not known to be pure.
                //     [Pure] static string Format(int x) => x.ToString();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x.ToString()").WithArguments("C.Format(int)", "'x.ToString()' calls 'int.ToString()', which is not known to be pure").WithLocation(13, 43),
                // (14,51): error CS20050: 'C.Interpolate(int)' is marked [Pure], but it is not pure: 'x' formats with the current culture.
                //     [Pure] static string Interpolate(int x) => $"{x}";
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x").WithArguments("C.Interpolate(int)", "'x' formats with the current culture").WithLocation(14, 51),
                // (15,53): error CS20050: 'C.Concat(string, int)' is marked [Pure], but it is not pure: 's + x' formats with the current culture.
                //     [Pure] static string Concat(string s, int x) => s + x;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s + x").WithArguments("C.Concat(string, int)", "'s + x' formats with the current culture").WithLocation(15, 53),
                // (16,48): error CS20050: 'C.StartsWith(string)' is marked [Pure], but it is not pure: 's.StartsWith("a")' calls 'string.StartsWith(string)', which is not known to be pure.
                //     [Pure] static bool StartsWith(string s) => s.StartsWith("a");
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, @"s.StartsWith(""a"")").WithArguments("C.StartsWith(string)", @"'s.StartsWith(""a"")' calls 'string.StartsWith(string)', which is not known to be pure").WithLocation(16, 48),
                // (17,56): error CS20050: 'C.Identity(object, object)' is marked [Pure], but it is not pure: 'ReferenceEquals(a, b)' observes object identity.
                //     [Pure] static bool Identity(object a, object b) => ReferenceEquals(a, b);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "ReferenceEquals(a, b)").WithArguments("C.Identity(object, object)", "'ReferenceEquals(a, b)' observes object identity").WithLocation(17, 56),
                // (18,52): error CS20050: 'C.ReferenceEqual(C, C)' is marked [Pure], but it is not pure: 'a == b' observes object identity.
                //     [Pure] static bool ReferenceEqual(C a, C b) => a == b;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a == b").WithArguments("C.ReferenceEqual(C, C)", "'a == b' observes object identity").WithLocation(18, 52),
                // (19,35): error CS20050: 'C.Print()' is marked [Pure], but it is not pure: 'Console.WriteLine()' calls 'System.Console.WriteLine()', which is not known to be pure.
                //     [Pure] static void Print() => Console.WriteLine();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Console.WriteLine()").WithArguments("C.Print()", "'Console.WriteLine()' calls 'System.Console.WriteLine()', which is not known to be pure").WithLocation(19, 35),
                // (20,42): error CS20050: 'C.NewList()' is marked [Pure], but it is not pure: 'new List<int>()' calls 'System.Collections.Generic.List<int>.List()', which is not known to be pure.
                //     [Pure] static List<int> NewList() => new List<int>();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "new List<int>()").WithArguments("C.NewList()", "'new List<int>()' calls 'System.Collections.Generic.List<int>.List()', which is not known to be pure").WithLocation(20, 42),
                // (21,38): error CS20050: 'C.NewUnmarked()' is marked [Pure], but it is not pure: 'new C(1)' calls 'C.C(int)', which is not known to be pure.
                //     [Pure] static C NewUnmarked() => new C(1);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "new C(1)").WithArguments("C.NewUnmarked()", "'new C(1)' calls 'C.C(int)', which is not known to be pure").WithLocation(21, 38),
                // (22,42): error CS20050: 'C.WithInitializer()' is marked [Pure], but it is not pure: 'new C { }' calls 'C.C()', which is not known to be pure.
                //     [Pure] static C WithInitializer() => new C { };
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "new C { }").WithArguments("C.WithInitializer()", "'new C { }' calls 'C.C()', which is not known to be pure").WithLocation(22, 42),
                // (23,37): error CS20050: 'C.Now()' is marked [Pure], but it is not pure: 'DateTime.Now' calls 'System.DateTime.Now', which is not known to be pure.
                //     [Pure] static DateTime Now() => DateTime.Now;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "DateTime.Now").WithArguments("C.Now()", "'DateTime.Now' calls 'System.DateTime.Now', which is not known to be pure").WithLocation(23, 37),
                // (24,40): error CS20050: 'C.Lock(object)' is marked [Pure], but it is not pure: 'lock (o) { return 1; }' is not allowed in a pure member.
                //     [Pure] static int Lock(object o) { lock (o) { return 1; } }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "lock (o) { return 1; }").WithArguments("C.Lock(object)", "'lock (o) { return 1; }' is not allowed in a pure member").WithLocation(24, 40),
                // (25,103): error CS20050: 'C.Await(Task<int>)' is marked [Pure], but it is not pure: 'await t' is not allowed in a pure member.
                //     [Pure] static async System.Threading.Tasks.Task<int> Await(System.Threading.Tasks.Task<int> t) => await t;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "await t").WithArguments("C.Await(System.Threading.Tasks.Task<int>)", "'await t' is not allowed in a pure member").WithLocation(25, 103));
        }

        [Fact]
        public void Pure_IdentityOfImmutableArray()
        {
            // ImmutableArray<T>'s Equals, GetHashCode, == and != compare the underlying array by reference (#185).
            var source = """
                using System.Collections.Immutable;
                static class C
                {
                    [Pure] static bool Same(ImmutableArray<int> a, ImmutableArray<int> b) => a.Equals(b);
                    [Pure] static bool SameObject(ImmutableArray<int> a, object b) => a.Equals(b);
                    [Pure] static int Hash(ImmutableArray<int> a) => a.GetHashCode();
                    [Pure] static bool Operator(ImmutableArray<int> a, ImmutableArray<int> b) => a == b;
                    [Pure] static bool TupleEquals((ImmutableArray<int>, int) a) => a.Equals(a);
                    [Pure] static bool Ok(ImmutableArray<int> a, ImmutableList<int> l, (int, string) t) => a.IsDefaultOrEmpty || a.Length == 0 || t.Equals(t) || l.IsEmpty;
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (4,78): error CS20050: 'C.Same(ImmutableArray<int>, ImmutableArray<int>)' is marked [Pure], but it is not pure: 'a.Equals(b)' observes object identity.
                //     [Pure] static bool Same(ImmutableArray<int> a, ImmutableArray<int> b) => a.Equals(b);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a.Equals(b)").WithArguments("C.Same(System.Collections.Immutable.ImmutableArray<int>, System.Collections.Immutable.ImmutableArray<int>)", "'a.Equals(b)' observes object identity").WithLocation(4, 78),
                // (5,71): error CS20050: 'C.SameObject(ImmutableArray<int>, object)' is marked [Pure], but it is not pure: 'a.Equals(b)' observes object identity.
                //     [Pure] static bool SameObject(ImmutableArray<int> a, object b) => a.Equals(b);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a.Equals(b)").WithArguments("C.SameObject(System.Collections.Immutable.ImmutableArray<int>, object)", "'a.Equals(b)' observes object identity").WithLocation(5, 71),
                // (6,54): error CS20050: 'C.Hash(ImmutableArray<int>)' is marked [Pure], but it is not pure: 'a.GetHashCode()' observes object identity.
                //     [Pure] static int Hash(ImmutableArray<int> a) => a.GetHashCode();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a.GetHashCode()").WithArguments("C.Hash(System.Collections.Immutable.ImmutableArray<int>)", "'a.GetHashCode()' observes object identity").WithLocation(6, 54),
                // (7,82): error CS20050: 'C.Operator(ImmutableArray<int>, ImmutableArray<int>)' is marked [Pure], but it is not pure: 'a == b' observes object identity.
                //     [Pure] static bool Operator(ImmutableArray<int> a, ImmutableArray<int> b) => a == b;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a == b").WithArguments("C.Operator(System.Collections.Immutable.ImmutableArray<int>, System.Collections.Immutable.ImmutableArray<int>)", "'a == b' observes object identity").WithLocation(7, 82),
                // (8,69): error CS20050: 'C.TupleEquals((ImmutableArray<int>, int))' is marked [Pure], but it is not pure: 'a.Equals(a)' observes object identity.
                //     [Pure] static bool TupleEquals((ImmutableArray<int>, int) a) => a.Equals(a);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a.Equals(a)").WithArguments("C.TupleEquals((System.Collections.Immutable.ImmutableArray<int>, int))", "'a.Equals(a)' observes object identity").WithLocation(8, 69));
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
                // (23,23): error CS20051: 'S.Get()' is marked [Pure], so it must be readonly: declare it 'readonly', or make the struct 'readonly'.
                //     [Pure] public int Get() => _x;
                Diagnostic(ErrorCode.ERR_SharperPureStructMemberNotReadOnly, "Get").WithArguments("S.Get()").WithLocation(23, 23),
                // (24,23): error CS20051: 'S.Property' is marked [Pure], so it must be readonly: declare it 'readonly', or make the struct 'readonly'.
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
                // (15,25): warning CS20053: The out argument 'box.X' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out box.X);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "box.X").WithArguments("C.TryGet(Box, out int)", "box.X").WithLocation(15, 25),
                // (16,25): warning CS20053: The out argument '_field' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out _field);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "_field").WithArguments("C.TryGet(Box, out int)", "_field").WithLocation(16, 25),
                // (17,25): warning CS20053: The out argument 'xs[0]' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out xs[0]);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "xs[0]").WithArguments("C.TryGet(Box, out int)", "xs[0]").WithLocation(17, 25),
                // (19,25): warning CS20053: The out argument 'r' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
                //         TryGet(box, out r);
                Diagnostic(ErrorCode.WRN_SharperPureOutArgumentNotFresh, "r").WithArguments("C.TryGet(Box, out int)", "r").WithLocation(19, 25),
                // (20,25): warning CS20053: The out argument 'o' of the [Pure] member 'C.TryGet(Box, out int)' is not a fresh local, so 'C.TryGet(Box, out int)' may observe its own write through another argument.
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
                // (4,32): warning CS20053: The out argument '_field' of the [Pure] member 'Lib.TryGet(out int)' is not a fresh local, so 'Lib.TryGet(out int)' may observe its own write through another argument.
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
                // (12,31): error CS20050: 'C.C(int)' is marked [Pure], but it is not pure: 'Compute()' calls 'C.Compute()', which is not known to be pure.
                //     private readonly int _y = Compute();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Compute()").WithArguments("C.C(int)", "'Compute()' calls 'C.Compute()', which is not known to be pure").WithLocation(12, 31),
                // (17,24): error CS20050: 'C.C(string)' is marked [Pure], but it is not pure: ': base()' calls 'Base.Base()', which is not known to be pure.
                //     [Pure] C(string s) : base() { _x = s.Length; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, ": base()").WithArguments("C.C(string)", "': base()' calls 'Base.Base()', which is not known to be pure").WithLocation(17, 24),
                // (18,39): error CS20050: 'C.C(long)' is marked [Pure], but it is not pure: 's_count++' writes state outside the member.
                //     [Pure] C(long x) : this((int)x) { s_count++; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_count++").WithArguments("C.C(long)", "'s_count++' writes state outside the member").WithLocation(18, 39),
                // (36,31): error CS20050: 'Primary.Primary(int)' is marked [Pure], but it is not pure: 'Impure()' calls 'Primary.Impure()', which is not known to be pure.
                //     private readonly int _y = Impure();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Impure()").WithArguments("Primary.Primary(int)", "'Impure()' calls 'Primary.Impure()', which is not known to be pure").WithLocation(36, 31));
        }

        [Fact]
        public void Pure_InitializersAndWith()
        {
            // A pure member does not infer: auto-property setters and synthesized clones are pure, hand-written setters need
            // [AssumePure]. A clone of a record that is not sealed dispatches virtually.
            var source = """
                sealed record R(int A) { public int B { get; init; } }
                record Open(int A);
                class Setter { private int _x; public int X { get => _x; set => _x = value; } }

                static class C
                {
                    [Pure] static R Make(int a) => new R(a) { B = a };
                    [Pure] static R Bump(R r) => r with { A = r.A + 1 };
                    [Pure] static Open Copy(Open o) => o with { A = 1 };
                    [Pure] static Setter Set() => new Setter { X = 1 };
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (9,40): error CS20050: 'C.Copy(Open)' is marked [Pure], but it is not pure: 'o with { A = 1 }' calls 'Open.<Clone>$()', which is not known to be pure.
                //     [Pure] static Open Copy(Open o) => o with { A = 1 };
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "o with { A = 1 }").WithArguments("C.Copy(Open)", "'o with { A = 1 }' calls 'Open.<Clone>$()', which is not known to be pure").WithLocation(9, 40),
                // (10,48): error CS20050: 'C.Set()' is marked [Pure], but it is not pure: 'X = 1' calls 'Setter.X.set', which is not known to be pure.
                //     [Pure] static Setter Set() => new Setter { X = 1 };
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "X = 1").WithArguments("C.Set()", "'X = 1' calls 'Setter.X.set', which is not known to be pure").WithLocation(10, 48));
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
                // (4,47): error CS20050: 'C.CallsUnmarked(int)' is marked [Pure], but it is not pure: 'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure.
                //     [Pure] static int CallsUnmarked(int x) => Lib.Unmarked(x);
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Lib.Unmarked(x)").WithArguments("C.CallsUnmarked(int)", "'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure").WithLocation(4, 47));
            CreateImmutableCompilation(source, references: [libraryComp.ToMetadataReference()]).VerifyDiagnostics(
                // (4,47): error CS20050: 'C.CallsUnmarked(int)' is marked [Pure], but it is not pure: 'Lib.Unmarked(x)' calls 'Lib.Unmarked(int)', which is not known to be pure.
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

        private static CSharpCompilation CreateMixedPurityCompilation(string sharper, string csharp, MetadataReference[]? references = null)
            => CreateCompilation(
                [CSharpTestSource.Parse(sharper, "a.cx", s_sharperOptions), CSharpTestSource.Parse(csharp, "b.cs", TestOptions.Regular)],
                targetFramework: TargetFramework.Net90,
                references: references);

        [Fact]
        public void PureSlot_InterfaceCall()
        {
            var source = """
                using System;

                Console.Write(C.Total([new Square(2), new Square(3)]) + C.Generic(new Square(1)));

                interface IShape
                {
                    [Pure] int Area();
                    [Pure] int Sides { get; }
                    int Unmarked();
                }

                sealed class Square(int side) : IShape
                {
                    [Pure] public int Area() => side * side;
                    [Pure] public int Sides => 4;
                    public int Unmarked() => 0;
                }

                static class C
                {
                    [Pure] public static int Total(IShape[] shapes)
                    {
                        int sum = 0;
                        foreach (var shape in shapes) sum += shape.Area() * shape.Sides;
                        return sum;
                    }

                    [Pure] public static int Generic<T>(T shape) where T : IShape => shape.Area();
                }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "53" : null);
        }

        [Fact]
        public void PureSlot_CSharpTwin()
        {
            // The flag off: the attribute does not exist, and nothing else is reported.
            var source = """
                interface I { [Pure] int M(); }
                abstract class Base { [Pure] public abstract int A(); }
                class Derived : Base, I
                {
                    public override int A() => 1;
                    public int M() => 2;
                }
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (1,16): error CS0246: The type or namespace name 'PureAttribute' could not be found (are you missing a using directive or an assembly reference?)
                // interface I { [Pure] int M(); }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Pure").WithArguments("PureAttribute").WithLocation(1, 16),
                // (1,16): error CS0246: The type or namespace name 'Pure' could not be found (are you missing a using directive or an assembly reference?)
                // interface I { [Pure] int M(); }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Pure").WithArguments("Pure").WithLocation(1, 16),
                // (2,24): error CS0246: The type or namespace name 'PureAttribute' could not be found (are you missing a using directive or an assembly reference?)
                // abstract class Base { [Pure] public abstract int A(); }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Pure").WithArguments("PureAttribute").WithLocation(2, 24),
                // (2,24): error CS0246: The type or namespace name 'Pure' could not be found (are you missing a using directive or an assembly reference?)
                // abstract class Base { [Pure] public abstract int A(); }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Pure").WithArguments("Pure").WithLocation(2, 24));

            // Nor when the compilation declares the attribute itself.
            var declared = """
                namespace Sharper { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class PureAttribute : System.Attribute { } }
                interface I { [Sharper.Pure] int M(); }
                abstract class Base { [Sharper.Pure] public abstract int A(); }
                class Derived : Base, I
                {
                    static int s_count;
                    public override int A() => s_count++;
                    public int M() => s_count++;
                }
                """;
            CreateImmutableCompilation(declared, parseOptions: TestOptions.Regular).VerifyDiagnostics();
        }

        [Fact]
        public void PureSlot_VirtualCall()
        {
            var source = """
                abstract class Base
                {
                    [Pure] public virtual int V() => 1;
                    [Pure] public abstract int A();
                    [Pure] public abstract int P { get; }
                    public abstract int Q { [Pure] get; }
                    [Pure] public abstract int this[int i] { get; }
                    [Pure] public abstract T G<T>(T x);
                    public virtual int Unmarked() => 1;
                    public virtual int Auto { get; }

                    [Pure] public int Calls() => V() + A() + P + Q + this[0] + G(1);
                    [Pure] public static int CallsOn(Base b) => b.V() + b.A() + b.P + b.Q + b[0] + b.G(1);
                    [Pure] public int CallsUnmarked() => Unmarked();
                    [Pure] public int ReadsAuto() => Auto;
                }

                class Derived : Base
                {
                    [Pure] public override int V() => base.V() + 1;
                    [Pure] public override int A() => 2;
                    [Pure] public override int P => 3;
                    public override int Q { [Pure] get => 4; }
                    [Pure] public override int this[int i] => i;
                    [Pure] public override T G<T>(T x) => x;
                    [Pure] public override int Unmarked() => 5;

                    [Pure] public static int CallsOn(Derived d) => d.V() + d.A() + d.P + d.Q + d[0] + d.G(1) + d.Unmarked();
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (14,42): error CS20050: 'Base.CallsUnmarked()' is marked [Pure], but it is not pure: 'Unmarked()' calls 'Base.Unmarked()', which is not known to be pure.
                //     [Pure] public int CallsUnmarked() => Unmarked();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Unmarked()").WithArguments("Base.CallsUnmarked()", "'Unmarked()' calls 'Base.Unmarked()', which is not known to be pure").WithLocation(14, 42),
                // (15,38): error CS20050: 'Base.ReadsAuto()' is marked [Pure], but it is not pure: 'Auto' calls 'Base.Auto', which is not known to be pure.
                //     [Pure] public int ReadsAuto() => Auto;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "Auto").WithArguments("Base.ReadsAuto()", "'Auto' calls 'Base.Auto', which is not known to be pure").WithLocation(15, 38));
        }

        [Fact]
        public void PureSlot_OverrideNotPure()
        {
            var source = """
                abstract class Base
                {
                    [Pure] public virtual int V() => 1;
                    [Pure] public abstract int A();
                    [Pure] public abstract int P { get; }
                    public abstract int Q { [Pure] get; set; }
                    [Pure] public abstract int this[int i] { get; }
                    [Pure] public abstract T G<T>(T x);
                    public virtual int Unmarked() => 1;
                }

                abstract class Middle : Base
                {
                    public abstract override int A();
                    [Pure] public sealed override int V() => 2;
                }

                class Derived : Middle
                {
                    static int s_count;
                    public override int A() => 2;
                    public override int P => 3;
                    public override int Q { get => 4; set { } }
                    public override int this[int i] => i;
                    public override T G<T>(T x) => x;
                    public override int Unmarked() => s_count++;
                }

                class Impure : Middle
                {
                    static int s_count;
                    [Pure] public override int A() => s_count++;
                    [Pure] public override int P => s_count;
                    [AssumePure] public override int Q { get => s_count++; set { } }
                    [AssumePure] public override int this[int i] => s_count++;
                    [Pure] public override T G<T>(T x) => x;
                }

                static class C
                {
                    [Pure] static int Calls(Derived d, Impure i) => d.A() + d.P + i.Q + i[0];
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (14,34): error CS20064: 'Middle.A()' overrides or implements the [Pure] member 'Base.A()', so it must be marked [Pure] too.
                //     public abstract override int A();
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "A").WithArguments("Middle.A()", "Base.A()").WithLocation(14, 34),
                // (21,25): error CS20064: 'Derived.A()' overrides or implements the [Pure] member 'Base.A()', so it must be marked [Pure] too.
                //     public override int A() => 2;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "A").WithArguments("Derived.A()", "Base.A()").WithLocation(21, 25),
                // (22,25): error CS20064: 'Derived.P' overrides or implements the [Pure] member 'Base.P', so it must be marked [Pure] too.
                //     public override int P => 3;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "P").WithArguments("Derived.P", "Base.P").WithLocation(22, 25),
                // (23,25): error CS20064: 'Derived.Q' overrides or implements the [Pure] member 'Base.Q', so it must be marked [Pure] too.
                //     public override int Q { get => 4; set { } }
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "Q").WithArguments("Derived.Q", "Base.Q").WithLocation(23, 25),
                // (24,25): error CS20064: 'Derived.this[int]' overrides or implements the [Pure] member 'Base.this[int]', so it must be marked [Pure] too.
                //     public override int this[int i] => i;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "this").WithArguments("Derived.this[int]", "Base.this[int]").WithLocation(24, 25),
                // (25,23): error CS20064: 'Derived.G<T>(T)' overrides or implements the [Pure] member 'Base.G<T>(T)', so it must be marked [Pure] too.
                //     public override T G<T>(T x) => x;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "G").WithArguments("Derived.G<T>(T)", "Base.G<T>(T)").WithLocation(25, 23),
                // (32,39): error CS20050: 'Impure.A()' is marked [Pure], but it is not pure: 's_count++' writes state outside the member.
                //     [Pure] public override int A() => s_count++;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_count++").WithArguments("Impure.A()", "'s_count++' writes state outside the member").WithLocation(32, 39),
                // (33,37): error CS20050: 'Impure.P.get' is marked [Pure], but it is not pure: 's_count' reads static state that can change.
                //     [Pure] public override int P => s_count;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_count").WithArguments("Impure.P.get", "'s_count' reads static state that can change").WithLocation(33, 37));
        }

        [Fact]
        public void PureSlot_OverrideInCSharpFile()
        {
            var sharper = """
                public abstract class Base
                {
                    [Pure] public abstract int A();
                    [Pure] public abstract int P { get; }
                }

                public interface I
                {
                    [Pure] int M();
                }

                static class C
                {
                    [Pure] static int Calls(Derived d, I i) => d.A() + d.P + i.M() + d.M();
                }
                """;
            var csharp = """
                public class Derived : Base, I
                {
                    public override int A() => 1;
                    public override int P => 2;
                    public int M() => 3;
                }

                public class Explicit : I
                {
                    int I.M() => 3;
                }
                """;
            CreateMixedPurityCompilation(sharper, csharp).VerifyDiagnostics(
                // b.cs(3,25): warning CS20065: 'Derived.A()' overrides or implements the [Pure] member 'Base.A()', so it must be pure too, but it is not marked [Pure], and a C# file is not checked. Move it to a Sharper file and mark it [Pure].
                //     public override int A() => 1;
                Diagnostic(ErrorCode.WRN_SharperPureOverrideNotPure, "A").WithArguments("Derived.A()", "Base.A()").WithLocation(3, 25),
                // b.cs(4,25): warning CS20065: 'Derived.P' overrides or implements the [Pure] member 'Base.P', so it must be pure too, but it is not marked [Pure], and a C# file is not checked. Move it to a Sharper file and mark it [Pure].
                //     public override int P => 2;
                Diagnostic(ErrorCode.WRN_SharperPureOverrideNotPure, "P").WithArguments("Derived.P", "Base.P").WithLocation(4, 25),
                // b.cs(5,16): warning CS20065: 'Derived.M()' overrides or implements the [Pure] member 'I.M()', so it must be pure too, but it is not marked [Pure], and a C# file is not checked. Move it to a Sharper file and mark it [Pure].
                //     public int M() => 3;
                Diagnostic(ErrorCode.WRN_SharperPureOverrideNotPure, "M").WithArguments("Derived.M()", "I.M()").WithLocation(5, 16),
                // b.cs(10,11): warning CS20065: 'Explicit.I.M()' overrides or implements the [Pure] member 'I.M()', so it must be pure too, but it is not marked [Pure], and a C# file is not checked. Move it to a Sharper file and mark it [Pure].
                //     int I.M() => 3;
                Diagnostic(ErrorCode.WRN_SharperPureOverrideNotPure, "M").WithArguments("Explicit.I.M()", "I.M()").WithLocation(10, 11),
                // a.cx(14,70): error CS20050: 'C.Calls(Derived, I)' is marked [Pure], but it is not pure: 'd.M()' calls 'Derived.M()', which is not known to be pure.
                //     [Pure] static int Calls(Derived d, I i) => d.A() + d.P + i.M() + d.M();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "d.M()").WithArguments("C.Calls(Derived, I)", "'d.M()' calls 'Derived.M()', which is not known to be pure").WithLocation(14, 70));
        }

        [Fact]
        public void PureSlot_Implementations()
        {
            var source = """
                interface I
                {
                    [Pure] int M();
                    [Pure] int P { get; }
                    [Pure] int D() => 1;
                    [Pure] static abstract int S();
                    void Unmarked();
                }

                class Implicit : I
                {
                    public int M() => 1;
                    public int P => 2;
                    public static int S() => 3;
                    public void Unmarked() { }
                }

                class Explicit : I
                {
                    int I.M() => 1;
                    int I.P => 2;
                    int I.D() => 3;
                    static int I.S() => 4;
                    void I.Unmarked() { }
                }

                class Marked : I
                {
                    static int s_count;
                    [Pure] public int M() => s_count++;
                    [Pure] int I.P => 2;
                    [AssumePure] int I.D() => s_count++;
                    [Pure] public static int S() => 3;
                    public void Unmarked() { }
                }

                struct Struct : I
                {
                    [Pure] public int M() => 1;
                    [Pure] public readonly int P => 2;
                    [Pure] public static int S() => 3;
                    public void Unmarked() { }
                }

                class BaseImplementation
                {
                    public int M() => 1;
                    [Pure] public int P => 2;
                    [Pure] public static int S() => 3;
                    public void Unmarked() { }
                }

                class Inherits : BaseImplementation, I
                {
                }

                class InheritsAgain : Inherits
                {
                }

                interface IDerived : I
                {
                    int I.M() => 1;
                    [Pure] int I.P => 2;
                }

                static class C
                {
                    [Pure] static int Calls<T>(T x) where T : I => x.M() + x.P + x.D() + T.S();
                    [Pure] static int CallsImplicit(Implicit x) => x.M();
                    [Pure] static int CallsUnmarked(I x) { x.Unmarked(); return 0; }
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (12,16): error CS20064: 'Implicit.M()' overrides or implements the [Pure] member 'I.M()', so it must be marked [Pure] too.
                //     public int M() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "M").WithArguments("Implicit.M()", "I.M()").WithLocation(12, 16),
                // (13,16): error CS20064: 'Implicit.P' overrides or implements the [Pure] member 'I.P', so it must be marked [Pure] too.
                //     public int P => 2;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "P").WithArguments("Implicit.P", "I.P").WithLocation(13, 16),
                // (14,23): error CS20064: 'Implicit.S()' overrides or implements the [Pure] member 'I.S()', so it must be marked [Pure] too.
                //     public static int S() => 3;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "S").WithArguments("Implicit.S()", "I.S()").WithLocation(14, 23),
                // (20,11): error CS20064: 'Explicit.I.M()' overrides or implements the [Pure] member 'I.M()', so it must be marked [Pure] too.
                //     int I.M() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "M").WithArguments("Explicit.I.M()", "I.M()").WithLocation(20, 11),
                // (21,11): error CS20064: 'Explicit.I.P' overrides or implements the [Pure] member 'I.P', so it must be marked [Pure] too.
                //     int I.P => 2;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "P").WithArguments("Explicit.I.P", "I.P").WithLocation(21, 11),
                // (22,11): error CS20064: 'Explicit.I.D()' overrides or implements the [Pure] member 'I.D()', so it must be marked [Pure] too.
                //     int I.D() => 3;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "D").WithArguments("Explicit.I.D()", "I.D()").WithLocation(22, 11),
                // (23,18): error CS20064: 'Explicit.I.S()' overrides or implements the [Pure] member 'I.S()', so it must be marked [Pure] too.
                //     static int I.S() => 4;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "S").WithArguments("Explicit.I.S()", "I.S()").WithLocation(23, 18),
                // (30,30): error CS20050: 'Marked.M()' is marked [Pure], but it is not pure: 's_count++' writes state outside the member.
                //     [Pure] public int M() => s_count++;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "s_count++").WithArguments("Marked.M()", "'s_count++' writes state outside the member").WithLocation(30, 30),
                // (39,23): error CS20051: 'Struct.M()' is marked [Pure], so it must be readonly: declare it 'readonly', or make the struct 'readonly'.
                //     [Pure] public int M() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureStructMemberNotReadOnly, "M").WithArguments("Struct.M()").WithLocation(39, 23),
                // (53,7): error CS20064: 'BaseImplementation.M()' overrides or implements the [Pure] member 'I.M()', so it must be marked [Pure] too.
                // class Inherits : BaseImplementation, I
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "Inherits").WithArguments("BaseImplementation.M()", "I.M()").WithLocation(53, 7),
                // (63,11): error CS20064: 'IDerived.I.M()' overrides or implements the [Pure] member 'I.M()', so it must be marked [Pure] too.
                //     int I.M() => 1;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "M").WithArguments("IDerived.I.M()", "I.M()").WithLocation(63, 11),
                // (70,52): error CS20050: 'C.CallsImplicit(Implicit)' is marked [Pure], but it is not pure: 'x.M()' calls 'Implicit.M()', which is not known to be pure.
                //     [Pure] static int CallsImplicit(Implicit x) => x.M();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x.M()").WithArguments("C.CallsImplicit(Implicit)", "'x.M()' calls 'Implicit.M()', which is not known to be pure").WithLocation(70, 52),
                // (71,44): error CS20050: 'C.CallsUnmarked(I)' is marked [Pure], but it is not pure: 'x.Unmarked()' calls 'I.Unmarked()', which is not known to be pure.
                //     [Pure] static int CallsUnmarked(I x) { x.Unmarked(); return 0; }
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "x.Unmarked()").WithArguments("C.CallsUnmarked(I)", "'x.Unmarked()' calls 'I.Unmarked()', which is not known to be pure").WithLocation(71, 44));
        }

        [Fact]
        public void PureSlot_Metadata()
        {
            var library = """
                public abstract class Base
                {
                    [Pure] public abstract int A();
                    [Pure] public virtual int P => 1;
                    public virtual int Unmarked() => 1;
                }

                public interface I
                {
                    [Pure] int M();
                }

                public class Trusted : Base
                {
                    [Pure] public override int A() => 1;
                }
                """;
            var libraryComp = CreateImmutableCompilation(library);
            libraryComp.VerifyDiagnostics();

            var source = """
                class Derived : Trusted, I
                {
                    public override int A() => 2;
                    public override int P => 3;
                    public int M() => 4;
                }

                class Marked : Base, I
                {
                    [Pure] public override int A() => 2;
                    [Pure] public override int P => 3;
                    [Pure] public int M() => 4;
                }

                static class C
                {
                    [Pure] static int Calls(Base b, I i, Trusted t, Derived d) => b.A() + b.P + i.M() + t.A() + t.P + d.A();
                    [Pure] static int CallsUnmarked(Base b) => b.Unmarked();
                }
                """;
            foreach (var reference in new[] { libraryComp.EmitToImageReference(), libraryComp.ToMetadataReference() })
            {
                CreateImmutableCompilation(source, references: [reference]).VerifyDiagnostics(
                // (3,25): error CS20064: 'Derived.A()' overrides or implements the [Pure] member 'Trusted.A()', so it must be marked [Pure] too.
                //     public override int A() => 2;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "A").WithArguments("Derived.A()", "Trusted.A()").WithLocation(3, 25),
                // (4,25): error CS20064: 'Derived.P' overrides or implements the [Pure] member 'Base.P', so it must be marked [Pure] too.
                //     public override int P => 3;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "P").WithArguments("Derived.P", "Base.P").WithLocation(4, 25),
                // (5,16): error CS20064: 'Derived.M()' overrides or implements the [Pure] member 'I.M()', so it must be marked [Pure] too.
                //     public int M() => 4;
                Diagnostic(ErrorCode.ERR_SharperPureOverrideNotPure, "M").WithArguments("Derived.M()", "I.M()").WithLocation(5, 16),
                // (18,48): error CS20050: 'C.CallsUnmarked(Base)' is marked [Pure], but it is not pure: 'b.Unmarked()' calls 'Base.Unmarked()', which is not known to be pure.
                //     [Pure] static int CallsUnmarked(Base b) => b.Unmarked();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "b.Unmarked()").WithArguments("C.CallsUnmarked(Base)", "'b.Unmarked()' calls 'Base.Unmarked()', which is not known to be pure").WithLocation(18, 48));
            }

            // A C# compilation is not checked, and its override is trusted by a Sharper consumer.
            var csharp = """
                public class CSharpDerived : Base, I
                {
                    public override int A() => 2;
                    public int M() => 4;
                }
                """;
            var csharpComp = CreateImmutableCompilation(csharp, parseOptions: TestOptions.Regular, references: [libraryComp.EmitToImageReference()]);
            csharpComp.VerifyDiagnostics();

            var consumer = """
                static class C
                {
                    [Pure] static int Calls(CSharpDerived d) => d.A() + ((I)d).M();
                    [Pure] static int CallsImplementation(CSharpDerived d) => d.M();
                }
                """;
            CreateImmutableCompilation(consumer, references: [libraryComp.EmitToImageReference(), csharpComp.EmitToImageReference()]).VerifyDiagnostics(
                // (4,63): error CS20050: 'C.CallsImplementation(CSharpDerived)' is marked [Pure], but it is not pure: 'd.M()' calls 'CSharpDerived.M()', which is not known to be pure.
                //     [Pure] static int CallsImplementation(CSharpDerived d) => d.M();
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "d.M()").WithArguments("C.CallsImplementation(CSharpDerived)", "'d.M()' calls 'CSharpDerived.M()', which is not known to be pure").WithLocation(4, 63));

            // In a mixed compilation, the C# file gets the warning for a slot from metadata too.
            CreateMixedPurityCompilation("class Sharper { }", csharp, references: [libraryComp.EmitToImageReference()]).VerifyDiagnostics(
                // b.cs(3,25): warning CS20065: 'CSharpDerived.A()' overrides or implements the [Pure] member 'Base.A()', so it must be pure too, but it is not marked [Pure], and a C# file is not checked. Move it to a Sharper file and mark it [Pure].
                //     public override int A() => 2;
                Diagnostic(ErrorCode.WRN_SharperPureOverrideNotPure, "A").WithArguments("CSharpDerived.A()", "Base.A()").WithLocation(3, 25),
                // b.cs(4,16): warning CS20065: 'CSharpDerived.M()' overrides or implements the [Pure] member 'I.M()', so it must be pure too, but it is not marked [Pure], and a C# file is not checked. Move it to a Sharper file and mark it [Pure].
                //     public int M() => 4;
                Diagnostic(ErrorCode.WRN_SharperPureOverrideNotPure, "M").WithArguments("CSharpDerived.M()", "I.M()").WithLocation(4, 16));
        }

        [Fact]
        public void PureSlot_Observers()
        {
            // An observer of an immutable type calls [Pure] members, virtual ones too: the same IsPure as a [Pure] body.
            var source = """
                [Immutable]
                class Money
                {
                    private readonly int _amount;
                    public Money(int amount) { _amount = amount; }

                    [Pure] protected virtual int Scale() => 2;
                    [Pure] protected virtual int[] Parts() => new int[1];
                    [Pure] static int Twice(int x) => 2 * x;

                    public int Scaled => _amount * Scale();
                    public int Twofold => Twice(_amount);
                    public int Absolute => System.Math.Abs(_amount);
                    public int Count => Parts().Length;
                    public string Text => "x".Substring(Scale());
                }

                [Immutable]
                sealed class Euro : Money
                {
                    public Euro(int amount) : base(amount) { }
                    [Pure] protected override int Scale() => 100;
                }

                [Immutable]
                sealed class Unmarked
                {
                    private readonly int _amount = 1;
                    int Helper() => 2;
                    public int Wrong => _amount * Helper();
                }

                [Immutable]
                sealed class Fresh
                {
                    [Pure] static int[] Parts() => new int[1];
                    public int[] Mutable => Parts();
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (26,14): error CS20035: 'Unmarked' is marked [Immutable], but it cannot be proven immutable: the observer 'Unmarked.Wrong' does more than read fields and pure observers: 'Helper()'.
                // sealed class Unmarked
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Unmarked").WithArguments("Unmarked", "the observer 'Unmarked.Wrong' does more than read fields and pure observers: 'Helper()'").WithLocation(26, 14),
                // (34,14): error CS20035: 'Fresh' is marked [Immutable], but it cannot be proven immutable: the observer 'Fresh.Mutable' does more than read fields and pure observers: 'Parts()'.
                // sealed class Fresh
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Fresh").WithArguments("Fresh", "the observer 'Fresh.Mutable' does more than read fields and pure observers: 'Parts()'").WithLocation(34, 14));
        }

        [Fact]
        public void PureSlot_Operators()
        {
            var source = """
                struct V
                {
                    public int X;
                    [Pure] public static V operator +(V a, V b) => new V { X = a.X + b.X };
                    public static V operator -(V a, V b) => new V { X = a.X - b.X };
                    [Pure] public static V Add(V a, V b) => a + b;
                    [Pure] public static V Subtract(V a, V b) => a - b;
                }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (7,50): error CS20050: 'V.Subtract(V, V)' is marked [Pure], but it is not pure: 'a - b' calls 'V.operator -(V, V)', which is not known to be pure.
                //     [Pure] public static V Subtract(V a, V b) => a - b;
                Diagnostic(ErrorCode.ERR_SharperPureNotPure, "a - b").WithArguments("V.Subtract(V, V)", "'a - b' calls 'V.operator -(V, V)', which is not known to be pure").WithLocation(7, 50));
        }
    }
}
