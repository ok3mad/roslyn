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
    /// Step N2 of function type parameter names (issue #213): the names of <c>(int width, int height) -> int</c> are
    /// carried on the constructed <c>Func</c> or <c>Action</c> symbol, the way tuple element names are carried, and
    /// never change what a program means.
    /// </summary>
    public sealed partial class SharperTests
    {
        /// <summary>
        /// The type of every local declared with a declarator, as <c>name: type</c>.
        /// </summary>
        private static string[] FunctionTypeNames_LocalTypes(CSharpCompilation comp)
        {
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            return tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Select(d => model.GetDeclaredSymbol(d)).OfType<ILocalSymbol>()
                .Select(local => local.Name + ": " + local.Type.ToTestDisplayString())
                .ToArray();
        }

        [Fact]
        public void FunctionTypeNames_OnTypeSymbol()
        {
            var source = """
                using System;

                (int width, int height) -> int area = (w, h) => w * h;
                var g = area;
                (int x, int) -> void log = (x, y) => Console.Write(x + y);
                var l = log;
                (int a) -> (int b) -> int adder = a => b => a + b;
                var inner = adder(1);
                // The C# they stand for.
                Func<int, int, int> twin = (w, h) => w * h;
                var t = twin;

                Console.Write(g(3, 4));
                Console.Write(t(3, 4));
                l(1, 2);
                Console.Write(inner(5));
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "121236" : null).VerifyDiagnostics();

            AssertEx.Equal(
            [
                "area: System.Func<System.Int32 width, System.Int32 height, System.Int32>",
                "g: System.Func<System.Int32 width, System.Int32 height, System.Int32>",
                "log: System.Action<System.Int32 x, System.Int32>",
                "l: System.Action<System.Int32 x, System.Int32>",
                "adder: System.Func<System.Int32 a, System.Func<System.Int32 b, System.Int32>>",
                "inner: System.Func<System.Int32 b, System.Int32>",
                "twin: System.Func<System.Int32, System.Int32, System.Int32>",
                "t: System.Func<System.Int32, System.Int32, System.Int32>",
            ], FunctionTypeNames_LocalTypes(comp));

            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var declarators = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().ToArray();
            var g = (INamedTypeSymbol)((ILocalSymbol)model.GetDeclaredSymbol(declarators[1])!).Type;
            var log = (INamedTypeSymbol)((ILocalSymbol)model.GetDeclaredSymbol(declarators[2])!).Type;
            var twin = (INamedTypeSymbol)((ILocalSymbol)model.GetDeclaredSymbol(declarators[6])!).Type;

            // The Invoke method of the type has the names; a parameter without a name keeps Func's or Action's.
            Assert.Equal("System.Int32 System.Func<System.Int32 width, System.Int32 height, System.Int32>.Invoke(System.Int32 width, System.Int32 height)",
                g.DelegateInvokeMethod.ToTestDisplayString());
            Assert.Equal("void System.Action<System.Int32 x, System.Int32>.Invoke(System.Int32 x, System.Int32 arg2)",
                log.DelegateInvokeMethod.ToTestDisplayString());
            Assert.Equal("System.Int32 System.Func<System.Int32, System.Int32, System.Int32>.Invoke(System.Int32 arg1, System.Int32 arg2)",
                twin.DelegateInvokeMethod.ToTestDisplayString());
            Assert.Equal("arg1", g.DelegateInvokeMethod!.Parameters[0].MetadataName);
            Assert.Same(g.DelegateInvokeMethod, g.DelegateInvokeMethod);

            // A member lookup finds Func's Invoke, and the two methods are the same member.
            var invoke = (IMethodSymbol)g.GetMembers("Invoke").Single();
            Assert.Equal("arg1", invoke.Parameters[0].Name);
            Assert.Equal(invoke, g.DelegateInvokeMethod, SymbolEqualityComparer.Default);

            // The names are not part of the type's meaning, but the default comparer tells the types apart, as it
            // does tuples with other names.
            Assert.NotEqual(twin, g, SymbolEqualityComparer.Default);
            Assert.Equal(twin.GetHashCode(), g.GetHashCode());
            Assert.True(twin.GetSymbol<NamedTypeSymbol>().Equals(g.GetSymbol<NamedTypeSymbol>(), TypeCompareKind.IgnoreTupleNames));
            Assert.True(comp.ClassifyConversion(g, twin).IsIdentity);
            Assert.Equal(twin.OriginalDefinition, g.OriginalDefinition, SymbolEqualityComparer.Default);
            Assert.Equal(twin.TypeArguments, g.TypeArguments, SymbolEqualityComparer.Default);

            // GetTypeInfo gives the type with its names, and an invocation the Invoke method with them.
            var functionTypes = tree.GetRoot().DescendantNodes().OfType<FunctionTypeSyntax>().ToArray();
            Assert.Equal("System.Func<System.Int32 width, System.Int32 height, System.Int32>", model.GetTypeInfo(functionTypes[0]).Type.ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32 b, System.Int32>", model.GetTypeInfo(functionTypes[3]).Type.ToTestDisplayString());
            var call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.Expression.ToString() == "g");
            Assert.Equal("System.Int32 System.Func<System.Int32 width, System.Int32 height, System.Int32>.Invoke(System.Int32 width, System.Int32 height)",
                model.GetSymbolInfo(call).Symbol.ToTestDisplayString());
            Assert.Equal("System.Func<System.Int32 width, System.Int32 height, System.Int32>", model.GetTypeInfo(call.Expression).Type.ToTestDisplayString());

            // Error messages show the names; the minimal format too.
            Assert.Equal("System.Func<int width, int height, int>", g.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            Assert.Equal("Func<int width, int height, int>", g.ToMinimalDisplayString(model, declarators[1].SpanStart));
        }

        [Fact]
        public void FunctionTypeNames_FlowThroughExpressions()
        {
            // Names go through var, generic inference, ?:, ??, arrays, tuples and switch expressions. Where two types
            // meet, a name is kept only where both agree, as for tuple element names.
            var source = """
                using System;
                using System.Collections.Generic;

                bool b = args.Length == 0;
                (int width, int height) -> int area = (w, h) => w * h;
                (int width, int height) -> int same = (w, h) => w + h;
                (int width, int depth) -> int other = (w, d) => w - d;
                (int, int) -> int unnamed = (x, y) => x;
                (int width, int height) -> int maybe = null;

                var id = Id(area);
                var pickSame = Pick(area, same);
                var pickOther = Pick(area, other);
                var pickUnnamed = Pick(area, unnamed);
                var condSame = b ? area : same;
                var condOther = b ? area : other;
                var condUnnamed = b ? unnamed : area;
                var condLambda = b ? area : (w, h) => 0;
                var coalesce = maybe ?? area;
                var coalesceOther = maybe ?? other;
                var arraySame = new[] { area, same };
                var arrayOther = new[] { area, other };
                var element = arraySame[0];
                var tuple = (area, 1);
                Console.Write(tuple.Item2);
                var list = new List<(int width, int height) -> int> { area };
                var first = list[0];
                var made = Make(area);
                var sw = b switch { true => area, false => other };
                var wrapped = Wrap(area)();
                Console.Write(id(1, 2) + pickOther(3, 4) + first(5, 6) + wrapped(1, 1));

                static T Id<T>(T t) => t;
                static T Pick<T>(T x, T y) => x;
                static List<T> Make<T>(T t) => new List<T> { t };
                static () -> T Wrap<T>(T t) => () => t;
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "145" : null).VerifyDiagnostics();

            const string named = "System.Func<System.Int32 width, System.Int32 height, System.Int32>";
            const string partly = "System.Func<System.Int32 width, System.Int32, System.Int32>";
            const string plain = "System.Func<System.Int32, System.Int32, System.Int32>";
            AssertEx.Equal(
            [
                "b: System.Boolean",
                "area: " + named,
                "same: " + named,
                "other: System.Func<System.Int32 width, System.Int32 depth, System.Int32>",
                "unnamed: " + plain,
                "maybe: " + named,
                "id: " + named,
                "pickSame: " + named,
                "pickOther: " + partly,
                "pickUnnamed: " + plain,
                "condSame: " + named,
                "condOther: " + partly,
                "condUnnamed: " + plain,
                "condLambda: " + named,
                "coalesce: " + named,
                "coalesceOther: " + named,
                "arraySame: " + named + "[]",
                "arrayOther: " + partly + "[]",
                "element: " + named,
                "tuple: (" + named + " area, System.Int32)",
                "list: System.Collections.Generic.List<" + named + ">",
                "first: " + named,
                "made: System.Collections.Generic.List<" + named + ">",
                "sw: " + partly,
                "wrapped: " + named,
            ], FunctionTypeNames_LocalTypes(comp));
        }

        [Fact]
        public void FunctionTypeNames_FlowThroughExpressions_TupleTwin()
        {
            // The same program with tuples in place of function types: the names are kept and dropped in the same places.
            var source = """
                bool b = args.Length == 0;
                (int width, int height) area = (1, 2);
                (int width, int height) same = (1, 2);
                (int width, int depth) other = (1, 2);
                (int, int) unnamed = (1, 2);
                (int width, int height)? maybe = null;

                var id = Id(area);
                var pickSame = Pick(area, same);
                var pickOther = Pick(area, other);
                var pickUnnamed = Pick(area, unnamed);
                var condSame = b ? area : same;
                var condOther = b ? area : other;
                var condUnnamed = b ? unnamed : area;
                var coalesce = maybe ?? area;
                var coalesceOther = maybe ?? other;
                var arraySame = new[] { area, same };
                var arrayOther = new[] { area, other };
                var sw = b switch { true => area, false => other };

                static T Id<T>(T t) => t;
                static T Pick<T>(T x, T y) => x;
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            comp.VerifyDiagnostics();

            const string named = "(System.Int32 width, System.Int32 height)";
            const string partly = "(System.Int32 width, System.Int32)";
            const string plain = "(System.Int32, System.Int32)";
            AssertEx.Equal(
            [
                "b: System.Boolean",
                "area: " + named,
                "same: " + named,
                "other: (System.Int32 width, System.Int32 depth)",
                "unnamed: " + plain,
                "maybe: " + named + "?",
                "id: " + named,
                "pickSame: " + named,
                "pickOther: " + partly,
                "pickUnnamed: " + plain,
                "condSame: " + named,
                "condOther: " + partly,
                "condUnnamed: " + plain,
                "coalesce: " + named,
                "coalesceOther: " + named,
                "arraySame: " + named + "[]",
                "arrayOther: " + partly + "[]",
                "sw: " + partly,
            ], FunctionTypeNames_LocalTypes(comp));
        }

        [Theory, CombinatorialData]
        public void FunctionTypeNames_LambdaNaturalType(bool sharper)
        {
            // In a Sharper file, the natural type of a lambda has the names of its parameters, as the type of a tuple
            // literal has the names of its elements. With the flag off nothing changes.
            var source = """
                using System;

                var f = (int x, int y) => x + y;
                var action = (string text) => Console.Write(text);
                var discards = (int _, int _) => 0;
                var oneDiscard = (int _, int y) => y;
                var none = () => 1;
                var anonymous = delegate (int a) { return a; };
                var methodGroup = string.IsNullOrEmpty;
                var inferred = Id((int q) => q);
                var array = new[] { (int x) => x, (int x) => -x };
                var mixed = new[] { (int x) => x, (int z) => -z };
                var byRef = (ref int r) => r;
                action(f(1, 2).ToString() + inferred(3) + array[1](4));

                static T Id<T>(T t) => t;
                """;
            var comp = CreateCompilation(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "33-4" : null).VerifyDiagnostics();

            AssertEx.Equal(sharper
                ?
                [
                    "f: System.Func<System.Int32 x, System.Int32 y, System.Int32>",
                    "action: System.Action<System.String text>",
                    "discards: System.Func<System.Int32, System.Int32, System.Int32>",
                    "oneDiscard: System.Func<System.Int32 _, System.Int32 y, System.Int32>",
                    "none: System.Func<System.Int32>",
                    "anonymous: System.Func<System.Int32 a, System.Int32>",
                    "methodGroup: System.Func<System.String?, System.Boolean>",
                    "inferred: System.Func<System.Int32 q, System.Int32>",
                    "array: System.Func<System.Int32 x, System.Int32>[]",
                    "mixed: System.Func<System.Int32, System.Int32>[]",
                    "byRef: <anonymous delegate>",
                ]
                :
                [
                    "f: System.Func<System.Int32, System.Int32, System.Int32>",
                    "action: System.Action<System.String>",
                    "discards: System.Func<System.Int32, System.Int32, System.Int32>",
                    "oneDiscard: System.Func<System.Int32, System.Int32, System.Int32>",
                    "none: System.Func<System.Int32>",
                    "anonymous: System.Func<System.Int32, System.Int32>",
                    "methodGroup: System.Func<System.String?, System.Boolean>",
                    "inferred: System.Func<System.Int32, System.Int32>",
                    "array: System.Func<System.Int32, System.Int32>[]",
                    "mixed: System.Func<System.Int32, System.Int32>[]",
                    "byRef: <anonymous delegate>",
                ], FunctionTypeNames_LocalTypes(comp));
        }

        [Fact]
        public void FunctionTypeNames_ConversionsAndOverloads()
        {
            // No name changes a conversion or the result of overload resolution, and there is no warning for names
            // that differ (tuples have CS8123 for a tuple literal; function types have nothing like it).
            var source = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static string M((int a) -> int f) => "func";
                    static string M(object o) => "object";
                    static string G<T>((T item) -> T f) => typeof(T).Name;
                    static string H((int a) -> int f, (int b) -> int g) => "h";

                    static void Main()
                    {
                        (int a) -> int a1 = x => x;
                        (int b) -> int b1 = a1;
                        (int) -> int plain = b1;
                        Func<int, int> twin = plain;
                        a1 = twin;
                        a1 = (int other) => other;
                        List<(int a) -> int> listA = new List<(int b) -> int>();
                        ((int a) -> int)[] arrayA = new ((int b) -> int)[0];
                        ((int a) -> int f, int n) tuple = (b1, 1);
                        IEnumerable<(int a) -> int> sequence = new List<Func<int, int>>();
                        var cast = ((int c) -> int)a1;

                        Console.Write(M(a1) + M(b1) + M(plain) + M(twin) + M((int z) => z));
                        Console.Write(G(a1) + G((string s) => s) + H(b1, a1));
                        Console.Write(a1 == b1);
                        Console.Write(tuple.n);
                        Console.Write(cast is (int d) -> int);
                        Console.Write(typeof((int a) -> int) == typeof((int b) -> int));
                    }
                }
                """;
            var twinSource = """
                using System;
                using System.Collections.Generic;

                class C
                {
                    static string M(Func<int, int> f) => "func";
                    static string M(object o) => "object";
                    static string G<T>(Func<T, T> f) => typeof(T).Name;
                    static string H(Func<int, int> f, Func<int, int> g) => "h";

                    static void Main()
                    {
                        Func<int, int> a1 = x => x;
                        Func<int, int> b1 = a1;
                        Func<int, int> plain = b1;
                        Func<int, int> twin = plain;
                        a1 = twin;
                        a1 = (int other) => other;
                        List<Func<int, int>> listA = new List<Func<int, int>>();
                        Func<int, int>[] arrayA = new Func<int, int>[0];
                        (Func<int, int> f, int n) tuple = (b1, 1);
                        IEnumerable<Func<int, int>> sequence = new List<Func<int, int>>();
                        var cast = (Func<int, int>)a1;

                        Console.Write(M(a1) + M(b1) + M(plain) + M(twin) + M((int z) => z));
                        Console.Write(G(a1) + G((string s) => s) + H(b1, a1));
                        Console.Write(a1 == b1);
                        Console.Write(tuple.n);
                        Console.Write(cast is Func<int, int>);
                        Console.Write(typeof(Func<int, int>) == typeof(Func<int, int>));
                    }
                }
                """;
            const string expectedOutput = "funcfuncfuncfuncfuncInt32StringhFalse1TrueTrue";
            var verifier = CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.ReleaseExe,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? expectedOutput : null).VerifyDiagnostics();
            var twinVerifier = CompileAndVerify(twinSource, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.ReleaseExe,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? expectedOutput : null).VerifyDiagnostics();

            // The names leave no trace in the code.
            Assert.Equal(twinVerifier.VisualizeIL("C.Main"), verifier.VisualizeIL("C.Main"));
        }

        [Fact]
        public void FunctionTypeNames_MembersThatDifferOnlyInNamesClash()
        {
            var source = """
                using System;

                class C
                {
                    void M((int a) -> int f) { }
                    void M((int b) -> int f) { }
                    void N((int a) -> int f) { }
                    void N(Func<int, int> f) { }
                    void P(out (int a) -> int f) => throw null;
                    void P(ref (int b) -> int f) { }
                    public static implicit operator C((int a) -> int f) => null;
                    public static implicit operator C((int b) -> int f) => null;
                }

                interface I<T> { }
                class D : I<(int a) -> int>, I<(int b) -> int> { }
                class E : I<Func<int, int>>, I<Func<int, int>> { }
                class F : I<(int a, int b)>, I<(int c, int d)> { }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (6,10): error CS0111: Type 'C' already defines a member called 'M' with the same parameter types
                //     void M((int b) -> int f) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "M").WithArguments("M", "C").WithLocation(6, 10),
                // (8,10): error CS0111: Type 'C' already defines a member called 'N' with the same parameter types
                //     void N(Func<int, int> f) { }
                Diagnostic(ErrorCode.ERR_MemberAlreadyExists, "N").WithArguments("N", "C").WithLocation(8, 10),
                // (10,10): error CS0663: 'C' cannot define an overloaded method that differs only on parameter modifiers 'ref' and 'out'
                //     void P(ref (int b) -> int f) { }
                Diagnostic(ErrorCode.ERR_OverloadRefKind, "P").WithArguments("C", "method", "ref", "out").WithLocation(10, 10),
                // (12,37): error CS0557: Duplicate user-defined conversion in type 'C'
                //     public static implicit operator C((int b) -> int f) => null;
                Diagnostic(ErrorCode.ERR_DuplicateConversionInClass, "C").WithArguments("C").WithLocation(12, 37),
                // (16,7): error CS8779: 'I<Func<int b, int>>' is already listed in the interface list on type 'D' as 'I<Func<int a, int>>'.
                // class D : I<(int a) -> int>, I<(int b) -> int> { }
                Diagnostic(ErrorCode.ERR_DuplicateInterfaceWithDifferencesInBaseList, "D").WithArguments("I<System.Func<int b, int>>", "I<System.Func<int a, int>>", "D").WithLocation(16, 7),
                // (17,30): error CS0528: 'I<Func<int, int>>' is already listed in interface list
                // class E : I<Func<int, int>>, I<Func<int, int>> { }
                Diagnostic(ErrorCode.ERR_DuplicateInterfaceInBaseList, "I<Func<int, int>>").WithArguments("I<System.Func<int, int>>").WithLocation(17, 30),
                // (18,7): error CS8140: 'I<(int c, int d)>' is already listed in the interface list on type 'F' with different tuple element names, as 'I<(int a, int b)>'.
                // class F : I<(int a, int b)>, I<(int c, int d)> { }
                Diagnostic(ErrorCode.ERR_DuplicateInterfaceWithTupleNamesInBaseList, "F").WithArguments("I<(int c, int d)>", "I<(int a, int b)>", "F").WithLocation(18, 7));
        }

        [Fact]
        public void FunctionTypeNames_NamedArguments()
        {
            // Until the names are in metadata (N4), a named argument cannot use them: with a metadata reference they
            // would be gone, and the same call would mean something else. Func's own names keep working.
            var source = """
                using System;

                (int width, int height) -> int area = (w, h) => w * h;
                Func<int, int, int> twin = area;
                Console.Write(area(arg1: 3, arg2: 4));
                Console.Write(area(arg2: 3, arg1: 4));
                Console.Write(twin(arg1: 3, arg2: 4));
                Console.Write(area.Invoke(arg1: 3, arg2: 4));
                area(width: 3, height: 4);
                area(3, height: 4);
                twin(width: 3, height: 4);
                area.Invoke(width: 3, height: 4);
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            comp.VerifyDiagnostics(
                // (9,6): error CS1746: The delegate 'Func<int, int, int>' does not have a parameter named 'width'
                // area(width: 3, height: 4);
                Diagnostic(ErrorCode.ERR_BadNamedArgumentForDelegateInvoke, "width").WithArguments("System.Func<int, int, int>", "width").WithLocation(9, 6),
                // (10,9): error CS1746: The delegate 'Func<int, int, int>' does not have a parameter named 'height'
                // area(3, height: 4);
                Diagnostic(ErrorCode.ERR_BadNamedArgumentForDelegateInvoke, "height").WithArguments("System.Func<int, int, int>", "height").WithLocation(10, 9),
                // (11,6): error CS1746: The delegate 'Func<int, int, int>' does not have a parameter named 'width'
                // twin(width: 3, height: 4);
                Diagnostic(ErrorCode.ERR_BadNamedArgumentForDelegateInvoke, "width").WithArguments("System.Func<int, int, int>", "width").WithLocation(11, 6),
                // (12,13): error CS1739: The best overload for 'Invoke' does not have a parameter named 'width'
                // area.Invoke(width: 3, height: 4);
                Diagnostic(ErrorCode.ERR_BadNamedArgument, "width").WithArguments("Invoke", "width").WithLocation(12, 13));
        }

        [Fact]
        public void FunctionTypeNames_Overrides()
        {
            // As for tuple element names: an override or implementation cannot change a name (CS8139 and CS8141 for
            // tuples), but may drop all of them.
            var source = """
                using System;

                abstract class B
                {
                    public abstract void M((int a) -> int f);
                    public abstract (int a) -> int R();
                    public abstract (int a) -> int P { get; }
                    public abstract event (int a) -> void E;
                    public abstract void Plain(Func<int, int> f);
                    public abstract void Tuple((int a, int b) t);
                    public abstract void Both((int a, int b) t, (int c) -> int f);
                }

                class Same : B
                {
                    public override void M((int a) -> int f) { }
                    public override (int a) -> int R() => null;
                    public override (int a) -> int P => null;
                    public override event (int a) -> void E { add { } remove { } }
                    public override void Plain(Func<int, int> f) { }
                    public override void Tuple((int a, int b) t) { }
                    public override void Both((int a, int b) t, (int c) -> int f) { }
                }

                class Dropped : B
                {
                    public override void M((int) -> int f) { }
                    public override Func<int, int> R() => null;
                    public override (int) -> int P => null;
                    public override event Action<int> E { add { } remove { } }
                    public override void Plain(Func<int, int> f) { }
                    public override void Tuple((int, int) t) { }
                    public override void Both((int, int) t, (int) -> int f) { }
                }

                class Changed : B
                {
                    public override void M((int x) -> int f) { }
                    public override (int x) -> int R() => null;
                    public override (int x) -> int P => null;
                    public override event (int x) -> void E { add { } remove { } }
                    public override void Plain((int x) -> int f) { }
                    public override void Tuple((int x, int y) t) { }
                    public override void Both((int a, int b) t, (int x) -> int f) { }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (38,26): error CS20064: 'Changed.M(Func<int x, int>)': cannot change function type parameter names when overriding inherited member 'B.M(Func<int a, int>)'
                //     public override void M((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "M").WithArguments("Changed.M(System.Func<int x, int>)", "B.M(System.Func<int a, int>)").WithLocation(38, 26),
                // (39,36): error CS20064: 'Changed.R()': cannot change function type parameter names when overriding inherited member 'B.R()'
                //     public override (int x) -> int R() => null;
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "R").WithArguments("Changed.R()", "B.R()").WithLocation(39, 36),
                // (40,36): error CS20064: 'Changed.P': cannot change function type parameter names when overriding inherited member 'B.P'
                //     public override (int x) -> int P => null;
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "P").WithArguments("Changed.P", "B.P").WithLocation(40, 36),
                // (40,41): error CS20064: 'Changed.P.get': cannot change function type parameter names when overriding inherited member 'B.P.get'
                //     public override (int x) -> int P => null;
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "null").WithArguments("Changed.P.get", "B.P.get").WithLocation(40, 41),
                // (41,43): error CS20064: 'Changed.E': cannot change function type parameter names when overriding inherited member 'B.E'
                //     public override event (int x) -> void E { add { } remove { } }
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "E").WithArguments("Changed.E", "B.E").WithLocation(41, 43),
                // (41,47): error CS20064: 'Changed.E.add': cannot change function type parameter names when overriding inherited member 'B.E.add'
                //     public override event (int x) -> void E { add { } remove { } }
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "add").WithArguments("Changed.E.add", "B.E.add").WithLocation(41, 47),
                // (41,55): error CS20064: 'Changed.E.remove': cannot change function type parameter names when overriding inherited member 'B.E.remove'
                //     public override event (int x) -> void E { add { } remove { } }
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "remove").WithArguments("Changed.E.remove", "B.E.remove").WithLocation(41, 55),
                // (42,26): error CS20064: 'Changed.Plain(Func<int x, int>)': cannot change function type parameter names when overriding inherited member 'B.Plain(Func<int, int>)'
                //     public override void Plain((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperCantChangeFunctionTypeNamesOnOverride, "Plain").WithArguments("Changed.Plain(System.Func<int x, int>)", "B.Plain(System.Func<int, int>)").WithLocation(42, 26),
                // (43,26): error CS8139: 'Changed.Tuple((int x, int y))': cannot change tuple element names when overriding inherited member 'B.Tuple((int a, int b))'
                //     public override void Tuple((int x, int y) t) { }
                Diagnostic(ErrorCode.ERR_CantChangeTupleNamesOnOverride, "Tuple").WithArguments("Changed.Tuple((int x, int y))", "B.Tuple((int a, int b))").WithLocation(43, 26),
                // (44,26): error CS8139: 'Changed.Both((int a, int b), Func<int x, int>)': cannot change tuple element names when overriding inherited member 'B.Both((int a, int b), Func<int c, int>)'
                //     public override void Both((int a, int b) t, (int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_CantChangeTupleNamesOnOverride, "Both").WithArguments("Changed.Both((int a, int b), System.Func<int x, int>)", "B.Both((int a, int b), System.Func<int c, int>)").WithLocation(44, 26));
        }

        [Fact]
        public void FunctionTypeNames_Implementations()
        {
            var source = """
                using System;

                interface I
                {
                    void M((int a) -> int f);
                    (int a) -> int R();
                    (int a) -> int P { get; }
                    void Plain(Func<int, int> f);
                    void Tuple((int a, int b) t);
                }

                class Same : I
                {
                    public void M((int a) -> int f) { }
                    public (int a) -> int R() => null;
                    public (int a) -> int P => null;
                    public void Plain(Func<int, int> f) { }
                    public void Tuple((int a, int b) t) { }
                }

                class Dropped : I
                {
                    public void M((int) -> int f) { }
                    public Func<int, int> R() => null;
                    public (int) -> int P => null;
                    public void Plain(Func<int, int> f) { }
                    public void Tuple((int, int) t) { }
                }

                class Changed : I
                {
                    public void M((int x) -> int f) { }
                    public (int x) -> int R() => null;
                    public (int x) -> int P => null;
                    public void Plain((int x) -> int f) { }
                    public void Tuple((int x, int y) t) { }
                }

                class ExplicitDropped : I
                {
                    void I.M((int) -> int f) { }
                    (int) -> int I.R() => null;
                    Func<int, int> I.P => null;
                    void I.Plain(Func<int, int> f) { }
                    void I.Tuple((int, int) t) { }
                }

                class ExplicitChanged : I
                {
                    void I.M((int x) -> int f) { }
                    (int x) -> int I.R() => null;
                    (int x) -> int I.P => null;
                    void I.Plain((int x) -> int f) { }
                    void I.Tuple((int x, int y) t) { }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (32,17): error CS20065: The function type parameter names in the signature of 'Changed.M(Func<int x, int>)' must match the function type parameter names of interface member 'I.M(Func<int a, int>)' (including on the return type).
                //     public void M((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "M").WithArguments("Changed.M(System.Func<int x, int>)", "I.M(System.Func<int a, int>)").WithLocation(32, 17),
                // (33,27): error CS20065: The function type parameter names in the signature of 'Changed.R()' must match the function type parameter names of interface member 'I.R()' (including on the return type).
                //     public (int x) -> int R() => null;
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "R").WithArguments("Changed.R()", "I.R()").WithLocation(33, 27),
                // (34,27): error CS20065: The function type parameter names in the signature of 'Changed.P' must match the function type parameter names of interface member 'I.P' (including on the return type).
                //     public (int x) -> int P => null;
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "P").WithArguments("Changed.P", "I.P").WithLocation(34, 27),
                // (34,32): error CS20065: The function type parameter names in the signature of 'Changed.P.get' must match the function type parameter names of interface member 'I.P.get' (including on the return type).
                //     public (int x) -> int P => null;
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "null").WithArguments("Changed.P.get", "I.P.get").WithLocation(34, 32),
                // (35,17): error CS20065: The function type parameter names in the signature of 'Changed.Plain(Func<int x, int>)' must match the function type parameter names of interface member 'I.Plain(Func<int, int>)' (including on the return type).
                //     public void Plain((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "Plain").WithArguments("Changed.Plain(System.Func<int x, int>)", "I.Plain(System.Func<int, int>)").WithLocation(35, 17),
                // (36,17): error CS8141: The tuple element names in the signature of method 'Changed.Tuple((int x, int y))' must match the tuple element names of interface method 'I.Tuple((int a, int b))' (including on the return type).
                //     public void Tuple((int x, int y) t) { }
                Diagnostic(ErrorCode.ERR_ImplBadTupleNames, "Tuple").WithArguments("Changed.Tuple((int x, int y))", "I.Tuple((int a, int b))").WithLocation(36, 17),
                // (50,12): error CS20065: The function type parameter names in the signature of 'ExplicitChanged.I.M(Func<int x, int>)' must match the function type parameter names of interface member 'I.M(Func<int a, int>)' (including on the return type).
                //     void I.M((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "M").WithArguments("ExplicitChanged.I.M(System.Func<int x, int>)", "I.M(System.Func<int a, int>)").WithLocation(50, 12),
                // (51,22): error CS20065: The function type parameter names in the signature of 'ExplicitChanged.I.R()' must match the function type parameter names of interface member 'I.R()' (including on the return type).
                //     (int x) -> int I.R() => null;
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "R").WithArguments("ExplicitChanged.I.R()", "I.R()").WithLocation(51, 22),
                // (52,22): error CS20065: The function type parameter names in the signature of 'ExplicitChanged.I.P' must match the function type parameter names of interface member 'I.P' (including on the return type).
                //     (int x) -> int I.P => null;
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "P").WithArguments("ExplicitChanged.I.P", "I.P").WithLocation(52, 22),
                // (53,12): error CS20065: The function type parameter names in the signature of 'ExplicitChanged.I.Plain(Func<int x, int>)' must match the function type parameter names of interface member 'I.Plain(Func<int, int>)' (including on the return type).
                //     void I.Plain((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperImplBadFunctionTypeNames, "Plain").WithArguments("ExplicitChanged.I.Plain(System.Func<int x, int>)", "I.Plain(System.Func<int, int>)").WithLocation(53, 12),
                // (54,12): error CS8141: The tuple element names in the signature of method 'ExplicitChanged.I.Tuple((int x, int y))' must match the tuple element names of interface method 'I.Tuple((int a, int b))' (including on the return type).
                //     void I.Tuple((int x, int y) t) { }
                Diagnostic(ErrorCode.ERR_ImplBadTupleNames, "Tuple").WithArguments("ExplicitChanged.I.Tuple((int x, int y))", "I.Tuple((int a, int b))").WithLocation(54, 12));
        }

        [Fact]
        public void FunctionTypeNames_PartialMembers()
        {
            // As for tuple element names (CS8142): both parts of a partial member use the same names.
            var source = """
                using System;

                partial class C
                {
                    partial void Same((int a) -> int f);
                    partial void Same((int a) -> int f) { }
                    partial void Changed((int a) -> int f);
                    partial void Changed((int x) -> int f) { }
                    partial void Dropped((int a) -> int f);
                    partial void Dropped(Func<int, int> f) { }
                    partial void Added((int) -> int f);
                    partial void Added((int a) -> int f) { }
                    public partial (int a) -> int R();
                    public partial (int x) -> int R() => null;
                    public partial (int a) -> int P { get; }
                    public partial (int x) -> int P => null;
                    partial void Tuple((int a, int b) t);
                    partial void Tuple((int x, int y) t) { }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (8,18): error CS20066: Both partial member declarations, 'C.Changed(Func<int a, int>)' and 'C.Changed(Func<int x, int>)', must use the same function type parameter names.
                //     partial void Changed((int x) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperPartialMemberInconsistentFunctionTypeNames, "Changed").WithArguments("C.Changed(System.Func<int a, int>)", "C.Changed(System.Func<int x, int>)").WithLocation(8, 18),
                // (10,18): error CS20066: Both partial member declarations, 'C.Dropped(Func<int a, int>)' and 'C.Dropped(Func<int, int>)', must use the same function type parameter names.
                //     partial void Dropped(Func<int, int> f) { }
                Diagnostic(ErrorCode.ERR_SharperPartialMemberInconsistentFunctionTypeNames, "Dropped").WithArguments("C.Dropped(System.Func<int a, int>)", "C.Dropped(System.Func<int, int>)").WithLocation(10, 18),
                // (12,18): error CS20066: Both partial member declarations, 'C.Added(Func<int, int>)' and 'C.Added(Func<int a, int>)', must use the same function type parameter names.
                //     partial void Added((int a) -> int f) { }
                Diagnostic(ErrorCode.ERR_SharperPartialMemberInconsistentFunctionTypeNames, "Added").WithArguments("C.Added(System.Func<int, int>)", "C.Added(System.Func<int a, int>)").WithLocation(12, 18),
                // (14,35): error CS20066: Both partial member declarations, 'C.R()' and 'C.R()', must use the same function type parameter names.
                //     public partial (int x) -> int R() => null;
                Diagnostic(ErrorCode.ERR_SharperPartialMemberInconsistentFunctionTypeNames, "R").WithArguments("C.R()", "C.R()").WithLocation(14, 35),
                // (16,35): error CS20066: Both partial member declarations, 'C.P' and 'C.P', must use the same function type parameter names.
                //     public partial (int x) -> int P => null;
                Diagnostic(ErrorCode.ERR_SharperPartialMemberInconsistentFunctionTypeNames, "P").WithArguments("C.P", "C.P").WithLocation(16, 35),
                // (18,18): error CS8142: Both partial member declarations, 'C.Tuple((int a, int b))' and 'C.Tuple((int x, int y))', must use the same tuple element names.
                //     partial void Tuple((int x, int y) t) { }
                Diagnostic(ErrorCode.ERR_PartialMemberInconsistentTupleNames, "Tuple").WithArguments("C.Tuple((int a, int b))", "C.Tuple((int x, int y))").WithLocation(18, 18));
        }

        [Fact]
        public void FunctionTypeNames_Nullable()
        {
            // The names stay on the type when nullable analysis rewrites its type arguments, and the warnings are those
            // of the Func spelling.
            var source = """
                #nullable enable
                using System;

                class C
                {
                    static void Main()
                    {
                        bool b = false;
                        (string? text) -> string f = s => s ?? "";
                        (string text) -> string? g = s => null;
                        var copy = f;
                        var cond = b ? f : g;
                        var id = Id(f);
                        var array = new[] { f, g };
                        var maybe = b ? f : null;
                        var lambda = (string? text, string other) => text ?? other;
                        f = g;
                        g = f;
                        maybe(null);
                        f(null);
                        g(null);

                        Func<string?, string> ft = s => s ?? "";
                        Func<string, string?> gt = s => null;
                        ft = gt;
                        gt(null);
                    }

                    static T Id<T>(T t) => t;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            comp.VerifyDiagnostics(
                // (17,13): warning CS8619: Nullability of reference types in value of type 'Func<string text, string?>' doesn't match target type 'Func<string? text, string>'.
                //         f = g;
                Diagnostic(ErrorCode.WRN_NullabilityMismatchInAssignment, "g").WithArguments("System.Func<string text, string?>", "System.Func<string? text, string>").WithLocation(17, 13),
                // (19,9): warning CS8602: Dereference of a possibly null reference.
                //         maybe(null);
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "maybe").WithLocation(19, 9),
                // (21,11): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         g(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(21, 11),
                // (25,14): warning CS8619: Nullability of reference types in value of type 'Func<string, string?>' doesn't match target type 'Func<string?, string>'.
                //         ft = gt;
                Diagnostic(ErrorCode.WRN_NullabilityMismatchInAssignment, "gt").WithArguments("System.Func<string, string?>", "System.Func<string?, string>").WithLocation(25, 14),
                // (26,12): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         gt(null);
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(26, 12));
            Assert.Equal("", string.Join("|", FunctionTypeNames_LocalTypes(comp)));
        }

        [Fact]
        public void FunctionTypeNames_Substitution()
        {
            // Names survive the substitution of type arguments, in members of constructed types and in constructed methods.
            var source = """
                class G<T>
                {
                    public (T item, int count) -> T F;
                    public ((T item) -> U) Make<U>((U result) -> T f) => null;
                    public class Nested<V> { public (T first, V second) -> void A; }
                }

                class D : G<string> { }

                class C
                {
                    static void Main()
                    {
                        var g = new G<int>();
                        var f = g.F;
                        var made = g.Make<long>(null);
                        var nested = new G<int>.Nested<string>().A;
                        var inherited = new D().F;
                    }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            comp.VerifyDiagnostics(
                // (3,37): warning CS0649: Field 'G<T>.F' is never assigned to, and will always have its default value null
                //     public (T item, int count) -> T F;
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "F").WithArguments("G<T>.F", "null").WithLocation(3, 37),
                // (5,65): warning CS0649: Field 'G<T>.Nested<V>.A' is never assigned to, and will always have its default value null
                //     public class Nested<V> { public (T first, V second) -> void A; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "A").WithArguments("G<T>.Nested<V>.A", "null").WithLocation(5, 65));
            AssertEx.Equal(
            [
                "g: G<System.Int32>",
                "f: System.Func<System.Int32 item, System.Int32 count, System.Int32>",
                "made: System.Func<System.Int32 item, System.Int64>",
                "nested: System.Action<System.Int32 first, System.String second>",
                "inherited: System.Func<System.String item, System.Int32 count, System.String>",
            ], FunctionTypeNames_LocalTypes(comp));

            Assert.Equal("System.Func<System.Int64 result, System.Int32> f",
                comp.GetMember<NamedTypeSymbol>("G").Construct(comp.GetSpecialType(SpecialType.System_Int32)).GetMember<MethodSymbol>("Make")
                    .Construct(comp.GetSpecialType(SpecialType.System_Int64)).Parameters[0].ToTestDisplayString());
        }

        [Fact]
        public void FunctionTypeNames_NotInMetadata()
        {
            // Names exist within one compilation only (metadata is step N4): nothing is emitted for them, the name of an
            // explicit implementation does not contain them, and a reference to the assembly sees the plain delegates.
            var source = """
                public interface I<T> { void M(T t); }

                public class C : I<(int a) -> int>
                {
                    public (int width, int height) -> int Area;
                    public ((int x) -> int f, (int a, int b) t) Mixed;
                    public (int width, int height) -> int M((string text) -> void log) => null;
                    void I<(int a) -> int>.M((int a) -> int t) { }
                }
                """;
            var twinSource = """
                using System;

                public interface I<T> { void M(T t); }

                public class C : I<Func<int, int>>
                {
                    public Func<int, int, int> Area;
                    public (Func<int, int> f, (int a, int b) t) Mixed;
                    public Func<int, int, int> M(Action<string> log) => null;
                    void I<Func<int, int>>.M(Func<int, int> t) { }
                }
                """;

            string dump(ModuleSymbol module)
            {
                var c = module.GlobalNamespace.GetMember<NamedTypeSymbol>("C");
                return string.Join("\n", c.GetMembers().Select(m => m.ToTestDisplayString() + " [" + string.Join(", ", m.GetAttributes().Select(a => a.ToString())) + "] " + m.MetadataName)
                    .Concat(c.GetMember<MethodSymbol>("M").Parameters.Select(p => p.ToTestDisplayString() + " [" + string.Join(", ", p.GetAttributes()) + "]")));
            }

            string? metadata = null;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            CompileAndVerify(comp, symbolValidator: module => metadata = dump(module)).VerifyDiagnostics();
            string? twinMetadata = null;
            CompileAndVerify(twinSource, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, symbolValidator: module => twinMetadata = dump(module)).VerifyDiagnostics();
            Assert.Equal(twinMetadata, metadata);
            Assert.Contains("I<System.Func<System.Int32,System.Int32>>.M", metadata);

            var user = """
                var c = new C();
                var area = c.Area;
                var mixed = c.Mixed;
                """;
            AssertEx.Equal(
            [
                "c: C",
                "area: System.Func<System.Int32, System.Int32, System.Int32>",
                "mixed: (System.Func<System.Int32, System.Int32> f, (System.Int32 a, System.Int32 b) t)",
            ], FunctionTypeNames_LocalTypes(CreateCompilation(user, [comp.EmitToImageReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics()));

            // A compilation reference shares the symbols, and with them the names.
            // PROTOTYPE(sharper): the two kinds of reference agree once the names are in metadata (https://github.com/ok3mad/roslyn/issues/215).
            AssertEx.Equal(
            [
                "c: C",
                "area: System.Func<System.Int32 width, System.Int32 height, System.Int32>",
                "mixed: (System.Func<System.Int32 x, System.Int32> f, (System.Int32 a, System.Int32 b) t)",
            ], FunctionTypeNames_LocalTypes(CreateCompilation(user, [comp.ToMetadataReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics()));
        }
    }
}
