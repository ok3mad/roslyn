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
    /// Sharper's refined parameters, slice 1 steps 1 and 2 (issue #33): <c>static int Page(int i is &gt;=0)</c> parses
    /// and binds its refinement, and the parameter is readonly. Nothing reads the refinement yet: there is no guard, and
    /// calls and switches behave as in C#.
    /// </summary>
    public sealed partial class SharperTests
    {
        private static ParameterSyntax ParseRefinedParameter(string member, CSharpParseOptions options)
        {
            var tree = SyntaxFactory.ParseSyntaxTree("class C { " + member + " }", options);
            return tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().First();
        }

        [Fact]
        public void Refinement_Parsing()
        {
            var parameter = ParseRefinedParameter("static int Page(int i is >=0) => i;", s_sharperOptions);
            parameter.SyntaxTree.GetDiagnostics().Verify();
            Assert.Equal("int i is >=0", parameter.ToString());
            Assert.Equal(SyntaxKind.RefinementClause, parameter.Refinement!.Kind());
            Assert.Equal(SyntaxKind.IsKeyword, parameter.Refinement.IsKeyword.Kind());
            Assert.Equal(SyntaxKind.RelationalPattern, parameter.Refinement.Pattern.Kind());
            Assert.Null(parameter.Default);
            Assert.Equal(9905, (int)SyntaxKind.RefinementClause);
            Assert.Single(System.Enum.GetValues(typeof(SyntaxKind)).Cast<SyntaxKind>(), k => (int)k == 9905);

            // The refinement comes before the default value, and its pattern ends at '=', ',' and ')'.
            parameter = ParseRefinedParameter("void M(int i is >=0 and <10 or 42 = 3, int j) { }", s_sharperOptions);
            parameter.SyntaxTree.GetDiagnostics().Verify();
            Assert.Equal("is >=0 and <10 or 42", parameter.Refinement!.ToString());
            Assert.Equal(SyntaxKind.OrPattern, parameter.Refinement.Pattern.Kind());
            Assert.Equal("= 3", parameter.Default!.ToString());
            Assert.Equal(new[] { "Identifier", "Refinement", "Default" },
                parameter.ChildNodesAndTokens().Skip(1).Select(n => n == parameter.Identifier ? "Identifier" : n == parameter.Refinement ? "Refinement" : n == parameter.Default ? "Default" : n.ToString()));

            parameter = ParseRefinedParameter("void M(string s is not (\"\" or \" \")) { }", s_sharperOptions);
            parameter.SyntaxTree.GetDiagnostics().Verify();
            Assert.Equal(SyntaxKind.NotPattern, parameter.Refinement!.Pattern.Kind());

            // 'infer' may carry a refinement (Feature 11): the type comes from the default value.
            parameter = ParseRefinedParameter("void Fetch(infer retries is >=0 = 3) { }", s_sharperOptions);
            parameter.SyntaxTree.GetDiagnostics().Verify();
            Assert.Equal("is >=0", parameter.Refinement!.ToString());
            Assert.Equal("= 3", parameter.Default!.ToString());
        }

        [Fact]
        public void Refinement_Parsing_DefaultValueKeepsCSharpMeaning()
        {
            // 'bool b = 0 is >=0' is valid C#: a default value of '(0 is >=0)'. Sharper reads it the same.
            foreach (var options in new[] { TestOptions.Regular, s_sharperOptions })
            {
                var parameter = ParseRefinedParameter("void M(bool b = 0 is >=0) { }", options);
                parameter.SyntaxTree.GetDiagnostics().Verify();
                Assert.Null(parameter.Refinement);
                Assert.Equal(SyntaxKind.IsPatternExpression, parameter.Default!.Value.Kind());
                Assert.Equal("0 is >=0", parameter.Default.Value.ToString());
            }

            // It is not a constant, so both modes report the same errors; a refinement written before '=' is one.
            var source = """
                class C
                {
                    static bool M(bool b = 0 is >=0) => b;
                }
                """;
            foreach (var options in new[] { TestOptions.Regular, s_sharperOptions })
            {
                CreateCompilation(source, parseOptions: options).VerifyDiagnostics(
                    // (3,28): warning CS8793: The given expression always matches the provided pattern.
                    //     static bool M(bool b = 0 is >=0) => b;
                    Diagnostic(ErrorCode.WRN_GivenExpressionAlwaysMatchesPattern, "0 is >=0").WithLocation(3, 28),
                    // (3,28): error CS1736: Default parameter value for 'b' must be a compile-time constant
                    //     static bool M(bool b = 0 is >=0) => b;
                    Diagnostic(ErrorCode.ERR_DefaultValueMustBeConstant, "0 is >=0").WithArguments("b").WithLocation(3, 28));
            }
        }

        [Fact]
        public void Refinement_Parsing_FeatureDisabled()
        {
            // Without Sharper, 'is' after a parameter name keeps C#'s syntax errors, and no refinement is made.
            var parameter = ParseRefinedParameter("static int Page(int i is >=0) => i;", TestOptions.Regular);
            Assert.Null(parameter.Refinement);
            Assert.NotEmpty(parameter.SyntaxTree.GetDiagnostics());

            var source = """
                class C
                {
                    static int Page(int i is >=0) => i;
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (3,27): error CS1003: Syntax error, ',' expected
                //     static int Page(int i is >=0) => i;
                Diagnostic(ErrorCode.ERR_SyntaxError, "is").WithArguments(",").WithLocation(3, 27));
        }

        [Fact]
        public void Refinement_Parsing_Positions()
        {
            // Every parameter list parsed by ParseParameter takes a refinement; the binder decides where it is allowed.
            var source = """
                delegate void D(int i is >0);
                record R(int X is >0);
                class P(int x is >0);
                class C
                {
                    C(int i is >0) { }
                    int this[int i is >0] => i;
                    public static C operator +(C c, int i is >0) => c;
                    void M()
                    {
                        void Local(int i is >0) { }
                        System.Action<int> a = delegate (int i is >0) { };
                    }
                }
                """;
            var tree = SyntaxFactory.ParseSyntaxTree(source, s_sharperOptions);
            tree.GetDiagnostics().Verify();
            Assert.Equal(8, tree.GetRoot().DescendantNodes().OfType<RefinementClauseSyntax>().Count());

            // A lambda's parameter list is scanned before it is parsed, and the scan does not take a refinement: it stays a
            // syntax error, as in C#.
            tree = SyntaxFactory.ParseSyntaxTree("class C { System.Func<int, int> f = (int i is >0) => i; }", s_sharperOptions);
            Assert.NotEmpty(tree.GetDiagnostics());
            Assert.Empty(tree.GetRoot().DescendantNodes().OfType<RefinementClauseSyntax>());
        }

        [Fact]
        public void Refinement_SyntaxFactory()
        {
            // The overloads that existed before the refinement keep working (shipped public API): Parameter and Update
            // with five arguments.
            var parameter = SyntaxFactory.Parameter(default, default, SyntaxFactory.ParseTypeName("int"), SyntaxFactory.Identifier("i"), null);
            Assert.Null(parameter.Refinement);

            var refinement = SyntaxFactory.RefinementClause(SyntaxFactory.RelationalPattern(
                SyntaxFactory.Token(SyntaxKind.GreaterThanEqualsToken), SyntaxFactory.ParseExpression("0")));
            var refined = parameter.WithRefinement(refinement);
            Assert.Equal("int i is >= 0", refined.NormalizeWhitespace().ToFullString());
            Assert.Equal("int i is >= 0 = 1", refined.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("1"))).NormalizeWhitespace().ToFullString());

            // The five-argument Update keeps the refinement of the node it updates.
            var updated = refined.Update(refined.AttributeLists, refined.Modifiers, refined.Type, SyntaxFactory.Identifier("j"), refined.Default);
            Assert.Equal("int j is >= 0", updated.NormalizeWhitespace().ToFullString());
            Assert.Equal("int i", refined.WithRefinement(null).NormalizeWhitespace().ToFullString());
        }

        [Fact]
        public void Refinement_Basic()
        {
            // A refinement changes nothing at runtime in this step: each method runs as its C# twin does.
            var source = """
                using System;

                enum Color { Red, Green, Blue }

                class C
                {
                    static int Page(int i is >=0) => i;
                    static int PageCSharp(int i) => i;

                    static string All(long l is >0 and <=100, double d is >=0.0, decimal m is >0m, char c is >='a' and <='z',
                        bool b is true, string s is not "", Color k is Color.Red or Color.Green, nint n is >=0, byte x is not 0) =>
                        $"{l} {d} {m} {c} {b} {s} {k} {n} {x}";

                    static int Retries(infer retries is >=0 = 3) => retries;

                    static void Main()
                    {
                        Console.Write(Page(1));
                        Console.Write(PageCSharp(1));
                        Console.Write(" ");
                        Console.Write(All(5, 0.5, 2m, 'q', true, "s", Color.Green, 7, 9));
                        Console.Write(" ");
                        Console.Write(Retries());
                        Console.Write(Local(4));

                        static int Local(int i is 1 or 2 or 4) => i * 2;
                    }

                    C(int i is >0) => Console.Write("");
                    int this[int i is >=0] => i;
                    public static C operator +(C c, int i is >=0) => c;
                }

                class P(int x is >=0)
                {
                    public int X => x;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "11 5 0.5 2 q True s Green 7 9 38" : null).VerifyDiagnostics();
        }

        [Fact]
        public void Refinement_NoGuardYet()
        {
            // Step 2 has no call-site check and no guard: a value outside the domain gets in, as in C#.
            // PROTOTYPE(sharper): steps 4 and 5 report 'Page(-1)' and guard the entry.
            var source = """
                class C
                {
                    static int Page(int i is >=0) => i;
                    static void Main() => System.Console.Write(Page(-1));
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, expectedOutput: "-1").VerifyDiagnostics();
        }

        [Fact]
        public void Refinement_ValueSet()
        {
            var source = """
                class C
                {
                    void M(int i is >=0 and <10 or 42, string s is "a" or "b", string t is not "x", Color k is not Color.Red) { }
                }
                enum Color { Red, Green }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            var method = comp.GetMember<MethodSymbol>("C.M");
            var parameters = method.Parameters.Cast<SourceComplexParameterSymbolBase>().ToArray();

            var i = parameters[0].SharperRefinement!;
            Assert.Same(i, parameters[0].SharperRefinement);
            Assert.False(i.HasErrors);
            Assert.True(i.Values!.Any(BinaryOperatorKind.Equal, ConstantValue.Create(0)));
            Assert.True(i.Values.Any(BinaryOperatorKind.Equal, ConstantValue.Create(9)));
            Assert.False(i.Values.Any(BinaryOperatorKind.Equal, ConstantValue.Create(10)));
            Assert.True(i.Values.Any(BinaryOperatorKind.Equal, ConstantValue.Create(42)));
            Assert.False(i.Values.Any(BinaryOperatorKind.LessThan, ConstantValue.Create(0)));
            Assert.False(i.MatchesNull);

            var s = parameters[1].SharperRefinement!;
            Assert.True(s.Values!.Any(BinaryOperatorKind.Equal, ConstantValue.Create("a")));
            Assert.False(s.Values.Any(BinaryOperatorKind.Equal, ConstantValue.Create("c")));
            Assert.False(s.MatchesNull);

            var t = parameters[2].SharperRefinement!;
            Assert.False(t.Values!.Any(BinaryOperatorKind.Equal, ConstantValue.Create("x")));
            Assert.True(t.Values.Any(BinaryOperatorKind.Equal, ConstantValue.Create("y")));
            Assert.True(t.MatchesNull);

            var k = parameters[3].SharperRefinement!;
            Assert.False(k.Values!.Any(BinaryOperatorKind.Equal, ConstantValue.Create(0)));
            Assert.True(k.Values.Any(BinaryOperatorKind.Equal, ConstantValue.Create(1)));

            // A parameter without a refinement has none, and stays a simple parameter.
            Assert.IsType<SourceSimpleParameterSymbol>(
                CreateCompilation("class C { void M(int i) { } }", parseOptions: s_sharperOptions).GetMember<MethodSymbol>("C.M").Parameters[0]);
        }

        [Fact]
        public void Refinement_NotClosed()
        {
            // Only constant and relational patterns, 'and', 'or', 'not' and parentheses; each other pattern is an error.
            var source = """
                class C
                {
                    const int Max = 10;
                    void M1(int i is >=0 and <=Max) { }
                    void M2(int i is (>0)) { }
                    void M3(int i is int) { }
                    void M4(int i is var x) { }
                    void M5(int i is { }) { }
                    void M6(int i is _) { }
                    void M7(int i is >0 or int) { }
                    void M8(int[] a is []) { }
                    void M9(int i, int j is >i) { } // Value-dependent refinements are out of scope.
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (6,22): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'int' is not.
                //     void M3(int i is int) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "int").WithArguments("int").WithLocation(6, 22),
                // (7,22): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'var x' is not.
                //     void M4(int i is var x) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "var x").WithArguments("var x").WithLocation(7, 22),
                // (8,22): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '{ }' is not.
                //     void M5(int i is { }) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "{ }").WithArguments("{ }").WithLocation(8, 22),
                // (9,22): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '_' is not.
                //     void M6(int i is _) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "_").WithArguments("_").WithLocation(9, 22),
                // (10,28): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; 'int' is not.
                //     void M7(int i is >0 or int) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "int").WithArguments("int").WithLocation(10, 28),
                // (11,24): error CS20058: A refinement must be a closed pattern over constants, made of constant and relational patterns, 'and', 'or', 'not' and parentheses, such as 'is >=0' or 'is 1 or 2'; '[]' is not.
                //     void M8(int[] a is []) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotClosed, "[]").WithArguments("[]").WithLocation(11, 24),
                // (12,29): error CS8781: Relational patterns may not be used for a value of type '?'.
                //     void M9(int i, int j is >i) { }
                Diagnostic(ErrorCode.ERR_UnsupportedTypeForRelationalPattern, ">i").WithArguments("?").WithLocation(12, 29),
                // (12,30): error CS0103: The name 'i' does not exist in the current context
                //     void M9(int i, int j is >i) { }
                Diagnostic(ErrorCode.ERR_NameNotInContext, "i").WithArguments("i").WithLocation(12, 30));
        }

        [Fact]
        public void Refinement_TypeNotSupported()
        {
            var source = """
                class C
                {
                    void M1(object o is >0) { }
                    void M2(int? i is >0) { }
                    void M3(S s is default(S)) { }
                    void M4<T>(T t is null) { }
                    void M5(System.DateTime d is >0) { }
                }
                struct S { }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (3,20): error CS20059: Cannot refine parameter 'o' of type 'object'. A refinement needs a type whose constants can be compared: an integer, floating-point, 'decimal', 'char', 'bool', 'string' or enum type.
                //     void M1(object o is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementTypeNotSupported, "o").WithArguments("o", "object").WithLocation(3, 20),
                // (4,18): error CS20059: Cannot refine parameter 'i' of type 'int?'. A refinement needs a type whose constants can be compared: an integer, floating-point, 'decimal', 'char', 'bool', 'string' or enum type.
                //     void M2(int? i is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementTypeNotSupported, "i").WithArguments("i", "int?").WithLocation(4, 18),
                // (5,20): error CS9135: A constant value of type 'S' is expected
                //     void M3(S s is default(S)) { }
                Diagnostic(ErrorCode.ERR_ConstantValueOfTypeExpected, "default(S)").WithArguments("S").WithLocation(5, 20),
                // (6,18): error CS20059: Cannot refine parameter 't' of type 'T'. A refinement needs a type whose constants can be compared: an integer, floating-point, 'decimal', 'char', 'bool', 'string' or enum type.
                //     void M4<T>(T t is null) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementTypeNotSupported, "t").WithArguments("t", "T").WithLocation(6, 18),
                // (7,34): error CS8781: Relational patterns may not be used for a value of type 'System.DateTime'.
                //     void M5(System.DateTime d is >0) { }
                Diagnostic(ErrorCode.ERR_UnsupportedTypeForRelationalPattern, ">0").WithArguments("System.DateTime").WithLocation(7, 34),
                // (7,35): error CS0029: Cannot implicitly convert type 'int' to 'System.DateTime'
                //     void M5(System.DateTime d is >0) { }
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "0").WithArguments("int", "System.DateTime").WithLocation(7, 35));
        }

        [Fact]
        public void Refinement_DefaultValue()
        {
            // The default value is a call site the compiler always sees: a default outside the domain is an error.
            var source = """
                class C
                {
                    void M1(int i is >=0 = 3) { }
                    void M2(int i is >=0 = -1) { }
                    void M3(int i is >=0 = default) { }
                    void M4(int i is >0 = default) { }
                    void M5(string s is not "x" = null) { }
                    void M6(string s is "a" or "b" = null) { }
                    void M7(infer retries is >=0 = 3) { }
                    void M8(infer retries is >=0 = -3) { }
                    void M9(double d is >0.0 = double.NaN) { }
                    void M10(Color c is not Color.Red = Color.Red) { }
                }
                enum Color { Red, Green }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (4,28): error CS20060: The default value of parameter 'i' (-1) does not satisfy its refinement '>=0'.
                //     void M2(int i is >=0 = -1) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, "-1").WithArguments("i", "-1", ">=0").WithLocation(4, 28),
                // (6,27): error CS20060: The default value of parameter 'i' (default) does not satisfy its refinement '>0'.
                //     void M4(int i is >0 = default) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, "default").WithArguments("i", "default", ">0").WithLocation(6, 27),
                // (8,38): error CS20060: The default value of parameter 's' (null) does not satisfy its refinement '"a" or "b"'.
                //     void M6(string s is "a" or "b" = null) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, "null").WithArguments("s", "null", @"""a"" or ""b""").WithLocation(8, 38),
                // (10,36): error CS20060: The default value of parameter 'retries' (-3) does not satisfy its refinement '>=0'.
                //     void M8(infer retries is >=0 = -3) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, "-3").WithArguments("retries", "-3", ">=0").WithLocation(10, 36),
                // (11,32): error CS20060: The default value of parameter 'd' (double.NaN) does not satisfy its refinement '>0.0'.
                //     void M9(double d is >0.0 = double.NaN) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, "double.NaN").WithArguments("d", "double.NaN", ">0.0").WithLocation(11, 32),
                // (12,41): error CS20060: The default value of parameter 'c' (Color.Red) does not satisfy its refinement 'not Color.Red'.
                //     void M10(Color c is not Color.Red = Color.Red) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementDefaultValueDisproven, "Color.Red").WithArguments("c", "Color.Red", "not Color.Red").WithLocation(12, 41));
        }

        [Fact]
        public void Refinement_ReadOnly()
        {
            // A refined parameter is implicitly readonly (#23), so its refinement holds in the whole body. Each write next
            // to the same write to its plain twin, which is allowed.
            var source = """
                class C
                {
                    static void Ref(ref int i) { }

                    void M(int x is >=0, int y)
                    {
                        x = 2;
                        y = 2;
                        x++;
                        y++;
                        Ref(ref x);
                        Ref(ref y);
                        System.Action a = () => x = 3;
                        System.Action b = () => y = 3;
                        int z = x + y;
                    }
                }

                class P(int p is >=0)
                {
                    void M() => p = 1;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (7,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x = 2;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(7, 9),
                // (9,9): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         x++;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(9, 9),
                // (11,17): error CS1657: Cannot use 'x' as a ref or out value because it is a 'readonly parameter'
                //         Ref(ref x);
                Diagnostic(ErrorCode.ERR_RefReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(11, 17),
                // (13,33): error CS1656: Cannot assign to 'x' because it is a 'readonly parameter'
                //         System.Action a = () => x = 3;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "x").WithArguments("x", "readonly parameter").WithLocation(13, 33),
                // (21,17): error CS1656: Cannot assign to 'p' because it is a 'readonly parameter'
                //     void M() => p = 1;
                Diagnostic(ErrorCode.ERR_AssgReadonlyLocalCause, "p").WithArguments("p", "readonly parameter").WithLocation(21, 17));
        }

        [Fact]
        public void Refinement_NotSupportedYet()
        {
            // A refinement is not part of the signature. Slice 1 reports an error where callers do not know which body
            // runs, where there is no body to guard, and on records, lambdas and 'ref' parameters.
            var source = """
                using System;

                interface I
                {
                    void M(int i is >0);
                    void N(int i);
                }

                abstract class B
                {
                    public virtual void V(int i is >0) { }
                    public abstract void A(int i is >0);
                }

                class D : B, I
                {
                    public override void V(int i is >0) { }
                    public override void A(int i is >0) { }
                    void I.M(int i is >0) { }
                    public void N(int i) { }
                }

                partial class C
                {
                    partial void P(int i is >0);
                    partial void P(int i is >0) { }
                    static extern void E(int i is >0);
                    void R(ref int r is >0, out int o is >0, in int n is >0) { o = 1; }

                    void L()
                    {
                        Action<int> a = delegate (int i is >0) { };
                    }
                }

                delegate void Del(int i is >0);
                record Rec(int X is >0);
                record struct RecS(int X is >0);

                static class Ext
                {
                    extension(int i is >0)
                    {
                        public int Twice() => i * 2;
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,18): error CS20061: A refinement is not supported on an interface member yet.
                //     void M(int i is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on an interface member").WithLocation(5, 18),
                // (11,33): error CS20061: A refinement is not supported on a virtual, abstract or override member yet.
                //     public virtual void V(int i is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a virtual, abstract or override member").WithLocation(11, 33),
                // (12,34): error CS20061: A refinement is not supported on a virtual, abstract or override member yet.
                //     public abstract void A(int i is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a virtual, abstract or override member").WithLocation(12, 34),
                // (17,34): error CS20061: A refinement is not supported on a virtual, abstract or override member yet.
                //     public override void V(int i is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a virtual, abstract or override member").WithLocation(17, 34),
                // (18,34): error CS20061: A refinement is not supported on a virtual, abstract or override member yet.
                //     public override void A(int i is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a virtual, abstract or override member").WithLocation(18, 34),
                // (19,20): error CS20061: A refinement is not supported on an explicit interface implementation yet.
                //     void I.M(int i is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on an explicit interface implementation").WithLocation(19, 20),
                // (25,26): error CS20061: A refinement is not supported on a partial member yet.
                //     partial void P(int i is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a partial member").WithLocation(25, 26),
                // (26,26): error CS20061: A refinement is not supported on a partial member yet.
                //     partial void P(int i is >0) { }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a partial member").WithLocation(26, 26),
                // (27,24): warning CS0626: Method, operator, or accessor 'C.E(int)' is marked external and has no attributes on it. Consider adding a DllImport attribute to specify the external implementation.
                //     static extern void E(int i is >0);
                Diagnostic(ErrorCode.WRN_ExternMethodNoImplementation, "E").WithArguments("C.E(int)").WithLocation(27, 24),
                // (27,32): error CS20061: A refinement is not supported on an extern member yet.
                //     static extern void E(int i is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on an extern member").WithLocation(27, 32),
                // (28,22): error CS20061: A refinement is not supported on a 'ref', 'out' or 'in' parameter yet.
                //     void R(ref int r is >0, out int o is >0, in int n is >0) { o = 1; }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a 'ref', 'out' or 'in' parameter").WithLocation(28, 22),
                // (28,39): error CS20061: A refinement is not supported on a 'ref', 'out' or 'in' parameter yet.
                //     void R(ref int r is >0, out int o is >0, in int n is >0) { o = 1; }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a 'ref', 'out' or 'in' parameter").WithLocation(28, 39),
                // (28,55): error CS20061: A refinement is not supported on a 'ref', 'out' or 'in' parameter yet.
                //     void R(ref int r is >0, out int o is >0, in int n is >0) { o = 1; }
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a 'ref', 'out' or 'in' parameter").WithLocation(28, 55),
                // (32,41): error CS20061: A refinement is not supported on a lambda or anonymous method parameter yet.
                //         Action<int> a = delegate (int i is >0) { };
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a lambda or anonymous method parameter").WithLocation(32, 41),
                // (36,25): error CS20061: A refinement is not supported on a delegate parameter yet.
                // delegate void Del(int i is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a delegate parameter").WithLocation(36, 25),
                // (37,18): error CS20061: A refinement is not supported on a positional parameter of a record yet.
                // record Rec(int X is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a positional parameter of a record").WithLocation(37, 18),
                // (38,26): error CS20061: A refinement is not supported on a positional parameter of a record yet.
                // record struct RecS(int X is >0);
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on a positional parameter of a record").WithLocation(38, 26),
                // (42,21): error CS20061: A refinement is not supported on an extension block receiver yet.
                //     extension(int i is >0)
                Diagnostic(ErrorCode.ERR_SharperRefinementNotSupportedYet, "is").WithArguments("on an extension block receiver").WithLocation(42, 21));
        }

        [Fact]
        public void Refinement_SemanticModel()
        {
            // The pattern of a refinement is not in a member's body; the semantic model answers for the parameter.
            var source = """
                class C
                {
                    const int Max = 10;
                    void M(int i is >=0 and <Max) { }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var parameter = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().Single();
            Assert.Equal("System.Int32 i", model.GetDeclaredSymbol(parameter).ToTestDisplayString());
            var max = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.ValueText == "Max");
            _ = model.GetSymbolInfo(max);
        }
    }
}
