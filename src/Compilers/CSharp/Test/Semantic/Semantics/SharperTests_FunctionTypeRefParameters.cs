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
    /// Function types that <c>Func</c> and <c>Action</c> cannot express (issue #56): <c>ref</c>, <c>out</c>, <c>in</c>,
    /// <c>ref readonly</c> and <c>scoped</c> parameters, ref returns and more than 16 parameters are the anonymous
    /// delegate of a lambda's natural type, the same type <c>var</c> gives the equivalent lambda.
    /// </summary>
    public sealed partial class SharperTests
    {
        /// <summary>
        /// The declared local of each declarator, by name.
        /// </summary>
        private static ILocalSymbol FunctionTypeRef_Local(CSharpCompilation comp, string name)
        {
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            return tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(d => d.Identifier.ValueText == name)
                .Select(d => (ILocalSymbol)model.GetDeclaredSymbol(d)!)
                .Single();
        }

        private static void FunctionTypeRef_AssertSameType(CSharpCompilation comp, string functionType, string twin)
        {
            var type = FunctionTypeRef_Local(comp, functionType).Type;
            var twinType = FunctionTypeRef_Local(comp, twin).Type;
            Assert.True(type.IsAnonymousType, functionType);
            Assert.Equal(TypeKind.Delegate, type.TypeKind);
            Assert.Equal(twinType, type, SymbolEqualityComparer.Default);
            // A var local is annotated at the top level; the annotations inside the delegate must agree.
            Assert.Equal(twinType.WithNullableAnnotation(CodeAnalysis.NullableAnnotation.NotAnnotated), type.WithNullableAnnotation(CodeAnalysis.NullableAnnotation.NotAnnotated), SymbolEqualityComparer.IncludeNullability);
        }

        [Fact]
        public void FunctionTypeRef_Modifiers()
        {
            // Each function type next to the lambda whose natural type it is.
            var source = """
                using System;

                (ref int) -> int refParameter = (ref int x) => x++;
                var refTwin = (ref int x) => x++;
                (out string) -> bool outParameter = (out string s) => { s = "o"; return true; };
                var outTwin = (out string s) => { s = "o"; return true; };
                (in int) -> int inParameter = (in int x) => x + 1;
                var inTwin = (in int x) => x + 1;
                (ref readonly int) -> int refReadonlyParameter = (ref readonly int x) => x + 2;
                var refReadonlyTwin = (ref readonly int x) => x + 2;
                (scoped ref int) -> void scopedRef = (scoped ref int x) => x = 3;
                var scopedRefTwin = (scoped ref int x) => { x = 3; };
                (scoped Span<int>) -> int scopedValue = (scoped Span<int> s) => s.Length;
                var scopedValueTwin = (scoped Span<int> s) => s.Length;
                (scoped in int) -> int scopedIn = (scoped in int x) => x;
                var scopedInTwin = (scoped in int x) => x;
                () -> ref int refReturn = () => ref Holder.Value;
                var refReturnTwin = () => ref Holder.Value;
                () -> ref readonly int refReadonlyReturn = () => ref Holder.Value;
                var refReadonlyReturnTwin = ref readonly int () => ref Holder.Value;
                (ref int, out string, int) -> ref int mixed = (ref int a, out string b, int c) => { b = "m"; return ref a; };
                var mixedTwin = (ref int a, out string b, int c) => { b = "m"; return ref a; };

                int v = 1;
                Console.Write(refParameter(ref v));
                Console.Write(v);
                outParameter(out var s);
                Console.Write(s);
                Console.Write(inParameter(in v));
                Console.Write(refReadonlyParameter(ref v));
                scopedRef(ref v);
                Console.Write(v);
                Console.Write(scopedValue(stackalloc int[4]));
                Console.Write(scopedIn(v));
                refReturn() = 5;
                Console.Write(refReadonlyReturn());
                mixed(ref v, out s, 0) = 7;
                Console.Write(v + s);

                // One type per signature: the same runtime type as the lambda's.
                Console.Write(refParameter.GetType() == refTwin.GetType());
                Console.Write(mixed.GetType() == mixedTwin.GetType());
                refTwin = refParameter;
                refParameter = refTwin;

                static class Holder
                {
                    public static int Value;
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "12o3434357mTrueTrue" : null, verify: Verification.Skipped).VerifyDiagnostics();

            FunctionTypeRef_AssertSameType(comp, "refParameter", "refTwin");
            FunctionTypeRef_AssertSameType(comp, "outParameter", "outTwin");
            FunctionTypeRef_AssertSameType(comp, "inParameter", "inTwin");
            FunctionTypeRef_AssertSameType(comp, "refReadonlyParameter", "refReadonlyTwin");
            FunctionTypeRef_AssertSameType(comp, "scopedRef", "scopedRefTwin");
            FunctionTypeRef_AssertSameType(comp, "scopedValue", "scopedValueTwin");
            FunctionTypeRef_AssertSameType(comp, "scopedIn", "scopedInTwin");
            FunctionTypeRef_AssertSameType(comp, "refReturn", "refReturnTwin");
            FunctionTypeRef_AssertSameType(comp, "refReadonlyReturn", "refReadonlyReturnTwin");
            FunctionTypeRef_AssertSameType(comp, "mixed", "mixedTwin");

            var invoke = ((INamedTypeSymbol)FunctionTypeRef_Local(comp, "mixed").Type).DelegateInvokeMethod!;
            AssertEx.Equal("ref System.Int32 <anonymous delegate>.Invoke(ref System.Int32 arg1, out System.String arg2, System.Int32 arg3)", invoke.ToTestDisplayString());
            Assert.Equal(RefKind.Ref, invoke.RefKind);
            Assert.Equal(ScopedKind.ScopedRef, ((INamedTypeSymbol)FunctionTypeRef_Local(comp, "scopedRef").Type).DelegateInvokeMethod!.Parameters[0].ScopedKind);
            Assert.Equal(ScopedKind.ScopedValue, ((INamedTypeSymbol)FunctionTypeRef_Local(comp, "scopedValue").Type).DelegateInvokeMethod!.Parameters[0].ScopedKind);
        }

        [Fact]
        public void FunctionTypeRef_SeventeenParameters()
        {
            var source = """
                using System;

                (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int sum =
                    (a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q) => a + b + c + d + e + f + g + h + i + j + k + l + m + n + o + p + q;
                var sumTwin = (int a, int b, int c, int d, int e, int f, int g, int h, int i, int j, int k, int l, int m, int n, int o, int p, int q) => a + q;
                (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> void log =
                    (a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q) => Console.Write(a + q);
                var logTwin = (int a, int b, int c, int d, int e, int f, int g, int h, int i, int j, int k, int l, int m, int n, int o, int p, int q) => Console.Write(a + q);
                // Sixteen is still Func.
                (int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int) -> int sixteen = null;
                Func<int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int> sixteenTwin = sixteen;

                Console.Write(sum(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1));
                log(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2);
                sum = sumTwin;
                log = logTwin;
                Console.Write(sum(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4));
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1735" : null).VerifyDiagnostics();

            FunctionTypeRef_AssertSameType(comp, "sum", "sumTwin");
            FunctionTypeRef_AssertSameType(comp, "log", "logTwin");
            Assert.False(FunctionTypeRef_Local(comp, "sixteen").Type.IsAnonymousType);
        }

        [Fact]
        public void FunctionTypeRef_Identity()
        {
            var source = """
                #pragma warning disable CS0219 // The variable is assigned but its value is never used
                (ref int a) -> int named = null;
                (ref int b) -> int otherNames = named;          // Names are not part of the type.
                (ref int) -> int unnamed = otherNames;
                (in int) -> int inKind = named;                 // Another ref kind is another type.
                (ref object) -> string source = null;
                (ref object) -> object covariant = source;      // Invariant, as all anonymous delegates are.
                D d = named;                                    // Converts to no named delegate,
                D viaInvoke = named.Invoke;                     // but its Invoke does,
                (ref int) -> int fromNamed = d;
                (ref int) -> int fromNamedInvoke = d.Invoke;    // and so does a named delegate's.
                (ref int) -> int fromMethod = M;
                static int M(ref int x) => x;
                delegate int D(ref int x);
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (5,26): error CS0029: Cannot implicitly convert type '<anonymous delegate>' to '<anonymous delegate>'
                // (in int) -> int inKind = named;                 // Another ref kind is another type.
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "named").WithArguments("<anonymous delegate>", "<anonymous delegate>").WithLocation(5, 26),
                // (7,36): error CS0029: Cannot implicitly convert type '<anonymous delegate>' to '<anonymous delegate>'
                // (ref object) -> object covariant = source;      // Invariant, as all anonymous delegates are.
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "source").WithArguments("<anonymous delegate>", "<anonymous delegate>").WithLocation(7, 36),
                // (8,7): error CS0029: Cannot implicitly convert type '<anonymous delegate>' to 'D'
                // D d = named;                                    // Converts to no named delegate,
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "named").WithArguments("<anonymous delegate>", "D").WithLocation(8, 7),
                // (10,30): error CS0029: Cannot implicitly convert type 'D' to '<anonymous delegate>'
                // (ref int) -> int fromNamed = d;
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "d").WithArguments("D", "<anonymous delegate>").WithLocation(10, 30));

            var named = FunctionTypeRef_Local(comp, "named").Type;
            Assert.Equal(named, FunctionTypeRef_Local(comp, "otherNames").Type, SymbolEqualityComparer.Default);
            Assert.Equal(named, FunctionTypeRef_Local(comp, "unnamed").Type, SymbolEqualityComparer.Default);
            // The anonymous delegate has its own parameter names, and does not take the function type's.
            AssertEx.Equal("System.Int32 <anonymous delegate>.Invoke(ref System.Int32 arg)", ((INamedTypeSymbol)named).DelegateInvokeMethod!.ToTestDisplayString());
        }

        [Fact]
        public void FunctionTypeRef_Nested()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                ((ref int) -> int) -> int apply = f => { int x = 4; return f(ref x); };
                Func<(ref int) -> int, int> applyTwin = apply;
                (int) -> (ref int) -> int curry = n => (ref int x) => x += n;
                (ref int) -> (ref int) -> void bothRef = (ref int x) => { int c = x; return (ref int y) => y = c; };
                ((ref int) -> int)[] array = [(ref int x) => x * 2];
                ((ref int) -> int)? nullable = null;
                List<(ref int) -> int> list = [(ref int x) => x * 3];
                Dictionary<string, () -> ref int> dictionary = new();

                Console.Write(apply((ref int x) => x + 1));
                Console.Write(apply(curry(10)));
                Console.Write(apply(array[0]));
                Console.Write(apply(list[0]));
                int a = 1, b = 2;
                bothRef(ref a)(ref b);
                Console.Write(b);
                Console.Write(Id(list[0]) == list[0]);
                Console.Write(nullable is null);

                static T Id<T>(T t) => t;
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe.WithNullableContextOptions(NullableContextOptions.Enable));
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "5148121TrueTrue" : null).VerifyDiagnostics();

            AssertEx.Equal("System.Func<<anonymous delegate>, System.Int32>", FunctionTypeRef_Local(comp, "apply").Type.ToTestDisplayString());
            AssertEx.Equal("System.Collections.Generic.List<<anonymous delegate>>", FunctionTypeRef_Local(comp, "list").Type.ToTestDisplayString());
            AssertEx.Equal("<anonymous delegate>[]", FunctionTypeRef_Local(comp, "array").Type.ToTestDisplayString());
            var curry = (INamedTypeSymbol)FunctionTypeRef_Local(comp, "curry").Type;
            Assert.True(curry.TypeArguments[1].IsAnonymousType);
        }

        [Fact]
        public void FunctionTypeRef_Members()
        {
            // Allowed wherever an internal type is: private and internal members, public members of internal types,
            // and type arguments in them, with an emitted signature that uses the synthesized delegate.
            var source = """
                using System;
                using System.Collections.Generic;

                var c = new C();
                int x = 1;
                c.Set((ref int v) => v += 10);
                Console.Write(c.Call(ref x));
                Console.Write(x);
                Console.Write(Internal.Make()(ref x));
                Console.Write(c.Count);

                public class C
                {
                    private (ref int) -> int _f;
                    private List<(ref int) -> int> _all = [];
                    internal void Set((ref int) -> int f) { _f = f; _all.Add(f); }
                    internal int Call(ref int x) => _f(ref x);
                    internal int Count => _all.Count;
                    private protected (ref int) -> int PrivateProtected => _f;
                }

                class Internal
                {
                    public static (ref int) -> int Make() => (ref int v) => v * 2;
                    public (ref int) -> int Field;
                    protected (out int) -> void Protected { get; set; }
                }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, options: TestOptions.DebugExe);
            CompileAndVerify(comp, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "1111221" : null).VerifyDiagnostics(
                // (25,29): warning CS0649: Field 'Internal.Field' is never assigned to, and will always have its default value null
                //     public (ref int) -> int Field;
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Field").WithArguments("Internal.Field", "null").WithLocation(25, 29));
        }

        [Fact]
        public void FunctionTypeRef_Visibility()
        {
            // A member visible outside the assembly cannot use the anonymous delegate, written or inferred. The C# twin
            // with an internal delegate reports the inconsistent accessibility errors.
            var source = """
                #pragma warning disable CS0067, CS0649 // The event is never used, the field is never assigned
                using System.Collections.Generic;

                public class C
                {
                    public (ref int) -> void Field;
                    public D FieldTwin;
                    protected void Protected((out int) -> void f) { }
                    protected void ProtectedTwin(D f) { }
                    protected internal () -> ref int ProtectedInternal => null;
                    public List<(in int) -> int> TypeArgument;
                    public (int) -> (ref int) -> void Nested;
                    public event (ref int) -> void Event;
                    public (ref int) -> void this[int i] => null;
                    public infer Inferred() => (ref int x) => { };
                    public infer InferredTwin() => (int x) => { };
                    internal (ref int) -> void Internal;
                    private protected (ref int) -> void PrivateProtected;
                    public static explicit operator C((ref int) -> void f) => null;
                    public C((ref int) -> void f) { }
                    public class Nested2 { public (ref int) -> void F; }
                }

                public delegate void PublicDelegate((ref int) -> void f);
                public class Base : List<(ref int) -> void> { }
                public interface I : IEnumerable<(ref int) -> void> { }
                public class G<T> where T : List<(ref int) -> void> { }
                internal delegate void D(ref int x);
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (6,30): error CS20075: 'C.Field' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public (ref int) -> void Field;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "Field").WithArguments("C.Field", "(ref int) -> void").WithLocation(6, 30),
                // (7,14): error CS0052: Inconsistent accessibility: field type 'D' is less accessible than field 'C.FieldTwin'
                //     public D FieldTwin;
                Diagnostic(ErrorCode.ERR_BadVisFieldType, "FieldTwin").WithArguments("C.FieldTwin", "D").WithLocation(7, 14),
                // (8,20): error CS20075: 'C.Protected(<anonymous delegate>)' is visible outside this assembly, but uses the function type '(out int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     protected void Protected((out int) -> void f) { }
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "Protected").WithArguments("C.Protected(<anonymous delegate>)", "(out int) -> void").WithLocation(8, 20),
                // (9,20): error CS0051: Inconsistent accessibility: parameter type 'D' is less accessible than method 'C.ProtectedTwin(D)'
                //     protected void ProtectedTwin(D f) { }
                Diagnostic(ErrorCode.ERR_BadVisParamType, "ProtectedTwin").WithArguments("C.ProtectedTwin(D)", "D").WithLocation(9, 20),
                // (10,38): error CS20075: 'C.ProtectedInternal' is visible outside this assembly, but uses the function type '() -> ref int', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     protected internal () -> ref int ProtectedInternal => null;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "ProtectedInternal").WithArguments("C.ProtectedInternal", "() -> ref int").WithLocation(10, 38),
                // (11,34): error CS20075: 'C.TypeArgument' is visible outside this assembly, but uses the function type '(in int) -> int', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public List<(in int) -> int> TypeArgument;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "TypeArgument").WithArguments("C.TypeArgument", "(in int) -> int").WithLocation(11, 34),
                // (12,39): error CS20075: 'C.Nested' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public (int) -> (ref int) -> void Nested;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "Nested").WithArguments("C.Nested", "(ref int) -> void").WithLocation(12, 39),
                // (13,36): error CS20075: 'C.Event' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public event (ref int) -> void Event;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "Event").WithArguments("C.Event", "(ref int) -> void").WithLocation(13, 36),
                // (14,30): error CS20075: 'C.this[int]' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public (ref int) -> void this[int i] => null;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "this").WithArguments("C.this[int]", "(ref int) -> void").WithLocation(14, 30),
                // (15,18): error CS20075: 'C.Inferred()' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public infer Inferred() => (ref int x) => { };
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "Inferred").WithArguments("C.Inferred()", "(ref int) -> void").WithLocation(15, 18),
                // (19,37): error CS20075: 'C.explicit operator C(<anonymous delegate>)' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public static explicit operator C((ref int) -> void f) => null;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "C").WithArguments("C.explicit operator C(<anonymous delegate>)", "(ref int) -> void").WithLocation(19, 37),
                // (20,12): error CS20075: 'C.C(<anonymous delegate>)' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public C((ref int) -> void f) { }
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "C").WithArguments("C.C(<anonymous delegate>)", "(ref int) -> void").WithLocation(20, 12),
                // (21,53): error CS20075: 'C.Nested2.F' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                //     public class Nested2 { public (ref int) -> void F; }
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "F").WithArguments("C.Nested2.F", "(ref int) -> void").WithLocation(21, 53),
                // (24,22): error CS20075: 'PublicDelegate' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                // public delegate void PublicDelegate((ref int) -> void f);
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "PublicDelegate").WithArguments("PublicDelegate", "(ref int) -> void").WithLocation(24, 22),
                // (25,14): error CS20075: 'Base' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                // public class Base : List<(ref int) -> void> { }
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "Base").WithArguments("Base", "(ref int) -> void").WithLocation(25, 14),
                // (26,18): error CS20075: 'I' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                // public interface I : IEnumerable<(ref int) -> void> { }
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "I").WithArguments("I", "(ref int) -> void").WithLocation(26, 18),
                // (27,29): error CS20075: 'G<T>' is visible outside this assembly, but uses the function type '(ref int) -> void', which no 'System.Func' or 'System.Action' delegate can express and which is internal to the assembly. Declare a named delegate instead.
                // public class G<T> where T : List<(ref int) -> void> { }
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeNotVisible, "List<(ref int) -> void>").WithArguments("G<T>", "(ref int) -> void").WithLocation(27, 29));
        }

        [Fact]
        public void FunctionTypeRef_ParamsAndThis()
        {
            var source = """
                #pragma warning disable CS0169 // The field is never used
                using System;
                class C<T>
                {
                    (params int[]) -> void paramsParameter;
                    (this string) -> void thisParameter;
                    (readonly int) -> void readonlyParameter;
                    (ref ref int) -> void duplicate;
                    (ref out int) -> void conflict;
                    (ref readonly readonly int) -> void duplicateReadonly;
                    (scoped scoped Span<int>) -> void duplicateScoped;
                    (scoped int) -> void scopedValue;
                    (scoped T) -> void scopedTypeParameter;
                    () -> ref void refVoid;
                    (ref Console) -> void staticParameter;
                    () -> ref Console staticReturn;
                    (ref int x, int x) -> void duplicateName;
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (5,6): error CS20008: A function type parameter cannot be 'params'. Declare a named delegate instead.
                //     (params int[]) -> void paramsParameter;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "params").WithArguments("params").WithLocation(5, 6),
                // (6,6): error CS20008: A function type parameter cannot be 'this'. Declare a named delegate instead.
                //     (this string) -> void thisParameter;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "this").WithArguments("this").WithLocation(6, 6),
                // (7,6): error CS20008: A function type parameter cannot be 'readonly'. Declare a named delegate instead.
                //     (readonly int) -> void readonlyParameter;
                Diagnostic(ErrorCode.ERR_SharperFunctionTypeParameterModifier, "readonly").WithArguments("readonly").WithLocation(7, 6),
                // (8,10): error CS1107: A parameter can only have one 'ref' modifier
                //     (ref ref int) -> void duplicate;
                Diagnostic(ErrorCode.ERR_DupParamMod, "ref").WithArguments("ref").WithLocation(8, 10),
                // (9,10): error CS8328:  The parameter modifier 'out' cannot be used with 'ref'
                //     (ref out int) -> void conflict;
                Diagnostic(ErrorCode.ERR_BadParameterModifiers, "out").WithArguments("out", "ref").WithLocation(9, 10),
                // (10,19): error CS1107: A parameter can only have one 'readonly' modifier
                //     (ref readonly readonly int) -> void duplicateReadonly;
                Diagnostic(ErrorCode.ERR_DupParamMod, "readonly").WithArguments("readonly").WithLocation(10, 19),
                // (11,13): error CS1107: A parameter can only have one 'scoped' modifier
                //     (scoped scoped Span<int>) -> void duplicateScoped;
                Diagnostic(ErrorCode.ERR_DupParamMod, "scoped").WithArguments("scoped").WithLocation(11, 13),
                // (12,13): error CS9048: The 'scoped' modifier can be used for refs and ref struct values only.
                //     (scoped int) -> void scopedValue;
                Diagnostic(ErrorCode.ERR_ScopedRefAndRefStructOnly, "int").WithLocation(12, 13),
                // (14,15): error CS1547: Keyword 'void' cannot be used in this context
                //     () -> ref void refVoid;
                Diagnostic(ErrorCode.ERR_NoVoidHere, "void").WithLocation(14, 15),
                // (15,10): error CS0721: 'Console': static types cannot be used as parameters
                //     (ref Console) -> void staticParameter;
                Diagnostic(ErrorCode.ERR_ParameterIsStaticClass, "Console").WithArguments("System.Console").WithLocation(15, 10),
                // (16,15): error CS0722: 'Console': static types cannot be used as return types
                //     () -> ref Console staticReturn;
                Diagnostic(ErrorCode.ERR_ReturnTypeIsStaticClass, "Console").WithArguments("System.Console").WithLocation(16, 15),
                // (17,21): error CS0100: The parameter name 'x' is a duplicate
                //     (ref int x, int x) -> void duplicateName;
                Diagnostic(ErrorCode.ERR_DuplicateParamName, "x").WithArguments("x").WithLocation(17, 21));
        }

        [Fact]
        public void FunctionTypeRef_Parsing_Scoped()
        {
            // scoped is a modifier before a type, as in a lambda's parameter list since C# 14. A type named scoped, which
            // only a library older than C# 11 can declare, keeps its meaning where scoped cannot be a modifier, and in
            // tuples.
            var library = CreateCompilation("public class scoped { }", parseOptions: TestOptions.Regular10, targetFramework: TargetFramework.Net90);
            library.VerifyDiagnostics(
                // (1,14): warning CS8981: The type name 'scoped' only contains lower-cased ascii characters. Such names may become reserved for the language.
                // public class scoped { }
                Diagnostic(ErrorCode.WRN_LowerCaseTypeName, "scoped").WithArguments("scoped").WithLocation(1, 14));

            var source = """
                #pragma warning disable CS0219 // The variable is assigned but its value is never used
                (scoped, int) -> int typeNamedScoped = null;
                (scoped) -> int onlyScoped = null;
                (scoped a, int b) tuple = default;
                (scoped S) -> void modifier = null;
                (scoped S s) -> void modifierWithName = null;
                (scoped ref int) -> void scopedRef = null;
                (scoped scoped) -> void scopedTypeNamedScoped = null;
                ref struct S { }
                """;
            var comp = CreateCompilation(source, references: [library.EmitToImageReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (8,9): error CS9048: The 'scoped' modifier can be used for refs and ref struct values only.
                // (scoped scoped) -> void scopedTypeNamedScoped = null;
                Diagnostic(ErrorCode.ERR_ScopedRefAndRefStructOnly, "scoped").WithLocation(8, 9));

            AssertEx.Equal("System.Func<scoped, System.Int32, System.Int32>", FunctionTypeRef_Local(comp, "typeNamedScoped").Type.ToTestDisplayString());
            AssertEx.Equal("System.Func<scoped, System.Int32>", FunctionTypeRef_Local(comp, "onlyScoped").Type.ToTestDisplayString());
            AssertEx.Equal("(scoped a, System.Int32 b)", FunctionTypeRef_Local(comp, "tuple").Type.ToTestDisplayString());
            Assert.True(FunctionTypeRef_Local(comp, "modifier").Type.IsAnonymousType);
            Assert.True(FunctionTypeRef_Local(comp, "modifierWithName").Type.IsAnonymousType);
            Assert.True(FunctionTypeRef_Local(comp, "scopedRef").Type.IsAnonymousType);
        }

        [Fact]
        public void FunctionTypeRef_FriendAssembly()
        {
            // A friend assembly sees the library's anonymous delegate as an unnamed type: it can call it and pass
            // lambdas to it, but its own function type of the same shape is a different type.
            var librarySource = """
                [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Friend")]
                public class Library
                {
                    internal static (ref int) -> int Twice = (ref int x) => x *= 2;
                    internal static int Apply((ref int) -> int f, int value) => f(ref value);
                }
                """;
            var library = CreateCompilation(librarySource, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, assemblyName: "Library");
            library.VerifyDiagnostics();

            var friendSource = """
                using System;
                int x = 3;
                Console.Write(Library.Twice(ref x));
                Console.Write(Library.Apply((ref int v) => v + 1, 1));
                (ref int) -> int own = (ref int v) => v;
                Console.Write(Library.Apply(own, 1));
                own = Library.Twice;
                var natural = (ref int v) => v;
                Console.Write(Library.Apply(natural, 1));
                Console.Write(Library.Apply(own.Invoke, 1));
                """;
            var friend = CreateCompilation(friendSource, references: [library.ToMetadataReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, assemblyName: "Friend");
            friend.VerifyDiagnostics();

            // From metadata, the library's delegate is the synthesized type <>F{00000001}<int, int>, whose name the test
            // helpers take for an unsubstituted placeholder, so only the codes are compared.
            friend = CreateCompilation(friendSource, references: [library.EmitToImageReference()], parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, assemblyName: "Friend");
            AssertEx.Equal(["CS1503", "CS0029", "CS1503"], friend.GetDiagnostics().Select(d => d.Id));
        }

        [Fact]
        public void FunctionTypeRef_Nullable()
        {
            // Nullable annotations are part of the anonymous delegate's parameter and return types, with the same
            // warnings as for the lambda's natural type.
            var source = """
                #nullable enable
                (ref string?) -> string f = (ref string? s) => s;
                var fTwin = string (ref string? s) => s ?? "";
                (ref string) -> string? g = (ref string s) => null;
                (ref string?) -> string? h = f;
                (ref string) -> string i = f;
                string? n = null;
                string r = h(ref n);
                string q = g(ref r);
                () -> ref string? refReturn = () => ref Holder.Value;
                string t = refReturn();
                static class Holder { public static string? Value; }
                """;
            var comp = CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // (2,48): warning CS8603: Possible null reference return.
                // (ref string?) -> string f = (ref string? s) => s;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "s").WithLocation(2, 48),
                // (5,30): warning CS8619: Nullability of reference types in value of type '<anonymous delegate>' doesn't match target type '<anonymous delegate>'.
                // (ref string?) -> string? h = f;
                Diagnostic(ErrorCode.WRN_NullabilityMismatchInAssignment, "f").WithArguments("<anonymous delegate>", "<anonymous delegate>").WithLocation(5, 30),
                // (6,28): warning CS8619: Nullability of reference types in value of type '<anonymous delegate>' doesn't match target type '<anonymous delegate>'.
                // (ref string) -> string i = f;
                Diagnostic(ErrorCode.WRN_NullabilityMismatchInAssignment, "f").WithArguments("<anonymous delegate>", "<anonymous delegate>").WithLocation(6, 28),
                // (8,12): warning CS8600: Converting null literal or possible null value to non-nullable type.
                // string r = h(ref n);
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "h(ref n)").WithLocation(8, 12),
                // (9,12): warning CS8600: Converting null literal or possible null value to non-nullable type.
                // string q = g(ref r);
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "g(ref r)").WithLocation(9, 12),
                // (9,18): warning CS8601: Possible null reference assignment.
                // string q = g(ref r);
                Diagnostic(ErrorCode.WRN_NullReferenceAssignment, "r").WithLocation(9, 18),
                // (11,12): warning CS8600: Converting null literal or possible null value to non-nullable type.
                // string t = refReturn();
                Diagnostic(ErrorCode.WRN_ConvertingNullableToNonNullable, "refReturn()").WithLocation(11, 12));
            FunctionTypeRef_AssertSameType(comp, "f", "fTwin");
        }

        [Fact]
        public void FunctionTypeRef_Patterns()
        {
            var source = """
                using System;
                object o = (ref int x) => x;
                if (o is (ref int) -> int f)
                {
                    int v = 6;
                    Console.Write(f(ref v));
                }
                Console.Write(o is (in int) -> int);
                Console.Write(o switch { (ref int) -> int => "ref", _ => "other" });
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "6Falseref" : null).VerifyDiagnostics();
        }

        [Fact]
        public void FunctionTypeRef_FlagOff()
        {
            // Without Sharper, the C# meaning: a parenthesized expression, here an error.
            var source = """
                class C
                {
                    (ref int) -> int f;
                }
                """;
            CreateCompilation(source, parseOptions: TestOptions.Regular, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (3,13): error CS8124: Tuple must contain at least two elements.
                //     (ref int) -> int f;
                Diagnostic(ErrorCode.ERR_TupleTooFewElements, ")").WithLocation(3, 13),
                // (3,15): error CS1519: Invalid token '->' in a member declaration
                //     (ref int) -> int f;
                Diagnostic(ErrorCode.ERR_InvalidMemberDecl, "->").WithArguments("->").WithLocation(3, 15),
                // (3,22): warning CS0169: The field 'C.f' is never used
                //     (ref int) -> int f;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "f").WithArguments("C.f").WithLocation(3, 22));
        }
    }
}
