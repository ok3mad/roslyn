// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's refined types, slice 1 step 3 (issue #33): the inferred refinement of the return of an <c>infer</c>
    /// method or local function and of a <c>val</c> local. Nothing is reported at call sites yet (step 4); the tests read
    /// the refinement from the symbols.
    /// </summary>
    public sealed partial class SharperTests
    {
        /// <summary>The text of the return refinement of each method of class C, or null: <c>Name: text</c>.</summary>
        private static string[] ReturnRefinements(CSharpCompilation comp, string type = "C")
            => comp.GetMember<NamedTypeSymbol>(type).GetMembers().OfType<MethodSymbol>()
                .Where(m => m.MethodKind is MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion)
                .Select(m => m.Name + ": " + (m.SharperReturnRefinement?.Text ?? "<none>")).ToArray();

        /// <summary>The refinement of each local declared with a declarator, and of each local function, in source order.</summary>
        private static string[] LocalRefinements(CSharpCompilation comp)
        {
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var result = new List<string>();
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is VariableDeclaratorSyntax { Parent.Parent: LocalDeclarationStatementSyntax } declarator)
                {
                    if (declarator.Initializer is { } initializer)
                    {
                        _ = model.GetTypeInfo(initializer.Value);
                    }

                    var local = model.GetDeclaredSymbol(declarator).GetSymbol<LocalSymbol>()!;
                    result.Add(local.Name + ": " + (local.SharperRefinement?.Text ?? "<none>"));
                }
                else if (node is LocalFunctionStatementSyntax localFunction)
                {
                    var method = model.GetDeclaredSymbol(localFunction).GetSymbol<MethodSymbol>()!;
                    result.Add(method.Name + "(): " + (method.SharperReturnRefinement?.Text ?? "<none>"));
                }
            }

            return result.ToArray();
        }

        [Fact]
        public void ReturnRefinement_Constants()
        {
            // An 'infer' method that returns a constant returns that value; the C# twin has the same type and no refinement.
            var source = """
                enum Color { Red, Green, Blue }

                class C
                {
                    const int Limit = 10;

                    static infer One() => 1;
                    static int One_CSharp() => 1;
                    static infer Folded() => Limit * 2 + 1;
                    static infer Negative() => -5L;
                    static infer Text() => "a";
                    static infer Letter() => 'x';
                    static infer Yes() => true;
                    static infer Half() => 0.5;
                    static infer Price() => 9.99m;
                    static infer Red() => Color.Red;
                    static infer Mask() => (Color)12;
                    static infer Unsigned() => 7u;
                    static infer NaN() => double.NaN;
                    static infer Block() { return 3; }
                    infer Instance() => 4;
                    public static infer Public() => 5;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "One: 1",
                    "One_CSharp: <none>",
                    "Folded: 21",
                    "Negative: -5",
                    "Text: \"a\"",
                    "Letter: 'x'",
                    "Yes: true",
                    "Half: 0.5",
                    "Price: 9.99m",
                    "Red: Color.Red",
                    "Mask: (Color)12",
                    "Unsigned: 7",
                    "NaN: double.NaN",
                    "Block: 3",
                    "Instance: 4",
                    "Public: 5",
                },
                ReturnRefinements(comp));

            var one = comp.GetMember<MethodSymbol>("C.One");
            Assert.Equal("System.Int32 C.One()", one.ToTestDisplayString());
            Assert.Equal(comp.GetMember<MethodSymbol>("C.One_CSharp").ReturnTypeWithAnnotations, one.ReturnTypeWithAnnotations);
            var refinement = one.SharperReturnRefinement!;
            Assert.False(refinement.HasErrors);
            Assert.False(refinement.MatchesNull);
            Assert.Equal("[1..1]", refinement.Values!.ToString());
            Assert.Same(refinement, one.SharperReturnRefinement);
        }

        [Fact]
        public void ReturnRefinement_RefinedParameterUnchanged()
        {
            // A refined parameter returned unchanged gives its refinement to the return, as it is written.
            var source = """
                class C
                {
                    static infer Same(int i is >=0 and <10 or 42) => i;
                    static int Same_CSharp(int i) => i;
                    static infer Plain(int i) => i;
                    static infer Block(string s is "a" or "b") { return s; }
                    static infer Second(int a, long b is not 0) => b;
                    static infer Through(int i is >0 and <10) => Same(Positive(i));
                    static infer Positive(int i is >0 and <10) => i;
                    static infer Converted(int i is >0) => (long)i;
                    static infer Identity(int i is >0) => (int)i;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "Same: >= 0 and < 10 or 42",
                    "Same_CSharp: <none>",
                    "Plain: <none>",
                    "Block: \"a\" or \"b\"",
                    "Second: not 0",
                    "Through: >= 0 and < 10 or 42",
                    "Positive: > 0 and < 10",
                    "Converted: <none>",
                    "Identity: > 0",
                },
                ReturnRefinements(comp));

            var same = comp.GetMember<MethodSymbol>("C.Same");
            Assert.Same(same.Parameters[0].SharperRefinement, same.SharperReturnRefinement);
            Assert.Equal(same.Parameters[0].SharperRefinement!.Values!.ToString(), same.SharperReturnRefinement!.Values!.ToString());
        }

        [Fact]
        public void ReturnRefinement_SwitchExpressions()
        {
            // A switch expression whose arms all yield values with a refinement returns their union, in normal form:
            // null, the constants in ascending order, then the other patterns. An arm that throws yields nothing.
            var source = """
                class C
                {
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    static int Sign_CSharp(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    static infer Duplicates(int x) => x switch { 1 => 5, 2 => 3, 3 => 5, _ => 3 };
                    static infer Nested(int x, bool b) => x switch { 0 => b switch { true => 10, false => 20 }, _ => 30 };
                    static infer Throws(int x) => x switch { 0 => "zero", 1 => "one", _ => throw new System.ArgumentException() };
                    static infer Conditional(bool b) => b ? 'y' : 'n';
                    static infer Mixed(int x, int i is >=100) => x switch { 0 => -1, 1 => 150, _ => i };
                    static infer WithNull(int x) => x switch { 0 => "a", 1 => null, _ => "b" };
                    static infer Widened(int x) => x switch { 0 => 1, _ => 2L };
                    static infer When(int x, int y) => x switch { 0 when y > 0 => 1, _ => 2 };
                    static infer Calls(int x) => x switch { 0 => Sign(x), _ => 7 };
                    static infer InBlock(int x) { return x switch { 0 => 1, _ => 2 }; }
                    static infer Open(int x) => x switch { 0 => 1, _ => x };
                    static infer AllBool(int x) => x switch { 0 => true, _ => false };
                    static infer Ranges(int a is >=0 and <10, int b is >=5, bool c) => c ? a : b;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "Sign: -1 or 0 or 1",
                    "Sign_CSharp: <none>",
                    "Duplicates: 3 or 5",
                    "Nested: 10 or 20 or 30",
                    "Throws: \"one\" or \"zero\"",
                    "Conditional: 'n' or 'y'",
                    "Mixed: -1 or >= 100",
                    "WithNull: null or \"a\" or \"b\"",
                    "Widened: 1 or 2",
                    "When: 1 or 2",
                    "Calls: -1 or 0 or 1 or 7",
                    "InBlock: 1 or 2",
                    "Open: <none>",
                    "AllBool: <none>",
                    "Ranges: >= 0 and < 10 or >= 5",
                },
                ReturnRefinements(comp));

            var sign = comp.GetMember<MethodSymbol>("C.Sign").SharperReturnRefinement!;
            Assert.Equal("[-1..1]", sign.Values!.ToString());
            Assert.False(sign.MatchesNull);
            Assert.True(comp.GetMember<MethodSymbol>("C.WithNull").SharperReturnRefinement!.MatchesNull);
            Assert.Equal("System.Int64 C.Widened(System.Int32 x)", comp.GetMember<MethodSymbol>("C.Widened").ToTestDisplayString());
        }

        [Fact]
        public void ReturnRefinement_BlockBodies()
        {
            // Every 'return' of a block body adds to the union; one return with no refinement leaves the method with none.
            var source = """
                class C
                {
                    static infer Classify(int n)
                    {
                        if (n < 0) return "negative";
                        if (n == 0) return "zero";
                        return "positive";
                    }

                    static string Classify_CSharp(int n)
                    {
                        if (n < 0) return "negative";
                        if (n == 0) return "zero";
                        return "positive";
                    }

                    static infer Widens(int n, int i is >=10)
                    {
                        if (n < 0) return -1;
                        if (n == 0) return 12;
                        return i;
                    }

                    static infer OneUnknown(int n)
                    {
                        if (n < 0) return -1;
                        return n;
                    }

                    static infer Lambdas(int n)
                    {
                        System.Func<int> f = () => n;
                        int local() => n;
                        return f() + local() > 0 ? 1 : 2;
                    }

                    static infer NullOrText(bool b)
                    {
                        if (b) return null;
                        return "x";
                    }

                    static infer Nothing() { }

                    static infer Loop(int[] xs)
                    {
                        foreach (var x in xs)
                        {
                            if (x > 0) return 1;
                        }

                        try { return 2; } finally { }
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "Classify: \"negative\" or \"positive\" or \"zero\"",
                    "Classify_CSharp: <none>",
                    "Widens: -1 or >= 10",
                    "OneUnknown: <none>",
                    "Lambdas: 1 or 2",
                    "NullOrText: null or \"x\"",
                    "Nothing: <none>",
                    "Loop: 1 or 2",
                },
                ReturnRefinements(comp));
        }

        [Fact]
        public void ReturnRefinement_None()
        {
            // No refinement: a value that has none, a type with no value set, a union that allows every value, a union
            // that is too large, an iterator, an 'async' method, and a member whose callers do not know which body runs.
            var many = string.Join(", ", Enumerable.Range(0, SharperRefinement.MaxInferredAlternatives + 1).Select(i => $"{i} => {i * 2}"));
            var atLimit = string.Join(", ", Enumerable.Range(0, SharperRefinement.MaxInferredAlternatives - 1).Select(i => $"{i} => {i * 2}"));
            var source = $$"""
                using System.Threading.Tasks;

                class C
                {
                    static int s_field = 1;
                    static int CSharp() => 1;

                    static infer Arithmetic(int i is >=0) => i + 1;
                    static infer Field() => s_field;
                    static infer CSharpCall() => CSharp();
                    static infer Local() { var x = 1; return x; }
                    static infer Object() => new object();
                    static infer NullableInt(bool b) => b ? 1 : (int?)null;
                    static infer Tuple() => (1, 2);
                    static infer Everything(bool b) => b ? true : false;
                    static infer NotNullOrNull(string s is not null, bool b) { if (b) return null; return s; }
                    static infer TooMany(int x) => x switch { {{many}}, _ => -1 };
                    static infer AtLimit(int x) => x switch { {{atLimit}}, _ => -1 };
                    static infer Iterator() { yield return 1; yield return 2; }
                    static async infer Async() { await Task.Yield(); return 1; }
                    static async infer AsyncExpression() => 1;
                    public virtual infer Virtual() => 1;
                    static infer Default() => default(int);
                    static infer Written(int i is >0) => i;
                    static int WrittenType(int i is >0) => i;
                    static infer Void() => System.Console.WriteLine();
                }

                class D : C
                {
                    public override infer Virtual() => 2;
                }

                interface I
                {
                    infer DefaultImplementation() => 1;
                    static infer Static() => 1;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var refinements = ReturnRefinements(comp);
            Assert.StartsWith("AtLimit: -1 or 0 or 2 or 4 or ", refinements.Single(r => r.StartsWith("AtLimit")));
            AssertEx.Equal(new[]
                {
                    "CSharp: <none>",
                    "Arithmetic: <none>",
                    "Field: <none>",
                    "CSharpCall: <none>",
                    "Local: <none>",
                    "Object: <none>",
                    "NullableInt: <none>",
                    "Tuple: <none>",
                    "Everything: <none>",
                    "NotNullOrNull: <none>",
                    "TooMany: <none>",
                    "Iterator: <none>",
                    "Async: <none>",
                    "AsyncExpression: <none>",
                    "Virtual: <none>",
                    "Default: 0",
                    "Written: > 0",
                    "WrittenType: <none>",
                    "Void: <none>",
                },
                refinements.Where(r => !r.StartsWith("AtLimit")));
            AssertEx.Equal(new[] { "Virtual: <none>" }, ReturnRefinements(comp, "D"));
            AssertEx.Equal(new[] { "DefaultImplementation: <none>", "Static: <none>" }, ReturnRefinements(comp, "I"));
        }

        [Fact]
        public void ReturnRefinement_ValLocals()
        {
            // 'val y = e' keeps the refinement of 'e'; 'var' and typed locals have none, since they can be assigned or
            // are C#. A 'val' returned unchanged gives its refinement to the return.
            var source = """
                class C
                {
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };

                    static infer M(int p is >=0, int q)
                    {
                        val constant = 3;
                        var csharp = 3;
                        int typed = 3;
                        val parameter = p;
                        val plain = q;
                        val call = Sign(q);
                        val copy = call;
                        val chosen = q > 0 ? constant : parameter;
                        val text = q switch { 0 => "none", _ => "some" };
                        val sum = constant + 1;
                        val fromVar = csharp;
                        if (q == 0) return copy;
                        return chosen;
                    }

                    static infer N()
                    {
                        val a = 1;
                        val b = a;
                        return b;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics(
                // (9,13): warning CS0219: The variable 'typed' is assigned but its value is never used
                //         int typed = 3;
                Diagnostic(ErrorCode.WRN_UnreferencedVarAssg, "typed").WithArguments("typed").WithLocation(9, 13));
            AssertEx.Equal(new[]
                {
                    "constant: 3",
                    "csharp: <none>",
                    "typed: <none>",
                    "parameter: >= 0",
                    "plain: <none>",
                    "call: -1 or 0 or 1",
                    "copy: -1 or 0 or 1",
                    "chosen: >= 0",
                    "text: \"none\" or \"some\"",
                    "sum: <none>",
                    "fromVar: <none>",
                    "a: 1",
                    "b: 1",
                },
                LocalRefinements(comp));
            AssertEx.Equal(new[] { "Sign: -1 or 0 or 1", "M: -1 or >= 0", "N: 1" }, ReturnRefinements(comp));
        }

        [Fact]
        public void ReturnRefinement_LocalFunctions()
        {
            var source = """
                class C
                {
                    static infer M(int p is >=0, int q)
                    {
                        infer sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                        int sign_CSharp(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                        static infer two() { return 2; }
                        infer captured() => p;
                        infer open() => q;
                        val s = sign(q);
                        val c = sign_CSharp(q);
                        val k = captured();
                        _ = open() + c + k + two();
                        return s;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "sign(): -1 or 0 or 1",
                    "sign_CSharp(): <none>",
                    "two(): 2",
                    "captured(): >= 0",
                    "open(): <none>",
                    "s: -1 or 0 or 1",
                    "c: <none>",
                    "k: >= 0",
                },
                LocalRefinements(comp));
            AssertEx.Equal(new[] { "M: -1 or 0 or 1" }, ReturnRefinements(comp));

            // A local function's closure method carries no attribute, and no attribute type is embedded for it alone.
            var localOnly = """
                class C
                {
                    static int M()
                    {
                        infer one() => 1;
                        return one();
                    }
                }
                """;
            CompileAndVerify(localOnly, parseOptions: s_sharperOptions, symbolValidator: module =>
                Assert.Null(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper"))).VerifyDiagnostics();
        }

        [Fact]
        public void ReturnRefinement_Recursion()
        {
            // A recursive 'infer' return is an error, as R1 decided for the type (CS20018, CS20033 for a cycle); the
            // method then has no refinement. With a written type the method is C#'s and has no refinement to infer.
            var source = """
                class C
                {
                    static infer Fact(int n) => n switch { 0 => 1, _ => Fact(n - 1) };
                    static int Fact_CSharp(int n) => n switch { 0 => 1, _ => Fact_CSharp(n - 1) };
                    static infer Even(int n) => n switch { 0 => 1, _ => Odd(n - 1) };
                    static infer Odd(int n) => n switch { 0 => 0, _ => Even(n - 1) };
                    static infer UsesCycle() => Even(2);
                    static infer Fine() => Fact_CSharp(3) > 0 ? 1 : 2;

                    static infer Val()
                    {
                        val v = Val();
                        return 1;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics(
                // (3,12): error CS20018: The return type of 'C.Fact(int)' cannot be inferred because the method uses itself. Specify the return type.
                //     static infer Fact(int n) => n switch { 0 => 1, _ => Fact(n - 1) };
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("C.Fact(int)").WithLocation(3, 12),
                // (5,12): error CS20033: The return type of 'C.Even(int)' cannot be inferred because of a cycle: 'C.Even(int)' uses 'C.Odd(int)', which uses 'C.Even(int)'. Specify one of the return types.
                //     static infer Even(int n) => n switch { 0 => 1, _ => Odd(n - 1) };
                Diagnostic(ErrorCode.ERR_SharperInferCycle, "infer").WithArguments("C.Even(int)", "'C.Even(int)' uses 'C.Odd(int)', which uses 'C.Even(int)'").WithLocation(5, 12),
                // (6,12): error CS20033: The return type of 'C.Odd(int)' cannot be inferred because of a cycle: 'C.Odd(int)' uses 'C.Even(int)', which uses 'C.Odd(int)'. Specify one of the return types.
                //     static infer Odd(int n) => n switch { 0 => 0, _ => Even(n - 1) };
                Diagnostic(ErrorCode.ERR_SharperInferCycle, "infer").WithArguments("C.Odd(int)", "'C.Odd(int)' uses 'C.Even(int)', which uses 'C.Odd(int)'").WithLocation(6, 12),
                // (10,12): error CS20018: The return type of 'C.Val()' cannot be inferred because the method uses itself. Specify the return type.
                //     static infer Val()
                Diagnostic(ErrorCode.ERR_SharperInferUsesItself, "infer").WithArguments("C.Val()").WithLocation(10, 12));
            AssertEx.Equal(new[]
                {
                    "Fact: <none>",
                    "Fact_CSharp: <none>",
                    "Even: <none>",
                    "Odd: <none>",
                    "UsesCycle: <none>",
                    "Fine: 1 or 2",
                    "Val: <none>",
                },
                ReturnRefinements(comp));
        }

        [Fact]
        public void ReturnRefinement_OrderAndThreads()
        {
            // The refinement is inferred with the type, from one binding of the body, so it does not depend on which
            // method is read first, or on which thread.
            var source = """
                class C
                {
                    static infer A(int x) => x switch { 0 => B(x), _ => 10 };
                    static infer B(int x) => x switch { 0 => C1(x), _ => 20 };
                    static infer C1(int x) => x switch { 0 => 30, _ => 40 };
                    static infer D(int x) { val a = A(x); return a; }
                }
                """;
            var expected = new[] { "A: 10 or 20 or 30 or 40", "B: 20 or 30 or 40", "C1: 30 or 40", "D: 10 or 20 or 30 or 40" };
            foreach (var first in new[] { "A", "B", "C1", "D" })
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
                Assert.NotNull(comp.GetMember<MethodSymbol>("C." + first).SharperReturnRefinement);
                AssertEx.Equal(expected, ReturnRefinements(comp));
                comp.VerifyDiagnostics();
            }

            for (int i = 0; i < 20; i++)
            {
                var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
                var results = new string[8][];
                Parallel.For(0, results.Length, t =>
                {
                    _ = comp.GetMember<MethodSymbol>("C." + new[] { "A", "B", "C1", "D" }[t % 4]).SharperReturnRefinement;
                    results[t] = ReturnRefinements(comp);
                });
                foreach (var result in results)
                {
                    AssertEx.Equal(expected, result);
                }
            }
        }

        [Fact]
        public void ReturnRefinement_Members()
        {
            // The refinement goes where the inferred type goes: every accessibility, partial methods (callers bind to
            // the definition), operators, extension members, and methods of generic types.
            var source = """
                public enum Level { Low, High }

                public partial class C
                {
                    public static infer Public() => 1;
                    internal static infer Internal() => 2;
                    protected infer Protected() => 3;
                    public static partial infer Partial(int x);
                    public static partial infer Partial(int x) => x switch { 0 => "a", _ => "b" };
                    public static infer operator -(C c) => 0;
                    public static explicit operator infer(C c) => "c";
                }

                public class G<T>
                {
                    public enum Kind { A, B }
                    public infer Count(T t) => 1;
                    public infer Which(T t) => Kind.B;
                    public infer Generic<U>(U u) => "g";
                    public infer Open(T t) => t;
                }

                public static class E
                {
                    public static infer Classic(this string s) => 7;
                    extension(string s)
                    {
                        public infer Block() => 8;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[]
                {
                    "Public: 1",
                    "Internal: 2",
                    "Protected: 3",
                    "Partial: \"a\" or \"b\"",
                    "op_UnaryNegation: 0",
                    "op_Explicit: \"c\"",
                },
                ReturnRefinements(comp));
            Assert.Equal("\"a\" or \"b\"", comp.GetMember<MethodSymbol>("C.Partial").PartialImplementationPart!.SharperReturnRefinement!.Text);
            AssertEx.Equal(new[] { "Count: 1", "Which: Kind.B", "Generic: \"g\"", "Open: <none>" }, ReturnRefinements(comp, "G"));

            // A constructed method keeps the refinement of its definition.
            var g = comp.GetMember<NamedTypeSymbol>("G").Construct(comp.GetSpecialType(SpecialType.System_Int32));
            Assert.Equal("1", g.GetMember<MethodSymbol>("Count").SharperReturnRefinement!.Text);
            Assert.Equal("Kind.B", g.GetMember<MethodSymbol>("Which").SharperReturnRefinement!.Text);
            Assert.Equal("\"g\"", g.GetMember<MethodSymbol>("Generic").Construct(comp.GetSpecialType(SpecialType.System_Byte)).SharperReturnRefinement!.Text);

            Assert.Equal("7", comp.GetMember<MethodSymbol>("E.Classic").SharperReturnRefinement!.Text);
        }

        [Fact]
        public void ReturnRefinementMetadata_Attribute()
        {
            // The return carries the embedded Sharper.SharperRefinementAttribute, on every accessibility, as a refined
            // parameter does. A return that could have a refinement and has none carries the discard pattern, so that
            // a body edit never adds or removes the attribute (Edit and Continue cannot); other returns carry nothing.
            var source = """
                public class C
                {
                    public static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    public static int Sign_CSharp(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    private static infer Private(int i is >=0) => i;
                    public static infer Text(bool b) => b ? "a\"b" : null;
                    public static infer Plain(int x) => x;
                    public virtual infer Virtual() => 1;
                    public static infer Object() => new object();
                    public static async infer Async() => 1;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, options: TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.All));
            CompileAndVerify(comp, symbolValidator: module =>
            {
                var attributeType = module.GlobalNamespace.GetMember<NamedTypeSymbol>("Sharper.SharperRefinementAttribute");
                Assert.Equal(Accessibility.Internal, attributeType.DeclaredAccessibility);
                var type = module.GlobalNamespace.GetMember<NamedTypeSymbol>("C");
                AssertEx.Equal(new[]
                    {
                        "Sign: Sharper.SharperRefinementAttribute(\"-1 or 0 or 1\")",
                        "Sign_CSharp: ",
                        "Private: Sharper.SharperRefinementAttribute(\">= 0\")",
                        "Text: Sharper.SharperRefinementAttribute(\"null or \\\"a\\\\\\\"b\\\"\")",
                        "Plain: Sharper.SharperRefinementAttribute(\"_\")",
                        "Virtual: ",
                        "Object: ",
                        "Async: ",
                    },
                    type.GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary)
                        .Select(m => m.Name + ": " + string.Join(", ", m.GetReturnTypeAttributes().Select(a => a.ToString()))));
                Assert.Empty(type.GetMember<MethodSymbol>("Sign").GetAttributes());
            }).VerifyDiagnostics();

            // Only a return refinement: the attribute type is embedded for it.
            CompileAndVerify("class C { static infer One() => 1; }", parseOptions: s_sharperOptions, symbolValidator: module =>
                Assert.NotNull(module.GlobalNamespace.GetMember<NamedTypeSymbol>("Sharper.SharperRefinementAttribute"))).VerifyDiagnostics();

            // No return that can have a refinement, no attribute type.
            CompileAndVerify("class C { static infer M(object x) => x; static async infer N() => 1; static int P(int x) => x; }", parseOptions: s_sharperOptions, symbolValidator: module =>
                Assert.Null(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper"))).VerifyDiagnostics();
        }

        [Fact]
        public void ReturnRefinementMetadata_RoundTrip()
        {
            // A Sharper compilation that references the assembly reads the return refinement back, with the same value
            // set and null rule, and infers with it; a C# compilation sees the plain type.
            var library = """
                public enum Color { Red, Green, Blue }

                public class C
                {
                    public static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    public static infer Text(bool b) => b ? "a\"b" : null;
                    public static infer Range(int i is >=0 and <10, int x) => x switch { 0 => 42, _ => i };
                    public static infer Tone(bool b) => b ? Color.Red : Color.Blue;
                    public static infer Ratio(bool b) => b ? 0.5 : double.NaN;
                    public infer Instance() => 'c';
                    public static infer operator -(C c) => 0L;
                    public static infer Plain(int x) => x;
                    public static int CSharp(int x) => 1;
                    public virtual infer Virtual() => 1;
                }

                public class G<T>
                {
                    public infer Count(T t) => 1;
                }
                """;
            var libraryComp = CreateCompilation(library, parseOptions: s_sharperOptions);
            libraryComp.VerifyDiagnostics();
            var expected = new[]
            {
                "Sign: -1 or 0 or 1",
                "Text: null or \"a\\\"b\"",
                "Range: 42 or >= 0 and < 10",
                "Tone: Color.Red or Color.Blue",
                "Ratio: 0.5 or double.NaN",
                "Instance: 'c'",
                "op_UnaryNegation: 0",
                "Plain: <none>",
                "CSharp: <none>",
                "Virtual: <none>",
            };
            AssertEx.Equal(expected, ReturnRefinements(libraryComp));

            var consumer = """
                class D
                {
                    static infer Forward(int x) => C.Sign(x);
                    static infer Widen(int x) => x switch { 0 => C.Sign(x), _ => 5 };
                    static infer Generic(G<string> g) => g.Count("");

                    static void M(int x)
                    {
                        val s = C.Sign(x);
                        val t = C.Text(x > 0);
                        val p = C.Plain(x);
                    }
                }
                """;
            foreach (var reference in new[] { libraryComp.EmitToImageReference(), libraryComp.ToMetadataReference() })
            {
                var comp = CreateCompilation(consumer, references: [reference], parseOptions: s_sharperOptions);
                comp.VerifyDiagnostics();
                AssertEx.Equal(expected, ReturnRefinements(comp));
                foreach (var name in new[] { "Sign", "Text", "Range", "Tone", "Ratio", "Instance" })
                {
                    var source = libraryComp.GetMember<MethodSymbol>("C." + name).SharperReturnRefinement!;
                    var imported = comp.GetMember<MethodSymbol>("C." + name).SharperReturnRefinement!;
                    Assert.Equal(source.Values!.ToString(), imported.Values!.ToString());
                    Assert.Equal(source.MatchesNull, imported.MatchesNull);
                }

                AssertEx.Equal(new[] { "Forward: -1 or 0 or 1", "Widen: -1 or 0 or 1 or 5", "Generic: 1", "M: <none>" }, ReturnRefinements(comp, "D"));
                AssertEx.Equal(new[] { "s: -1 or 0 or 1", "t: null or \"a\\\"b\"", "p: <none>" }, LocalRefinements(comp));
            }

            // A C# compilation sees the plain return type and an attribute it cannot name.
            var csharp = CreateCompilation("class D { static int M() => C.Sign(1) + C.Range(1, 2); }", references: [libraryComp.EmitToImageReference()]);
            csharp.VerifyDiagnostics();
            Assert.Equal("System.Int32 C.Sign(System.Int32 x)", csharp.GetMember<MethodSymbol>("C.Sign").ToTestDisplayString());
        }

        [Fact]
        public void ReturnRefinementMetadata_Untrusted()
        {
            // Text that does not parse or does not fit the type, a virtual member and a 'ref' return give no refinement.
            var il = """
                .class private auto ansi sealed beforefieldinit Sharper.SharperRefinementAttribute extends [mscorlib]System.Attribute
                {
                    .method public hidebysig specialname rtspecialname instance void .ctor(string pattern) cil managed
                    {
                        ldarg.0
                        call instance void [mscorlib]System.Attribute::.ctor()
                        ret
                    }
                }

                .class public auto ansi beforefieldinit C extends [mscorlib]System.Object
                {
                    .method public hidebysig static int32 Good() cil managed
                    {
                        .param [0]
                        .custom instance void Sharper.SharperRefinementAttribute::.ctor(string) = { string('1 or 2') }
                        ldc.i4.1
                        ret
                    }

                    .method public hidebysig static int32 Garbage() cil managed
                    {
                        .param [0]
                        .custom instance void Sharper.SharperRefinementAttribute::.ctor(string) = { string('1 or or') }
                        ldc.i4.1
                        ret
                    }

                    .method public hidebysig static int32 WrongType() cil managed
                    {
                        .param [0]
                        .custom instance void Sharper.SharperRefinementAttribute::.ctor(string) = { string('"a"') }
                        ldc.i4.1
                        ret
                    }

                    .method public hidebysig newslot virtual instance int32 Virtual() cil managed
                    {
                        .param [0]
                        .custom instance void Sharper.SharperRefinementAttribute::.ctor(string) = { string('1') }
                        ldc.i4.1
                        ret
                    }

                    .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
                    {
                        ldarg.0
                        call instance void [mscorlib]System.Object::.ctor()
                        ret
                    }
                }
                """;
            var comp = CreateCompilationWithIL("class D { static infer F() => C.Good(); static infer G() => C.Garbage(); }", il, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            AssertEx.Equal(new[] { "Good: 1 or 2", "Garbage: <none>", "WrongType: <none>", "Virtual: <none>" }, ReturnRefinements(comp));
            AssertEx.Equal(new[] { "F: 1 or 2", "G: <none>" }, ReturnRefinements(comp, "D"));
        }

        [Fact]
        public void ReturnRefinement_Runtime()
        {
            // A return refinement changes nothing at run time: the program prints what its C# twin prints.
            var body = """
                    static void Main()
                    {
                        foreach (var x in new[] { -3, 0, 4 })
                        {
                            System.Console.Write(Sign(x) + " " + Describe(x) + " " + Local(x) + "; ");
                        }
                    }
                """;
            var sharper = """
                class C
                {
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    static infer Describe(int x) { val s = Sign(x); if (s == 0) return "zero"; return s > 0 ? "plus" : "minus"; }
                    static infer Local(int x) { infer twice() => 2; val t = twice(); return t; }
                """ + "\n" + body + "}";
            var csharp = """
                class C
                {
                    static int Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    static string Describe(int x) { var s = Sign(x); if (s == 0) return "zero"; return s > 0 ? "plus" : "minus"; }
                    static int Local(int x) { int twice() => 2; var t = twice(); return t; }
                """ + "\n" + body + "}";
            const string expectedOutput = "-1 minus 2; 0 zero 2; 1 plus 2;";
            CompileAndVerify(sharper, parseOptions: s_sharperOptions, expectedOutput: expectedOutput).VerifyDiagnostics();
            CompileAndVerify(csharp, expectedOutput: expectedOutput).VerifyDiagnostics();
        }

        [Fact]
        public void ReturnRefinement_FlagOff()
        {
            // With the flag off, 'infer' and 'val' are not keywords, and C# methods and locals have no refinement.
            var source = """
                class C
                {
                    static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };

                    static int M()
                    {
                        val y = 3;
                        var z = 3;
                        return y + z;
                    }
                }
                """;
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics(
                // (3,12): error CS0246: The type or namespace name 'infer' could not be found (are you missing a using directive or an assembly reference?)
                //     static infer Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "infer").WithArguments("infer").WithLocation(3, 12),
                // (7,9): error CS0246: The type or namespace name 'val' could not be found (are you missing a using directive or an assembly reference?)
                //         val y = 3;
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "val").WithArguments("val").WithLocation(7, 9));
            AssertEx.Equal(new[] { "Sign: <none>", "M: <none>" }, ReturnRefinements(comp));
            AssertEx.Equal(new[] { "y: <none>", "z: <none>" }, LocalRefinements(comp));

            // The C# twin compiles as before, and embeds nothing.
            var twin = """
                class C
                {
                    static int Sign(int x) => x switch { >0 => 1, 0 => 0, _ => -1 };
                    static int M() { var y = 3; return y; }
                }
                """;
            foreach (var options in new[] { TestOptions.Regular, s_sharperOptions })
            {
                var twinComp = CreateCompilation(twin, parseOptions: options);
                CompileAndVerify(twinComp, symbolValidator: module =>
                    Assert.Null(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper"))).VerifyDiagnostics();
                AssertEx.Equal(new[] { "Sign: <none>", "M: <none>" }, ReturnRefinements(twinComp));
                AssertEx.Equal(new[] { "y: <none>" }, LocalRefinements(twinComp));
            }
        }
    }
}
