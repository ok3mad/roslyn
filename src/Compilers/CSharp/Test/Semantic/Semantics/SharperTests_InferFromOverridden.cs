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
    /// Sharper's inferred types, step P2 (issue #73): an override or an interface implementation copies its <c>infer</c>
    /// parameter and return types from the member it overrides or implements.
    /// </summary>
    public sealed partial class SharperTests
    {
        private static void VerifySameSignature(CSharpCompilation comp, string member, string twin)
        {
            var method = comp.GetMember<MethodSymbol>(member);
            var twinMethod = comp.GetMember<MethodSymbol>(twin);
            Assert.Equal(twinMethod.ReturnTypeWithAnnotations.ToTestDisplayString(), method.ReturnTypeWithAnnotations.ToTestDisplayString());
            Assert.Equal(twinMethod.ParameterCount, method.ParameterCount);
            for (int i = 0; i < method.ParameterCount; i++)
            {
                Assert.Equal(twinMethod.Parameters[i].TypeWithAnnotations.ToTestDisplayString(), method.Parameters[i].TypeWithAnnotations.ToTestDisplayString());
                Assert.Equal(twinMethod.Parameters[i].RefKind, method.Parameters[i].RefKind);
                Assert.Equal(twinMethod.Parameters[i].IsParams, method.Parameters[i].IsParams);
            }
        }

        [Fact]
        public void InferOverridden_Basic()
        {
            // Each override and implementation next to its C# twin, which writes the types out.
            var source = """
                using System;
                using System.Threading.Tasks;

                record ProfileResult(string Name);

                interface IProfiles
                {
                    Task<ProfileResult> Load(int userId);
                }

                class FakeProfiles(ProfileResult result) : IProfiles
                {
                    public infer Load(infer userId) => Task.FromResult(result with { Name = result.Name + userId });
                }

                class FakeProfilesCSharp(ProfileResult result) : IProfiles
                {
                    public Task<ProfileResult> Load(int userId) => Task.FromResult(result with { Name = result.Name + userId });
                }

                abstract class Shape
                {
                    public abstract double Area(double scale);
                    public virtual string Describe(string prefix, int digits) => prefix;
                }

                class Square(double side) : Shape
                {
                    public override infer Area(infer scale) => side * side * scale;
                    public override infer Describe(infer prefix, infer digits) => prefix + Math.Round(Area(1), digits);
                }

                class SquareCSharp(double side) : Shape
                {
                    public override double Area(double scale) => side * side * scale;
                    public override string Describe(string prefix, int digits) => prefix + Math.Round(Area(1), digits);
                }

                class Explicit : IProfiles
                {
                    infer IProfiles.Load(infer userId) => Task.FromResult(new ProfileResult("e" + userId));
                }

                class ExplicitCSharp : IProfiles
                {
                    Task<ProfileResult> IProfiles.Load(int userId) => Task.FromResult(new ProfileResult("e" + userId));
                }

                class Program
                {
                    static async Task Main()
                    {
                        IProfiles[] profiles = [new FakeProfiles(new("a")), new FakeProfilesCSharp(new("a")), new Explicit(), new ExplicitCSharp()];
                        foreach (var p in profiles)
                        {
                            Console.Write((await p.Load(1)).Name + ";");
                        }

                        Console.Write(new FakeProfiles(new("b")).Load(2).Result.Name + ";");
                        Shape[] shapes = [new Square(1.5), new SquareCSharp(1.5)];
                        foreach (var s in shapes)
                        {
                            Console.Write(s.Area(2) + s.Describe(":", 1) + ";");
                        }
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "a1;a1;e1;e1;b2;4.5:2.2;4.5:2.2;" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameSignature(comp, "FakeProfiles.Load", "FakeProfilesCSharp.Load");
            VerifySameSignature(comp, "Square.Area", "SquareCSharp.Area");
            VerifySameSignature(comp, "Square.Describe", "SquareCSharp.Describe");
            VerifySameSignature(comp, "Explicit.IProfiles.Load", "ExplicitCSharp.IProfiles.Load");
        }

        [Fact]
        public void InferOverridden_Copies()
        {
            // Ref kinds, params, nullability, tuple element names, generic substitutions and method type parameters are
            // copied as written in the member.
            var source = """
                #nullable enable
                using System.Collections.Generic;

                abstract class B<T>
                {
                    public abstract void Refs(ref int r, out string o, in long i, ref readonly T rr);
                    public abstract (int Count, T? Last) Tuple(List<T?> items, params T[] more);
                    public abstract U Generic<U>(U value, T other) where U : class;
                    public abstract string? Nullable(string? s, string t);
                }

                class C : B<string>
                {
                    public override void Refs(infer r, infer o, infer i, infer rr) { o = ""; }
                    public override infer Tuple(infer items, infer more) => (0, null);
                    public override infer Generic<V>(infer value, infer other) => value;
                    public override infer Nullable(infer s, infer t) => s;
                }

                class CCSharp : B<string>
                {
                    public override void Refs(ref int r, out string o, in long i, ref readonly string rr) { o = ""; }
                    public override (int Count, string? Last) Tuple(List<string?> items, params string[] more) => (0, null);
                    public override V Generic<V>(V value, string other) => value;
                    public override string? Nullable(string? s, string t) => s;
                }

                interface I
                {
                    int Sum(params int[] values);
                    void Out(out int x, ref string y);
                }

                class Implicit : I
                {
                    public infer Sum(infer values) => values.Length;
                    public void Out(infer x, infer y) { x = 1; }
                    static int Use(Implicit i) => i.Sum(1, 2, 3);
                }

                class ImplicitCSharp : I
                {
                    public int Sum(params int[] values) => values.Length;
                    public void Out(out int x, ref string y) { x = 1; }
                    static int Use(ImplicitCSharp i) => i.Sum(1, 2, 3);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            foreach (var name in new[] { "Refs", "Tuple", "Generic", "Nullable" })
            {
                VerifySameSignature(comp, "C." + name, "CCSharp." + name);
            }

            VerifySameSignature(comp, "Implicit.Sum", "ImplicitCSharp.Sum");
            VerifySameSignature(comp, "Implicit.Out", "ImplicitCSharp.Out");
            Assert.Equal("V C.Generic<V>(V value, System.String other)", comp.GetMember<MethodSymbol>("C.Generic").ToTestDisplayString());
            Assert.Equal("(System.Int32 Count, System.String? Last) C.Tuple(System.Collections.Generic.List<System.String?> items, params System.String[] more)",
                comp.GetMember<MethodSymbol>("C.Tuple").ToTestDisplayString());
        }

        [Fact]
        public void InferOverridden_WrittenTypesNarrow()
        {
            // Written types, ref kinds and types inferred from default values choose among overloads with the same name
            // and parameter count; 'infer' fills the rest.
            var source = """
                abstract class B
                {
                    public abstract string M(int a, string b);
                    public abstract string M(long a, string b);
                    public abstract string M(long a, object b);
                    public abstract int R(int a);
                    public abstract long R(long a);
                    public abstract void F(ref int a);
                    public abstract void F(int a);
                    public abstract string D(int a, long b);
                    public abstract string D(long a, long b);
                }

                class C : B
                {
                    public override infer M(int a, infer b) => b + a;
                    public override string M(long a, string b) => b;
                    public override infer M(infer a, object b) => "o" + a;
                    public override long R(infer a) => a;
                    public override int R(int a) => a;
                    public override void F(ref infer a) { }
                    public override void F(int a) { }
                    public override infer D(infer a = 1L, infer b = 2L) => "" + a + b;
                    public override string D(int a, long b) => "";
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var members = comp.GetMember<NamedTypeSymbol>("C").GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).Select(m => m.ToTestDisplayString());
            AssertEx.Equal(new[]
            {
                "System.String C.M(System.Int32 a, System.String b)",
                "System.String C.M(System.Int64 a, System.String b)",
                "System.String C.M(System.Int64 a, System.Object b)",
                "System.Int64 C.R(System.Int64 a)",
                "System.Int32 C.R(System.Int32 a)",
                "void C.F(ref System.Int32 a)",
                "void C.F(System.Int32 a)",
                "System.String C.D([System.Int64 a = 1], [System.Int64 b = 2])",
                "System.String C.D(System.Int32 a, System.Int64 b)",
            }, members);
        }

        [Fact]
        public void InferOverridden_Errors()
        {
            var source = """
                abstract class B
                {
                    public abstract void Overloads(int a);
                    public abstract void Overloads(string a);
                    public abstract int Count(int a);
                    public void NotVirtual(int a) { }
                    private protected virtual void Hidden(int a) { }
                }

                class C : B
                {
                    public override void Overloads(infer a) { }
                    public override infer Count(infer a, infer b) => 0;
                    public override void NotVirtual(infer a) { }
                    public override infer Missing() => 0;
                    public override void Count(string a) { }
                    public override Missing Count(infer a) => 0;
                }

                interface I1 { int M(int a); string N(int a); }
                interface I2 { long M(int a); string N(int a); }

                class D : I1, I2
                {
                    public infer M(infer a) => 0;
                    public infer N(infer a) => "";
                    public infer Unrelated(infer a) => 0;
                    infer I1.Missing(infer a) => 0;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (10,7): error CS0534: 'C' does not implement inherited abstract member 'B.Overloads(string)'
                // class C : B
                Diagnostic(ErrorCode.ERR_UnimplementedAbstractMethod, "C").WithArguments("C", "B.Overloads(string)").WithLocation(10, 7),
                // (10,7): error CS0534: 'C' does not implement inherited abstract member 'B.Overloads(int)'
                // class C : B
                Diagnostic(ErrorCode.ERR_UnimplementedAbstractMethod, "C").WithArguments("C", "B.Overloads(int)").WithLocation(10, 7),
                // (12,26): error CS20044: Cannot infer the types of 'Overloads', because it could override or implement more than one member: 'B.Overloads(int)', 'B.Overloads(string)'. Write the types of the one it overrides or implements.
                //     public override void Overloads(infer a) { }
                Diagnostic(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, "Overloads").WithArguments("Overloads", "'B.Overloads(int)', 'B.Overloads(string)'").WithLocation(12, 26),
                // (13,27): error CS20043: Cannot infer the types of 'Count', because it overrides or implements no member 'Count' with 2 parameters that matches its written types. Write the types.
                //     public override infer Count(infer a, infer b) => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "Count").WithArguments("Count", "2").WithLocation(13, 27),
                // (14,26): error CS20043: Cannot infer the types of 'NotVirtual', because it overrides or implements no member 'NotVirtual' with 1 parameters that matches its written types. Write the types.
                //     public override void NotVirtual(infer a) { }
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "NotVirtual").WithArguments("NotVirtual", "1").WithLocation(14, 26),
                // (15,27): error CS20043: Cannot infer the types of 'Missing', because it overrides or implements no member 'Missing' with 0 parameters that matches its written types. Write the types.
                //     public override infer Missing() => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "Missing").WithArguments("Missing", "0").WithLocation(15, 27),
                // (15,27): error CS0115: 'C.Missing()': no suitable method found to override
                //     public override infer Missing() => 0;
                Diagnostic(ErrorCode.ERR_OverrideNotExpected, "Missing").WithArguments("C.Missing()").WithLocation(15, 27),
                // (16,26): error CS0115: 'C.Count(string)': no suitable method found to override
                //     public override void Count(string a) { }
                Diagnostic(ErrorCode.ERR_OverrideNotExpected, "Count").WithArguments("C.Count(string)").WithLocation(16, 26),
                // (17,21): error CS0246: The type or namespace name 'Missing' could not be found (are you missing a using directive or an assembly reference?)
                //     public override Missing Count(infer a) => 0;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Missing").WithArguments("Missing").WithLocation(17, 21),
                // (23,11): error CS0535: 'D' does not implement interface member 'I1.M(int)'
                // class D : I1, I2
                Diagnostic(ErrorCode.ERR_UnimplementedInterfaceMember, "I1").WithArguments("D", "I1.M(int)").WithLocation(23, 11),
                // (23,15): error CS0535: 'D' does not implement interface member 'I2.M(int)'
                // class D : I1, I2
                Diagnostic(ErrorCode.ERR_UnimplementedInterfaceMember, "I2").WithArguments("D", "I2.M(int)").WithLocation(23, 15),
                // (25,18): error CS20044: Cannot infer the types of 'M', because it could override or implement more than one member: 'I1.M(int)', 'I2.M(int)'. Write the types of the one it overrides or implements.
                //     public infer M(infer a) => 0;
                Diagnostic(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, "M").WithArguments("M", "'I1.M(int)', 'I2.M(int)'").WithLocation(25, 18),
                // (27,12): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     public infer Unrelated(infer a) => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(27, 12),
                // (27,28): error CS20027: Cannot infer the type of parameter 'a', because it has no default value. Write the parameter type.
                //     public infer Unrelated(infer a) => 0;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("a").WithLocation(27, 28),
                // (28,14): error CS20043: Cannot infer the types of 'Missing', because it overrides or implements no member 'Missing' with 1 parameters that matches its written types. Write the types.
                //     infer I1.Missing(infer a) => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "Missing").WithArguments("Missing", "1").WithLocation(28, 14),
                // (28,14): error CS0539: 'D.Missing(infer)' in explicit interface declaration is not found among members of the interface that can be implemented
                //     infer I1.Missing(infer a) => 0;
                Diagnostic(ErrorCode.ERR_InterfaceMemberNotFound, "Missing").WithArguments("D.Missing(infer)").WithLocation(28, 14));
        }

        [Fact]
        public void InferOverridden_Order()
        {
            // A base whose own types are inferred (from a default value, or from its own base) is completed first,
            // whichever member is asked for first.
            var source = """
                class A
                {
                    public virtual string M(int a, infer b = 2L) => "A";
                }

                class B : A
                {
                    public override infer M(infer a, infer b) => "B" + a + b;
                }

                class C : B
                {
                    public override infer M(infer a, infer b) => "C" + a + b;
                }

                interface I
                {
                    double Get(infer x = 1.5);
                }

                class D : I
                {
                    public infer Get(infer x) => x;
                }
                """;
            foreach (var first in new[] { "C.M", "B.M", "A.M", "D.Get", "I.Get" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<MethodSymbol>(first).Parameters;
                comp.VerifyDiagnostics();
                Assert.Equal("System.String C.M(System.Int32 a, System.Int64 b)", comp.GetMember<MethodSymbol>("C.M").ToTestDisplayString());
                Assert.Equal("System.String B.M(System.Int32 a, System.Int64 b)", comp.GetMember<MethodSymbol>("B.M").ToTestDisplayString());
                Assert.Equal("System.Double D.Get(System.Double x)", comp.GetMember<MethodSymbol>("D.Get").ToTestDisplayString());
            }
        }

        [Fact]
        public void InferOverridden_Cycle()
        {
            // A member whose signature depends on the member that copies from it cannot give it a type. Calling a
            // method whose types are written reads no signature in a cycle.
            var source = """
                class B
                {
                    public virtual int M(infer x = new D().M(1)) => 0;
                    public virtual int N(infer x = D.K()) => 0;
                }

                class D : B
                {
                    public static int K() => new D().N(1);
                    public override int M(infer x) => 0;
                    public override int N(infer x) => 0;
                }
                """;
            foreach (var first in new[] { "B.M", "D.M", "B.N", "D.N", "D.K" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<MethodSymbol>(first).Parameters;
                comp.VerifyDiagnostics(
                    // (3,26): error CS20028: Cannot infer the type of parameter 'x', because its default value depends on the type of 'x' itself. Write the parameter type.
                    //     public virtual int M(infer x = new D().M(1)) => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferParameterCycle, "infer").WithArguments("x").WithLocation(3, 26),
                    // (3,46): error CS1503: Argument 1: cannot convert from 'int' to 'infer'
                    //     public virtual int M(infer x = new D().M(1)) => 0;
                    Diagnostic(ErrorCode.ERR_BadArgType, "1").WithArguments("1", "int", "infer").WithLocation(3, 46),
                    // (4,36): error CS1736: Default parameter value for 'x' must be a compile-time constant
                    //     public virtual int N(infer x = D.K()) => 0;
                    Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "D.K()").WithArguments("x").WithLocation(4, 36),
                    // (10,25): error CS20045: Cannot infer the types of 'M' from the member it overrides or implements, because the signature of that member depends on 'M' itself. Write the types.
                    //     public override int M(infer x) => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferOverriddenMemberCycle, "M").WithArguments("M").WithLocation(10, 25));
                Assert.Equal("System.Int32", comp.GetMember<MethodSymbol>("D.N").Parameters[0].Type.ToTestDisplayString());
            }
        }

        [Fact]
        public void InferOverridden_Cycle_Concurrent()
        {
            // Members in a cycle are searched for outside of their locks, so threads that start from either end neither
            // deadlock nor disagree.
            var source = """
                class B
                {
                    public virtual int M(infer x = new D().M(1)) => 0;
                }

                class D : B
                {
                    public override int M(infer x) => 0;
                }
                """;
            // A thread that completes B.M's signature from the published error of its default value must not then give
            // D.M a copy of that error type without D.M's own error: the results of a cycle are decided before any is
            // published.
            for (int i = 0; i < 100; i++)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                var methods = new[] { comp.GetMember<MethodSymbol>("B.M"), comp.GetMember<MethodSymbol>("D.M") };
                System.Threading.Tasks.Parallel.For(0, 8, t => Assert.Equal("infer", methods[t % 2].Parameters[0].Type.ToTestDisplayString()));
                Assert.Equal(1, comp.GetDiagnostics().Count(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterCycle));
                Assert.Equal(1, comp.GetDiagnostics().Count(d => d.Code == (int)ErrorCode.ERR_SharperInferOverriddenMemberCycle));
            }
        }

        [Fact]
        public void InferOverridden_Records()
        {
            // Overrides of the members a record synthesizes, and of object's members, copy like any other.
            var source = """
                using System;

                record Base(int X);

                record Derived(int X, int Y) : Base(X)
                {
                    protected override infer PrintMembers(infer builder)
                    {
                        builder.Append("custom");
                        return true;
                    }

                    public override infer GetHashCode() => 1;
                }

                class Point(int x)
                {
                    public override infer ToString() => "P" + x;
                    public override infer Equals(infer obj) => obj is Point;
                    public override int GetHashCode() => x;
                }

                class Program
                {
                    static void Main() => Console.Write(new Derived(1, 2) + ";" + new Point(3) + new Point(4).Equals(null));
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Derived { custom };P3False" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            Assert.Equal("System.Boolean Derived.PrintMembers(System.Text.StringBuilder builder)", comp.GetMember<MethodSymbol>("Derived.PrintMembers").ToTestDisplayString());
            Assert.Equal("System.Boolean Point.Equals(System.Object? obj)", comp.GetMember<MethodSymbol>("Point.Equals").ToTestDisplayString());
        }

        [Fact]
        public void InferOverridden_Nullable()
        {
            // The copied annotations are checked like written ones.
            var source = """
                #nullable enable
                abstract class B
                {
                    public abstract string? Find(string? key);
                }

                class C : B
                {
                    public override infer Find(infer key) => key.Length.ToString();
                    static int Use(C c) => c.Find(null).Length;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (9,46): warning CS8602: Dereference of a possibly null reference.
                //     public override infer Find(infer key) => key.Length.ToString();
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "key").WithLocation(9, 46),
                // (10,28): warning CS8602: Dereference of a possibly null reference.
                //     static int Use(C c) => c.Find(null).Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "c.Find(null)").WithLocation(10, 28));
        }

        [Fact]
        public void InferOverridden_SemanticModel()
        {
            var source = """
                #nullable enable
                interface I
                {
                    string? M(int a);
                }

                class C : I
                {
                    public infer M(infer a) => null;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Body is null && m.ExpressionBody is not null);

            var returnInfo = model.GetTypeInfo(method.ReturnType);
            Assert.Equal("System.String?", returnInfo.Type.ToTestDisplayString(includeNonNullable: true));
            Assert.Equal(CodeAnalysis.NullableAnnotation.Annotated, returnInfo.Nullability.Annotation);
            Assert.True(comp.GetMember<MethodSymbol>("C.M").ReturnType.GetPublicSymbol().Equals(model.GetSymbolInfo(method.ReturnType).Symbol, SymbolEqualityComparer.Default));

            var parameter = method.ParameterList.Parameters[0];
            Assert.Equal("System.Int32", model.GetTypeInfo(parameter.Type!).Type.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetSymbolInfo(parameter.Type!).Symbol.ToTestDisplayString());
            Assert.Equal("System.String? C.M(System.Int32 a)", model.GetDeclaredSymbol(method).ToTestDisplayString(includeNonNullable: true));
        }

        [Fact]
        public void InferOverridden_IOperation()
        {
            var source = """
                abstract class B
                {
                    public abstract long M(int a);
                }

                class C : B
                {
                    public override infer M(infer a) /*<bind>*/=> a + 1/*</bind>*/;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedOperationTree = """
                IBlockOperation (1 statements) (OperationKind.Block, Type: null) (Syntax: '=> a + 1')
                  IReturnOperation (OperationKind.Return, Type: null, IsImplicit) (Syntax: 'a + 1')
                    ReturnedValue:
                      IConversionOperation (TryCast: False, Unchecked) (OperationKind.Conversion, Type: System.Int64, IsImplicit) (Syntax: 'a + 1')
                        Conversion: CommonConversion (Exists: True, IsIdentity: False, IsNumeric: True, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                        Operand:
                          IBinaryOperation (BinaryOperatorKind.Add) (OperationKind.Binary, Type: System.Int32) (Syntax: 'a + 1')
                            Left:
                              IParameterReferenceOperation: a (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'a')
                            Right:
                              ILiteralOperation (OperationKind.Literal, Type: System.Int32, Constant: 1) (Syntax: '1')
                """;
            VerifyOperationTreeAndDiagnosticsForTest<ArrowExpressionClauseSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void InferOverridden_Metadata()
        {
            // The emitted members have the copied types, and override and implement as their C# twins do.
            var source = """
                public interface I
                {
                    string Name(int id);
                }

                public abstract class B
                {
                    public abstract long Twice(long x);
                }

                public class C : B, I
                {
                    public infer Name(infer id) => "n" + id;
                    public override infer Twice(infer x) => x * 2;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            CompileAndVerify(comp, symbolValidator: module =>
            {
                Assert.Equal("System.String C.Name(System.Int32 id)", module.GlobalNamespace.GetMember<MethodSymbol>("C.Name").ToTestDisplayString());
                Assert.Equal("System.Int64 C.Twice(System.Int64 x)", module.GlobalNamespace.GetMember<MethodSymbol>("C.Twice").ToTestDisplayString());
            }).VerifyDiagnostics();

            var csharp = """
                class D
                {
                    static void Main()
                    {
                        I i = new C();
                        B b = new C();
                        System.Console.Write(i.Name(1) + b.Twice(2) + new C().Name(3));
                    }
                }
                """;
            CompileAndVerify(csharp, references: [comp.EmitToImageReference()], targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "n14n3" : null).VerifyDiagnostics();
        }

        [Fact]
        public void InferOverridden_NotApplicable()
        {
            // Without an override or an implementation, 'infer' keeps its other sources and errors: a parameter needs a
            // default value, a return type a private body. A type named 'infer' wins, as elsewhere.
            var source = """
                interface I { int M(int a); }

                class C : I
                {
                    public int M(int a) => a;
                    private infer M(infer a, int b) => a;
                    internal void N(infer a) { }
                    public infer Q() => 1;
                }

                namespace N
                {
                    class infer { }
                    abstract class B { public abstract infer M(infer a); }
                    class D : B { public override infer M(infer a) => a; }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (6,21): error CS20027: Cannot infer the type of parameter 'a', because it has no default value. Write the parameter type.
                //     private infer M(infer a, int b) => a;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("a").WithLocation(6, 21),
                // (7,21): error CS20027: Cannot infer the type of parameter 'a', because it has no default value. Write the parameter type.
                //     internal void N(infer a) { }
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("a").WithLocation(7, 21),
                // (8,12): error CS20015: An inferred return type ('infer') is not supported on a member that is not private yet. Write the return type.
                //     public infer Q() => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a member that is not private").WithLocation(8, 12),
                // (13,11): warning CS8981: The type name 'infer' only contains lower-cased ascii characters. Such names may become reserved for the language.
                //     class infer { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "infer").WithArguments("infer").WithLocation(13, 11));
        }

        [Fact]
        public void InferOverridden_Concurrent()
        {
            // The members are found outside of the method locks; threads that race get the same types.
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("class C0 { public virtual int M(int a) => a; }");
            for (int i = 1; i < 30; i++)
            {
                builder.AppendLine($"class C{i} : C{i - 1} {{ public override infer M(infer a) => a + {i}; }}");
            }

            var comp = CreateCompilation(builder.ToString(), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var methods = Enumerable.Range(0, 30).Select(i => comp.GetMember<MethodSymbol>($"C{i}.M")).Reverse().ToArray();
            System.Threading.Tasks.Parallel.For(0, 8, t =>
            {
                foreach (var method in t % 2 == 0 ? methods : methods.Reverse())
                {
                    Assert.Equal("System.Int32 " + method.ContainingType.Name + ".M(System.Int32 a)", method.ToTestDisplayString());
                }
            });
            comp.VerifyDiagnostics();
        }
    }
}
