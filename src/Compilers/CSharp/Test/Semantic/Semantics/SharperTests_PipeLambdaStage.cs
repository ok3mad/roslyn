// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's lambda stage (issue #186): <c>x |&gt; (y =&gt; y + 1)</c> is <c>x |&gt; it + 1</c> with <c>y</c> for
    /// <c>it</c>, and no delegate.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void Pipe_LambdaStage()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                class C
                {
                    static string Show<T>(IEnumerable<T> xs) => string.Join(",", xs);

                    static void Main()
                    {
                        int x = 3;
                        int[] xs = [1, 4, 5];
                        Console.Write((x |> (y => y + 1)) + " " + (x |> it + 1));                 // the 'it' twin
                        Console.Write(" | " + (x |> (v => xs.Where(it > v)) |> Show));            // 'it' is Where's
                        Console.Write(" | " + (xs |> (ys => ys.Select(it * 2)) |> Show));         // 'it' is Select's
                        Console.Write(" | " + (x |> ((long y) => y * 2_000_000_000)));            // converts to long
                        Console.Write(" | " + (x |> ((int y) => y * 2)));                         // identity
                        Console.Write(" | " + (x |> ((val y) => y - 1)));                         // val: implicitly typed
                        Console.Write(" | " + (x |> (it => it * 3)));                             // a parameter named 'it'
                        Console.Write(" | " + (Next() |> (_ => 42)));                             // a discard: the left side still runs
                        Console.Write(" | " + (x |> ((y => y * y))));                             // nested parentheses
                        Console.Write(" | " + (x |> (a => a + 1) |> (b => b * 10)));              // a chain
                        int? r = x |> (y => y > 0 ? 1 : null);                                    // target-typed, as with 'it'
                        Console.Write(" | " + r);
                        object o = x |> (y => y + 1);                                             // natural type first: a boxed int
                        Console.Write(" | " + o.GetType().Name);

                        // A lambda with a free 'it' is the stage's value (target-typed, #187), not a lambda stage.
                        Func<int, int> f;
                        f = x |> (y => y + it);
                        Console.Write(" | " + f(10));
                    }

                    static int s_count;
                    static int Next() { s_count++; Console.Write("[next]"); return s_count; }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr
                    ? "4 4 | 4,5 | 2,8,10 | 6000000000 | 6 | 2 | 9[next] | 42 | 9 | 40 | 1 | Int32 | 13"
                    : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_LambdaStage_AwaitAndCaptures()
        {
            // The stage is not a lambda: 'await' works in it, and a lambda in it captures the named value, a fresh variable
            // for each evaluation of the pipe.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                class C
                {
                    static async Task<int> Load(int id) { await Task.Yield(); return id * 100; }
                    static async Task<int> Later(Func<int> f) { await Task.Yield(); return f(); }

                    static async Task Main()
                    {
                        Console.Write(await (2 |> (id => Load(id))));
                        Console.Write(" " + (3 |> (id => await Load(id))));
                        Console.Write(" " + (4 |> (n => n + await Later(() => n))));

                        var fs = new List<Func<int>>();
                        for (int i = 0; i < 3; i++)
                        {
                            i * 10 |> (v => fs.Add(() => v));
                        }

                        foreach (var f in fs) Console.Write(" " + f());
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "200 300 8 0 10 20" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_LambdaStage_NotALambdaStage()
        {
            // Only a lambda with one parameter and an expression body names the value; any other lambda is CS20048, with a
            // hint to use 'it'.
            var source = """
                using System;

                class C
                {
                    void M(int x)
                    {
                        _ = x |> (y => { return y; });             // 1: a block body
                        _ = x |> ((a, b) => a + b);                // 2: two parameters
                        _ = x |> (() => 1);                        // 3: no parameter
                        _ = x |> (async y => y);                   // 4: async
                        _ = x |> (static y => y);                  // 5: static
                        _ = x |> ([My] (y) => y);                  // 6: an attribute
                        _ = x |> (int (y) => y);                   // 7: an explicit return type
                        _ = x |> ((ref int y) => y);               // 8: a parameter modifier
                        _ = x |> ((int y = 1) => y);               // 9: a default value
                        _ = x |> (delegate (int y) { return y; }); // 10: an anonymous method
                    }
                }

                class MyAttribute : Attribute { }
                """;
            var hint = "a lambda that cannot name the value: only a lambda with one parameter and an expression body, without 'async', 'static', attributes or parameter modifiers, can. Use 'it' instead, as in 'x |> it + 1'";
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(y => { return y; })").WithArguments(hint).WithLocation(7, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "((a, b) => a + b)").WithArguments(hint).WithLocation(8, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(() => 1)").WithArguments(hint).WithLocation(9, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(async y => y)").WithArguments(hint).WithLocation(10, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(static y => y)").WithArguments(hint).WithLocation(11, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "([My] (y) => y)").WithArguments(hint).WithLocation(12, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(int (y) => y)").WithArguments(hint).WithLocation(13, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "((ref int y) => y)").WithArguments(hint).WithLocation(14, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "((int y = 1) => y)").WithArguments(hint).WithLocation(15, 18),
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(delegate (int y) { return y; })").WithArguments(hint).WithLocation(16, 18));
        }

        [Fact]
        public void Pipe_LambdaStage_Errors()
        {
            var source = """
                using System;
                using System.Linq;

                class C
                {
                    void M(int x, int[] xs)
                    {
                        _ = x |> (y => y = 2);                     // 1: read-only, like 'it'
                        _ = "a" |> ((int y) => y);                 // 2: no implicit conversion, as for 'int y = "a";'
                        _ = x |> ((byte y) => y);                  // 3: an explicit conversion is not enough
                        _ = x |> (_ => _);                         // 4: '_' is a discard
                        _ = x |> (y => y is int y);                // 5: a clash inside the stage
                        Func<int, bool> g = y => y is int y;       // 5: its C# twin
                        _ = x |> (y => y + it);                    // 6: a free 'it': the lambda is the value, with no type
                        _ = x |> (v => xs.Where(it > v).Count(it > 0)); // 7: innermost wins in each argument
                    }

                    void Shadowing(int x)
                    {
                        int y = 0;
                        _ = x |> (y => y + 1);                     // shadows, as a lambda's parameter does
                        Func<int, int> f = y => y + 1;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,24): error CS1656: Cannot assign to 'y' because it is a 'val variable'
                //         _ = x |> (y => y = 2);                     // 1: read-only, like 'it'
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "y").WithArguments("y", "val variable").WithLocation(8, 24),
                // (9,13): error CS0029: Cannot implicitly convert type 'string' to 'int'
                //         _ = "a" |> ((int y) => y);                 // 2: no implicit conversion, as for 'int y = "a";'
                Diagnostic(ErrorCode.ERR_NoImplicitConv, @"""a""").WithArguments("string", "int").WithLocation(9, 13),
                // (10,13): error CS0266: Cannot implicitly convert type 'int' to 'byte'. An explicit conversion exists (are you missing a cast?)
                //         _ = x |> ((byte y) => y);                  // 3: an explicit conversion is not enough
                Diagnostic(ErrorCode.ERR_NoImplicitConvCast, "x").WithArguments("int", "byte").WithLocation(10, 13),
                // (11,24): error CS0103: The name '_' does not exist in the current context
                //         _ = x |> (_ => _);                         // 4: '_' is a discard
                Diagnostic(ErrorCode.ERR_NameNotInContext, "_").WithArguments("_").WithLocation(11, 24),
                // (12,24): error CS0841: Cannot use local variable 'y' before it is declared
                //         _ = x |> (y => y is int y);                // 5: a clash inside the stage
                Diagnostic(ErrorCode.ERR_VariableUsedBeforeDeclaration, "y").WithArguments("y").WithLocation(12, 24),
                // (12,33): error CS0136: A local or parameter named 'y' cannot be declared in this scope because that name is used in an enclosing local scope to define a local or parameter
                //         _ = x |> (y => y is int y);                // 5: a clash inside the stage
                Diagnostic(ErrorCode.ERR_LocalIllegallyOverrides, "y").WithArguments("y").WithLocation(12, 33),
                // (13,34): error CS0841: Cannot use local variable 'y' before it is declared
                //         Func<int, bool> g = y => y is int y;       // 5: its C# twin
                Diagnostic(ErrorCode.ERR_VariableUsedBeforeDeclaration, "y").WithArguments("y").WithLocation(13, 34),
                // (13,43): error CS0136: A local or parameter named 'y' cannot be declared in this scope because that name is used in an enclosing local scope to define a local or parameter
                //         Func<int, bool> g = y => y is int y;       // 5: its C# twin
                Diagnostic(ErrorCode.ERR_LocalIllegallyOverrides, "y").WithArguments("y").WithLocation(13, 43),
                // (14,19): error CS8917: The delegate type could not be inferred.
                //         _ = x |> (y => y + it);                    // 6: a free 'it': the lambda is the value, with no type
                Diagnostic(ErrorCode.ERR_CannotInferDelegateType, "y => y + it").WithLocation(14, 19),
                // (20,13): warning CS0219: The variable 'y' is assigned but its value is never used
                //         int y = 0;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "y").WithArguments("y").WithLocation(20, 13));
        }

        [Fact]
        public void Pipe_LambdaStage_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    void M(string? maybe, string sure)
                    {
                        _ = maybe |> (s => s.Length);              // warning: 's' is inferred like 'var'
                        _ = maybe |> (s => s?.Length);
                        _ = sure |> (s => s.Length);
                        _ = maybe |> ((string s) => s.Length);     // warning at the conversion, and 's' may be null
                        _ = maybe |> ((string? s) => s.Length);    // warning
                        _ = sure |> ((string? s) => s.Length);     // 's' is not null here, as after 'string? s = sure;'
                        if (maybe != null)
                        {
                            _ = maybe |> (s => s.Length);
                        }

                        string t = maybe |> (s => s);              // warning
                        string? n = null;
                        string u = n |> ((string? s) => s ?? "");
                    }

                    void Twin(string? maybe)
                    {
                        string s = maybe;
                        _ = s.Length;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,28): warning CS8602: Dereference of a possibly null reference.
                //         _ = maybe |> (s => s.Length);              // warning: 's' is inferred like 'var'
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(6, 28),
                // (9,13): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         _ = maybe |> ((string s) => s.Length);     // warning at the conversion, and 's' may be null
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "maybe").WithLocation(9, 13),
                // (9,37): warning CS8602: Dereference of a possibly null reference.
                //         _ = maybe |> ((string s) => s.Length);     // warning at the conversion, and 's' may be null
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(9, 37),
                // (10,38): warning CS8602: Dereference of a possibly null reference.
                //         _ = maybe |> ((string? s) => s.Length);    // warning
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(10, 38),
                // (17,20): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         string t = maybe |> (s => s);              // warning
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "maybe |> (s => s)").WithLocation(17, 20),
                // (24,20): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         string s = maybe;
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "maybe").WithLocation(24, 20),
                // (25,13): warning CS8602: Dereference of a possibly null reference.
                //         _ = s.Length;
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(25, 13));
        }

        [Fact]
        public void Pipe_LambdaStage_RefStruct()
        {
            var source = """
                using System;

                class C
                {
                    static int Count(ReadOnlySpan<char> s) => s.Length;

                    static void Main()
                    {
                        ReadOnlySpan<char> s = "abc";
                        Console.Write(s |> (t => t.Slice(1)) |> Count);
                        Console.Write(" " + ("hello" |> ((ReadOnlySpan<char> t) => Count(t))));
                    }

                    static Span<int> Escape()
                    {
                        Span<int> local = stackalloc int[2];
                        return local |> (t => t);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (17,31): error CS8352: Cannot use variable 't' in this context because it may expose referenced variables outside of their declaration scope
                //         return local |> (t => t);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "t").WithArguments("t").WithLocation(17, 31));
        }

        [Fact]
        public void Pipe_LambdaStage_SemanticModel()
        {
            var source = """
                using System;

                class C
                {
                    int M(int a) => a |> (y => y + 1);
                    long N(int a) => a |> ((long y) => y);
                    int D(int a) => a |> (_ => 1);
                    Func<int, int> F(int a) => a |> (y => y + it);
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var pipes = tree.GetRoot().DescendantNodes().OfType<PipeExpressionSyntax>().ToArray();
            ParameterSyntax parameterOf(PipeExpressionSyntax pipe) => pipe.Stage.DescendantNodes().OfType<ParameterSyntax>().First();

            // The parameter declares the local that holds the value.
            var parameter = parameterOf(pipes[0]);
            var local = (ILocalSymbol)model.GetDeclaredSymbol((SyntaxNode)parameter)!;
            Assert.Equal("System.Int32 y", local.ToTestDisplayString());
            Assert.False(local.IsImplicitlyDeclared);
            Assert.Equal(parameter.Identifier.GetLocation(), local.Locations.Single());
            Assert.Same(parameter, local.DeclaringSyntaxReferences.Single().GetSyntax());
            Assert.Null(model.GetDeclaredSymbol(parameter));
            var use = pipes[0].Stage.DescendantNodes().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.Text == "y");
            Assert.Equal(local, model.GetSymbolInfo(use).Symbol);
            Assert.Equal("System.Int32", model.GetTypeInfo(pipes[0]).Type.ToTestDisplayString());

            // An explicit type is the local's type; the left side converts to it.
            var typed = (ILocalSymbol)model.GetDeclaredSymbol((SyntaxNode)parameterOf(pipes[1]))!;
            Assert.Equal("System.Int64 y", typed.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetTypeInfo(pipes[1].Left).Type.ToTestDisplayString());
            Assert.Equal("System.Int64", model.GetTypeInfo(pipes[1].Left).ConvertedType.ToTestDisplayString());

            // A discard declares nothing.
            Assert.Null(model.GetDeclaredSymbol((SyntaxNode)parameterOf(pipes[2])));

            // A lambda with a free 'it' is a lambda, whose parameter is a parameter.
            var lambdaParameter = model.GetDeclaredSymbol(parameterOf(pipes[3]))!;
            Assert.Equal("System.Int32 y", lambdaParameter.ToTestDisplayString());
            Assert.Equal(MethodKind.LambdaMethod, ((IMethodSymbol)lambdaParameter.ContainingSymbol).MethodKind);
            Assert.Same(lambdaParameter, model.GetDeclaredSymbol((SyntaxNode)parameterOf(pipes[3])));
        }

        [Fact]
        public void Pipe_LambdaStage_NoDelegate()
        {
            // A lambda stage compiles as its 'it' twin: no closure class, no delegate, in Debug and in Release.
            var source = """
                using System;

                class C
                {
                    static int Named(int x) => x |> (y => y + 1);
                    static int It(int x) => x |> it + 1;
                    static long Typed(int x) => x |> ((long y) => y * 2);

                    static void Main() => Console.Write(Named(1) + It(2) + Typed(3));
                }
                """;
            foreach (var options in new[] { TestOptions.DebugExe, TestOptions.ReleaseExe })
            {
                var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, options: options, targetFramework: TargetFramework.Net90,
                    expectedOutput: ExecutionConditionUtil.IsCoreClr ? "11" : null,
                    symbolValidator: module => Assert.Empty(module.GlobalNamespace.GetTypeMember("C").GetTypeMembers()));
                verifier.VerifyDiagnostics();
                verifier.VerifyIL("C.Named", options.OptimizationLevel == OptimizationLevel.Debug
                    ? """
                    {
                      // Code size        6 (0x6)
                      .maxstack  2
                      .locals init (int V_0) //y
                      IL_0000:  ldarg.0
                      IL_0001:  stloc.0
                      IL_0002:  ldloc.0
                      IL_0003:  ldc.i4.1
                      IL_0004:  add
                      IL_0005:  ret
                    }
                    """
                    : """
                    {
                      // Code size        4 (0x4)
                      .maxstack  2
                      IL_0000:  ldarg.0
                      IL_0001:  ldc.i4.1
                      IL_0002:  add
                      IL_0003:  ret
                    }
                    """);
                verifier.VerifyIL("C.Typed", options.OptimizationLevel == OptimizationLevel.Debug
                    ? """
                    {
                      // Code size        8 (0x8)
                      .maxstack  2
                      .locals init (long V_0) //y
                      IL_0000:  ldarg.0
                      IL_0001:  conv.i8
                      IL_0002:  stloc.0
                      IL_0003:  ldloc.0
                      IL_0004:  ldc.i4.2
                      IL_0005:  conv.i8
                      IL_0006:  mul
                      IL_0007:  ret
                    }
                    """
                    : """
                    {
                      // Code size        6 (0x6)
                      .maxstack  2
                      IL_0000:  ldarg.0
                      IL_0001:  conv.i8
                      IL_0002:  ldc.i4.2
                      IL_0003:  conv.i8
                      IL_0004:  mul
                      IL_0005:  ret
                    }
                    """);
            }
        }
    }
}
