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
        public void InferFromBody_Probe2()
        {
            var source = """
                class D
                {
                    static infer A(infer id) => B(id);
                    static infer B(int x) => A(x);
                    static void P(infer x) => Q(x);
                    static void Q(infer y) => P(y);
                    static infer R(infer x) => S(x);
                    static int S(int y) => T(y);
                    static infer T(int z) => R(z).ToString().Length;
                }
                """;
            var text = "";
            foreach (var order in new[] { "ABPQRST", "BAQPTSR" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
                foreach (var c in order)
                {
                    text += comp.GetMember<MethodSymbol>("D." + c).ToTestDisplayString() + "\n";
                }
                text += string.Join("\n", comp.GetDiagnostics().Select(d => d.ToString())) + "\n----\n";
            }
            System.IO.File.WriteAllText("/tmp/claude-0/-home-user-roslyn/34fe55e5-020b-59c0-94f5-c7d4c7551162/scratchpad/probe2.txt", text);
        }

        [Fact]
        public void InferFromBody_Probe()
        {
            var source = """
                #nullable enable
                using System;
                using System.Collections.Generic;
                using System.Linq;
                using System.Threading.Tasks;

                interface IRepo<T> { T Find(int id); void Put(T item); }
                record User(string Name);
                class Base { public Base(long id) { } }
                static class Ext { public static string Show(this IRepo<User> r, int width) => ""; public static int Twice(int x) => 2 * x; }

                class C : Base
                {
                    public C(infer id) : base(id) { }
                    C(infer a, int b) : this(a) { }
                    static void Named(int a, string b) { }
                    public static infer N(infer x, infer y) { Named(b: x, a: y); return 0; }
                    static infer Generic(IRepo<User> r, infer u) { r.Put(u); return r; }
                    static void ExplicitTypeArgs(infer x) => M<int>(x);
                    static void M<T>(T t) { }
                    static void Inferred(infer x) => M(x);
                    static void InParam(infer x) => TakeIn(x);
                    static void TakeIn(in int i) { }
                    static void RefArg(infer x) => TakeRef(ref x);
                    static void TakeRef(ref int i) { }
                    static void Params(infer x) => TakeParams(x);
                    static void TakeParams(params int[] xs) { }
                    static void StaticExt(infer x) => Ext.Twice(x);
                    static void ReducedExt(IRepo<User> r, infer w) => r.Show(w);
                    static void Receiver(infer r) => r.Show(1);
                    static void Overloads(infer x) => Console.WriteLine(x);
                    static void Local(infer x) { void L() => Ext.Twice(x); L(); }
                    static void Shadow(infer x) { Func<int, int> f = x => Ext.Twice(x); }
                    static void TargetNew(infer x) { User u = new(x); }
                    static async Task<int> Async(infer x) { await Task.Yield(); return Ext.Twice(x); }
                    static IEnumerable<int> Iter(infer x) { yield return Ext.Twice(x); }
                    static infer Interp(infer x) => $"{x}";
                    static infer Is(infer x) => x is int;
                    static infer Op(infer x) => x + 1;
                    static void Nullable(infer s) => TakeNullable(s);
                    static void TakeNullable(string? s) { }
                    static void Merge(infer s) { TakeNullable(s); TakeString(s); }
                    static void TakeString(string s) { }
                    static void Tuples(infer t) { TakeTuple(t); TakeTuple2(t); }
                    static void TakeTuple((int a, int b) t) { }
                    static void TakeTuple2((int x, int y) t) { }
                    void Recursive(infer x) { if (x is null) Recursive(x); TakeString(x); }
                    static void Owner<T>(IRepo<T> r, infer x) => r.Put(x);
                    static void LocalGeneric(infer x) { void L<U>(IRepo<U> r) => r.Put(x); }
                    public static infer operator +(C c, infer x) => Ext.Twice(x);
                    static void Lambda(infer x) { Func<int> f = () => Ext.Twice(x); }
                    static void InnerLambda() { Func<int, int> f = (infer y) => y; }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var diags = comp.GetDiagnostics();
            var c = comp.GetMember<NamedTypeSymbol>("C");
            var text = string.Join("\n", c.GetMembers().OfType<MethodSymbol>().Select(m => m.ToTestDisplayString())) + "\n" + string.Join("\n", diags.Select(d => d.ToString()));
            System.IO.File.WriteAllText("/tmp/claude-0/-home-user-roslyn/34fe55e5-020b-59c0-94f5-c7d4c7551162/scratchpad/probe.txt", text);
        }
    }
}
