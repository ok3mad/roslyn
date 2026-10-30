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
    /// Sharper's try expression, slice 1 (issue #28): <c>try body catch { arms }</c>.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void TryExpression_Basic()
        {
            var source = """
                using System;

                class C
                {
                    static int Parse(string s) => try int.Parse(s) catch
                    {
                        FormatException => -1,
                        OverflowException e when e.Message.Length > 0 => -2,
                    };

                    // The C# it stands for.
                    static int ParseCSharp(string s)
                    {
                        try { return int.Parse(s); }
                        catch (FormatException) { return -1; }
                        catch (OverflowException e) when (e.Message.Length > 0) { return -2; }
                    }

                    static void Main()
                    {
                        foreach (var s in new[] { "1", "x", "99999999999" })
                        {
                            Console.Write($"{Parse(s)}={ParseCSharp(s)} ");
                        }

                        try
                        {
                            _ = Parse(null);
                        }
                        catch (ArgumentNullException)
                        {
                            Console.Write("propagated");
                        }
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1=1 -1=-1 -2=-2 propagated" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_Union()
        {
            // The issue's example: the body and each arm convert to a declared C# 15 union.
            var source = """
                using System;
                using System.Net;
                using System.Net.Http;
                using System.Threading.Tasks;

                record RateTable(decimal Rate);
                record NoRate(string From, string To);
                record FxUnavailable(int Status);
                union RateResult(RateTable, NoRate, FxUnavailable);

                class C
                {
                    static async Task<RateTable> Get(int status)
                    {
                        await Task.Yield();
                        return status switch
                        {
                            0 => new RateTable(1.5m),
                            -1 => throw new TimeoutException(),
                            _ => throw new HttpRequestException("failed", null, (HttpStatusCode)status),
                        };
                    }

                    static async Task<RateResult> Quote(int status, string from, string to)
                    {
                        RateResult r = try await Get(status) catch
                        {
                            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => new NoRate(from, to),
                            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError and var s } => new FxUnavailable((int)s),
                            TimeoutException => new FxUnavailable(0),
                        };
                        return r;
                    }

                    static async Task Main()
                    {
                        foreach (var status in new[] { 0, 404, 503, -1 })
                        {
                            Console.Write((await Quote(status, "USD", "EUR")).Value);
                            Console.Write("; ");
                        }

                        try
                        {
                            await Quote(400, "USD", "EUR");
                        }
                        catch (HttpRequestException e)
                        {
                            Console.Write(e.StatusCode);
                        }
                    }
                }
                """;
            CompileAndVerify([source, UnionAttributeSource, IUnionSource], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "RateTable { Rate = 1.5 }; NoRate { From = USD, To = EUR }; FxUnavailable { Status = 503 }; FxUnavailable { Status = 0 }; BadRequest" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_NaturalType()
        {
            var source = """
                using System;

                class C
                {
                    static void Main()
                    {
                        var a = try int.Parse("x") catch { FormatException => 2L };
                        var b = try "s" catch { _ => null };
                        object c = try 1 catch { _ => "one" };
                        Console.Write($"{a.GetType().Name} {b.GetType().Name} {c}");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Int64 String 1" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_InLambdas()
        {
            // A lambda's return type is inferred from the natural type of a try expression, and a delegate's return type
            // is its target.
            var source = """
                #nullable enable
                using System;

                class C
                {
                    static void Main()
                    {
                        var f = (string s) => try int.Parse(s) catch { FormatException => 0L };
                        Func<string, object> g = s => try int.Parse(s) catch { FormatException => s };
                        var h = (string? s) => try s catch { _ => "" };
                        Console.Write($"{f("x").GetType().Name} {g("1")} {g("y")} {h(null) ?? "null"}");
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "Int64 1 y null" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_UnionNullable()
        {
            // A possibly null value converted into a union case warns when it is converted directly, but not as the body or
            // an arm of a target-typed try expression, the same as for a target-typed switch expression
            // (UnionsTests.UnionConversion_61_TargetTyped_Nullable, #146).
            var source = """
                #nullable enable
                class C
                {
                    static R M1(A? a) => a;
                    static R M2(A? a) => try a catch { _ => new B() };
                    static R M3(bool b, A? a) => b switch { true => a, false => new B() };
                }

                record A;
                record B;
                union R(A, B);
                """;
            CreateCompilation([source, UnionAttributeSource, IUnionSource], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (4,26): warning CS8604: Possible null reference argument for parameter 'value' in 'R.R(A value)'.
                //     static R M1(A? a) => a;
                Diagnostic(ErrorCode.WRN_NullReferenceArgument, "a").WithArguments("value", "R.R(A value)").WithLocation(4, 26));
        }

        [Fact]
        public void TryExpression_Errors()
        {
            var source = """
                using System;
                using System.Threading.Tasks;

                class C
                {
                    static Task<int> GetAsync() => Task.FromResult(1);

                    static async Task M()
                    {
                        var a = try 1 catch { _ => "" };
                        var b = try GetAsync() catch { _ => Task.FromResult(0) };
                        var c = try 1 catch { Exception => 2, FormatException => 3 };
                        var d = try 1 catch { Exception e when await Task.FromResult(true) => 2 };
                        int t = try 1 catch { _ => throw new InvalidOperationException() };
                        var f = try Console.WriteLine() catch { _ => 1 };
                        var g = try 1 catch { int => 2 };
                        var h = try 1 catch { };
                        var i = 1 + try 2 catch { _ => 3 };
                        var j = (try 1 catch { _ => 2 }).ToString();
                        var k = try (try 1 catch { _ => 2 }) catch { _ => 3 };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (10,17): error CS20029: The try expression has no type, because its body and arms (int, string) have no best common type. Give it a target type, such as a declared union of these types.
                //         var a = try 1 catch { _ => "" };
                Diagnostic(ErrorCode.ERR_SharperTryExpressionNoType, "try").WithArguments("int, string").WithLocation(10, 17),
                // (11,21): warning CS20030: The body of this try expression is a 'Task<int>', so the arms catch only the exceptions thrown before the task is returned. Write 'try await ...' to catch the exceptions of the operation.
                //         var b = try GetAsync() catch { _ => Task.FromResult(0) };
                Diagnostic(ErrorCode.WRN_SharperTryExpressionAwaitableBody, "GetAsync()").WithArguments("System.Threading.Tasks.Task<int>").WithLocation(11, 21),
                // (12,47): error CS8510: The pattern is unreachable. It has already been handled by a previous arm of the switch expression or it is impossible to match.
                //         var c = try 1 catch { Exception => 2, FormatException => 3 };
                Diagnostic(ErrorCode.ERR_SwitchArmSubsumed, "FormatException").WithLocation(12, 47),
                // (13,48): error CS7094: Cannot await in the filter expression of a catch clause
                //         var d = try 1 catch { Exception e when await Task.FromResult(true) => 2 };
                Diagnostic(ErrorCode.ERR_BadAwaitInCatchFilter, "await Task.FromResult(true)").WithLocation(13, 48),
                // (15,17): error CS20029: The try expression has no type, because its body and arms (void, int) have no best common type. Give it a target type, such as a declared union of these types.
                //         var f = try Console.WriteLine() catch { _ => 1 };
                Diagnostic(ErrorCode.ERR_SharperTryExpressionNoType, "try").WithArguments("void, int").WithLocation(15, 17),
                // (16,31): error CS8121: An expression of type 'Exception' cannot be handled by a pattern of type 'int'.
                //         var g = try 1 catch { int => 2 };
                Diagnostic(ErrorCode.ERR_PatternWrongType, "int").WithArguments("System.Exception", "int").WithLocation(16, 31),
                // (18,21): error CS1525: Invalid expression term 'try'
                //         var i = 1 + try 2 catch { _ => 3 };
                Diagnostic(ErrorCode.ERR_InvalidExprTerm, "try 2 catch { _ => 3 }").WithArguments("try").WithLocation(18, 21));
        }

        [Fact]
        public void TryExpression_Disabled()
        {
            // Without Sharper, `try` in an expression is CS1525, as in C#, followed by recovery errors.
            var source = """
                class C
                {
                    int M() => try 1 catch { _ => 2 };
                }
                """;
            var comp = CreateCompilation(source, targetFramework: TargetFramework.Net90);
            Assert.DoesNotContain(comp.SyntaxTrees[0].GetRoot().DescendantNodes(), n => n.IsKind(SyntaxKind.TryExpression));
            var first = comp.GetDiagnostics().First();
            Assert.Equal((int)ErrorCode.ERR_InvalidExprTerm, first.Code);
            Assert.Equal("try", first.Location.SourceTree!.GetText().ToString(first.Location.SourceSpan));
        }

        [Fact]
        public void TryExpression_StatementPosition()
        {
            // At the start of a statement, `try` is the try statement, as in C#.
            var source = """
                class C
                {
                    static int F() => 1;
                    void M() { try F() catch { _ => 2 }; }
                    void N() { try { F(); } catch { } }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (4,20): error CS1514: { expected
                //     void M() { try F() catch { _ => 2 }; }
                Diagnostic(ErrorCode.ERR_LbraceExpected, "F").WithLocation(4, 20),
                // (4,24): error CS1002: ; expected
                //     void M() { try F() catch { _ => 2 }; }
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "catch").WithLocation(4, 24),
                // (4,24): error CS1513: } expected
                //     void M() { try F() catch { _ => 2 }; }
                Diagnostic(ErrorCode.ERR_RbraceExpected, "catch").WithLocation(4, 24),
                // (4,39): error CS1002: ; expected
                //     void M() { try F() catch { _ => 2 }; }
                Diagnostic(ErrorCode.ERR_SemicolonExpected, "}").WithLocation(4, 39));
        }

        [Fact]
        public void TryExpression_UnmatchedKeepsStackTraceAndFirstPass()
        {
            // The arms run in a filter: an unmatched exception is never caught, so its stack trace is untouched, and the
            // filter runs before inner finally blocks.
            var source = """
                using System;

                class C
                {
                    static int Thrower(string kind)
                    {
                        try
                        {
                            throw kind == "io" ? new System.IO.IOException() : new InvalidOperationException();
                        }
                        finally
                        {
                            Console.Write("finally ");
                        }
                    }

                    static int M(string kind) => try Thrower(kind) catch
                    {
                        System.IO.IOException when Log("filter ") => 1,
                        ArgumentException when Log("filter ") => 2,
                    };

                    static bool Log(string s)
                    {
                        Console.Write(s);
                        return true;
                    }

                    static void Main()
                    {
                        Console.Write(M("io") + " ");
                        try
                        {
                            M("other");
                        }
                        catch (InvalidOperationException e)
                        {
                            Console.Write(e.StackTrace!.Contains("Thrower"));
                        }
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "filter finally 1 finally True" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_AwaitAndSpilling()
        {
            var source = """
                using System;
                using System.Threading.Tasks;

                class C
                {
                    static string Log(string s)
                    {
                        Console.Write(s);
                        return s;
                    }

                    static async Task<int> Fail()
                    {
                        await Task.Yield();
                        throw new InvalidOperationException("x");
                    }

                    static string Concat(string a, int b, string c) => a + b + c;

                    static async Task Main()
                    {
                        // Earlier operands are evaluated before the try expression, and later ones after it.
                        Console.WriteLine(" " + Concat(Log("a"), try await Fail() catch { InvalidOperationException e => await Task.FromResult(e.Message.Length) }, Log("c")));

                        int[] xs = { 1, 2 };
                        int i = 0;
                        xs[i++] += try await Fail() catch { _ => 10 };
                        Console.WriteLine(string.Join(",", xs) + " " + i);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? """
                    ac a1c
                    11,2 1
                    """ : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_RefAndSpilling()
        {
            var source = """
                using System;

                class C
                {
                    static int Add(ref int x, int y) => x += y;

                    static int Fail() => throw new InvalidOperationException();

                    static void Main()
                    {
                        int[] xs = { 1, 2 };
                        ref int r = ref xs[1];
                        Console.Write(Add(ref r, try Fail() catch { _ => 40 }));
                        Console.Write(" " + xs[1]);
                        Span<int> s = try stackalloc int[2] catch { _ => default };
                        Console.Write(" " + s.Length);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "42 42 2" : null, verify: Verification.Skipped).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_Iterators()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                class C
                {
                    static int Parse(string s) => int.Parse(s);

                    static IEnumerable<int> Numbers(string[] xs)
                    {
                        foreach (var x in xs)
                            yield return try Parse(x) catch { FormatException => -1 };
                    }

                    static async IAsyncEnumerable<int> NumbersAsync(string[] xs)
                    {
                        foreach (var x in xs)
                            yield return try await Task.FromResult(Parse(x)) catch { FormatException => await Task.FromResult(-1) };
                    }

                    static async Task Main()
                    {
                        string[] xs = { "1", "x", "3" };
                        Console.Write(string.Join(",", Numbers(xs)));
                        await foreach (var n in NumbersAsync(xs))
                            Console.Write(" " + n);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1,-1,3 1 -1 3" : null).VerifyDiagnostics();
        }

        [Fact]
        public void TryExpression_CatchFlags()
        {
            // An arm's value runs in the catch block (no stackalloc), its pattern and when clause in the filter (no await).
            var source = """
                using System;
                using System.Threading.Tasks;

                class C
                {
                    static async Task<int> M()
                    {
                        int a = try 1 catch { Exception e when await Task.FromResult(true) => 2 };
                        int b = try 1 catch { _ => await Task.FromResult(2) };
                        return a + b;
                    }

                    static void N()
                    {
                        Span<int> s = try stackalloc int[1] catch { _ => stackalloc int[2] };
                    }

                    static void O()
                    {
                        System.Linq.Expressions.Expression<Func<int>> e = () => try 1 catch { _ => 2 };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,48): error CS7094: Cannot await in the filter expression of a catch clause
                //         int a = try 1 catch { Exception e when await Task.FromResult(true) => 2 };
                Diagnostic(ErrorCode.ERR_BadAwaitInCatchFilter, "await Task.FromResult(true)").WithLocation(8, 48),
                // (15,58): error CS0255: stackalloc may not be used in a catch or finally block
                //         Span<int> s = try stackalloc int[1] catch { _ => stackalloc int[2] };
                Diagnostic(ErrorCode.ERR_StackallocInCatchFinally, "stackalloc int[2]").WithLocation(15, 58),
                // (20,65): error CS20031: An expression tree cannot contain a try expression.
                //         System.Linq.Expressions.Expression<Func<int>> e = () => try 1 catch { _ => 2 };
                Diagnostic(ErrorCode.ERR_SharperTryExpressionInExpressionTree, "try 1 catch { _ => 2 }").WithLocation(20, 65));
        }

        [Fact]
        public void TryExpression_DefiniteAssignmentAndNullable()
        {
            // An arm starts from the state before the body, joined with the states the body passes through, as a catch
            // block does: the body may have thrown anywhere.
            var source = """
                #nullable enable
                class C
                {
                    static int F() => 1;

                    static int M1()
                    {
                        int x;
                        int y = try (x = F()) catch { _ => x };
                        return x + y;
                    }

                    static int M1CSharp()
                    {
                        int x;
                        int y;
                        try { y = (x = F()); } catch { y = x; }
                        return x + y;
                    }

                    static int M2()
                    {
                        int x;
                        int y = try (x = F()) catch { _ => x = 2 };
                        return x + y;
                    }

                    static int M3(string? s)
                    {
                        return try F() + s.Length catch { _ => s.Length };
                    }

                    static int M3CSharp(string? s)
                    {
                        try { return F() + s.Length; } catch { return s.Length; }
                    }

                    static int M4(string? s)
                    {
                        string? t = "a";
                        int y = try F() + (t = s)!.Length + (t = "b").Length catch { _ => t.Length };
                        return y + t.Length;
                    }

                    static string M5(string? s) => try s catch { _ => "" };
                    static string M6(string? s) => try "" catch { _ => s };
                    static string M7(string? s) => try s! catch { System.IO.IOException e => e.Message };
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (9,44): error CS0165: Use of unassigned local variable 'x'
                //         int y = try (x = F()) catch { _ => x };
                Diagnostic(ErrorCode.ERR_UseDefViolation, "x").WithArguments("x").WithLocation(9, 44),
                // (17,44): error CS0165: Use of unassigned local variable 'x'
                //         try { y = (x = F()); } catch { y = x; }
                Diagnostic(ErrorCode.ERR_UseDefViolation, "x").WithArguments("x").WithLocation(17, 44),
                // (30,26): warning CS8602: Dereference of a possibly null reference.
                //         return try F() + s.Length catch { _ => s.Length };
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(30, 26),
                // (30,48): warning CS8602: Dereference of a possibly null reference.
                //         return try F() + s.Length catch { _ => s.Length };
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(30, 48),
                // (35,28): warning CS8602: Dereference of a possibly null reference.
                //         try { return F() + s.Length; } catch { return s.Length; }
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(35, 28),
                // (35,55): warning CS8602: Dereference of a possibly null reference.
                //         try { return F() + s.Length; } catch { return s.Length; }
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(35, 55),
                // (41,75): warning CS8602: Dereference of a possibly null reference.
                //         int y = try F() + (t = s)!.Length + (t = "b").Length catch { _ => t.Length };
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "t").WithLocation(41, 75),
                // (45,36): warning CS8603: Possible null reference return.
                //     static string M5(string? s) => try s catch { _ => "" };
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, @"try s catch { _ => """" }").WithLocation(45, 36),
                // (46,36): warning CS8603: Possible null reference return.
                //     static string M6(string? s) => try "" catch { _ => s };
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, @"try """" catch { _ => s }").WithLocation(46, 36));
        }

        [Fact]
        public void TryExpression_IOperation()
        {
            var source = """
                using System;

                class C
                {
                    static int M(string s) => /*<bind>*/try int.Parse(s) catch { FormatException e when e.Message != null => -1 }/*</bind>*/;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            // PROTOTYPE(sharper): the try expression has no IOperation shape yet (#144). It is an OperationKind.None node
            // whose only child is the body; the arms are left out.
            var expectedOperationTree = """
                IOperation:  (OperationKind.None, Type: System.Int32) (Syntax: 'try int.Par ... ull => -1 }')
                  Children(1):
                      IInvocationOperation (System.Int32 System.Int32.Parse(System.String s)) (OperationKind.Invocation, Type: System.Int32) (Syntax: 'int.Parse(s)')
                        Instance Receiver:
                          null
                        Arguments(1):
                            IArgumentOperation (ArgumentKind.Explicit, Matching Parameter: s) (OperationKind.Argument, Type: null) (Syntax: 's')
                              IParameterReferenceOperation: s (OperationKind.ParameterReference, Type: System.String) (Syntax: 's')
                              InConversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                              OutConversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                """;
            VerifyOperationTreeAndDiagnosticsForTest<TryExpressionSyntax>(comp, expectedOperationTree, []);
        }

        [Fact]
        public void TryExpression_FlowGraph()
        {
            var source = """
                using System;

                class C
                {
                    static int M(string s)
                    /*<bind>*/{
                        return try int.Parse(s) catch { FormatException e => e.Message.Length };
                    }/*</bind>*/
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            var expectedFlowGraph = """
                Block[B0] - Entry
                    Statements (0)
                    Next (Regular) Block[B1]
                Block[B1] - Block
                    Predecessors: [B0]
                    Statements (0)
                    Next (Return) Block[B2]
                        IOperation:  (OperationKind.None, Type: System.Int32) (Syntax: 'try int.Par ... ge.Length }')
                          Children(1):
                              IInvocationOperation (System.Int32 System.Int32.Parse(System.String s)) (OperationKind.Invocation, Type: System.Int32) (Syntax: 'int.Parse(s)')
                                Instance Receiver:
                                  null
                                Arguments(1):
                                    IArgumentOperation (ArgumentKind.Explicit, Matching Parameter: s) (OperationKind.Argument, Type: null) (Syntax: 's')
                                      IParameterReferenceOperation: s (OperationKind.ParameterReference, Type: System.String) (Syntax: 's')
                                      InConversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                                      OutConversion: CommonConversion (Exists: True, IsIdentity: True, IsNumeric: False, IsReference: False, IsUserDefined: False) (MethodSymbol: null)
                Block[B2] - Exit
                    Predecessors: [B1]
                    Statements (0)
                """;
            VerifyFlowGraphAndDiagnosticsForTest<BlockSyntax>(comp, expectedFlowGraph, []);
        }

        [Fact]
        public void TryExpression_SemanticModel()
        {
            var source = """
                #nullable enable
                using System;

                record A;
                record B;
                union R(A, B);

                class C
                {
                    static R M(Func<A> f) => try f() catch { InvalidOperationException e when e.Message.Length > 0 => new B() };
                    static object N(string s) => try s.Length catch { FormatException => 0L };
                }
                """;
            var comp = CreateCompilation([source, UnionAttributeSource, IUnionSource], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var tries = tree.GetRoot().DescendantNodes().OfType<TryExpressionSyntax>().ToArray();

            var typeInfo = model.GetTypeInfo(tries[0]);
            Assert.Null(typeInfo.Type);
            Assert.Equal("R", typeInfo.ConvertedType.ToTestDisplayString());
            Assert.Equal("A", model.GetTypeInfo(tries[0].Expression).Type.ToTestDisplayString());
            Assert.Equal("R", model.GetTypeInfo(tries[0].Expression).ConvertedType.ToTestDisplayString());
            Assert.True(model.GetConversion(tries[0].Expression).IsUnion);

            var designation = tries[0].Arms[0].Pattern.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Single();
            var local = (ILocalSymbol)model.GetDeclaredSymbol(designation)!;
            Assert.Equal("System.InvalidOperationException e", local.ToTestDisplayString());
            var reference = tries[0].Arms[0].WhenClause!.DescendantNodes().OfType<IdentifierNameSyntax>().First(n => n.Identifier.Text == "e");
            Assert.Equal(local, model.GetSymbolInfo(reference).Symbol);

            typeInfo = model.GetTypeInfo(tries[1]);
            Assert.Equal("System.Int64", typeInfo.Type.ToTestDisplayString());
            Assert.Equal("System.Object", typeInfo.ConvertedType.ToTestDisplayString());
        }

        [Fact]
        public void TryExpression_RegionAnalysis()
        {
            var source = """
                using System;

                class C
                {
                    static int M(string s, int fallback)
                    {
                        int r = try int.Parse(s) catch { FormatException e when e.Message.Length > fallback => fallback };
                        return r;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees.Single();
            var model = comp.GetSemanticModel(tree);
            var expression = tree.GetRoot().DescendantNodes().OfType<TryExpressionSyntax>().Single();

            var dataFlow = model.AnalyzeDataFlow(expression);
            Assert.True(dataFlow.Succeeded);
            Assert.Equal("s, fallback, e", string.Join(", ", dataFlow.ReadInside.Select(s => s.Name)));
            Assert.Equal("e", string.Join(", ", dataFlow.VariablesDeclared.Select(s => s.Name)));
            Assert.Equal("e", string.Join(", ", dataFlow.WrittenInside.Select(s => s.Name)));
        }

        [Theory]
        [CombinatorialData]
        public void TryExpression_Parsing(bool sharper)
        {
            var options = sharper ? s_sharperOptions : TestOptions.Regular;

            var expression = SyntaxFactory.ParseExpression("try F(x) catch { IOException { HResult: 5 } e when e.Message != null => 1, _ => 2, }", options: options);
            Assert.Equal(sharper ? SyntaxKind.TryExpression : SyntaxKind.IdentifierName, expression.Kind());
            if (sharper)
            {
                var tryExpression = (TryExpressionSyntax)expression;
                Assert.Equal("F(x)", tryExpression.Expression.ToString());
                Assert.Equal(2, tryExpression.Arms.Count);
                Assert.Equal(2, tryExpression.Arms.SeparatorCount);
                Assert.NotNull(tryExpression.Arms[0].WhenClause);
                Assert.False(expression.ContainsDiagnostics);
            }

            // The body may be any expression, including a lambda, an assignment or another try expression.
            expression = SyntaxFactory.ParseExpression("try x = try y catch { _ => 1 } catch { _ => 2 }", options: options);
            if (sharper)
            {
                var body = ((TryExpressionSyntax)expression).Expression;
                Assert.Equal(SyntaxKind.SimpleAssignmentExpression, body.Kind());
                Assert.Equal(SyntaxKind.TryExpression, ((AssignmentExpressionSyntax)body).Right.Kind());
                Assert.False(expression.ContainsDiagnostics);
            }

            // Nothing continues a try expression: it has the lowest precedence, so a member access needs parentheses.
            var statement = SyntaxFactory.ParseStatement("var s = (try x catch { _ => 1 }).ToString();", options: options);
            Assert.Equal(!sharper, statement.ContainsDiagnostics);
            statement = SyntaxFactory.ParseStatement("var s = try x catch { _ => 1 }.ToString();", options: options);
            Assert.True(statement.ContainsDiagnostics);
            statement = SyntaxFactory.ParseStatement("var s = 1 + try x catch { _ => 1 };", options: options);
            Assert.True(statement.ContainsDiagnostics);

            // A conditional's branches, a switch arm and an argument take it.
            statement = SyntaxFactory.ParseStatement("var s = b ? try x catch { _ => 1 } : y switch { _ => try z catch { _ => 2 } };", options: options);
            Assert.Equal(!sharper, statement.ContainsDiagnostics);
            statement = SyntaxFactory.ParseStatement("M(a, try x catch { _ => 1 }, b);", options: options);
            Assert.Equal(!sharper, statement.ContainsDiagnostics);

            // `try {` in an expression is a try statement in the wrong place, as in C#.
            statement = SyntaxFactory.ParseStatement("var s = try { } catch { };", options: options);
            Assert.True(statement.ContainsDiagnostics);
            Assert.DoesNotContain(statement.DescendantNodes(), n => n.IsKind(SyntaxKind.TryExpression));
        }

        [Fact]
        public void TryExpression_SyntaxKind()
        {
            Assert.Equal(9903, (int)SyntaxKind.TryExpression);
            Assert.Single(System.Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == (int)SyntaxKind.TryExpression);
        }

        [Fact]
        public void TryExpression_FactoryWithoutSharper()
        {
            // A try expression built with SyntaxFactory in a C# tree reports that Sharper is not enabled.
            var tree = SyntaxFactory.ParseSyntaxTree("class C { int M() => try 1 catch { _ => 2 }; }", s_sharperOptions);
            var csharpTree = tree.WithRootAndOptions(tree.GetRoot(), TestOptions.Regular);
            CreateCompilation(csharpTree, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (1,22): error CS8058: Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.
                // class C { int M() => try 1 catch { _ => 2 }; }
                Diagnostic(ErrorCode.ERR_FeatureIsExperimental, "try").WithArguments("Sharper", "sharper").WithLocation(1, 22));
        }
    }
}
