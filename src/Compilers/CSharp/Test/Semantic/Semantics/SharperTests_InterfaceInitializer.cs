// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper's interface initializer (issue #30): <c>new IProfiles { Load = id =&gt; ..., _ = Unexpected }</c> creates a
    /// generated class that implements the interface, where C# reports CS0144.
    /// </summary>
    public sealed partial class SharperTests
    {
        [Fact]
        public void InterfaceInitializer_Basic()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                record Profile(string Plan);

                interface IProfiles
                {
                    Profile Load(int id);
                    void Save(Profile p);
                    string Source { get; }
                    int Count => 0;
                }

                class C
                {
                    static void Main()
                    {
                        var saved = new List<Profile>();
                        var profiles = new IProfiles { Load = id => new Profile("Pro" + id), Save = saved.Add, Source = "test" };
                        profiles.Save(profiles.Load(1));
                        Console.Write(profiles.Load(2).Plan + " " + saved.Count + " " + profiles.Source + " " + profiles.Count);

                        IProfiles overridden = new IProfiles { Load = id => new Profile("T"), Save = p => { }, Source = "s", Count = 7 };
                        Console.Write(" " + overridden.Load(0).Plan + overridden.Count);

                        var strict = new IProfiles { Load = id => new Profile("S"), _ = Unexpected };
                        Console.Write(" " + strict.Load(0).Plan + strict.Count);
                        try { strict.Save(new Profile("x")); }
                        catch (InvalidOperationException e) { Console.Write(" | " + e.Message); }
                        try { _ = strict.Source; }
                        catch (InvalidOperationException e) { Console.Write(" | " + e.Message); }
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr
                    ? "Pro2 1 test 0 T7 S0 | 'IProfiles.Save(Profile)' is not assigned in the interface initializer that created this object ('_ = Unexpected'). | 'IProfiles.Source' is not assigned in the interface initializer that created this object ('_ = Unexpected')."
                    : null).VerifyDiagnostics();

            // With the flag off, C# reports that an interface cannot be created.
            CreateCompilation(source, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (19,24): error CS0144: Cannot create an instance of the abstract type or interface 'IProfiles'
                //         var profiles = new IProfiles { Load = id => new Profile("Pro" + id), Save = saved.Add, Source = "test" };
                Diagnostic(ErrorCode.ERR_NoNewAbstract, @"new IProfiles { Load = id => new Profile(""Pro"" + id), Save = saved.Add, Source = ""test"" }").WithArguments("IProfiles").WithLocation(19, 24),
                // (19,40): error CS1913: Member 'Load' cannot be initialized. It is not a field or property.
                //         var profiles = new IProfiles { Load = id => new Profile("Pro" + id), Save = saved.Add, Source = "test" };
                Diagnostic(ErrorCode.ERR_MemberCannotBeInitialized, "Load").WithArguments("Load").WithLocation(19, 40),
                // (19,78): error CS1913: Member 'Save' cannot be initialized. It is not a field or property.
                //         var profiles = new IProfiles { Load = id => new Profile("Pro" + id), Save = saved.Add, Source = "test" };
                Diagnostic(ErrorCode.ERR_MemberCannotBeInitialized, "Save").WithArguments("Save").WithLocation(19, 78),
                // (19,96): error CS0200: Property or indexer 'IProfiles.Source' cannot be assigned to -- it is read only
                //         var profiles = new IProfiles { Load = id => new Profile("Pro" + id), Save = saved.Add, Source = "test" };
                Diagnostic(ErrorCode.ERR_AssgReadonlyProp, "Source").WithArguments("IProfiles.Source").WithLocation(19, 96),
                // (23,32): error CS0144: Cannot create an instance of the abstract type or interface 'IProfiles'
                //         IProfiles overridden = new IProfiles { Load = id => new Profile("T"), Save = p => { }, Source = "s", Count = 7 };
                Diagnostic(ErrorCode.ERR_NoNewAbstract, @"new IProfiles { Load = id => new Profile(""T""), Save = p => { }, Source = ""s"", Count = 7 }").WithArguments("IProfiles").WithLocation(23, 32),
                // (23,48): error CS1913: Member 'Load' cannot be initialized. It is not a field or property.
                //         IProfiles overridden = new IProfiles { Load = id => new Profile("T"), Save = p => { }, Source = "s", Count = 7 };
                Diagnostic(ErrorCode.ERR_MemberCannotBeInitialized, "Load").WithArguments("Load").WithLocation(23, 48),
                // (23,79): error CS1913: Member 'Save' cannot be initialized. It is not a field or property.
                //         IProfiles overridden = new IProfiles { Load = id => new Profile("T"), Save = p => { }, Source = "s", Count = 7 };
                Diagnostic(ErrorCode.ERR_MemberCannotBeInitialized, "Save").WithArguments("Save").WithLocation(23, 79),
                // (23,96): error CS0200: Property or indexer 'IProfiles.Source' cannot be assigned to -- it is read only
                //         IProfiles overridden = new IProfiles { Load = id => new Profile("T"), Save = p => { }, Source = "s", Count = 7 };
                Diagnostic(ErrorCode.ERR_AssgReadonlyProp, "Source").WithArguments("IProfiles.Source").WithLocation(23, 96),
                // (23,110): error CS0200: Property or indexer 'IProfiles.Count' cannot be assigned to -- it is read only
                //         IProfiles overridden = new IProfiles { Load = id => new Profile("T"), Save = p => { }, Source = "s", Count = 7 };
                Diagnostic(ErrorCode.ERR_AssgReadonlyProp, "Count").WithArguments("IProfiles.Count").WithLocation(23, 110),
                // (26,22): error CS0144: Cannot create an instance of the abstract type or interface 'IProfiles'
                //         var strict = new IProfiles { Load = id => new Profile("S"), _ = Unexpected };
                Diagnostic(ErrorCode.ERR_NoNewAbstract, @"new IProfiles { Load = id => new Profile(""S""), _ = Unexpected }").WithArguments("IProfiles").WithLocation(26, 22),
                // (26,38): error CS1913: Member 'Load' cannot be initialized. It is not a field or property.
                //         var strict = new IProfiles { Load = id => new Profile("S"), _ = Unexpected };
                Diagnostic(ErrorCode.ERR_MemberCannotBeInitialized, "Load").WithArguments("Load").WithLocation(26, 38),
                // (26,69): error CS0117: 'IProfiles' does not contain a definition for '_'
                //         var strict = new IProfiles { Load = id => new Profile("S"), _ = Unexpected };
                Diagnostic(ErrorCode.ERR_NoSuchMember, "_").WithArguments("IProfiles", "_").WithLocation(26, 69),
                // (26,73): error CS0103: The name 'Unexpected' does not exist in the current context
                //         var strict = new IProfiles { Load = id => new Profile("S"), _ = Unexpected };
                Diagnostic(ErrorCode.ERR_NameNotInContext, "Unexpected").WithArguments("Unexpected").WithLocation(26, 73));
        }

        [Fact]
        public void InterfaceInitializer_GenericsInheritanceAndOrder()
        {
            var source = """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                interface IBase { string Name(); }
                interface IRepo<T> : IBase { T Get(int id); IEnumerable<T> All { get; } }
                interface IPair { int A { get; } int B { get; } }

                class C
                {
                    static IRepo<T> Single<T>(T value) => new IRepo<T> { Get = _ => value, All = new[] { value }, Name = () => "single" };
                    static int Log(string s, int v) { Console.Write(s); return v; }

                    static void Main()
                    {
                        var repo = new IRepo<string> { Get = id => "x" + id, All = new[] { "a", "b" }, Name = () => "repo" };
                        Console.Write(repo.Get(1) + " " + string.Join(",", repo.All) + " " + repo.Name());
                        var single = Single(42);
                        Console.Write(" " + single.Get(0) + " " + single.All.Count() + " " + single.Name() + " ");

                        var pair = new IPair { B = Log("b", 2), A = Log("a", 1) };       // values in source order
                        Console.Write(" " + pair.A + pair.B);
                        var same = new IPair { B = 4, A = 3 };
                        Console.Write(" " + (pair.GetType() == same.GetType()) + " " + pair.GetType().Name);
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "x1 a,b repo 42 1 single ba 12 True <IPair>Stub" : null).VerifyDiagnostics();
        }

        [Fact]
        public void InterfaceInitializer_UnexpectedCoversEveryKind()
        {
            var source = """
                using System;

                interface IEverything
                {
                    int Value { get; set; }
                    int this[int i] { get; }
                    event Action Changed;
                    T Id<T>(T x);
                    void Run();
                    string Name => "default";
                }

                class C
                {
                    static void Try(Action a)
                    {
                        try { a(); Console.Write("no throw "); }
                        catch (InvalidOperationException) { Console.Write("throws "); }
                    }

                    static void Main()
                    {
                        var e = new IEverything { Run = () => Console.Write("run "), _ = Unexpected };
                        e.Run();
                        Console.Write(e.Name + " ");
                        Try(() => _ = e.Value);
                        Try(() => e.Value = 1);
                        Try(() => _ = e[0]);
                        Try(() => e.Changed += () => { });
                        Try(() => e.Id(1));
                    }
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90,
                expectedOutput: ExecutionConditionUtil.IsCoreClr ? "run default throws throws throws throws throws " : null).VerifyDiagnostics();
        }

        [Fact]
        public void InterfaceInitializer_Errors()
        {
            var source = """
                using System;
                using System.Linq.Expressions;

                interface I
                {
                    int Load(int id);
                    int Save(int x);
                    int Settable { get; set; }
                    event Action Changed;
                    T Id<T>(T x);
                    void Over(int x);
                    void Over(string x);
                }

                interface IStatic { static abstract int Make(); }
                interface IOne { int Get(); }

                class C
                {
                    static void Main()
                    {
                        var missing = new IOne { };
                        var unknown = new IOne { Get = () => 1, Nope = 2 };
                        var duplicate = new IOne { Get = () => 1, Get = () => 2 };
                        var nested = new IOne { Get = { } };
                        var bad = new I { Load = x => x, Settable = 1, Changed = null, Id = null, Over = null, _ = Unexpected };
                        var stat = new IStatic { };
                        var wrong = new IOne { Get = () => "s" };
                        Expression<Func<IOne>> tree = () => new IOne { Get = () => 1 };
                        var noInitializer = new IOne();
                        IOne targetTyped = new() { Get = () => 1 };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90).VerifyDiagnostics(
                // (22,23): error CS20071: The initializer of 'IOne' does not assign 'IOne.Get()', which has no default implementation. Assign it, or add '_ = Unexpected' to make unassigned members throw.
                //         var missing = new IOne { };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerMissingMembers, "new IOne { }").WithArguments("IOne", "'IOne.Get()'").WithLocation(22, 23),
                // (23,49): error CS0117: 'IOne' does not contain a definition for 'Nope'
                //         var unknown = new IOne { Get = () => 1, Nope = 2 };
                Diagnostic(ErrorCode.ERR_NoSuchMember, "Nope").WithArguments("IOne", "Nope").WithLocation(23, 49),
                // (24,51): error CS1912: Duplicate initialization of member 'Get'
                //         var duplicate = new IOne { Get = () => 1, Get = () => 2 };
                Diagnostic(ErrorCode.ERR_MemberAlreadyInitialized, "Get").WithArguments("Get").WithLocation(24, 51),
                // (25,22): error CS20071: The initializer of 'IOne' does not assign 'IOne.Get()', which has no default implementation. Assign it, or add '_ = Unexpected' to make unassigned members throw.
                //         var nested = new IOne { Get = { } };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerMissingMembers, "new IOne { Get = { } }").WithArguments("IOne", "'IOne.Get()'").WithLocation(25, 22),
                // (25,33): error CS0747: Invalid initializer member declarator
                //         var nested = new IOne { Get = { } };
                Diagnostic(ErrorCode.ERR_InvalidInitializerElementInitializer, "Get = { }").WithLocation(25, 33),
                // (26,42): error CS20072: 'I.Settable' cannot be assigned in an interface initializer. Only methods that are not generic, and properties with a getter and no setter, can be assigned, and only when no other member of the interface has the same name.
                //         var bad = new I { Load = x => x, Settable = 1, Changed = null, Id = null, Over = null, _ = Unexpected };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerBadMember, "Settable").WithArguments("I.Settable").WithLocation(26, 42),
                // (26,56): error CS20072: 'I.Changed' cannot be assigned in an interface initializer. Only methods that are not generic, and properties with a getter and no setter, can be assigned, and only when no other member of the interface has the same name.
                //         var bad = new I { Load = x => x, Settable = 1, Changed = null, Id = null, Over = null, _ = Unexpected };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerBadMember, "Changed").WithArguments("I.Changed").WithLocation(26, 56),
                // (26,72): error CS20072: 'I.Id<T>(T)' cannot be assigned in an interface initializer. Only methods that are not generic, and properties with a getter and no setter, can be assigned, and only when no other member of the interface has the same name.
                //         var bad = new I { Load = x => x, Settable = 1, Changed = null, Id = null, Over = null, _ = Unexpected };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerBadMember, "Id").WithArguments("I.Id<T>(T)").WithLocation(26, 72),
                // (26,83): error CS20072: 'I.Over(string)' cannot be assigned in an interface initializer. Only methods that are not generic, and properties with a getter and no setter, can be assigned, and only when no other member of the interface has the same name.
                //         var bad = new I { Load = x => x, Settable = 1, Changed = null, Id = null, Over = null, _ = Unexpected };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerBadMember, "Over").WithArguments("I.Over(string)").WithLocation(26, 83),
                // (27,20): error CS20073: Cannot create an instance of 'IStatic' with an initializer, because it has the static abstract member 'IStatic.Make()'.
                //         var stat = new IStatic { };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerStaticAbstract, "new IStatic { }").WithArguments("IStatic", "IStatic.Make()").WithLocation(27, 20),
                // (28,44): error CS0029: Cannot implicitly convert type 'string' to 'int'
                //         var wrong = new IOne { Get = () => "s" };
                Diagnostic(ErrorCode.ERR_NoImplicitConv, @"""s""").WithArguments("string", "int").WithLocation(28, 44),
                // (28,44): error CS1662: Cannot convert lambda expression to intended delegate type because some of the return types in the block are not implicitly convertible to the delegate return type
                //         var wrong = new IOne { Get = () => "s" };
                Diagnostic(ErrorCode.ERR_CantConvAnonMethReturns, @"""s""").WithArguments("lambda expression").WithLocation(28, 44),
                // (29,45): error CS20074: An expression tree cannot contain an interface initializer.
                //         Expression<Func<IOne>> tree = () => new IOne { Get = () => 1 };
                Diagnostic(ErrorCode.ERR_SharperInterfaceInitializerInExpressionTree, "new IOne { Get = () => 1 }").WithLocation(29, 45),
                // (30,29): error CS0144: Cannot create an instance of the abstract type or interface 'IOne'
                //         var noInitializer = new IOne();
                Diagnostic(ErrorCode.ERR_NoNewAbstract, "new IOne()").WithArguments("IOne").WithLocation(30, 29),
                // (31,28): error CS0144: Cannot create an instance of the abstract type or interface 'IOne'
                //         IOne targetTyped = new() { Get = () => 1 };
                Diagnostic(ErrorCode.ERR_NoNewAbstract, "new() { Get = () => 1 }").WithArguments("IOne").WithLocation(31, 28),
                // (31,36): error CS1913: Member 'Get' cannot be initialized. It is not a field or property.
                //         IOne targetTyped = new() { Get = () => 1 };
                Diagnostic(ErrorCode.ERR_MemberCannotBeInitialized, "Get").WithArguments("Get").WithLocation(31, 36));
        }

        [Fact]
        public void InterfaceInitializer_Nullable()
        {
            var source = """
                #nullable enable
                interface IName { string Name { get; } string? Nick { get; } string Greet(string? who); }

                class C
                {
                    static void Main()
                    {
                        var a = new IName { Name = null, Nick = null, Greet = who => who };
                        var b = new IName { Name = "n", Nick = null, Greet = who => who ?? "you" };
                    }
                }
                """;
            CreateCompilation(source, parseOptions: s_sharperOptions).VerifyDiagnostics(
                // (8,36): warning CS8625: Cannot convert null literal to non-nullable reference type.
                //         var a = new IName { Name = null, Nick = null, Greet = who => who };
                Diagnostic(ErrorCode.WRN_NullAsNonNullable, "null").WithLocation(8, 36),
                // (8,70): warning CS8603: Possible null reference return.
                //         var a = new IName { Name = null, Nick = null, Greet = who => who };
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "who").WithLocation(8, 70));
        }
    }
}
