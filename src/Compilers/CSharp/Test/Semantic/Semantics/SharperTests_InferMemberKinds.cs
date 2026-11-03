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
    /// Sharper's inferred types, step P2 for every member kind (issue #166): properties, indexers, operators, conversion
    /// operators and events of overrides and implementations copy their <c>infer</c> types from the member they override
    /// or implement, as methods do.
    /// </summary>
    public sealed partial class SharperTests
    {
        private static void VerifySameType(CSharpCompilation comp, string member, string twin)
        {
            var symbol = comp.GetMember(member);
            var twinSymbol = comp.GetMember(twin);
            Assert.Equal(twinSymbol.GetTypeOrReturnType().ToTestDisplayString(), symbol.GetTypeOrReturnType().ToTestDisplayString());
            var parameters = symbol.GetParameters();
            var twinParameters = twinSymbol.GetParameters();
            Assert.Equal(twinParameters.Length, parameters.Length);
            for (int i = 0; i < parameters.Length; i++)
            {
                Assert.Equal(twinParameters[i].TypeWithAnnotations.ToTestDisplayString(), parameters[i].TypeWithAnnotations.ToTestDisplayString());
                Assert.Equal(twinParameters[i].RefKind, parameters[i].RefKind);
                Assert.Equal(twinParameters[i].IsParams, parameters[i].IsParams);
            }
        }

        [Fact]
        public void InferMemberKinds_Basic()
        {
            // The issue's examples, each next to its C# twin, which writes the types out.
            var source = """
                using System;

                abstract class B
                {
                    public abstract string Name { get; }
                    public abstract int this[string key] { get; }
                    public virtual event EventHandler Changed;
                    protected void Raise() => Changed?.Invoke(this, EventArgs.Empty);
                }

                class C : B
                {
                    public override infer Name => "c";
                    public override infer this[infer key] => key.Length;
                    public override event infer Changed;
                    public void RaiseOwn() => Changed?.Invoke(this, EventArgs.Empty);
                }

                class CSharp : B
                {
                    public override string Name => "c";
                    public override int this[string key] => key.Length;
                    public override event EventHandler Changed;
                    public void RaiseOwn() => Changed?.Invoke(this, EventArgs.Empty);
                }

                interface IAdd<T> where T : IAdd<T> { static abstract T operator +(T a, T b); }

                struct S : IAdd<S>
                {
                    public int V;
                    public static infer operator +(infer a, infer b) => new S { V = a.V + b.V };
                }

                struct SCSharp : IAdd<SCSharp>
                {
                    public int V;
                    public static SCSharp operator +(SCSharp a, SCSharp b) => new SCSharp { V = a.V + b.V };
                }

                class Program
                {
                    static T Sum<T>(T a, T b) where T : IAdd<T> => a + b;

                    static void Main()
                    {
                        var c = new C();
                        c.Changed += (_, _) => Console.Write("raised;");
                        c.RaiseOwn();
                        var csharp = new CSharp();
                        csharp.Changed += (_, _) => Console.Write("raised;");
                        csharp.RaiseOwn();
                        B b = c;
                        Console.Write(b.Name + b["ab"] + c.Name + c["abc"] + ";");
                        Console.Write(Sum(new S { V = 1 }, new S { V = 2 }).V + ";" + Sum(new SCSharp { V = 1 }, new SCSharp { V = 2 }).V);
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "raised;raised;c2c3;3;3" : null,
                verify: Verification.Skipped);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameType(comp, "C.Name", "CSharp.Name");
            VerifySameType(comp, "C.this[]", "CSharp.this[]");
            VerifySameType(comp, "C.Changed", "CSharp.Changed");
            Assert.Equal("S S.op_Addition(S a, S b)", comp.GetMember("S.op_Addition").ToTestDisplayString());
            Assert.Equal("System.Int32 C.this[System.String key].get", comp.GetMember<PropertySymbol>("C.this[]").GetMethod.ToTestDisplayString());
            Assert.Equal("void C.Changed.add", comp.GetMember<EventSymbol>("C.Changed").AddMethod.ToTestDisplayString());
            Assert.Equal("System.EventHandler", comp.GetMember<EventSymbol>("C.Changed").AddMethod.Parameters[0].Type.ToTestDisplayString());
            Assert.Same(comp.GetMember("B.Name"), comp.GetMember<PropertySymbol>("C.Name").OverriddenProperty);
            Assert.Same(comp.GetMember("B.Changed"), comp.GetMember<EventSymbol>("C.Changed").OverriddenEvent);
        }

        [Fact]
        public void InferMemberKinds_Accessors()
        {
            // Accessors follow the copied type: get, set, init, add and remove; expression and block bodies; auto-properties
            // and explicit implementations.
            var source = """
                using System;

                interface IThing
                {
                    string Name { get; init; }
                    long Size { get; set; }
                    int this[int i, string s] { get; set; }
                    event Action<string> Renamed;
                }

                class Thing : IThing
                {
                    private Action<string> _renamed;
                    private long _size;
                    public infer Name { get; init; }
                    public infer Size
                    {
                        get { return _size; }
                        set { _size = value * 2; }
                    }
                    infer IThing.this[infer i, infer s]
                    {
                        get => i + s.Length;
                        set => _size = value;
                    }
                    public event infer Renamed
                    {
                        add { _renamed += value; }
                        remove => _renamed -= value;
                    }
                    public void Rename(string name) => _renamed?.Invoke(name);
                }

                class ThingCSharp : IThing
                {
                    private Action<string> _renamed;
                    private long _size;
                    public string Name { get; init; }
                    public long Size
                    {
                        get { return _size; }
                        set { _size = value * 2; }
                    }
                    int IThing.this[int i, string s]
                    {
                        get => i + s.Length;
                        set => _size = value;
                    }
                    public event Action<string> Renamed
                    {
                        add { _renamed += value; }
                        remove => _renamed -= value;
                    }
                    public void Rename(string name) => _renamed?.Invoke(name);
                }

                class Program
                {
                    static void Main()
                    {
                        var thing = new Thing { Name = "a", Size = 2 };
                        thing.Renamed += n => Console.Write(n + ";");
                        thing.Rename(thing.Name + thing.Size);
                        IThing i = thing;
                        Console.Write(i[1, "ab"] + ";");
                        i[0, ""] = 5;
                        Console.Write(i.Size + ";");
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "a4;3;5;" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameType(comp, "Thing.Name", "ThingCSharp.Name");
            VerifySameType(comp, "Thing.Size", "ThingCSharp.Size");
            VerifySameType(comp, "Thing.IThing.this[]", "ThingCSharp.IThing.this[]");
            VerifySameType(comp, "Thing.Renamed", "ThingCSharp.Renamed");
            Assert.Equal("void modreq(System.Runtime.CompilerServices.IsExternalInit) Thing.Name.init", comp.GetMember<PropertySymbol>("Thing.Name").SetMethod.ToTestDisplayString());
            Assert.Equal("System.Int64 value", comp.GetMember<PropertySymbol>("Thing.Size").SetMethod.Parameters[0].ToTestDisplayString());
            Assert.Equal("System.String Thing.<Name>k__BackingField", comp.GetMember<FieldSymbol>("Thing.<Name>k__BackingField").ToTestDisplayString());
            Assert.Equal("System.Action<System.String> value", comp.GetMember<EventSymbol>("Thing.Renamed").RemoveMethod.Parameters[0].ToTestDisplayString());
        }

        [Fact]
        public void InferMemberKinds_Operators()
        {
            // Operators that implement static abstract (or virtual) interface operators, implicitly or explicitly, copy
            // from them: binary, unary, checked and conversion operators. The written types choose among overloads.
            var source = """
                using System;

                interface INum<T> where T : INum<T>
                {
                    static abstract T operator +(T a, T b);
                    static abstract T operator +(T a, int b);
                    static abstract T operator -(T a);
                    static virtual T operator checked -(T a) => -a;
                    static abstract bool operator ==(T a, T b);
                    static abstract bool operator !=(T a, T b);
                    static abstract explicit operator int(T a);
                }

                readonly struct N(int v) : INum<N>
                {
                    public int V => v;
                    public static infer operator +(infer a, N b) => new N(a.V + b.V);
                    public static infer operator +(infer a, int b) => new N(a.V + b * 10);
                    public static infer operator -(infer a) => new N(-a.V);
                    public static infer operator checked -(infer a) => new N(checked(-a.V));
                    public static infer operator ==(infer a, infer b) => a.V == b.V;
                    public static infer operator !=(infer a, infer b) => a.V != b.V;
                    static explicit INum<N>.operator infer(infer a) => a.V;
                    public override bool Equals(object o) => o is N n && n == this;
                    public override int GetHashCode() => v;
                }

                class Program
                {
                    static int Use<T>(T a, T b) where T : INum<T> => (int)(-(a + b + 1)) + (a == b ? 100 : 0);

                    static void Main()
                    {
                        Console.Write(Use(new N(1), new N(2)) + ";" + Use(new N(1), new N(1)));
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "-13;88" : null,
                verify: Verification.Skipped);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            var additions = comp.GetMembers("N.op_Addition").Select(m => m.ToTestDisplayString()).ToArray();
            Assert.Equal(["N N.op_Addition(N a, N b)", "N N.op_Addition(N a, System.Int32 b)"], additions);
            Assert.Equal("N N.op_UnaryNegation(N a)", comp.GetMember("N.op_UnaryNegation").ToTestDisplayString());
            Assert.Equal("N N.op_CheckedUnaryNegation(N a)", comp.GetMember("N.op_CheckedUnaryNegation").ToTestDisplayString());
            Assert.Equal("System.Boolean N.op_Equality(N a, N b)", comp.GetMember("N.op_Equality").ToTestDisplayString());
            Assert.Equal("System.Int32 N.INum<N>.op_Explicit(N a)", comp.GetMember("N.INum<N>.op_Explicit").ToTestDisplayString());
        }

        [Fact]
        public void InferMemberKinds_WrittenTypesNarrow()
        {
            // As for methods: candidates of the member's kind, name and parameter count, narrowed by the written types,
            // the types of 'infer' parameters with a default value, and the written ref kinds.
            var source = """
                abstract class B
                {
                    public abstract string this[int i] { get; }
                    public abstract long this[string s] { get; }
                    public abstract double this[int i, int j] { get; }
                    public abstract char this[in int i, string s, int k] { get; }
                    public abstract bool this[int i, string s, int k] { get; }
                }

                class C : B
                {
                    public override infer this[int i] => "";
                    public override long this[infer s] => s.Length;
                    public override infer this[infer i, infer j = 1] => i + j;
                    public override infer this[in infer i, infer s, infer k] => 'c';
                    public override infer this[int i, infer s, int k] => true;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            Assert.Equal(
                [
                    "System.String C.this[System.Int32 i] { get; }",
                    "System.Int64 C.this[System.String s] { get; }",
                    "System.Double C.this[System.Int32 i, [System.Int32 j = 1]] { get; }",
                    "System.Char C.this[in modreq(System.Runtime.InteropServices.InAttribute) System.Int32 i, System.String s, System.Int32 k] { get; }",
                    "System.Boolean C.this[System.Int32 i, System.String s, System.Int32 k] { get; }",
                ],
                comp.GetMembers("C.this[]").Select(m => m.ToTestDisplayString()));
        }

        [Fact]
        public void InferMemberKinds_Errors()
        {
            // No candidate is CS20043 and several are CS20044, as for methods; an 'infer' the error leaves without a type
            // is an error type, which reports nothing more.
            var source = """
                using System;

                abstract class B
                {
                    public abstract int this[int i] { get; }
                    public abstract int this[string s] { get; }
                    public string NotVirtual { get; }
                    public virtual event Action Changed;
                }

                interface IOp<T> where T : IOp<T>
                {
                    static abstract T operator +(T a, int b);
                    static abstract T operator +(T a, long b);
                }

                interface I1 { int P { get; } }
                interface I2 { long P { get; } }

                class C : B, I1, I2
                {
                    public override infer this[infer i] => 0;
                    public override infer this[infer a, infer b] => 0;
                    public override infer NotVirtual => "";
                    public override event infer Changed2;
                    public infer P => 0;
                }

                struct S : IOp<S>
                {
                    public static infer operator +(S a, infer b) => a;
                    public static infer operator -(S a, infer b) => a;
                    public static S operator +(S a, int b) => a;
                    public static S operator +(S a, long b) => a;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (8,33): warning CS0067: The event 'B.Changed' is never used
                //     public virtual event Action Changed;
                Diagnostic(ErrorCode.WRN_UnreferencedEvent, "Changed").WithArguments("B.Changed").WithLocation(8, 33),
                // (20,7): error CS0534: 'C' does not implement inherited abstract member 'B.this[string].get'
                // class C : B, I1, I2
                Diagnostic(ErrorCode.ERR_UnimplementedAbstractMethod, "C").WithArguments("C", "B.this[string].get").WithLocation(20, 7),
                // (20,7): error CS0534: 'C' does not implement inherited abstract member 'B.this[int].get'
                // class C : B, I1, I2
                Diagnostic(ErrorCode.ERR_UnimplementedAbstractMethod, "C").WithArguments("C", "B.this[int].get").WithLocation(20, 7),
                // (20,14): error CS0738: 'C' does not implement interface member 'I1.P'. 'C.P' cannot implement 'I1.P' because it does not have the matching return type of 'int'.
                // class C : B, I1, I2
                Diagnostic(ErrorCode.ERR_CloseUnimplementedInterfaceMemberWrongReturnType, "I1").WithArguments("C", "I1.P", "C.P", "int").WithLocation(20, 14),
                // (20,18): error CS0738: 'C' does not implement interface member 'I2.P'. 'C.P' cannot implement 'I2.P' because it does not have the matching return type of 'long'.
                // class C : B, I1, I2
                Diagnostic(ErrorCode.ERR_CloseUnimplementedInterfaceMemberWrongReturnType, "I2").WithArguments("C", "I2.P", "C.P", "long").WithLocation(20, 18),
                // (22,27): error CS20044: Cannot infer the types of 'this', because it could override or implement more than one member: 'B.this[int]', 'B.this[string]'. Write the types of the one it overrides or implements.
                //     public override infer this[infer i] => 0;
                Diagnostic(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, "this").WithArguments("this", "'B.this[int]', 'B.this[string]'").WithLocation(22, 27),
                // (23,27): error CS20043: Cannot infer the types of 'this' from the member it overrides or implements, because it could override or implement no member 'this' with the same number of parameters. Write the types.
                //     public override infer this[infer a, infer b] => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "this").WithArguments("this", "it could override or implement no member 'this' with the same number of parameters").WithLocation(23, 27),
                // (24,27): error CS20043: Cannot infer the types of 'NotVirtual' from the member it overrides or implements, because it could override or implement no member 'NotVirtual' with the same number of parameters. Write the types.
                //     public override infer NotVirtual => "";
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "NotVirtual").WithArguments("NotVirtual", "it could override or implement no member 'NotVirtual' with the same number of parameters").WithLocation(24, 27),
                // (24,27): error CS0506: 'C.NotVirtual': cannot override inherited member 'B.NotVirtual' because it is not marked virtual, abstract, or override
                //     public override infer NotVirtual => "";
                Diagnostic(ErrorCode.ERR_CantOverrideNonVirtual, "NotVirtual").WithArguments("C.NotVirtual", "B.NotVirtual").WithLocation(24, 27),
                // (25,33): error CS20043: Cannot infer the types of 'Changed2' from the member it overrides or implements, because it could override or implement no member 'Changed2' with the same number of parameters. Write the types.
                //     public override event infer Changed2;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "Changed2").WithArguments("Changed2", "it could override or implement no member 'Changed2' with the same number of parameters").WithLocation(25, 33),
                // (25,33): error CS0115: 'C.Changed2': no suitable method found to override
                //     public override event infer Changed2;
                Diagnostic(ErrorCode.ERR_OverrideNotExpected, "Changed2").WithArguments("C.Changed2").WithLocation(25, 33),
                // (25,33): warning CS0067: The event 'C.Changed2' is never used
                //     public override event infer Changed2;
                Diagnostic(ErrorCode.WRN_UnreferencedEvent, "Changed2").WithArguments("C.Changed2").WithLocation(25, 33),
                // (26,18): error CS20044: Cannot infer the types of 'P', because it could override or implement more than one member: 'I1.P', 'I2.P'. Write the types of the one it overrides or implements.
                //     public infer P => 0;
                Diagnostic(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, "P").WithArguments("P", "'I1.P', 'I2.P'").WithLocation(26, 18),
                // (31,34): error CS20044: Cannot infer the types of 'operator +', because it could override or implement more than one member: 'IOp<S>.operator +(S, int)', 'IOp<S>.operator +(S, long)'. Write the types of the one it overrides or implements.
                //     public static infer operator +(S a, infer b) => a;
                Diagnostic(ErrorCode.ERR_SharperInferAmbiguousOverriddenMember, "+").WithArguments("operator +", "'IOp<S>.operator +(S, int)', 'IOp<S>.operator +(S, long)'").WithLocation(31, 34),
                // (32,41): error CS20027: Cannot infer the type of parameter 'b', because it has no default value. Write the parameter type.
                //     public static infer operator -(S a, infer b) => a;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("b").WithLocation(32, 41));
        }

        [Fact]
        public void InferMemberKinds_Errors_RuledOut()
        {
            // CS20043 names the members the written types rule out, as for methods: by a parameter, or by the type.
            var source = """
                abstract class B
                {
                    public abstract int this[long i, string s] { get; }
                }

                interface I { string this[int i] { get; } }

                class C : B, I
                {
                    public override infer this[int i, infer s] => 0;
                    public long this[infer i] => 0;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (8,7): error CS0534: 'C' does not implement inherited abstract member 'B.this[long, string].get'
                // class C : B, I
                Diagnostic(ErrorCode.ERR_UnimplementedAbstractMethod, "C").WithArguments("C", "B.this[long, string].get").WithLocation(8, 7),
                // (8,14): error CS0535: 'C' does not implement interface member 'I.this[int]'
                // class C : B, I
                Diagnostic(ErrorCode.ERR_UnimplementedInterfaceMember, "I").WithArguments("C", "I.this[int]").WithLocation(8, 14),
                // (10,27): error CS20043: Cannot infer the types of 'this' from the member it overrides or implements, because its written types rule out every member 'this' with the same number of parameters: 'B.this[long, string]' (it takes 'long' for 'i'). Write the types.
                //     public override infer this[int i, infer s] => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "this").WithArguments("this", "its written types rule out every member 'this' with the same number of parameters: 'B.this[long, string]' (it takes 'long' for 'i')").WithLocation(10, 27),
                // (11,17): error CS20043: Cannot infer the types of 'this' from the member it overrides or implements, because its written types rule out every member 'this' with the same number of parameters: 'I.this[int]' (it returns 'string'). Write the types.
                //     public long this[infer i] => 0;
                Diagnostic(ErrorCode.ERR_SharperInferNoOverriddenMember, "this").WithArguments("this", "its written types rule out every member 'this' with the same number of parameters: 'I.this[int]' (it returns 'string')").WithLocation(11, 17));
        }

        [Fact]
        public void InferMemberKinds_NotApplicable()
        {
            // A property, an indexer or an event that is neither an override nor an implementation has no member to copy
            // from: a property and an indexer keep R4's error (CS20015), an event reports CS20054. So does a member that
            // is not public, and a public one whose type's interfaces have no member of its kind, name and parameter count.
            var source = """
                using System;

                interface I
                {
                    int M();
                    event Action E;
                }

                class C : I
                {
                    public infer P => 1;
                    private infer Q { get; set; }
                    public infer this[int i] => i;
                    public event infer X;
                    private event infer Y { add { } remove { } }
                    public infer M { get; }
                    int I.M() => 0;
                    event Action I.E { add { } remove { } }
                }

                class D
                {
                    public static infer operator +(D a, D b) => a;
                    public static infer operator -(infer a, D b) => b;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (11,12): error CS20015: An inferred return type ('infer') is not supported on a property or indexer yet. Write the return type.
                //     public infer P => 1;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a property or indexer").WithLocation(11, 12),
                // (12,13): error CS20015: An inferred return type ('infer') is not supported on a property or indexer yet. Write the return type.
                //     private infer Q { get; set; }
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a property or indexer").WithLocation(12, 13),
                // (13,12): error CS20015: An inferred return type ('infer') is not supported on a property or indexer yet. Write the return type.
                //     public infer this[int i] => i;
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a property or indexer").WithLocation(13, 12),
                // (14,18): error CS20054: Cannot infer the type of event 'C.X', because it overrides or implements no event. Write the type.
                //     public event infer X;
                Diagnostic(ErrorCode.ERR_SharperInferEventNoOverriddenMember, "infer").WithArguments("C.X").WithLocation(14, 18),
                // (14,24): warning CS0067: The event 'C.X' is never used
                //     public event infer X;
                Diagnostic(ErrorCode.WRN_UnreferencedEvent, "X").WithArguments("C.X").WithLocation(14, 24),
                // (15,19): error CS20054: Cannot infer the type of event 'C.Y', because it overrides or implements no event. Write the type.
                //     private event infer Y { add { } remove { } }
                Diagnostic(ErrorCode.ERR_SharperInferEventNoOverriddenMember, "infer").WithArguments("C.Y").WithLocation(15, 19),
                // (16,12): error CS20015: An inferred return type ('infer') is not supported on a property or indexer yet. Write the return type.
                //     public infer M { get; }
                Diagnostic(ErrorCode.ERR_SharperInferNotSupportedYet, "infer").WithArguments("on a property or indexer").WithLocation(16, 12),
                // (24,36): error CS20027: Cannot infer the type of parameter 'a', because it has no default value. Write the parameter type.
                //     public static infer operator -(infer a, D b) => b;
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("a").WithLocation(24, 36));

            // With the flag off, 'infer' is an ordinary type name.
            var errors = CreateCompilation(source, targetFramework: TargetFramework.Net90).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.Equal(9, errors.Length);
            Assert.All(errors, d => Assert.Equal((int)ErrorCode.ERR_SingleTypeNameNotFound, d.Code));
        }

        [Fact]
        public void InferMemberKinds_GenericsAndNullable()
        {
            // The types are copied as written in the member: nullable annotations, tuple element names and the type
            // arguments of a generic base.
            var source = """
                #nullable enable
                using System;

                abstract class B<T>
                {
                    public abstract T Value { get; set; }
                    public abstract (T First, string? Second) this[T key] { get; }
                    public abstract event Action<T?>? Changed;
                }

                class C : B<string>
                {
                    public override infer Value { get; set; } = "";
                    public override infer this[infer key] => (key, null);
                    public override event infer Changed;
                    void M()
                    {
                        Value = null;
                        Changed?.Invoke(null);
                        string s = this[""].Second;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (18,17): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         Value = null;
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(18, 17),
                // (20,20): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         string s = this[""].Second;
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, @"this[""""].Second").WithLocation(20, 20));
            Assert.Equal("System.String! C.Value { get; set; }", comp.GetMember<PropertySymbol>("C.Value").ToTestDisplayString(includeNonNullable: true));
            Assert.Equal("(System.String! First, System.String? Second) C.this[System.String! key] { get; }", comp.GetMember<PropertySymbol>("C.this[]").ToTestDisplayString(includeNonNullable: true));
            Assert.Equal("event System.Action<System.String?>? C.Changed", comp.GetMember<EventSymbol>("C.Changed").ToTestDisplayString(includeNonNullable: true));
        }

        [Fact]
        public void InferMemberKinds_SemanticModel()
        {
            // GetTypeInfo and GetSymbolInfo on 'infer' give the copied type, as for 'var'.
            var source = """
                #nullable enable
                using System;

                interface I<T> where T : I<T>
                {
                    string? Name { get; }
                    int this[long key] { get; }
                    event Action? Changed;
                    static abstract T operator +(T a, T b);
                }

                class C : I<C>
                {
                    public infer Name => null;
                    public infer this[infer key] => 0;
                    public event infer Changed;
                    public static infer operator +(infer a, C b) => a;
                    void Raise() => Changed?.Invoke();
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var type = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

            var property = type.Members.OfType<PropertyDeclarationSyntax>().Single();
            var propertyInfo = model.GetTypeInfo(property.Type);
            Assert.Equal("System.String?", propertyInfo.Type.ToTestDisplayString(includeNonNullable: true));
            Assert.Equal(CodeAnalysis.NullableAnnotation.Annotated, propertyInfo.Nullability.Annotation);
            Assert.Equal("System.String", model.GetSymbolInfo(property.Type).Symbol.ToTestDisplayString());

            var indexer = type.Members.OfType<IndexerDeclarationSyntax>().Single();
            Assert.Equal("System.Int32", model.GetTypeInfo(indexer.Type).Type.ToTestDisplayString());
            Assert.Equal("System.Int64", model.GetTypeInfo(indexer.ParameterList.Parameters[0].Type!).Type.ToTestDisplayString());
            Assert.Equal("System.Int32 C.this[System.Int64 key] { get; }", model.GetDeclaredSymbol(indexer).ToTestDisplayString());

            var @event = type.Members.OfType<EventFieldDeclarationSyntax>().Single();
            Assert.Equal("System.Action?", model.GetTypeInfo(@event.Declaration.Type).Type.ToTestDisplayString());
            Assert.Equal(CodeAnalysis.NullableAnnotation.Annotated, model.GetTypeInfo(@event.Declaration.Type).Nullability.Annotation);
            Assert.Equal("System.Action", model.GetSymbolInfo(@event.Declaration.Type).Symbol.ToTestDisplayString());

            var @operator = type.Members.OfType<OperatorDeclarationSyntax>().Single();
            Assert.Equal("C", model.GetTypeInfo(@operator.ReturnType).Type.ToTestDisplayString());
            Assert.Equal("C", model.GetTypeInfo(@operator.ParameterList.Parameters[0].Type!).Type.ToTestDisplayString());
            Assert.Equal("C C.op_Addition(C a, C b)", model.GetDeclaredSymbol(@operator).ToTestDisplayString());
        }

        [Fact]
        public void InferMemberKinds_Metadata()
        {
            // The emitted members have the copied types, and override and implement as their C# twins do.
            var source = """
                using System;

                public interface I
                {
                    string Name { get; }
                    event Action<int> Changed;
                }

                public abstract class B
                {
                    public abstract long this[int i] { get; set; }
                }

                public class C : B, I
                {
                    private long _value;
                    public infer Name => "n";
                    public override infer this[infer i] { get => _value + i; set => _value = value; }
                    public event infer Changed { add => value(1); remove { } }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            CompileAndVerify(comp, symbolValidator: module =>
            {
                Assert.Equal("System.String C.Name { get; }", module.GlobalNamespace.GetMember<PropertySymbol>("C.Name").ToTestDisplayString());
                Assert.Equal("System.Int64 C.this[System.Int32 i] { get; set; }", module.GlobalNamespace.GetMember<PropertySymbol>("C.this[]").ToTestDisplayString());
                Assert.Equal("event System.Action<System.Int32> C.Changed", module.GlobalNamespace.GetMember<EventSymbol>("C.Changed").ToTestDisplayString());
            }).VerifyDiagnostics();

            var csharp = """
                class D
                {
                    static void Main()
                    {
                        I i = new C();
                        B b = new C();
                        b[0] = 2;
                        i.Changed += n => System.Console.Write(n);
                        System.Console.Write(i.Name + b[1] + new C().Name);
                    }
                }
                """;
            CompileAndVerify(csharp, references: [comp.EmitToImageReference()], targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1n3n" : null).VerifyDiagnostics();
        }

        [Fact]
        public void InferMemberKinds_Cycle()
        {
            // An indexer whose base's signature depends on it, through a default value, is a cycle, from either end.
            var source = """
                class B
                {
                    public virtual int this[int i, infer j = new D()[1, 2]] => 0;
                }

                class D : B
                {
                    public override int this[infer i, infer j] => 0;
                }
                """;
            foreach (var first in new[] { "B.this[]", "D.this[]" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                _ = comp.GetMember<PropertySymbol>(first).Parameters;
                comp.VerifyDiagnostics(
                    // (3,36): error CS20028: Cannot infer the type of parameter 'j', because its default value depends on the type of 'j' itself. Write the parameter type.
                    //     public virtual int this[int i, infer j = new D()[1, 2]] => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferParameterCycle, "infer").WithArguments("j").WithLocation(3, 36),
                    // (8,25): error CS20045: Cannot infer the types of 'this' from the member it overrides or implements, because the signature of that member depends on 'this' itself. Write the types.
                    //     public override int this[infer i, infer j] => 0;
                    Diagnostic(ErrorCode.ERR_SharperInferOverriddenMemberCycle, "this").WithArguments("this").WithLocation(8, 25));
            }
        }

        [Fact]
        public void InferMemberKinds_Concurrent()
        {
            // The members are found outside of the signature locks; threads that race get the same types.
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("class C0 { public virtual int P => 0; public virtual int this[long i] => 0; public virtual event System.Action E; }");
            for (int i = 1; i < 30; i++)
            {
                builder.AppendLine($"class C{i} : C{i - 1} {{ public override infer P => {i}; public override infer this[infer i] => {i}; public override event infer E; }}");
            }

            var comp = CreateCompilation(builder.ToString(), parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var types = Enumerable.Range(0, 30).Select(i => comp.GetMember<NamedTypeSymbol>($"C{i}")).Reverse().ToArray();
            System.Threading.Tasks.Parallel.For(0, 8, t =>
            {
                foreach (var type in t % 2 == 0 ? types : types.Reverse())
                {
                    Assert.Equal("System.Int32 " + type.Name + ".P { get; }", type.GetMember<PropertySymbol>("P").ToTestDisplayString());
                    Assert.Equal("System.Int32 " + type.Name + ".this[System.Int64 i] { get; }", type.GetMember<PropertySymbol>("this[]").ToTestDisplayString());
                    Assert.Equal("event System.Action " + type.Name + ".E", type.GetMember<EventSymbol>("E").ToTestDisplayString());
                }
            });
            Assert.Empty(comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }
    }
}
