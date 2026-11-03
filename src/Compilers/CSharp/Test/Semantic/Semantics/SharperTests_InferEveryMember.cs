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
    /// Sharper's inferred return type, step R4 (issue #70): <c>infer</c> on every member kind and accessibility.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void InferMember_Accessibility()
        {
            // Public, internal and protected members infer like private ones, with no warning.
            var source = """
                using System;

                public class C
                {
                    public static infer Hello(int id) => $"Hello, {id}!";
                    internal static infer Twice(int x) => x * 2;
                    protected infer Half(int x) { if (x < 0) return 0L; return x / 2; }
                    private protected static infer Names() { yield return "a"; yield return "b"; }
                    protected internal infer Nothing() { }
                    public async infer LoadAsync(int id) { await System.Threading.Tasks.Task.Yield(); return id; }

                    public static string HelloCSharp(int id) => $"Hello, {id}!";
                    internal static int TwiceCSharp(int x) => x * 2;
                    protected long HalfCSharp(int x) { if (x < 0) return 0L; return x / 2; }
                    private protected static System.Collections.Generic.IEnumerable<string> NamesCSharp() { yield return "a"; yield return "b"; }
                    protected internal void NothingCSharp() { }
                    public async System.Threading.Tasks.Task<int> LoadAsyncCSharp(int id) { await System.Threading.Tasks.Task.Yield(); return id; }

                    static void Main()
                    {
                        var c = new C();
                        c.Nothing();
                        Console.WriteLine($"{Hello(1)} {Twice(2)} {c.Half(9)} {string.Join(",", Names())} {c.LoadAsync(5).Result}");
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Hello, 1! 4 4 a,b 5" : null).VerifyDiagnostics();
            foreach (var name in new[] { "Hello", "Twice", "Half", "Names", "Nothing", "LoadAsync" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }
        }

        [Fact]
        public void InferMember_VirtualAndOverride()
        {
            // A virtual member infers from its body. An override copies the type of the member it overrides (step P2),
            // also when that type is inferred, and its body then converts to it as to a written type.
            var source = """
                using System;

                public class B
                {
                    public virtual infer V(int x) => x + 1;
                    public virtual infer Name() { return "b"; }
                }

                public class D : B
                {
                    public override infer V(int x) => x + 2;
                    public override string Name() => "d";
                }

                public interface IDefault
                {
                    infer Default() => 1.5;
                    static virtual infer Create() => "created";
                }

                class Program : IDefault
                {
                    static void Main()
                    {
                        B b = new D();
                        IDefault i = new Program();
                        Console.WriteLine($"{b.V(1)} {b.Name()} {i.Default()} {Get<Program>()}");
                    }

                    static string Get<T>() where T : IDefault => T.Create();
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "3 d 1.5 created" : null, verify: Verification.FailsILVerify).VerifyDiagnostics();
            Assert.Equal("System.Int32 D.V(System.Int32 x)", comp.GetMember<MethodSymbol>("D.V").ToTestDisplayString());
            Assert.True(comp.GetMember<SourceOrdinaryMethodSymbol>("D.V").IsSharperReturnTypeFromOverriddenMember);
            Assert.True(comp.GetMember<SourceOrdinaryMethodSymbol>("B.V").IsSharperInferredReturnType);
            Assert.Equal("System.Double IDefault.Default()", comp.GetMember<MethodSymbol>("IDefault.Default").ToTestDisplayString());
        }

        [Fact]
        public void InferMember_VirtualAndOverride_Errors()
        {
            // An override never infers from its body: a body that does not convert to the copied type is an error, as for
            // the type written out. A body-inferred type is checked against overrides and interfaces as a written one.
            var source = """
                public class B
                {
                    public virtual infer V() => 1;
                    public infer N() => 1;
                    public infer L() => 1L;
                }

                public class D : B
                {
                    public override infer V() => "d";
                }

                public class DCSharp : B
                {
                    public override int V() => "d";
                }

                public class E : B
                {
                    public override string V() => "e";
                }

                public interface I { int N(); }
                public interface IL { int L(); }
                public class Implements : B, I { }
                public class Mismatch : B, IL { }

                public interface IAbstract
                {
                    infer M();
                    static abstract infer S();
                }

                public abstract class A
                {
                    public abstract infer M();
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (10,34): error CS0029: Cannot implicitly convert type 'string' to 'int'
                //     public override infer V() => "d";
                Diagnostic(ErrorCode.ERR_NoImplicitConv, @"""d""").WithArguments("string", "int").WithLocation(10, 34),
                // (15,32): error CS0029: Cannot implicitly convert type 'string' to 'int'
                //     public override int V() => "d";
                Diagnostic(ErrorCode.ERR_NoImplicitConv, @"""d""").WithArguments("string", "int").WithLocation(15, 32),
                // (20,28): error CS0508: 'E.V()': return type must be 'int' to match overridden member 'B.V()'
                //     public override string V() => "e";
                Diagnostic(ErrorCode.ERR_CantChangeReturnTypeOnOverride, "V").WithArguments("E.V()", "B.V()", "int").WithLocation(20, 28),
                // (26,28): error CS0738: 'Mismatch' does not implement interface member 'IL.L()'. 'B.L()' cannot implement 'IL.L()' because it does not have the matching return type of 'int'.
                // public class Mismatch : B, IL { }
                Diagnostic(ErrorCode.ERR_CloseUnimplementedInterfaceMemberWrongReturnType, "IL").WithArguments("Mismatch", "IL.L()", "B.L()", "int").WithLocation(26, 28),
                // (30,5): error CS20016: 'IAbstract.M()' has no body to infer its return type from. Write the return type.
                //     infer M();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("IAbstract.M()").WithLocation(30, 5),
                // (31,21): error CS20016: 'IAbstract.S()' has no body to infer its return type from. Write the return type.
                //     static abstract infer S();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("IAbstract.S()").WithLocation(31, 21),
                // (36,21): error CS20016: 'A.M()' has no body to infer its return type from. Write the return type.
                //     public abstract infer M();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("A.M()").WithLocation(36, 21));
        }

        [Fact]
        public void InferMember_Partial()
        {
            // A partial definition takes the return type of its implementation part, inferred or written.
            var source = """
                using System;
                using System.Collections.Generic;

                public partial class P
                {
                    public partial infer M(int x);
                    public partial infer M(int x) => x.ToString();

                    public partial infer Written(int x);
                    public partial long Written(int x) => x;

                    internal partial int Inferred(int x);
                    internal partial infer Inferred(int x) => x + 1;

                    public partial infer G<T>(T t);
                    public partial infer G<T>(T t) => new List<T> { t };

                    public partial string MCSharp(int x);
                    public partial string MCSharp(int x) => x.ToString();
                    public partial List<T> GCSharp<T>(T t);
                    public partial List<T> GCSharp<T>(T t) => new List<T> { t };

                    static void Main()
                    {
                        var p = new P();
                        Console.WriteLine($"{p.M(1)} {p.Written(2)} {p.Inferred(3)} {p.G("g")[0]}");
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1 2 4 g" : null).VerifyDiagnostics();
            VerifySameSignature(comp, "P.M", "P.MCSharp");
            VerifySameSignature(comp, "P.G", "P.GCSharp");
            var m = comp.GetMember<MethodSymbol>("P.M");
            Assert.True(m.IsPartialDefinition());
            Assert.Equal("System.String", m.PartialImplementationPart!.ReturnType.ToTestDisplayString());
            Assert.Equal("System.Int64 P.Written(System.Int32 x)", comp.GetMember<MethodSymbol>("P.Written").ToTestDisplayString());
            Assert.Equal("System.Int32 P.Inferred(System.Int32 x)", comp.GetMember<MethodSymbol>("P.Inferred").PartialImplementationPart!.ToTestDisplayString());
        }

        [Fact]
        public void InferMember_Partial_Errors()
        {
            var source = """
                public partial class P
                {
                    public partial infer NoImplementation();
                    private partial infer PrivateNoImplementation();

                    public partial int Mismatch();
                    public partial infer Mismatch() => "";

                    public partial int MismatchCSharp();
                    public partial string MismatchCSharp() => "";

                    public partial infer Self(int x);
                    public partial infer Self(int x) => Self(x - 1);
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,20): error CS20016: 'P.NoImplementation()' has no body to infer its return type from. Write the return type.
                //     public partial infer NoImplementation();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("P.NoImplementation()").WithLocation(3, 20),
                // (3,26): error CS8795: Partial method 'P.NoImplementation()' must have an implementation part because it has accessibility modifiers.
                //     public partial infer NoImplementation();
                Diagnostic(ErrorCode.ERR_PartialMethodWithAccessibilityModsMustHaveImplementation, "NoImplementation").WithArguments("P.NoImplementation()").WithLocation(3, 26),
                // (4,21): error CS20016: 'P.PrivateNoImplementation()' has no body to infer its return type from. Write the return type.
                //     private partial infer PrivateNoImplementation();
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("P.PrivateNoImplementation()").WithLocation(4, 21),
                // (4,27): error CS8795: Partial method 'P.PrivateNoImplementation()' must have an implementation part because it has accessibility modifiers.
                //     private partial infer PrivateNoImplementation();
                Diagnostic(ErrorCode.ERR_PartialMethodWithAccessibilityModsMustHaveImplementation, "PrivateNoImplementation").WithArguments("P.PrivateNoImplementation()").WithLocation(4, 27),
                // (7,26): error CS8817: Both partial method declarations must have the same return type.
                //     public partial infer Mismatch() => "";
                Diagnostic(ErrorCode.ERR_PartialMethodReturnTypeDifference, "Mismatch").WithLocation(7, 26),
                // (10,27): error CS8817: Both partial method declarations must have the same return type.
                //     public partial string MismatchCSharp() => "";
                Diagnostic(ErrorCode.ERR_PartialMethodReturnTypeDifference, "MismatchCSharp").WithLocation(10, 27),
                // (13,20): error CS20018: Cannot infer the return type of 'P.Self(int)', because its body uses 'P.Self(int)' itself. Write the return type.
                //     public partial infer Self(int x) => Self(x - 1);
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("P.Self(int)").WithLocation(13, 20));
        }

        [Fact]
        public void InferMember_Operators()
        {
            // Operators and conversion operators infer from their bodies; the checks of their signatures apply to the
            // inferred type.
            var source = """
                using System;

                public struct S
                {
                    public int Value;
                    public S(int value) => Value = value;

                    public static infer operator +(S a, S b) => new S(a.Value + b.Value);
                    public static infer operator -(S a) { return new S(-a.Value); }
                    public static infer operator ++(S a) => new S(a.Value + 1);
                    public static infer operator ==(S a, S b) => a.Value == b.Value;
                    public static infer operator !=(S a, S b) => a.Value != b.Value;
                    public static infer operator true(S a) => a.Value != 0;
                    public static infer operator false(S a) => a.Value == 0;
                    public static infer operator *(S a, int n) => a.Value * n;
                    public static implicit operator infer(S s) => (long)s.Value;
                    public static explicit operator S(long l) => new S((int)l);

                    public override bool Equals(object o) => o is S s && this == s;
                    public override int GetHashCode() => Value;
                }

                public struct T
                {
                    public int Value;
                    public T(int value) => Value = value;

                    public static T operator +(T a, T b) => new T(a.Value + b.Value);
                    public static T operator -(T a) { return new T(-a.Value); }
                    public static T operator ++(T a) => new T(a.Value + 1);
                    public static bool operator ==(T a, T b) => a.Value == b.Value;
                    public static bool operator !=(T a, T b) => a.Value != b.Value;
                    public static bool operator true(T a) => a.Value != 0;
                    public static bool operator false(T a) => a.Value == 0;
                    public static int operator *(T a, int n) => a.Value * n;
                    public static implicit operator long(T s) => (long)s.Value;
                    public static explicit operator T(long l) => new T((int)l);

                    public override bool Equals(object o) => o is T t && this == t;
                    public override int GetHashCode() => Value;
                }

                class Program
                {
                    static void Main()
                    {
                        var s = new S(2) + new S(3);
                        s++;
                        long l = -s;
                        var r = s ? "true" : "false";
                        Console.WriteLine($"{s.Value} {l} {s == new S(6)} {r} {s * 2} {((S)7L).Value}");
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "6 -6 True true 12 7" : null).VerifyDiagnostics();
            var s = comp.GetMember<NamedTypeSymbol>("S");
            var t = comp.GetMember<NamedTypeSymbol>("T");
            foreach (var name in new[] { "op_Addition", "op_UnaryNegation", "op_Increment", "op_Equality", "op_Inequality", "op_True", "op_False", "op_Multiply", "op_Implicit" })
            {
                var op = s.GetMember<MethodSymbol>(name);
                var twin = t.GetMember<MethodSymbol>(name);
                Assert.True(((SourceOrdinaryMethodOrUserDefinedOperatorSymbol)op).IsSharperInferredReturnType);
                Assert.Equal(twin.ToTestDisplayString().Replace("T", "S"), op.ToTestDisplayString().Replace("T", "S"));
            }
        }

        [Fact]
        public void InferMember_Operators_Errors()
        {
            // The signature checks of an operator apply to the inferred type, as to the type written out.
            var source = """
                public interface IOp<T> where T : IOp<T> { static abstract T operator +(T a, T b); }
                public interface IAbstract { static abstract infer operator -(IAbstract a); }

                public struct S : IOp<S>
                {
                    public static infer operator +(S a, S b) => a;
                    public static infer operator true(S s) => 1;
                    public static infer operator false(S s) => false;
                    public static infer operator ++(S s) => 1;
                    public static implicit operator infer(S s) => "s";
                    public static extern infer operator -(S s);
                }

                public struct T
                {
                    public static int operator true(T s) => 1;
                    public static bool operator false(T s) => false;
                    public static int operator ++(T s) => 1;
                    public static implicit operator string(T s) => "s";
                }

                public struct U : IOp<U>
                {
                    static infer IOp<U>.operator +(U a, U b) => a;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (2,46): error CS20016: 'IAbstract.operator -(IAbstract)' has no body to infer its return type from. Write the return type.
                // public interface IAbstract { static abstract infer operator -(IAbstract a); }
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("IAbstract.operator -(IAbstract)").WithLocation(2, 46),
                // (7,34): error CS0216: The operator 'S.operator true(S)' requires a matching operator 'false' to also be defined
                //     public static infer operator true(S s) => 1;
                Diagnostic(ErrorCode.ERR_OperatorNeedsMatch, "true").WithArguments("S.operator true(S)", "false").WithLocation(7, 34),
                // (7,34): error CS0215: The return type of operator True or False must be bool
                //     public static infer operator true(S s) => 1;
                Diagnostic(ErrorCode.ERR_OpTFRetType, "true").WithLocation(7, 34),
                // (8,34): error CS0216: The operator 'S.operator false(S)' requires a matching operator 'true' to also be defined
                //     public static infer operator false(S s) => false;
                Diagnostic(ErrorCode.ERR_OperatorNeedsMatch, "false").WithArguments("S.operator false(S)", "true").WithLocation(8, 34),
                // (9,34): error CS0448: The return type for ++ or -- operator must match the parameter type or be derived from the parameter type
                //     public static infer operator ++(S s) => 1;
                Diagnostic(ErrorCode.ERR_BadIncDecRetType, "++").WithLocation(9, 34),
                // (11,26): error CS20016: 'S.operator -(S)' has no body to infer its return type from. Write the return type.
                //     public static extern infer operator -(S s);
                Diagnostic(ErrorCode.ERR_SharperInferNoBody, "infer").WithArguments("S.operator -(S)").WithLocation(11, 26),
                // (11,41): warning CS0626: Method, operator, or accessor 'S.operator -(S)' is marked external and has no attributes on it. Consider adding a DllImport attribute to specify the external implementation.
                //     public static extern infer operator -(S s);
                Diagnostic(ErrorCode.WRN_ExternMethodNoImplementation, "-").WithArguments("S.operator -(S)").WithLocation(11, 41),
                // (16,32): error CS0215: The return type of operator True or False must be bool
                //     public static int operator true(T s) => 1;
                Diagnostic(ErrorCode.ERR_OpTFRetType, "true").WithLocation(16, 32),
                // (16,32): error CS0216: The operator 'T.operator true(T)' requires a matching operator 'false' to also be defined
                //     public static int operator true(T s) => 1;
                Diagnostic(ErrorCode.ERR_OperatorNeedsMatch, "true").WithArguments("T.operator true(T)", "false").WithLocation(16, 32),
                // (17,33): error CS0216: The operator 'T.operator false(T)' requires a matching operator 'true' to also be defined
                //     public static bool operator false(T s) => false;
                Diagnostic(ErrorCode.ERR_OperatorNeedsMatch, "false").WithArguments("T.operator false(T)", "true").WithLocation(17, 33),
                // (18,32): error CS0448: The return type for ++ or -- operator must match the parameter type or be derived from the parameter type
                //     public static int operator ++(T s) => 1;
                Diagnostic(ErrorCode.ERR_BadIncDecRetType, "++").WithLocation(18, 32),
                // (22,19): error CS0535: 'U' does not implement interface member 'IOp<U>.operator +(U, U)'
                // public struct U : IOp<U>
                Diagnostic(ErrorCode.ERR_UnimplementedInterfaceMember, "IOp<U>").WithArguments("U", "IOp<U>.operator +(U, U)").WithLocation(22, 19),
                // (24,12): error CS20015: An inferred return type ('infer') is not supported on an explicit interface implementation yet. Write the return type.
                //     static infer IOp<U>.operator +(U a, U b) => a;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on an explicit interface implementation").WithLocation(24, 12),
                // (24,34): error CS9334: 'U.operator +(U, U)': return type must be 'U' to match implemented member 'IOp<U>.operator +(U, U)'
                //     static infer IOp<U>.operator +(U a, U b) => a;
                Diagnostic(ErrorCode.ERR_ExplicitInterfaceMemberReturnTypeMismatch, "+").WithArguments("U.operator +(U, U)", "U", "IOp<U>.operator +(U, U)").WithLocation(24, 34));
        }

        [Fact]
        public void InferMember_ExtensionBlock()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                public static class E
                {
                    extension(int i)
                    {
                        public infer Twice() => i * 2;
                        private infer Thrice() => i * 3;
                        public static infer Zero() => 0L;
                        public infer Both() => i.Twice() + i.Thrice();
                        public static infer operator -(int a, string b) => a.ToString() + b;
                    }

                    extension<T>(List<T> list)
                    {
                        public infer First() => list[0];
                    }
                }

                class Program
                {
                    static void Main()
                    {
                        Console.WriteLine($"{3.Twice()} {3.Both()} {int.Zero()} {new List<string> { "x" }.First()} {E.Twice(4)} {3 - "a"}");
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "6 15 0 x 8 3a" : null).VerifyDiagnostics();
            var implementations = comp.GetMember<NamedTypeSymbol>("E").GetMembers().OfType<MethodSymbol>().Select(m => m.ToTestDisplayString());
            AssertEx.Equal([
                "System.Int32 E.Twice(this System.Int32 i)",
                "System.Int32 E.Thrice(this System.Int32 i)",
                "System.Int64 E.Zero()",
                "System.Int32 E.Both(this System.Int32 i)",
                "System.String E.op_Subtraction(System.Int32 a, System.String b)",
                "T E.First<T>(this System.Collections.Generic.List<T> list)",
                ], implementations);
        }

        [Fact]
        public void InferMember_AccessibilityOfInferredType()
        {
            // The inferred type is checked as a written one: a public member cannot return a private type, and an
            // anonymous type is internal (CS0050).
            var source = """
                public class C
                {
                    private class Secret { }
                    public infer Leak() => new Secret();
                    public infer Anonymous() => new { A = 1 };
                    internal infer AnonymousInternal() => new { A = 1 };
                    private infer AnonymousPrivate() => new { A = 1 };
                    public static infer operator +(C a, C b) => new Secret();

                    public Secret LeakCSharp() => new Secret();
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (4,18): error CS0050: Inconsistent accessibility: return type 'C.Secret' is less accessible than method 'C.Leak()'
                //     public infer Leak() => new Secret();
                Diagnostic(ErrorCode.ERR_BadVisReturnType, "Leak").WithArguments("C.Leak()", "C.Secret").WithLocation(4, 18),
                // (5,18): error CS0050: Inconsistent accessibility: return type '<anonymous type: int A>' is less accessible than method 'C.Anonymous()'
                //     public infer Anonymous() => new { A = 1 };
                Diagnostic(ErrorCode.ERR_BadVisReturnType, "Anonymous").WithArguments("C.Anonymous()", "<anonymous type: int A>").WithLocation(5, 18),
                // (8,34): error CS0056: Inconsistent accessibility: return type 'C.Secret' is less accessible than operator 'C.operator +(C, C)'
                //     public static infer operator +(C a, C b) => new Secret();
                Diagnostic(ErrorCode.ERR_BadVisOpReturn, "+").WithArguments("C.operator +(C, C)", "C.Secret").WithLocation(8, 34),
                // (10,19): error CS0050: Inconsistent accessibility: return type 'C.Secret' is less accessible than method 'C.LeakCSharp()'
                //     public Secret LeakCSharp() => new Secret();
                Diagnostic(ErrorCode.ERR_BadVisReturnType, "LeakCSharp").WithArguments("C.LeakCSharp()", "C.Secret").WithLocation(10, 19));
        }

        [Fact]
        public void InferMember_RefSafety()
        {
            // An inferred ref struct type gets the same escape analysis as a written one.
            var source = """
                using System;

                public class C
                {
                    public static infer Pass(Span<int> s) => s;
                    public static infer Wrap(ref int x) => new Span<int>(ref x);
                    public static infer Escapes() { Span<int> s = stackalloc int[1]; return s; }
                    public static Span<int> EscapesCSharp() { Span<int> s = stackalloc int[1]; return s; }
                    public static infer Local() { int x = 1; return new Span<int>(ref x); }
                    public static Span<int> LocalCSharp() { int x = 1; return new Span<int>(ref x); }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (7,77): error CS8352: Cannot use variable 's' in this context because it may expose referenced variables outside of their declaration scope
                //     public static infer Escapes() { Span<int> s = stackalloc int[1]; return s; }
                Diagnostic(ErrorCode.ERR_EscapeVariable, "s").WithArguments("s").WithLocation(7, 77),
                // (8,87): error CS8352: Cannot use variable 's' in this context because it may expose referenced variables outside of their declaration scope
                //     public static Span<int> EscapesCSharp() { Span<int> s = stackalloc int[1]; return s; }
                Diagnostic(ErrorCode.ERR_EscapeVariable, "s").WithArguments("s").WithLocation(8, 87),
                // (9,53): error CS8347: Cannot use a result of 'Span<int>.Span(ref int)' in this context because it may expose variables referenced by parameter 'reference' outside of their declaration scope
                //     public static infer Local() { int x = 1; return new Span<int>(ref x); }
                Diagnostic(ErrorCode.ERR_EscapeCall, "new Span<int>(ref x)").WithArguments("System.Span<int>.Span(ref int)", "reference").WithLocation(9, 53),
                // (9,71): error CS8168: Cannot return local 'x' by reference because it is not a ref local
                //     public static infer Local() { int x = 1; return new Span<int>(ref x); }
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "x").WithArguments("x").WithLocation(9, 71),
                // (10,63): error CS8347: Cannot use a result of 'Span<int>.Span(ref int)' in this context because it may expose variables referenced by parameter 'reference' outside of their declaration scope
                //     public static Span<int> LocalCSharp() { int x = 1; return new Span<int>(ref x); }
                Diagnostic(ErrorCode.ERR_EscapeCall, "new Span<int>(ref x)").WithArguments("System.Span<int>.Span(ref int)", "reference").WithLocation(10, 63),
                // (10,81): error CS8168: Cannot return local 'x' by reference because it is not a ref local
                //     public static Span<int> LocalCSharp() { int x = 1; return new Span<int>(ref x); }
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "x").WithArguments("x").WithLocation(10, 81));
            Assert.Equal("System.Span<System.Int32> C.Pass(System.Span<System.Int32> s)", comp.GetMember<MethodSymbol>("C.Pass").ToTestDisplayString());
        }

        [Fact]
        public void InferMember_Metadata()
        {
            // A C# compilation that references the assembly sees the inferred types, with their nullable annotations.
            var source = """
                #nullable enable
                using System.Collections.Generic;

                public class C
                {
                    public static infer Name(string? s) => s;
                    public virtual infer Count() => 1;
                    public static infer operator +(C a, C b) => "plus";
                    public static explicit operator infer(C c) => 1L;
                }

                public partial class P
                {
                    public partial infer Load(int id);
                    public partial infer Load(int id) => new List<int> { id };
                }

                public static class E
                {
                    extension(int i)
                    {
                        public infer Twice() => i * 2;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyEmitDiagnostics();

            var consumer = """
                #nullable enable
                using System.Collections.Generic;

                class Consumer
                {
                    static void M(C c)
                    {
                        string? name = C.Name(null);
                        int count = c.Count();
                        string plus = c + c;
                        long l = (long)c;
                        List<int> list = new P().Load(1);
                        int twice = 2.Twice();
                        string s = C.Name(null);
                    }
                }

                class D : C
                {
                    public override int Count() => 2;
                }
                """;
            foreach (var reference in new[] { comp.EmitToImageReference(), comp.ToMetadataReference() })
            {
                var csharp = CreateCompilation(consumer, references: [reference], targetFramework: TargetFramework.Net90);
                csharp.VerifyDiagnostics(
                    // (14,20): warning CS8600: Converting null literal or possible null value to non-nullable type.
                    //         string s = C.Name(null);
                    Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "C.Name(null)").WithLocation(14, 20));
                Assert.Equal("System.String? C.Name(System.String? s)", csharp.GetMember<MethodSymbol>("C.Name").ToTestDisplayString(includeNonNullable: true));
                Assert.Equal("System.String! C.operator +(C! a, C! b)", csharp.GetMember<MethodSymbol>("C.op_Addition").ToTestDisplayString(includeNonNullable: true));
            }
        }

        [Fact]
        public void InferMember_SemanticModel()
        {
            var source = """
                public struct S
                {
                    public static infer operator +(S a, S b) => 1;
                    public static implicit operator infer(S s) => "s";
                }

                public partial class P
                {
                    public partial infer M();
                    public partial infer M() => 1L;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var inferSyntaxes = new TypeSyntax[]
            {
                root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Single().ReturnType,
                root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>().Single().Type,
                root.DescendantNodes().OfType<MethodDeclarationSyntax>().First().ReturnType,
                root.DescendantNodes().OfType<MethodDeclarationSyntax>().Last().ReturnType,
            };
            AssertEx.Equal(["System.Int32", "System.String", "System.Int64", "System.Int64"], inferSyntaxes.Select(t => model.GetTypeInfo(t).Type.ToTestDisplayString()));
            AssertEx.Equal(["System.Int32", "System.String", "System.Int64", "System.Int64"], inferSyntaxes.Select(t => model.GetSymbolInfo(t).Symbol.ToTestDisplayString()));
        }

        [Fact]
        public void InferMember_IOperation()
        {
            var source = """
                public struct S
                {
                    public int Value;
                    public static infer operator +(S a, S b) /*<bind>*/=> a.Value + b.Value/*</bind>*/;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IBlockOperation (1 statements) (OperationKind.Block, Type: null) (Syntax: '=> a.Value + b.Value')
                  IReturnOperation (OperationKind.Return, Type: null, IsImplicit) (Syntax: 'a.Value + b.Value')
                    ReturnedValue:
                      IBinaryOperation (BinaryOperatorKind.Add) (OperationKind.Binary, Type: System.Int32) (Syntax: 'a.Value + b.Value')
                        Left:
                          IFieldReferenceOperation: System.Int32 S.Value (OperationKind.FieldReference, Type: System.Int32) (Syntax: 'a.Value')
                            Instance Receiver:
                              IParameterReferenceOperation: a (OperationKind.ParameterReference, Type: S) (Syntax: 'a')
                        Right:
                          IFieldReferenceOperation: System.Int32 S.Value (OperationKind.FieldReference, Type: System.Int32) (Syntax: 'b.Value')
                            Instance Receiver:
                              IParameterReferenceOperation: b (OperationKind.ParameterReference, Type: S) (Syntax: 'b')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<ArrowExpressionClauseSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void InferMember_UsesInferredMembers_Order()
        {
            // An inferred method can use inferred operators, conversions and partial methods, in any order of inference.
            var source = """
                public struct S
                {
                    public int Value;
                    public static infer operator +(S a, S b) => new S { Value = a.Value + b.Value };
                    public static implicit operator infer(S s) => (long)s.Value;
                    public static infer Sum(S a, S b) { long l = a + b; return l + P.Half(l); }
                }

                public partial class P
                {
                    public static partial infer Half(long l);
                    public static partial infer Half(long l) => l / 2.0;
                }
                """;
            var names = new[] { "S.op_Addition", "S.op_Implicit", "S.Sum", "P.Half" };
            foreach (var first in names)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<MethodSymbol>(first).ReturnType;
                comp.VerifyDiagnostics();
                AssertEx.Equal(["S", "System.Int64", "System.Double", "System.Double"], names.Select(n => comp.GetMember<MethodSymbol>(n).ReturnType.ToTestDisplayString()));
            }
        }

        [Fact]
        public void InferMember_Cycle()
        {
            // A conversion operator whose body converts from its own type needs its own return type, through the lookup
            // of user-defined conversions (CS20018). With an error type, the operator converts to nothing, so the body
            // reports CS0029 when it is compiled. A method that uses the operator from outside its body infers after it.
            var source = """
                public struct S
                {
                    public static implicit operator infer(S s) { long l = s; return l; }
                }

                public struct R
                {
                    public int Value;
                    public static implicit operator infer(R r) => r.Value;
                    public static infer Twice(R r) { int i = r; return i * 2; }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,37): error CS20018: Cannot infer the return type of 'S.implicit operator infer(S)', because its body uses 'S.implicit operator infer(S)' itself. Write the return type.
                //     public static implicit operator infer(S s) { long l = s; return l; }
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("S.implicit operator infer(S)").WithLocation(3, 37),
                // (3,59): error CS0029: Cannot implicitly convert type 'S' to 'long'
                //     public static implicit operator infer(S s) { long l = s; return l; }
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "s").WithArguments("S", "long").WithLocation(3, 59));
        }
    }
}
