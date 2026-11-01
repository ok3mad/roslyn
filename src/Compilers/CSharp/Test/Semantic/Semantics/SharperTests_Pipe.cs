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
    /// Sharper's pipe without lifting (issue #25): <c>x |&gt; F</c> is <c>F(x)</c>, and <c>x |&gt; F(it, y)</c> is
    /// <c>F(x, y)</c>.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void Pipe_Basic()
        {
            var source = """
                using System;

                record Order(string Id, int Qty);

                class C
                {
                    static Order ParseOrder(string raw) => new Order(raw.Split(':')[0], int.Parse(raw.Split(':')[1]));
                    static string Resolve(Order o, string accounts) => $"{o.Id}x{o.Qty}@{accounts}";

                    static string Process(string raw, string accounts) =>
                        raw |> ParseOrder
                            |> Resolve(it, accounts);

                    // The C# it stands for.
                    static string ProcessCSharp(string raw, string accounts) =>
                        Resolve(ParseOrder(raw), accounts);

                    static void Main()
                    {
                        Console.Write(Process("a:2", "acc"));
                        Console.Write(" ");
                        Console.Write(ProcessCSharp("a:2", "acc"));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "ax2@acc ax2@acc" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_BareStages()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                static class C
                {
                    static string F(int x) => "int " + x;
                    static string F(string x) => "string " + x;
                    static T Id<T>(T x) => x;
                    static Func<int, int> Add(int y) => x => x + y;
                    static int Twice(this int x) => 2 * x;

                    static void Main()
                    {
                        Func<int, int> square = x => x * x;
                        var list = new List<int>();

                        Console.Write((1 |> F) + ", " + F(1));                       // overloads
                        Console.Write(" | " + ("a" |> F) + ", " + F("a"));
                        Console.Write(" | " + (3 |> square) + ", " + square(3));      // a delegate value
                        Console.Write(" | " + (3 |> Add(4)) + ", " + Add(4)(3));      // a call that returns a delegate
                        Console.Write(" | " + (5 |> Id<long>) + ", " + Id<long>(5));  // explicit type arguments
                        Console.Write(" | " + (5 |> Id) + ", " + Id(5));              // inferred type arguments
                        Console.Write(" | " + (5 |> C.Twice) + ", " + C.Twice(5));    // an extension method, called as static
                        7 |> list.Add;                                               // a statement
                        list.Add(7);
                        Console.Write(" | " + string.Join(",", list));
                        Console.Write(" | " + (new[] { 1, 2, 3 } |> Enumerable.Sum) + ", " + Enumerable.Sum(new[] { 1, 2, 3 }));
                        Console.Write(" | " + (new[] { 1, 2, 3 } |> it.Sum()) + ", " + new[] { 1, 2, 3 }.Sum());
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "int 1, int 1 | string a, string a | 9, 9 | 7, 7 | 5, 5 | 5, 5 | 10, 10 | 7,7 | 6, 6 | 6, 6" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_ItStages()
        {
            var source = """
                using System;
                using System.Linq;

                class C
                {
                    static string Render(int x, Func<int, string> load) => $"{x}:{load(x)}";

                    static void Main()
                    {
                        var xs = new[] { 1, 2, 3 };
                        Console.Write(2 |> it * it + 1);
                        Console.Write(" " + (xs |> it.Length + it[0]));
                        Console.Write(" " + ("ab" |> $"<{it}>"));
                        Console.Write(" " + (2 |> it |> it * 10));                          // stage 2 sees stage 1's result
                        Console.Write(" " + (2 |> Render(it, n => (n + it).ToString())));    // 'it' inside an explicit lambda
                        Console.Write(" " + (2 |> xs.Where(x => x > it).Count()));
                        Console.Write(" " + (2 |> it switch { 1 => "one", 2 => "two", _ => "many" }));
                        Console.Write(" " + string.Join(",", xs.Select(it |> it * 10)));      // an implicit lambda around a pipe
                        Console.Write(" " + (3 |> (2 |> it * 100) + it));                    // the inner stage's 'it' is 2
                        // A variable declared in a stage inside an implicit lambda belongs to the stage.
                        Console.Write(" " + string.Join(",", xs.Select(it * 2 |> (it > 2 && it is var big ? big : 0))));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "5 4 <ab> 20 2:4 1 two 10,20,30 203 0,4,6" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_EvaluationOrder()
        {
            // The left side is evaluated once, before the stage, including a bare stage's receiver.
            var source = """
                using System;

                class C
                {
                    static int Log(int x) { Console.Write($"left{x} "); return x; }
                    static C Receiver() { Console.Write("receiver "); return new C(); }
                    int M(int x) { Console.Write($"M{x} "); return x; }
                    static Func<int, int> Function() { Console.Write("function "); return x => x; }

                    static void Main()
                    {
                        _ = Log(1) |> Receiver().M;
                        _ = Log(2) |> Function();
                        _ = Log(3) |> it + it + it;
                        Console.Write("| ");
                        _ = Receiver().M(Log(4));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "left1 receiver M1 left2 function left3 | receiver left4 M4 " : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_Precedence()
        {
            var source = """
                using System;

                class C
                {
                    static string S(int x) => $"S{x}";
                    static string N(string s) => $"N{s}";
                    static bool Even(int x) => x % 2 == 0;

                    static void Main()
                    {
                        string n = null;
                        Console.Write(1 + 2 |> S);                              // (1 + 2) |> S
                        Console.Write(" " + (n ?? "d" |> N));                    // (n ?? "d") |> N
                        Console.Write(" " + (2 |> Even ? "even" : "odd"));       // (2 |> Even) ? ... : ...
                        Console.Write(" " + (false ? "a" : 4 |> S));            // false ? "a" : (4 |> S)
                        Console.Write(" " + (5 |> it > 3 && it < 9));            // the stage is 'it > 3 && it < 9'
                        Console.Write(" " + (n |> it ?? "none"));                // the stage is 'it ?? "none"'
                        string r = 6 |> S |> N;                                  // (6 |> S) |> N
                        Console.Write(" " + r);
                        Func<int, string> f = x => x |> S;                       // the lambda body is 'x |> S'
                        Console.Write(" " + f(7));
                        Console.Write(" " + (8 |> S).Length);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "S3 Nd even S4 True none NS6 S7 2" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_Await()
        {
            var source = """
                using System;
                using System.Threading.Tasks;

                class C
                {
                    static async Task<int> Double(int x) { await Task.Yield(); return 2 * x; }
                    static async Task<string> Show(int x) { await Task.Yield(); return $"<{x}>"; }

                    static async Task Main()
                    {
                        // No lifting: a stage that takes an int gets an int, so 'await' is written.
                        Console.Write(await (3 |> Double));
                        Console.Write(" " + (3 |> await Double(it)));
                        Console.Write(" " + await (3 |> Double |> await it |> Show));
                        Console.Write(" " + (await Double(1) |> it + await Double(it)));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "6 6 <6> 6" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_ImplicitIt()
        {
            // The innermost function argument claims an 'it', as anywhere else: a stage is the outermost slot.
            var source = """
                using System;
                                using System.Linq;

                record User(int Id, bool Active);

                class C
                {
                    static string Render(User u, Func<int, string> load) => load(u.Id);
                    static string Load(int id) => $"profile{id}";

                    static void Main()
                    {
                        var users = new[] { new User(1, true), new User(2, false) };
                        // An explicit lambda never supplies 'it': 'it.Id' is the stage's user.
                        Console.Write(users[0] |> Render(it, x => Load(it.Id)));
                        // A pipe inside an implicit lambda: the stage owns its 'it', so no signature is needed to tell the two apart.
                        Console.Write(" " + string.Join(",", users.Select(it.Id |> it * 10)));
                        // The stage's 'it' as a receiver, next to an explicit lambda.
                        Console.Write(" " + (users |> it.Count(u => u.Active)));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "profile1 10,20 1" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_Errors()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                class C
                {
                    static void V() { }
                    static int F(int x) => x;
                    static int G(Func<int, int> f) => f(1);

                    void M(int[] xs, string s, IEnumerable<int[]> ys)
                    {
                        _ = V() |> F;                       // 1: no value
                        _ = null |> F;                      // 2: no type
                        _ = 1 |> s.Length;                  // 3: not a function
                        _ = 1 |> xs.Where(it > 0);          // 4: 'it' belongs to Where
                        _ = xs |> it.Select(it * 2);        // 5: two meanings
                        _ = 1 |> F(it, 2);                  // 6: an ordinary error
                        _ = "a" |> F;                       // 7: an ordinary error, at the left side
                        _ = 1 |> (it = 2);                  // 8: read-only
                        1 |> it + 1;                        // 9: not a statement
                        _ = 1 |> (y => y);                  // 10: a lambda has no type
                        _ = xs |> G(F(it));                 // 11: G takes a function, and the innermost argument claims 'it'
                        System.Linq.Expressions.Expression<Func<int, int>> e = x => x |> F;  // 12: expression tree
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (13,13): error CS20043: The left side of '|>' must be a value with a type, but it is void.
                //         _ = V() |> F;                       // 1: no value
                Diagnostic(ErrorCode.ERR_SharperPipeNoValue, "V()").WithArguments("void").WithLocation(13, 13),
                // (14,13): error CS20043: The left side of '|>' must be a value with a type, but it is <null>.
                //         _ = null |> F;                      // 2: no type
                Diagnostic(ErrorCode.ERR_SharperPipeNoValue, "null").WithArguments("<null>").WithLocation(14, 13),
                // (15,18): error CS20045: The stage after '|>' must be a function, such as a method group or a delegate, or an expression that uses 'it', but it is int.
                //         _ = 1 |> s.Length;                  // 3: not a function
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "s.Length").WithArguments("int").WithLocation(15, 18),
                // (16,27): error CS20044: This stage does not use the value piped into it: its 'it' belongs to an inner lambda, the argument of 'xs.Where'. Name that lambda's parameter, as in 'x => ...', or use the piped 'it' outside it.
                //         _ = 1 |> xs.Where(it > 0);          // 4: 'it' belongs to Where
                Diagnostic(ErrorCode.ERR_SharperPipeStageItNotUsed, "it").WithArguments("xs.Where").WithLocation(16, 27),
                // (17,29): error CS20020: 'it' has two meanings here: the parameter of an outer lambda and of this inner one. Name one of them, as in 'x => ...'.
                //         _ = xs |> it.Select(it * 2);        // 5: two meanings
                Diagnostic(ErrorCode.ERR_SharperItTwoMeanings, "it").WithLocation(17, 29),
                // (18,18): error CS1501: No overload for method 'F' takes 2 arguments
                //         _ = 1 |> F(it, 2);                  // 6: an ordinary error
                Diagnostic(ErrorCode.ERR_BadArgCount, "F").WithArguments("F", "2").WithLocation(18, 18),
                // (19,13): error CS1503: Argument 1: cannot convert from 'string' to 'int'
                //         _ = "a" |> F;                       // 7: an ordinary error, at the left side
                Diagnostic(ErrorCode.ERR_BadArgType, @"""a""").WithArguments("1", "string", "int").WithLocation(19, 13),
                // (20,19): error CS1656: Cannot assign to 'it' because it is a 'val variable'
                //         _ = 1 |> (it = 2);                  // 8: read-only
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "it").WithArguments("it", "val variable").WithLocation(20, 19),
                // (21,9): error CS0201: Only assignment, call, increment, decrement, await, and new object expressions can be used as a statement
                //         1 |> it + 1;                        // 9: not a statement
                Diagnostic(ErrorCode.ERR_IllegalStatement, "1 |> it + 1").WithLocation(21, 9),
                // (22,18): error CS20045: The stage after '|>' must be a function, such as a method group or a delegate, or an expression that uses 'it', but it is lambda expression.
                //         _ = 1 |> (y => y);                  // 10: a lambda has no type
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "(y => y)").WithArguments("lambda expression").WithLocation(22, 18),
                // (23,23): error CS20044: This stage does not use the value piped into it: its 'it' belongs to an inner lambda, the argument of 'G'. Name that lambda's parameter, as in 'x => ...', or use the piped 'it' outside it.
                //         _ = xs |> G(F(it));                 // 11: G takes a function, and the innermost argument claims 'it'
                Diagnostic(ErrorCode.ERR_SharperPipeStageItNotUsed, "it").WithArguments("G").WithLocation(23, 23),
                // (24,69): error CS20046: An expression tree cannot contain a pipe '|>'.
                //         System.Linq.Expressions.Expression<Func<int, int>> e = x => x |> F;  // 12: expression tree
                Diagnostic(ErrorCode.ERR_SharperPipeInExpressionTree, "x |> F").WithLocation(24, 69));
        }

        [Fact]
        public void Pipe_StageScope()
        {
            var source = """
                using System;

                class C
                {
                    static bool TryHalf(int x, out int half) { half = x / 2; return x % 2 == 0; }

                    static void Main()
                    {
                        // A stage is its own scope, like a lambda body, so the conditional goes inside it.
                        var a = 8 |> (TryHalf(it, out var h) ? h : -1);
                        var b = 7 |> (TryHalf(it, out var h) ? h : -1);
                        var c = (object)"text" |> (it is string { Length: var n } ? n : 0);
                        Console.Write($"{a} {b} {c}");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "4 -1 4" : null).VerifyDiagnostics();

            var errors = """
                class C
                {
                    static bool TryHalf(int x, out int half) { half = x / 2; return x % 2 == 0; }

                    int M()
                    {
                        // '?:' is looser than '|>', so the stage ends before '?', and 'h' is out of scope after it.
                        return 8 |> TryHalf(it, out var h) ? h : -1;
                    }
                }
                """;
            CreateCompilation(errors, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,46): error CS0103: The name 'h' does not exist in the current context
                //         return 8 |> TryHalf(it, out var h) ? h : -1;
                Diagnostic(ErrorCode.ERR_NameNotInContext, "h").WithArguments("h").WithLocation(8, 46));
        }

        [Fact]
        public void Pipe_Nullable()
        {
            var source = """
                #nullable enable
                class C
                {
                    static int Length(string s) => s.Length;

                    void M(string? maybe, string sure)
                    {
                        _ = maybe |> Length;           // warning
                        _ = sure |> Length;
                        _ = maybe |> it.Length;        // warning
                        _ = maybe |> it?.Length;
                        if (maybe != null)
                        {
                            _ = maybe |> Length;
                            _ = maybe |> it.Length;
                        }

                        string s = maybe |> it;        // warning
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,13): warning CS8604: Possible null reference argument for parameter 's' in 'int C.Length(string s)'.
                //         _ = maybe |> Length;           // warning
                Diagnostic(ErrorCode.WRN_NullReferenceArgument, "maybe").WithArguments("s", "int C.Length(string s)").WithLocation(8, 13),
                // (10,22): warning CS8602: Dereference of a possibly null reference.
                //         _ = maybe |> it.Length;        // warning
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "it").WithLocation(10, 22),
                // (18,20): warning CS8600: Converting null literal or possible null value to non-nullable type.
                //         string s = maybe |> it;        // warning
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "maybe |> it").WithLocation(18, 20));
        }

        [Fact]
        public void Pipe_FlagOff()
        {
            var source = """
                class C
                {
                    static int F(int x) => x;
                    int M() => 1 |> F;
                }
                """;
            // C#'s error: no expression starts with '>'.
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (4,19): error CS1525: Invalid expression term '>'
                //     int M() => 1 |> F;
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, ">").WithArguments(">").WithLocation(4, 19));
        }

        [Fact]
        public void Pipe_Parsing()
        {
            var tree = SyntaxFactory.ParseSyntaxTree("_ = x |> F<int> |> G(it, a < b, c > d);", s_sharperOptions.WithKind(SourceCodeKind.Script));
            tree.GetDiagnostics().Verify();
            var pipe = tree.GetRoot().DescendantNodes().OfType<PipeExpressionSyntax>().First();
            Assert.Equal("x |> F<int> |> G(it, a < b, c > d)", pipe.ToString());
            Assert.Equal("x |> F<int>", ((PipeExpressionSyntax)pipe).Left.ToString());
            Assert.Equal(SyntaxKind.BarGreaterThanToken, pipe.OperatorToken.Kind());
            Assert.Equal("|>", SyntaxFacts.GetText(SyntaxKind.BarGreaterThanToken));
            Assert.Equal(8231, (int)SyntaxKind.BarGreaterThanToken);
            Assert.Equal(9904, (int)SyntaxKind.PipeExpression);
            Assert.Single(System.Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == 8231);
            Assert.Single(System.Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == 9904);
            Assert.True(SyntaxFacts.IsPunctuation(SyntaxKind.BarGreaterThanToken));

            // A space between | and > is two tokens, as for '>>'; C# has no meaning for either.
            var spaced = SyntaxFactory.ParseExpression("x | > F", options: s_sharperOptions);
            Assert.DoesNotContain(spaced.DescendantNodesAndSelf(), n => n.IsKind(SyntaxKind.PipeExpression));
            Assert.NotEmpty(spaced.GetDiagnostics());

            // With the flag off, '|>' keeps C#'s errors.
            var off = SyntaxFactory.ParseExpression("x |> F");
            Assert.DoesNotContain(off.DescendantNodesAndSelf(), n => n.IsKind(SyntaxKind.PipeExpression));
            Assert.NotEmpty(off.GetDiagnostics());

            // Round trip through the factory.
            var built = SyntaxFactory.PipeExpression(SyntaxFactory.IdentifierName("x"), SyntaxFactory.IdentifierName("F"));
            Assert.Equal("x|>F", built.ToFullString());
        }

        [Fact]
        public void Pipe_BatchExample()
        {
            // The brainstorm's batch, without lifting: each stage takes the value the previous one returns.
            var source = """
                using System;
                using System.Linq;

                record RawOrder(string Text);
                record Order(string From, string To, decimal Amount);
                record Resolved(Order Order, string Accounts);
                record Quoted(Resolved Resolved, decimal Rate);

                static class C
                {
                    static Order ParseOrder(RawOrder raw) => raw.Text.Split(',') is [var f, var t, var a] ? new Order(f, t, decimal.Parse(a)) : throw new FormatException();
                    static Resolved Resolve(Order o, string accounts) => new Resolved(o, accounts);
                    static Quoted Quote(Resolved r, decimal fx) => new Quoted(r, fx);
                    static string Validate(Quoted q) => $"{q.Resolved.Order.From}->{q.Resolved.Order.To}: {q.Resolved.Order.Amount * q.Rate} ({q.Resolved.Accounts})";

                    static string Process(RawOrder raw, string accounts, decimal fx) =>
                        raw |> ParseOrder
                            |> Resolve(it, accounts)
                            |> Quote(it, fx)
                            |> Validate;

                    // The C# it stands for.
                    static string ProcessCSharp(RawOrder raw, string accounts, decimal fx) =>
                        Validate(Quote(Resolve(ParseOrder(raw), accounts), fx));

                    static void Main()
                    {
                        var raws = new[] { new RawOrder("a,b,10"), new RawOrder("c,d,2") };
                        Console.WriteLine(string.Join("; ", raws.Select(Process(it, "acc", 1.5m))));
                        Console.WriteLine(string.Join("; ", raws.Select(r => ProcessCSharp(r, "acc", 1.5m))));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                    a->b: 15.0 (acc); c->d: 3.0 (acc)
                    a->b: 15.0 (acc); c->d: 3.0 (acc)
                    """ : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_ExistingIt()
        {
            // An 'it' that C# finds keeps its C# meaning, so the stage has no 'it' of its own and is a function.
            var source = """
                using System;

                class C
                {
                    static int F(int x) => x;
                    static int Pick(int x) => x;
                    static int Pick(Func<int, int> f) => f(0);

                    void M()
                    {
                        int it = 5;
                        _ = 1 |> F(it);         // F(5) is not a function
                        _ = 1 |> F(@it);
                    }

                    void N()
                    {
                        // A value or a function at an argument, with the stage's 'it' outside: an error, as for an outer implicit lambda.
                        _ = 1 |> Pick(it + 1);
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (12,18): error CS20045: The stage after '|>' must be a function, such as a method group or a delegate, or an expression that uses 'it', but it is int.
                //         _ = 1 |> F(it);         // F(5) is not a function
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "F(it)").WithArguments("int").WithLocation(12, 18),
                // (13,18): error CS20045: The stage after '|>' must be a function, such as a method group or a delegate, or an expression that uses 'it', but it is int.
                //         _ = 1 |> F(@it);
                Diagnostic(ErrorCode.ERR_SharperPipeStageNotFunction, "F(@it)").WithArguments("int").WithLocation(13, 18),
                // (19,23): error CS20021: 'it' is ambiguous here: 'Pick' takes a value or a function at this argument, and the outer 'it' fits both. Write the lambda, as in 'x => ...', or name the outer value.
                //         _ = 1 |> Pick(it + 1);
                Diagnostic(ErrorCode.ERR_SharperItValueOrFunction, "it").WithArguments("Pick").WithLocation(19, 23));
        }

        [Fact]
        public void Pipe_Dynamic()
        {
            var source = """
                using System;

                class C
                {
                    static string F(int x) => "int " + x;
                    static string F(string x) => "string " + x;

                    static void Main()
                    {
                        dynamic d = "s";
                        Console.Write(d |> F);                          // F(d), bound at runtime
                        dynamic g = (Func<int, int>)(x => x + 1);
                        Console.Write(" " + (1 |> g));                   // a dynamic delegate
                        Console.Write(" " + (d |> it.Length));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "string s 2 1" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_Captures()
        {
            // 'it' is a variable: lambdas capture it, also across 'await', and each evaluation of the pipe has its own.
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                class C
                {
                    static async Task<int> Later(Func<int> f) { await Task.Yield(); return f(); }

                    static async Task Main()
                    {
                        var fs = new List<Func<int>>();
                        for (int i = 0; i < 3; i++)
                        {
                            i * 10 |> fs.Add(() => it);
                        }

                        foreach (var f in fs) Console.Write(f() + " ");
                        Console.Write(await (7 |> Later(() => it * 2)));
                        Console.Write(" " + (3 |> it + await Later(() => it)));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0 10 20 14 6" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Pipe_RefStruct()
        {
            var source = """
                using System;

                class C
                {
                    static int Count(ReadOnlySpan<char> s) => s.Length;

                    static void Main()
                    {
                        ReadOnlySpan<char> s = "abc";
                        Console.Write(s |> Count);
                        Console.Write(" " + (s |> it.Slice(1) |> Count));
                    }

                    static Span<int> Escape()
                    {
                        Span<int> local = stackalloc int[2];
                        return local |> it;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (17,25): error CS8352: Cannot use variable 'it' in this context because it may expose referenced variables outside of their declaration scope
                //         return local |> it;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "it").WithArguments("it").WithLocation(17, 25));
        }

        [Fact]
        public void Pipe_DefiniteAssignment()
        {
            var source = """
                using System;

                class C
                {
                    static void Main()
                    {
                        int y;
                        _ = 4 |> (y = it * 2);
                        Console.Write(y);
                    }

                    static int M(bool b)
                    {
                        int z;
                        _ = b |> (it && (z = 1) > 0);
                        return z;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (16,16): error CS0165: Use of unassigned local variable 'z'
                //         return z;
                Diagnostic(ErrorCode.ERR_UseDefViolation, "z").WithArguments("z").WithLocation(16, 16));
        }

        [Fact]
        public void Pipe_SemanticModel()
        {
            var source = """
                class C
                {
                    static string Show(int x) => x.ToString();
                    object M(int a) => a + 1 |> Show |> it.Length;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var pipes = tree.GetRoot().DescendantNodes().OfType<PipeExpressionSyntax>().ToArray();
            var outer = pipes[0];
            var inner = (PipeExpressionSyntax)outer.Left;
            Assert.Equal("a + 1 |> Show", inner.ToString());

            // The bare stage is the call.
            Assert.Equal("System.String C.Show(System.Int32 x)", model.GetSymbolInfo(inner.Stage).Symbol.ToTestDisplayString());
            Assert.Equal("System.String", model.GetTypeInfo(inner).Type.ToTestDisplayString());
            Assert.Equal("System.Int32", model.GetTypeInfo(inner.Left).Type.ToTestDisplayString());

            // The stage's 'it' is a local of the left side's type.
            var it = outer.Stage.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.Text == "it");
            var local = (ILocalSymbol)model.GetSymbolInfo(it).Symbol!;
            Assert.Equal("System.String it", local.ToTestDisplayString());
            Assert.True(local.IsImplicitlyDeclared);
            Assert.Equal("System.Int32", model.GetTypeInfo(outer).Type.ToTestDisplayString());
            Assert.Equal("System.Object", model.GetTypeInfo(outer).ConvertedType.ToTestDisplayString());
            // PROTOTYPE(sharper): the semantic model finds scopes by syntax, so lookup inside a stage does not see 'it' yet (#156).
            Assert.DoesNotContain(model.LookupSymbols(it.SpanStart), s => s.Name == "it");

            var operation = model.GetOperation(outer)!;
            Assert.Equal(OperationKind.None, operation.Kind);
        }

        [Fact]
        public void Pipe_IOperation()
        {
            var source = """
                class C
                {
                    static int F(int x) => x;
                    void M(int a)
                    {
                        _ = /*<bind>*/a |> F/*</bind>*/;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            VerifyOperationTreeAndDiagnosticsForTest<PipeExpressionSyntax>(comp, """
                IOperation:  (OperationKind.None, Type: System.Int32) (Syntax: 'a |> F')
                  Children(1):
                      IParameterReferenceOperation: a (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'a')
                """, DiagnosticDescription.None);

            var flowSource = """
                class C
                {
                    static int F(int x) => x;
                    void M(int a)
                    /*<bind>*/{
                        _ = a |> F(it);
                    }/*</bind>*/
                }
                """;
            var flowComp = CreateCompilation(flowSource, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            // PROTOTYPE(sharper): the stage is not in the flow graph: the control flow graph has no region for its 'it' (#155).
            VerifyFlowGraphAndDiagnosticsForTest<BlockSyntax>(flowComp, """
                Block[B0] - Entry
                    Statements (0)
                    Next (Regular) Block[B1]
                Block[B1] - Block
                    Predecessors: [B0]
                    Statements (1)
                        IExpressionStatementOperation (OperationKind.ExpressionStatement, Type: null) (Syntax: '_ = a |> F(it);')
                          Expression:
                            ISimpleAssignmentOperation (OperationKind.SimpleAssignment, Type: System.Int32) (Syntax: '_ = a |> F(it)')
                              Left:
                                IDiscardOperation (Symbol: System.Int32 _) (OperationKind.Discard, Type: System.Int32) (Syntax: '_')
                              Right:
                                IOperation:  (OperationKind.None, Type: System.Int32) (Syntax: 'a |> F(it)')
                                  Children(1):
                                      IParameterReferenceOperation: a (OperationKind.ParameterReference, Type: System.Int32) (Syntax: 'a')
                    Next (Regular) Block[B2]
                Block[B2] - Exit
                    Predecessors: [B1]
                    Statements (0)
                """, DiagnosticDescription.None);
        }

        [Fact]
        public void Pipe_RegionAnalysis()
        {
            var source = """
                class C
                {
                    static int F(int x, int y) => x + y;
                    int M(int a, int b)
                    {
                        return a |> F(it, b);
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var pipe = tree.GetRoot().DescendantNodes().OfType<PipeExpressionSyntax>().Single();
            var dataFlow = model.AnalyzeDataFlow(pipe)!;
            Assert.True(dataFlow.Succeeded);
            Assert.Equal("a, b", string.Join(", ", dataFlow.DataFlowsIn.Select(s => s.Name)));
            Assert.Equal("a, b, it", string.Join(", ", dataFlow.ReadInside.Select(s => s.Name)));
        }
    }
}
