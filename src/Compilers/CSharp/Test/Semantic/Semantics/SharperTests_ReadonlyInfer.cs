// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's inferred types, step P3 (issue #74): <c>readonly infer</c> parameters, whose type is inferred (from the
    /// default value, P1, or from the overridden or implemented member, P2) and which cannot be reassigned.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void ReadonlyInfer_Basic()
        {
            // Each member kind next to its twin, which writes the type out; both read their parameters as usual.
            var source = """
                using System;

                var c = new C();
                Console.Write(c.M() + ";");
                Console.Write(c.M(5) + ";");
                Console.Write(Local() + ";");
                var f = (readonly infer x = 3L) => x + 1;
                Console.Write(f(4) + ";");
                Console.Write(new P().Get() + ";");

                static string Local(readonly infer name = "local") => name;

                class C
                {
                    public C(readonly infer start = 1u) { Console.Write(start + ";"); }
                    public long M(readonly infer x = 10L, readonly infer day = DayOfWeek.Monday) => x + (long)day;
                }

                class CCSharp
                {
                    public CCSharp(readonly uint start = 1u) { }
                    public long M(readonly long x = 10L, readonly DayOfWeek day = DayOfWeek.Monday) => x + (long)day;
                }

                class P(readonly infer value = 'p')
                {
                    public char Get() => value;
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1;11;6;local;5;p;" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameSignature(comp, "C.M", "CCSharp.M");
            VerifySameSignature(comp, "C..ctor", "CCSharp..ctor");
            foreach (var parameter in comp.GetMember<MethodSymbol>("C.M").Parameters)
            {
                Assert.True(((SourceComplexParameterSymbolBase)parameter).IsSharperReadOnly);
            }
        }

        [Fact]
        public void ReadonlyInfer_Writes()
        {
            // Writes to a readonly infer parameter report the readonly parameter errors, in every member kind, also from
            // lambdas and local functions; the plain infer twin is writable. The issue's example also keeps CS1736.
            var source = """
                using System;

                class C
                {
                    static void Ref(ref int i) { }

                    void Fetch(readonly infer timeout = TimeSpan.FromSeconds(5)) { timeout = default; }

                    void M(readonly infer x = 1, infer y = 1)
                    {
                        x = 2;
                        y = 2;
                        x++;
                        y++;
                        Ref(ref x);
                        Ref(ref y);
                        Action a = () => x = 3;
                        Local();
                        void Local() { x += 4; }
                    }

                    C(readonly infer s = "a") { s = "b"; }

                    void N()
                    {
                        Local();
                        void Local(readonly infer z = 1L) { z = 2; }
                        Action<int> f = (readonly infer w = 1) => w = 2;
                    }
                }

                class P(readonly infer p = 1)
                {
                    void M() { p = 2; }
                }

                struct S
                {
                    public int F;
                    void M(readonly infer s = default(S), infer t = default(S)) { s.F = 1; t.F = 1; }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (7,41): error CS1736: Default parameter value for 'timeout' must be a compile-time constant
                //     void Fetch(readonly infer timeout = TimeSpan.FromSeconds(5)) { timeout = default; }
                Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "TimeSpan.FromSeconds(5)").WithArguments("timeout").WithLocation(7, 41),
                // (7,68): error CS1656: Cannot assign to 'timeout' because it is a 'readonly parameter'
                //     void Fetch(readonly infer timeout = TimeSpan.FromSeconds(5)) { timeout = default; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "timeout").WithArguments("timeout", "readonly parameter").WithLocation(7, 68),
                // (11,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(11, 9),
                // (13,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(13, 9),
                // (15,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly parameter'
                //         Ref(ref x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(15, 17),
                // (17,26): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         Action a = () => x = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(17, 26),
                // (19,24): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         void Local() { x += 4; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(19, 24),
                // (22,33): error CS1656: Cannot assign to 's' because it is a 'readonly parameter'
                //     C(readonly infer s = "a") { s = "b"; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "s").WithArguments("s", "readonly parameter").WithLocation(22, 33),
                // (27,45): error CS1656: Cannot assign to 'z' because it is a 'readonly parameter'
                //         void Local(readonly infer z = 1L) { z = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "z").WithArguments("z", "readonly parameter").WithLocation(27, 45),
                // (28,51): error CS1656: Cannot assign to 'w' because it is a 'readonly parameter'
                //         Action<int> f = (readonly infer w = 1) => w = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "w").WithArguments("w", "readonly parameter").WithLocation(28, 51),
                // (34,16): error CS1656: Cannot assign to 'p' because it is a 'readonly parameter'
                //     void M() { p = 2; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "p").WithArguments("p", "readonly parameter").WithLocation(34, 16),
                // (40,67): error CS1654: Cannot modify members of 's' because it is a 'readonly parameter'
                //     void M(readonly infer s = default(S), infer t = default(S)) { s.F = 1; t.F = 1; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocal2Cause, "s.F").WithArguments("s", "readonly parameter").WithLocation(40, 67));
        }

        [Fact]
        public void ReadonlyInfer_Overridden()
        {
            // An override or implementation copies the types (P2) and the parameter stays readonly; the plain infer s is
            // writable. A copied ref kind gets the readonly ref rules: writes through the ref follow C#, ref reassignment is
            // the readonly parameter error.
            var source = """
                abstract class B
                {
                    public abstract int Get(int x, string s);
                    public abstract void Refs(ref int r, in long i, out string o);
                }

                interface I
                {
                    int Next(int x);
                    int Twice(int x);
                }

                class C : B, I
                {
                    static int j;
                    static long k;

                    public override infer Get(readonly infer x, infer s) { x = 1; s = ""; return x; }
                    public override void Refs(readonly infer r, readonly infer i, readonly infer o)
                    {
                        r = 5;
                        i = 5;
                        o = "";
                        r = ref j;
                        i = ref k;
                    }

                    public infer Next(readonly infer x) => x++;
                    infer I.Twice(readonly infer x) => x *= 2;
                }

                class CCSharp : B, I
                {
                    public override int Get(readonly int x, string s) { return x; }
                    public override void Refs(readonly ref int r, readonly in long i, readonly out string o) { o = ""; }
                    public int Next(readonly int x) => x;
                    int I.Twice(readonly int x) => x;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (18,60): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     public override infer Get(readonly infer x, infer s) { x = 1; s = ""; return x; }
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(18, 60),
                // (22,9): error CS8331: Cannot assign to variable 'i' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         i = 5;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "i").WithArguments("variable", "i").WithLocation(22, 9),
                // (24,9): error CS1656: Cannot assign to 'r' because it is a 'readonly parameter'
                //         r = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "r").WithArguments("r", "readonly parameter").WithLocation(24, 9),
                // (25,9): error CS1656: Cannot assign to 'i' because it is a 'readonly parameter'
                //         i = ref k;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "i").WithArguments("i", "readonly parameter").WithLocation(25, 9),
                // (28,44): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     public infer Next(readonly infer x) => x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(28, 44),
                // (29,40): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //     infer I.Twice(readonly infer x) => x *= 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(29, 40));
            VerifySameSignature(comp, "C.Get", "CCSharp.Get");
            VerifySameSignature(comp, "C.Refs", "CCSharp.Refs");
            VerifySameSignature(comp, "C.Next", "CCSharp.Next");
            VerifySameSignature(comp, "C.I.Twice", "CCSharp.I.Twice");
        }

        [Fact]
        public void ReadonlyInfer_Overridden_Run()
        {
            // At run time the copied types behave as written.
            var source = """
                using System;

                B b = new C();
                Console.Write(b.Get(2, "s") + ";");
                I i = new C();
                Console.Write(i.Next(3) + ";");
                int r = 1;
                b.Inc(ref r);
                Console.Write(r);

                abstract class B
                {
                    public abstract string Get(int x, string s);
                    public abstract void Inc(ref int r);
                }

                interface I { int Next(int x); }

                class C : B, I
                {
                    public override infer Get(readonly infer x, readonly infer s) => s + x;
                    public override void Inc(readonly infer r) { r++; }
                    public infer Next(readonly infer x) => x + 1;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "s2;4;2" : null).VerifyDiagnostics();
        }

        [Fact]
        public void ReadonlyInfer_Ref()
        {
            // With a default value, each ref kind reports what its C# twin does: CS1741 for ref, CS9200 for ref readonly. On
            // in and ref readonly, ref reassignment is the readonly parameter error and writes are CS8331, as for readonly in int.
            var source = """
                class C
                {
                    static int j;

                    void M(readonly in infer i = 1, readonly ref infer r = 1, readonly ref readonly infer rr = 1)
                    {
                        i = 2;
                        i = ref j;
                        rr = ref j;
                    }

                    void MCSharp(readonly in int i = 1, readonly ref int r = 1, readonly ref readonly int rr = 1)
                    {
                        i = 2;
                        i = ref j;
                        rr = ref j;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (5,46): error CS1741: A ref or out parameter cannot have a default value
                //     void M(readonly in infer i = 1, readonly ref infer r = 1, readonly ref readonly infer rr = 1)
                Diagnostic(ErrorCode.ERR_RefOutDefaultValue, "ref").WithLocation(5, 46),
                // (5,96): warning CS9200: A default value is specified for 'ref readonly' parameter 'rr', but 'ref readonly' should be used only for references. Consider declaring the parameter as 'in'.
                //     void M(readonly in infer i = 1, readonly ref infer r = 1, readonly ref readonly infer rr = 1)
                Diagnostic(ErrorCode.WRN_RefReadonlyParameterDefaultValue, "1").WithArguments("rr").WithLocation(5, 96),
                // (7,9): error CS8331: Cannot assign to variable 'i' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         i = 2;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "i").WithArguments("variable", "i").WithLocation(7, 9),
                // (8,9): error CS1656: Cannot assign to 'i' because it is a 'readonly parameter'
                //         i = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "i").WithArguments("i", "readonly parameter").WithLocation(8, 9),
                // (9,9): error CS1656: Cannot assign to 'rr' because it is a 'readonly parameter'
                //         rr = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "rr").WithArguments("rr", "readonly parameter").WithLocation(9, 9),
                // (12,50): error CS1741: A ref or out parameter cannot have a default value
                //     void MCSharp(readonly in int i = 1, readonly ref int r = 1, readonly ref readonly int rr = 1)
                Diagnostic(ErrorCode.ERR_RefOutDefaultValue, "ref").WithLocation(12, 50),
                // (12,96): warning CS9200: A default value is specified for 'ref readonly' parameter 'rr', but 'ref readonly' should be used only for references. Consider declaring the parameter as 'in'.
                //     void MCSharp(readonly in int i = 1, readonly ref int r = 1, readonly ref readonly int rr = 1)
                Diagnostic(ErrorCode.WRN_RefReadonlyParameterDefaultValue, "1").WithArguments("rr").WithLocation(12, 96),
                // (14,9): error CS8331: Cannot assign to variable 'i' or use it as the right hand side of a ref assignment because it is a readonly variable
                //         i = 2;
                Diagnostic(ErrorCode.ERR_AssignReadonlyNotField, "i").WithArguments("variable", "i").WithLocation(14, 9),
                // (15,9): error CS1656: Cannot assign to 'i' because it is a 'readonly parameter'
                //         i = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "i").WithArguments("i", "readonly parameter").WithLocation(15, 9),
                // (16,9): error CS1656: Cannot assign to 'rr' because it is a 'readonly parameter'
                //         rr = ref j;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "rr").WithArguments("rr", "readonly parameter").WithLocation(16, 9));
            VerifySameSignature(comp, "C.M", "C.MCSharp");
        }

        [Fact]
        public void ReadonlyInfer_StructMemberCallOnCopy()
        {
            // A non-readonly member of a struct readonly infer parameter runs on a copy and warns (CS20013), whichever
            // source gave the type.
            var source = """
                using System;

                new C().M();
                Console.Write(";");
                new C().N(new S());

                struct S
                {
                    public int F;
                    public void Mutate() { F++; }
                    public readonly void ReadOnlyMethod() { }
                }

                abstract class B
                {
                    public abstract void N(S s);
                }

                class C : B
                {
                    public void M(readonly infer s = default(S))
                    {
                        s.Mutate();
                        s.ReadOnlyMethod();
                        Console.Write(s.F);
                    }

                    public override void N(readonly infer s)
                    {
                        s.Mutate();
                        Console.Write(s.F);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0;0" : null).VerifyDiagnostics(
                // (23,9): warning CS20013: Call to non-readonly member 'S.Mutate()' on readonly parameter 's' runs on a copy, so 's' is not changed.
                //         s.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(23, 9),
                // (30,9): warning CS20013: Call to non-readonly member 'S.Mutate()' on readonly parameter 's' runs on a copy, so 's' is not changed.
                //         s.Mutate();
                Diagnostic(ErrorCode.WRN_SharperReadOnlyParameterMemberCallOnCopy, "s").WithArguments("S.Mutate()", "s").WithLocation(30, 9));
        }

        [Fact]
        public void ReadonlyInfer_FeatureDisabled()
        {
            // Without Sharper, readonly keeps CS9190 and infer is a type name.
            var source = """
                class C
                {
                    void M(readonly infer x = 1) { x = 2; }
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,12): error CS9190: 'readonly' modifier must be specified after 'ref'.
                //     void M(readonly infer x = 1) { x = 2; }
                Diagnostic(ErrorCode.ERR_RefReadOnlyWrongOrdering, "readonly").WithLocation(3, 12),
                // (3,21): error CS0246: The type or namespace name 'infer' could not be found (are you missing a using directive or an assembly reference?)
                //     void M(readonly infer x = 1) { x = 2; }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "infer").WithArguments("infer").WithLocation(3, 21),
                // (3,27): error CS1750: A value of type 'int' cannot be used as a default parameter because there are no standard conversions to type 'infer'
                //     void M(readonly infer x = 1) { x = 2; }
                Diagnostic(ErrorCode.ERR_NoConversionForDefaultParam, "x").WithArguments("int", "infer").WithLocation(3, 27));
        }
    }
}
