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
    /// Sharper's inferred parameter types, step P5 (issue #76): an <c>infer</c> parameter passed to an overloaded or
    /// generic call is pinned when every candidate that is applicable with the placeholder in place takes the same type
    /// at that position.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void InferFromOverloads_Basic()
        {
            // The issue's examples and every kind of call with several candidates, next to the C# twin.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                record Point
                {
                    public Point(long x, long y) => (X, Y) = (x, y);
                    public Point(string s) => (X, Y) = (s.Length, 0);
                    public long X { get; }
                    public long Y { get; }
                }
                class Base
                {
                    public Base(string name, int n) => Name = name + n;
                    public Base(int n) => Name = "#" + n;
                    public string Name { get; }
                }
                static class Ext
                {
                    public static string Pad(this string s, int width) => s.PadLeft(width);
                    public static string Pad(this string s) => s;
                }

                class C(Dictionary<string, int> dict)
                {
                    static string Format(int n, string format) => n.ToString(format);
                    static string Format(double d, int digits) => Math.Round(d, digits).ToString();
                    static string Format(string s) => s;
                    static T Twice<T>(T a, T b) => b;
                    static string Opt(int a, string b = "-", long c = 0) => a + b + c;
                    static string Opt(string a) => a;
                    static int Sum(params int[] xs) => xs.Sum();

                    public infer Show(infer n) => Format(n, "N2");
                    public string ShowCSharp(int n) => Format(n, "N2");

                    public infer Pick(infer key) => dict.GetValueOrDefault(key);
                    public int PickCSharp(string key) => dict.GetValueOrDefault(key);

                    public infer Generic(infer x) => Twice(x, 1L);
                    public long GenericCSharp(long x) => Twice(x, 1L);

                    public infer Named(infer x) => Opt(1, c: x);
                    public string NamedCSharp(long x) => Opt(1, c: x);

                    public infer Expanded(infer x) => Sum(1, x);
                    public int ExpandedCSharp(int x) => Sum(1, x);

                    public infer Make(infer x) => new Point(x, 2);
                    public Point MakeCSharp(long x) => new Point(x, 2);

                    public infer Extension(infer w) => "e".Pad(w);
                    public string ExtensionCSharp(int w) => "e".Pad(w);

                    public IEnumerable<string> Lambda(int[] xs, infer n) => xs.Select(x => Format(n, "N1"));
                    public IEnumerable<string> LambdaCSharp(int[] xs, int n) => xs.Select(x => Format(n, "N1"));
                }

                class D : Base
                {
                    public D(infer name) : base(name, 1) { }
                }

                class DCSharp : Base
                {
                    public DCSharp(string name) : base(name, 1) { }
                }

                class Program
                {
                    static void Main()
                    {
                        var c = new C(new() { ["a"] = 7 });
                        Console.Write(c.Show(3) + c.ShowCSharp(3) + ";");
                        Console.Write(c.Pick("a") + c.PickCSharp("a") + ";");
                        Console.Write(c.Generic(4) + c.GenericCSharp(4) + ";");
                        Console.Write(c.Named(5) + c.NamedCSharp(5) + ";");
                        Console.Write(c.Expanded(6) + c.ExpandedCSharp(6) + ";");
                        Console.Write(c.Make(7).X + c.MakeCSharp(7).X + ";");
                        Console.Write(c.Extension(2) + c.ExtensionCSharp(2) + ";");
                        Console.Write(string.Join(",", c.Lambda([1], 8)) + string.Join(",", c.LambdaCSharp([1], 8)) + ";");
                        Console.Write(new D("d").Name + new DCSharp("d").Name);
                    }
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "3.003.00;14;2;1-51-5;14;14; e e;8.08.0;d1d1" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            foreach (var name in new[] { "Show", "Pick", "Generic", "Named", "Expanded", "Make", "Extension", "Lambda" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }

            Assert.Equal("D..ctor(System.String name)", comp.GetMember<NamedTypeSymbol>("D").InstanceConstructors.Single().ToTestDisplayString());
        }

        [Fact]
        public void InferFromOverloads_PinsNothing()
        {
            // Candidates that disagree pin nothing, and the error lists each one with the type it takes; so does a call
            // where no candidate accepts the other arguments, a type parameter nothing fixes, and the two forms of params.
            var source = """
                class C
                {
                    static void Over(int x) { }
                    static void Over(string x) { }
                    static void Over(long x, int y) { }
                    static void Gen<T>(T t) { }
                    static void Gen(int i, int j) { }
                    static void Params(params int[] xs) { }
                    static void Params(string s, int i) { }
                    static void Two(int a, string b) { }
                    static void Two(string a, int b) { }

                    static void Disagree(infer x) => Over(x);
                    static void NoneApplicable(infer x) => Over(x, "s");
                    static void TypeParameter(infer x) => Gen(x);
                    static void BothForms(infer x) => Params(x);
                    static void RefArgument(infer x) => Over(ref x);
                    static void Placeholders(infer a, infer b) => Two(a, b);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (13,26): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and the candidates of a call to 'Over' take different types there: 'C.Over(int)' takes 'int', 'C.Over(string)' takes 'string'. Write the parameter type.
                //     static void Disagree(infer x) => Over(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "the candidates of a call to 'Over' take different types there: 'C.Over(int)' takes 'int', 'C.Over(string)' takes 'string'").WithLocation(13, 26),
                // (14,32): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and no candidate of a call to 'Over' accepts the other arguments. Write the parameter type.
                //     static void NoneApplicable(infer x) => Over(x, "s");
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "no candidate of a call to 'Over' accepts the other arguments").WithLocation(14, 32),
                // (15,31): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and 'Gen' takes a type parameter there, which pins nothing yet. Write the parameter type.
                //     static void TypeParameter(infer x) => Gen(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "'Gen' takes a type parameter there, which pins nothing yet").WithLocation(15, 31),
                // (16,27): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and a params parameter pins nothing yet. Write the parameter type.
                //     static void BothForms(infer x) => Params(x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "a params parameter pins nothing yet").WithLocation(16, 27),
                // (17,29): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and an argument whose ref kind its parameter does not take pins nothing. Write the parameter type.
                //     static void RefArgument(infer x) => Over(ref x);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "an argument whose ref kind its parameter does not take pins nothing").WithLocation(17, 29),
                // (18,30): error CS20027: Cannot infer the type of parameter 'a', because it has no default value, and the candidates of a call to 'Two' take different types there: 'C.Two(int, string)' takes 'int', 'C.Two(string, int)' takes 'string'. Write the parameter type.
                //     static void Placeholders(infer a, infer b) => Two(a, b);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("a", "the candidates of a call to 'Two' take different types there: 'C.Two(int, string)' takes 'int', 'C.Two(string, int)' takes 'string'").WithLocation(18, 30),
                // (18,39): error CS20027: Cannot infer the type of parameter 'b', because it has no default value, and the candidates of a call to 'Two' take different types there: 'C.Two(int, string)' takes 'string', 'C.Two(string, int)' takes 'int'. Write the parameter type.
                //     static void Placeholders(infer a, infer b) => Two(a, b);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("b", "the candidates of a call to 'Two' take different types there: 'C.Two(int, string)' takes 'string', 'C.Two(string, int)' takes 'int'").WithLocation(18, 39));

            // The error points at the argument.
            var disagree = comp.GetDiagnostics().First();
            Assert.Equal(source.IndexOf("Over(x);") + "Over(".Length, disagree.AdditionalLocations.Single().SourceSpan.Start);

            // #201: the types the message lists, for the IDE fix, as reference IDs in a property; only where candidates disagree.
            Assert.Equal("System.Int32\nSystem.String", disagree.Properties[SharperInferredParameterType.CandidateTypesPropertyName]);
            Assert.All(comp.GetDiagnostics().Where(d => !d.GetMessage().Contains("take different types")), d => Assert.Empty(d.Properties));
        }

        [Fact]
        public void InferFromOverloads_CandidateTypesProperty_LeavesOutRuledOutCandidates()
        {
            // #201: the third 'Over' does not accept the string, so its type is not listed.
            var source = """
                using System.Collections.Generic;
                class C
                {
                    static void Over(List<int> x, string s) { }
                    static void Over(int[] x, string s) { }
                    static void Over(long x, int i) { }
                    static void F(infer x) => Over(x, "");
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var diagnostic = comp.GetDiagnostics().Single(d => d.Code == (int)ErrorCode.ERR_SharperInferParameterNoDefault);
            Assert.Equal(
                "System.Collections.Generic.List{System.Int32}\nSystem.Int32[]",
                diagnostic.Properties[SharperInferredParameterType.CandidateTypesPropertyName]);
        }

        [Fact]
        public void InferFromOverloads_Applicability()
        {
            // Applicable means applicable with the placeholder in place: the other arguments are bound and converted
            // first, names, optional parameters and the expanded form of params count, and the ref kind must fit.
            var source = """
                class C
                {
                    static void Count(int x) { }
                    static void Count(string x, int y) { }
                    static void Other(long x, string s) { }
                    static void Other(string x, int i) { }
                    static void Named(int a, long b) { }
                    static void Named(long b, string c) { }
                    static void Optional(int a, short b = 0) { }
                    static void Optional(string a, int b, int c) { }
                    static void RefKinds(ref int x) { }
                    static void RefKinds(string x) { }
                    static void In(in long x) { }
                    static void In(string x, int y) { }
                    static void Agree(int x) { }
                    static void Agree(int x, int y = 0, int z = 0) { }
                    static void Expanded(params long[] xs) { }
                    static void Expanded(string s) { }

                    static void ByCount(infer x) => Count(x);
                    static void ByOtherArgument(infer x) => Other(x, "s");
                    static void ByName(infer x) => Named(b: x, c: "s");
                    static void ByOptional(infer x) => Optional(1, x);
                    static void ByRefKind(infer x) => RefKinds(x);
                    static void ByIn(infer x) => In(x);
                    static void AllAgree(infer x) => Agree(x);
                    static void ByExpanded(infer x) => Expanded(1, x);

                    static void ByCountCSharp(int x) => Count(x);
                    static void ByOtherArgumentCSharp(long x) => Other(x, "s");
                    static void ByNameCSharp(long x) => Named(b: x, c: "s");
                    static void ByOptionalCSharp(short x) => Optional(1, x);
                    static void ByRefKindCSharp(string x) => RefKinds(x);
                    static void ByInCSharp(long x) => In(x);
                    static void AllAgreeCSharp(int x) => Agree(x);
                    static void ByExpandedCSharp(long x) => Expanded(1, x);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            foreach (var name in new[] { "ByCount", "ByOtherArgument", "ByName", "ByOptional", "ByRefKind", "ByIn", "AllAgree", "ByExpanded" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }
        }

        [Fact]
        public void InferFromOverloads_Generics()
        {
            // A type parameter at the position pins when other arguments or the receiver fix it; nothing else does.
            var source = """
                using System.Collections.Generic;

                interface IRepo<T> { void Put(T item); void Put(T item, int count); }
                static class Ext
                {
                    public static void Add<T>(this List<T> list, T a, T b) { }
                    public static TValue Get<TKey, TValue>(this IDictionary<TKey, TValue> d, TKey key) => d[key];
                }

                class C
                {
                    static T First<T>(T a, T b) => a;
                    static T First<T>(T a, T b, T c) => a;
                    static void Pair<T>(T a, List<T> b) { }

                    static infer FromArgument(infer x) => First("s", x);
                    static void FromReceiver(List<long> list, infer x) => list.Add(x, 1);
                    static infer FromTypeArguments(IDictionary<string, int> d, infer key) => d.Get(key);
                    static void FromContainingType(IRepo<string> r, infer x) => r.Put(x);
                    static void FromList(List<int> list, infer x) => Pair(x, list);
                    static void Open(infer x, infer y) => First(x, y);

                    static string FromArgumentCSharp(string x) => First("s", x);
                    static void FromReceiverCSharp(List<long> list, long x) => list.Add(x, 1);
                    static int FromTypeArgumentsCSharp(IDictionary<string, int> d, string key) => d.Get(key);
                    static void FromContainingTypeCSharp(IRepo<string> r, string x) => r.Put(x);
                    static void FromListCSharp(List<int> list, int x) => Pair(x, list);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (21,29): error CS20027: Cannot infer the type of parameter 'x', because it has no default value, and 'First' takes a type parameter there, which pins nothing yet. Write the parameter type.
                //     static void Open(infer x, infer y) => First(x, y);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("x", "'First' takes a type parameter there, which pins nothing yet").WithLocation(21, 22),
                // (21,38): error CS20027: Cannot infer the type of parameter 'y', because it has no default value, and 'First' takes a type parameter there, which pins nothing yet. Write the parameter type.
                //     static void Open(infer x, infer y) => First(x, y);
                Diagnostic(ErrorCode.ERR_SharperInferParameterNoDefault, "infer").WithArguments("y", "'First' takes a type parameter there, which pins nothing yet").WithLocation(21, 31));
            foreach (var name in new[] { "FromArgument", "FromReceiver", "FromTypeArguments", "FromContainingType", "FromList" })
            {
                VerifySameSignature(comp, "C." + name, "C." + name + "CSharp");
            }
        }

        [Fact]
        public void InferFromOverloads_Lambdas()
        {
            // A lambda bound for several delegate types counts only in the binding that overload resolution keeps.
            var source = """
                using System;
                class C
                {
                    static int Count(string s) => s.Length;
                    static int Count(int a, int b) => a + b;
                    static int Run(Func<int, int> f) => f(1);
                    static int Run(Func<string, int> f) => f("ab");

                    static infer InLambda(infer x) => Run(v => v.Length + Count(x));
                    static int InLambdaCSharp(string x) => Run(v => v.Length + Count(x));
                    static infer Local(infer x) { int L(string s) => s.Length + Count(x); return L("a"); }
                    static int LocalCSharp(string x) { int L(string s) => s.Length + Count(x); return L("a"); }

                    static void Main() => Console.Write(InLambda("abc") + InLambdaCSharp("abc") + ";" + Local("bc") + LocalCSharp("bc"));
                }
                """;
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "10;33" : null);
            verifier.VerifyDiagnostics();
            var comp = (CSharpCompilation)verifier.Compilation;
            VerifySameSignature(comp, "C.InLambda", "C.InLambdaCSharp");
            VerifySameSignature(comp, "C.Local", "C.LocalCSharp");
        }

        [Fact]
        public void InferFromOverloads_Uses()
        {
            // A use in an overloaded call is a use like any other: uses must agree, and nullability merges.
            var source = """
                #nullable enable
                class C
                {
                    static void Over(string s) { }
                    static void Over(int i, int j) { }
                    static void Maybe(string? s) { }
                    static void Maybe(int i, int j) { }
                    static void Take(long x) { }

                    static void Disagree(infer x) { Over(x); Take(x); }
                    static void Merge(infer s) { Over(s); Maybe(s); }
                    static void Overloads(infer x) { Over(x); System.Console.WriteLine(x); }
                    static void MergeCSharp(string s) { Over(s); Maybe(s); }
                    static void OverloadsCSharp(string x) { Over(x); System.Console.WriteLine(x); }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (10,26): error CS20057: Cannot infer the type of parameter 'x', because its uses need different types: 'string' for 'C.Over(string)' and 'long' for 'C.Take(long)'. Write the parameter type.
                //     static void Disagree(infer x) { Over(x); Take(x); }
                Diagnostic(ErrorCode.ERR_SharperInferParameterUsesDisagree, "infer").WithArguments("x", "string", "C.Over(string)", "long", "C.Take(long)").WithLocation(10, 26));
            VerifySameSignature(comp, "C.Merge", "C.MergeCSharp");
            VerifySameSignature(comp, "C.Overloads", "C.OverloadsCSharp");
        }
    }
}
