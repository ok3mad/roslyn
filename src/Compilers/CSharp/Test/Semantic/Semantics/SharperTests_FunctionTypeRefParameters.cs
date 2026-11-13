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
    public sealed partial class SharperTests
    {
        [Fact]
        public void FunctionTypeRef_Probe()
        {
            var source = """
                using System;
                class C
                {
                    static int s_value;
                    static void Main()
                    {
                        (ref int, out string) -> bool tryRead = (ref int pos, out string s) => { s = "x"; pos++; return true; };
                        () -> ref int slot = () => ref s_value;
                        var f = (ref int x) => x++;
                        (ref int) -> int g = f;
                        var h = (in int x) => x;
                        (in int) -> int h2 = h;
                        (ref readonly int) -> void rr = (ref readonly int x) => { };
                        (scoped ref int) -> void sc = (scoped ref int x) => { };
                        (scoped Span<int>) -> void sv = (scoped Span<int> x) => { };
                        () -> ref readonly int ro = () => ref s_value;
                        (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int many = (a, b, c, d, e, f2, g2, h3, i, j, k, l, m, n, o, p, q) => a + q;
                        var many2 = (int a, int b, int c, int d, int e, int f2, int g2, int h3, int i, int j, int k, int l, int m, int n, int o, int p, int q) => a + q;
                        many = many2;
                        int v = 1;
                        tryRead(ref v, out var str);
                        Console.Write(v + str + g(ref v) + many(1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2));
                        slot() = 5;
                        Console.Write(s_value);
                        Console.Write(f.GetType() == g.GetType());
                        (out int) -> void o = (out int x) => { x = 1; };
                        var o2 = (out int x) => { x = 1; };
                        o = o2;
                        (params int[]) -> void p1 = null;
                        (this int) -> void p2 = null;
                        (readonly int) -> void p3 = null;
                        (scoped int) -> void p6 = null;
                        () -> ref void p7 = null;
                        (ref Console) -> void p8 = null;
                        (ref int*) -> void p9 = null;
                        (ref int x, ref int x) -> void p10 = null;
                        Action<int> named = null;
                        (ref int) -> void notNamed = named;
                        D d = g;
                        (ref int) -> int back = d;
                        (ref int) -> int inv = d.Invoke;
                        object obj = g;
                        _ = obj is (ref int) -> int z;
                    }
                }
                delegate int D(ref int x);
                public class P
                {
                    public (ref int) -> void F;
                    internal (ref int) -> void G;
                    protected void M((ref int) -> void x) { }
                    public (out int) -> void Prop { get; set; }
                    public System.Collections.Generic.List<(ref int) -> void> L;
                    private (ref int) -> void Ok;
                    (ref ref int) -> void p4;
                    (ref out int) -> void p5;
                    (scoped scoped Span<int>) -> void p6;
                    (ref readonly readonly int) -> void p7;
                    public (scoped Span<int>, ref readonly int, ref int, out int) -> ref readonly int Shown;
                    public infer I() => (ref int x) => { };
                    public event (ref int) -> void E;
                }
                class Internal
                {
                    public (ref int) -> void F;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.UnsafeReleaseExe);
            comp.VerifyDiagnostics();
        }
    }
}
