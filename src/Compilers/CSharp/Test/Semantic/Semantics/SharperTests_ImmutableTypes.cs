// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Sharper: immutable types, slice 1 (<c>[Immutable]</c>, <c>[ImmutableHierarchy]</c>, implementation plan Feature 10).
    /// </summary>
    public sealed partial class SharperTests
    {
        private static CSharpCompilation CreateImmutableCompilation(string source, CSharpParseOptions? parseOptions = null, MetadataReference[]? references = null)
            => CreateCompilation(source, parseOptions: parseOptions ?? s_sharperOptions, targetFramework: TargetFramework.Net90, references: references);

        private static bool IsImmutable(CSharpCompilation compilation, string typeName, bool exact)
            => compilation.SharperImmutability.IsImmutable(compilation.GetMember<NamedTypeSymbol>(typeName), exact);

        [Fact]
        public void Immutable_Records()
        {
            var source = """
                [Immutable] sealed record Money(decimal Amount, string Currency);
                [Immutable] record Person(string Name);
                [Immutable] readonly record struct Point(int X, int Y);
                [Immutable] record struct MutablePoint(int X, int Y);
                #nullable enable
                [Immutable] sealed record Node(int Value, Node? Next);
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();

            Assert.True(IsImmutable(comp, "Money", exact: false));
            Assert.True(IsImmutable(comp, "Person", exact: true));
            Assert.False(IsImmutable(comp, "Person", exact: false));
            Assert.True(IsImmutable(comp, "Point", exact: false));
            Assert.True(IsImmutable(comp, "MutablePoint", exact: false));
            Assert.True(IsImmutable(comp, "Node", exact: false));
        }

        [Fact]
        public void Immutable_CSharpTwin()
        {
            // The same records in C#: the attributes do not exist.
            var source = """
                [Immutable] sealed record Money(decimal Amount, string Currency);
                """;
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (1,2): error CS0246: The type or namespace name 'ImmutableAttribute' could not be found (are you missing a using directive or an assembly reference?)
                // [Immutable] sealed record Money(decimal Amount, string Currency);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Immutable").WithArguments("ImmutableAttribute").WithLocation(1, 2),
                // (1,2): error CS0246: The type or namespace name 'Immutable' could not be found (are you missing a using directive or an assembly reference?)
                // [Immutable] sealed record Money(decimal Amount, string Currency);
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Immutable").WithArguments("Immutable").WithLocation(1, 2));
        }

        [Fact]
        public void Immutable_Disproven()
        {
            var source = """
                using System.Collections.Generic;
                [Immutable] sealed class Field { public int X; }
                [Immutable] sealed class Setter { public int X { get; set; } }
                [Immutable] sealed class Captured(int x) { public int X => x; }
                [Immutable] sealed record ListRecord(List<int> Xs);
                [Immutable] sealed class ArrayField { public readonly int[] Xs = []; }
                [Immutable] sealed class Event { public event System.Action E = null!; }
                [Immutable] sealed class InitOnly { public int X { get; init; } public readonly string S = ""; }
                [Immutable] struct MutableStruct { public int X; public string S; public int P { get; set; } }
                [Immutable] struct StructWithList { public List<int> Xs; }
                [Immutable] struct StructCaptured(List<int> xs) { public int Count => xs.Count; }
                [Immutable] struct StructEvent { public event System.Action E; }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (2,26): error CS20030: 'Field' is marked [Immutable], but it is mutable: 'Field.X' is not readonly.
                // [Immutable] sealed class Field { public int X; }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "Field").WithArguments("Field", "'Field.X' is not readonly").WithLocation(2, 26),
                // (2,45): warning CS0649: Field 'Field.X' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class Field { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("Field.X", "0").WithLocation(2, 45),
                // (3,26): error CS20030: 'Setter' is marked [Immutable], but it is mutable: 'Setter.X' has a set accessor.
                // [Immutable] sealed class Setter { public int X { get; set; } }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "Setter").WithArguments("Setter", "'Setter.X' has a set accessor").WithLocation(3, 26),
                // (4,26): error CS20030: 'Captured' is marked [Immutable], but it is mutable: the parameter 'x' is captured in a field that is not readonly.
                // [Immutable] sealed class Captured(int x) { public int X => x; }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "Captured").WithArguments("Captured", "the parameter 'x' is captured in a field that is not readonly").WithLocation(4, 26),
                // (5,27): error CS20029: 'ListRecord' is marked [Immutable], but it cannot be proven immutable: 'ListRecord.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                // [Immutable] sealed record ListRecord(List<int> Xs);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "ListRecord").WithArguments("ListRecord", "'ListRecord.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(5, 27),
                // (6,26): error CS20029: 'ArrayField' is marked [Immutable], but it cannot be proven immutable: 'ArrayField.Xs' has type 'int[]', which is not immutable.
                // [Immutable] sealed class ArrayField { public readonly int[] Xs = []; }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "ArrayField").WithArguments("ArrayField", "'ArrayField.Xs' has type 'int[]', which is not immutable").WithLocation(6, 26),
                // (7,26): error CS20030: 'Event' is marked [Immutable], but it is mutable: 'Event.E' is an event, which can be subscribed to.
                // [Immutable] sealed class Event { public event System.Action E = null!; }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "Event").WithArguments("Event", "'Event.E' is an event, which can be subscribed to").WithLocation(7, 26),
                // (7,61): warning CS0414: The field 'Event.E' is assigned but its value is never used
                // [Immutable] sealed class Event { public event System.Action E = null!; }
                Diagnostic(ErrorCode.WRN_UnreferencedFieldAssg, "E").WithArguments("Event.E").WithLocation(7, 61),
                // (9,47): warning CS0649: Field 'MutableStruct.X' is never assigned to, and will always have its default value 0
                // [Immutable] struct MutableStruct { public int X; public string S; public int P { get; set; } }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("MutableStruct.X", "0").WithLocation(9, 47),
                // (9,64): warning CS0649: Field 'MutableStruct.S' is never assigned to, and will always have its default value null
                // [Immutable] struct MutableStruct { public int X; public string S; public int P { get; set; } }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "S").WithArguments("MutableStruct.S", "null").WithLocation(9, 64),
                // (10,20): error CS20029: 'StructWithList' is marked [Immutable], but it cannot be proven immutable: 'StructWithList.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                // [Immutable] struct StructWithList { public List<int> Xs; }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "StructWithList").WithArguments("StructWithList", "'StructWithList.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(10, 20),
                // (10,54): warning CS0649: Field 'StructWithList.Xs' is never assigned to, and will always have its default value null
                // [Immutable] struct StructWithList { public List<int> Xs; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Xs").WithArguments("StructWithList.Xs", "null").WithLocation(10, 54),
                // (11,20): error CS20029: 'StructCaptured' is marked [Immutable], but it cannot be proven immutable: 'xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                // [Immutable] struct StructCaptured(List<int> xs) { public int Count => xs.Count; }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "StructCaptured").WithArguments("StructCaptured", "'xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(11, 20),
                // (12,20): error CS20030: 'StructEvent' is marked [Immutable], but it is mutable: 'StructEvent.E' is an event, which can be subscribed to.
                // [Immutable] struct StructEvent { public event System.Action E; }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "StructEvent").WithArguments("StructEvent", "'StructEvent.E' is an event, which can be subscribed to").WithLocation(12, 20),
                // (12,61): warning CS0067: The event 'StructEvent.E' is never used
                // [Immutable] struct StructEvent { public event System.Action E; }
                Diagnostic(ErrorCode.WRN_UnreferencedEvent, "E").WithArguments("StructEvent.E").WithLocation(12, 61));
        }

        [Fact]
        public void Immutable_StaticTypes()
        {
            var source = """
                using System.Collections.Generic;
                [Immutable] record Person(string Name);
                [Immutable, ImmutableHierarchy] record Shape;
                [ImmutableHierarchy] interface IShape { }
                interface IOpen { }
                [Immutable] sealed record HasPerson(Person P);
                [Immutable] sealed record HasShape(Shape S, IShape I);
                [Immutable] sealed record HasOpen(IOpen O);
                [Immutable] sealed record HasObject(object O);
                [Immutable] sealed record HasValues((int, string) T, KeyValuePair<int, string> K, int? N, System.DateTime D, System.Guid G, Kind E);
                [Immutable] sealed record HasMutableTuple((int, List<int>) T);
                enum Kind { A }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics(
                // (6,27): error CS20029: 'HasPerson' is marked [Immutable], but it cannot be proven immutable: 'HasPerson.P' has type 'Person', which is neither sealed nor marked [ImmutableHierarchy].
                // [Immutable] sealed record HasPerson(Person P);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "HasPerson").WithArguments("HasPerson", "'HasPerson.P' has type 'Person', which is neither sealed nor marked [ImmutableHierarchy]").WithLocation(6, 27),
                // (8,27): error CS20029: 'HasOpen' is marked [Immutable], but it cannot be proven immutable: 'HasOpen.O' has type 'IOpen', which is neither sealed nor marked [ImmutableHierarchy].
                // [Immutable] sealed record HasOpen(IOpen O);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "HasOpen").WithArguments("HasOpen", "'HasOpen.O' has type 'IOpen', which is neither sealed nor marked [ImmutableHierarchy]").WithLocation(8, 27),
                // (9,27): error CS20029: 'HasObject' is marked [Immutable], but it cannot be proven immutable: 'HasObject.O' has type 'object', which is neither sealed nor marked [ImmutableHierarchy].
                // [Immutable] sealed record HasObject(object O);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "HasObject").WithArguments("HasObject", "'HasObject.O' has type 'object', which is neither sealed nor marked [ImmutableHierarchy]").WithLocation(9, 27),
                // (11,27): error CS20029: 'HasMutableTuple' is marked [Immutable], but it cannot be proven immutable: 'HasMutableTuple.T' has type '(int, System.Collections.Generic.List<int>)', which is not immutable.
                // [Immutable] sealed record HasMutableTuple((int, List<int>) T);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "HasMutableTuple").WithArguments("HasMutableTuple", "'HasMutableTuple.T' has type '(int, System.Collections.Generic.List<int>)', which is not immutable").WithLocation(11, 27));
            Assert.True(comp.SharperImmutability.IsImmutable(comp.GetSpecialType(SpecialType.System_Object), exact: true));
            Assert.False(comp.SharperImmutability.IsImmutable(comp.GetSpecialType(SpecialType.System_Object), exact: false));
        }

        [Fact]
        public void Immutable_Observers()
        {
            var source = """
                #nullable enable
                using System;
                [Immutable] sealed class Clock { public DateTime Now => DateTime.Now; }
                [Immutable] sealed class Reader(string name)
                {
                    public string Name { get; } = name;
                    public int Length => Name.Length;
                    public string Same => this.Name;
                    public int Roll() => Random.Shared.Next();
                    public override bool Equals(object? o) => o is Reader r && r.Name == Name;
                    public override int GetHashCode() => Name.GetHashCode();
                }
                [Immutable] sealed class Concat { public readonly string A = "", B = ""; public string Both => A + B; }
                [Immutable] sealed class Hash { public readonly int A; public override int GetHashCode() => HashCode.Combine(A); }
                [Immutable] sealed class Assumed { public readonly int A; [AssumePure] public override int GetHashCode() => HashCode.Combine(A); }
                [Immutable] sealed class Fresh { public object O => new object(); }
                [Immutable] sealed class Boxing { public readonly int A; public object O => A; }
                [Immutable] sealed class Money(decimal a, decimal b) { public readonly decimal A = a, B = b; public decimal Sum => A + B; public bool Same => A == B; }
                [Immutable] sealed class Lazy(string a)
                {
                    [UnobservableWrite] private string? _cache;
                    public readonly string A = a;
                    public string Text => _cache ??= A;
                }
                [Immutable] sealed class LazyAssumed
                {
                    [UnobservableWrite] private string? _cache;
                    public readonly int A;
                    [AssumePure] public string Text => _cache ??= A.ToString();
                }
                [Immutable] sealed class Indexer { private readonly int _a; public int this[int i] => i == 0 ? _a : 0; }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (3,26): error CS20029: 'Clock' is marked [Immutable], but it cannot be proven immutable: the observer 'Clock.Now' does more than read fields and pure observers: 'DateTime.Now'.
                // [Immutable] sealed class Clock { public DateTime Now => DateTime.Now; }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Clock").WithArguments("Clock", "the observer 'Clock.Now' does more than read fields and pure observers: 'DateTime.Now'").WithLocation(3, 26),
                // (13,26): error CS20029: 'Concat' is marked [Immutable], but it cannot be proven immutable: the observer 'Concat.Both' does more than read fields and pure observers: 'A + B'.
                // [Immutable] sealed class Concat { public readonly string A = "", B = ""; public string Both => A + B; }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Concat").WithArguments("Concat", "the observer 'Concat.Both' does more than read fields and pure observers: 'A + B'").WithLocation(13, 26),
                // (14,26): error CS20029: 'Hash' is marked [Immutable], but it cannot be proven immutable: the observer 'Hash.GetHashCode()' does more than read fields and pure observers: 'HashCode.Combine(A)'.
                // [Immutable] sealed class Hash { public readonly int A; public override int GetHashCode() => HashCode.Combine(A); }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Hash").WithArguments("Hash", "the observer 'Hash.GetHashCode()' does more than read fields and pure observers: 'HashCode.Combine(A)'").WithLocation(14, 26),
                // (14,53): warning CS0649: Field 'Hash.A' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class Hash { public readonly int A; public override int GetHashCode() => HashCode.Combine(A); }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "A").WithArguments("Hash.A", "0").WithLocation(14, 53),
                // (15,56): warning CS0649: Field 'Assumed.A' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class Assumed { public readonly int A; [AssumePure] public override int GetHashCode() => HashCode.Combine(A); }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "A").WithArguments("Assumed.A", "0").WithLocation(15, 56),
                // (16,26): error CS20029: 'Fresh' is marked [Immutable], but it cannot be proven immutable: the observer 'Fresh.O' does more than read fields and pure observers: 'new object()'.
                // [Immutable] sealed class Fresh { public object O => new object(); }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Fresh").WithArguments("Fresh", "the observer 'Fresh.O' does more than read fields and pure observers: 'new object()'").WithLocation(16, 26),
                // (17,26): error CS20029: 'Boxing' is marked [Immutable], but it cannot be proven immutable: the observer 'Boxing.O' does more than read fields and pure observers: 'A'.
                // [Immutable] sealed class Boxing { public readonly int A; public object O => A; }
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Boxing").WithArguments("Boxing", "the observer 'Boxing.O' does more than read fields and pure observers: 'A'").WithLocation(17, 26),
                // (17,55): warning CS0649: Field 'Boxing.A' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class Boxing { public readonly int A; public object O => A; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "A").WithArguments("Boxing.A", "0").WithLocation(17, 55),
                // (28,25): warning CS0649: Field 'LazyAssumed.A' is never assigned to, and will always have its default value 0
                //     public readonly int A;
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "A").WithArguments("LazyAssumed.A", "0").WithLocation(28, 25),
                // (31,57): warning CS0649: Field 'Indexer._a' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class Indexer { private readonly int _a; public int this[int i] => i == 0 ? _a : 0; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "_a").WithArguments("Indexer._a", "0").WithLocation(31, 57));
        }

        [Fact]
        public void Immutable_Cycles()
        {
            var source = """
                #nullable enable
                using System.Collections.Generic;
                [Immutable] sealed record A(B? B);
                [Immutable] sealed record B(A? A, C? C);
                [Immutable] sealed record C(B? B, List<int> Xs);
                [Immutable] sealed record D(D? Self, E? E);
                [Immutable] sealed record E(D? D);
                """;
            // From each end.
            foreach (var start in new[] { "A", "B", "C", "D", "E" })
            {
                var comp = CreateImmutableCompilation(source);
                var expected = start is "A" or "B" or "C" ? false : true;
                Assert.Equal(expected, IsImmutable(comp, start, exact: false));
                comp.VerifyDiagnostics(
                    // (3,27): error CS20029: 'A' is marked [Immutable], but it cannot be proven immutable: 'A.B' has type 'B', which is not immutable.
                    // [Immutable] sealed record A(B? B);
                    Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "A").WithArguments("A", "'A.B' has type 'B', which is not immutable").WithLocation(3, 27),
                    // (4,27): error CS20029: 'B' is marked [Immutable], but it cannot be proven immutable: 'B.A' has type 'A', which is not immutable.
                    // [Immutable] sealed record B(A? A, C? C);
                    Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "B").WithArguments("B", "'B.A' has type 'A', which is not immutable").WithLocation(4, 27),
                    // (5,27): error CS20029: 'C' is marked [Immutable], but it cannot be proven immutable: 'C.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                    // [Immutable] sealed record C(B? B, List<int> Xs);
                    Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "C").WithArguments("C", "'C.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(5, 27));
            }
        }

        [Fact]
        public void Immutable_Generics()
        {
            var source = """
                #nullable enable
                using System.Collections.Generic;
                [Immutable] sealed record Box<T>(T Value);
                [Immutable] sealed record Tagged<[Phantom] TTag, T>(T Value);
                [Immutable] sealed record BadPhantom<[Phantom] T>(T Value);
                [Immutable] sealed record BadPhantomList<[Phantom] T>(List<T> Values);
                [Immutable] sealed record Nested<T>(T Value, Nested<List<T>>? Next);
                [Immutable] sealed record Recursive<T>(T Value, Recursive<T>? Next);
                [Immutable] sealed record UsesBox(Box<int> I, Box<string> S, Tagged<List<int>, int> T);
                [Immutable] sealed record UsesMutableBox(Box<List<int>> L);
                [Immutable] sealed record Outer<T>
                {
                    [Immutable] public sealed record Inner(T Value);
                }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics(
                // (5,27): error CS20029: 'BadPhantom<T>' is marked [Immutable], but it cannot be proven immutable: 'BadPhantom<T>.Value' has type 'T', which uses a [Phantom] type parameter.
                // [Immutable] sealed record BadPhantom<[Phantom] T>(T Value);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "BadPhantom").WithArguments("BadPhantom<T>", "'BadPhantom<T>.Value' has type 'T', which uses a [Phantom] type parameter").WithLocation(5, 27),
                // (5,48): error CS20036: Type parameter 'T' is marked [Phantom], but 'BadPhantom<T>.Value' uses it in the state of the type.
                // [Immutable] sealed record BadPhantom<[Phantom] T>(T Value);
                Diagnostic(ErrorCode.ERR_SharperPhantomTypeParameterInState, "T").WithArguments("T", "BadPhantom<T>.Value").WithLocation(5, 48),
                // (6,27): error CS20029: 'BadPhantomList<T>' is marked [Immutable], but it cannot be proven immutable: 'BadPhantomList<T>.Values' has type 'System.Collections.Generic.List<T>', which comes from another assembly and is not marked [Immutable].
                // [Immutable] sealed record BadPhantomList<[Phantom] T>(List<T> Values);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "BadPhantomList").WithArguments("BadPhantomList<T>", "'BadPhantomList<T>.Values' has type 'System.Collections.Generic.List<T>', which comes from another assembly and is not marked [Immutable]").WithLocation(6, 27),
                // (6,52): error CS20036: Type parameter 'T' is marked [Phantom], but 'BadPhantomList<T>.Values' uses it in the state of the type.
                // [Immutable] sealed record BadPhantomList<[Phantom] T>(List<T> Values);
                Diagnostic(ErrorCode.ERR_SharperPhantomTypeParameterInState, "T").WithArguments("T", "BadPhantomList<T>.Values").WithLocation(6, 52),
                // (7,27): error CS20029: 'Nested<T>' is marked [Immutable], but it cannot be proven immutable: 'Nested<T>.Next' has type 'Nested<System.Collections.Generic.List<T>>', which is not immutable.
                // [Immutable] sealed record Nested<T>(T Value, Nested<List<T>>? Next);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "Nested").WithArguments("Nested<T>", "'Nested<T>.Next' has type 'Nested<System.Collections.Generic.List<T>>', which is not immutable").WithLocation(7, 27),
                // (10,27): error CS20029: 'UsesMutableBox' is marked [Immutable], but it cannot be proven immutable: 'UsesMutableBox.L' has type 'Box<System.Collections.Generic.List<int>>', which is not immutable.
                // [Immutable] sealed record UsesMutableBox(Box<List<int>> L);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "UsesMutableBox").WithArguments("UsesMutableBox", "'UsesMutableBox.L' has type 'Box<System.Collections.Generic.List<int>>', which is not immutable").WithLocation(10, 27));
        }

        [Fact]
        public void Immutable_Bases()
        {
            var source = """
                using System.Collections.Generic;
                class Mutable { public int X; }
                [Immutable] sealed class Derived : Mutable { }
                [Immutable] abstract class Base { public abstract int Area { get; } public int Id { get; init; } }
                [Immutable] sealed class Square(int side) : Base { public readonly int Side = side; public override int Area => Side * Side; }
                class Open { }
                [Immutable] sealed class FromOpen : Open { }
                [Immutable] sealed class FromException : System.Exception { }
                [Immutable] sealed class FromList : List<int> { }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (2,28): warning CS0649: Field 'Mutable.X' is never assigned to, and will always have its default value 0
                // class Mutable { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("Mutable.X", "0").WithLocation(2, 28),
                // (3,26): error CS20031: 'Derived' is marked [Immutable], but its base type 'Mutable' is not immutable.
                // [Immutable] sealed class Derived : Mutable { }
                Diagnostic(ErrorCode.ERR_SharperImmutableBaseNotImmutable, "Derived").WithArguments("Derived", "Mutable").WithLocation(3, 26),
                // (8,26): error CS20031: 'FromException' is marked [Immutable], but its base type 'Exception' is not immutable.
                // [Immutable] sealed class FromException : System.Exception { }
                Diagnostic(ErrorCode.ERR_SharperImmutableBaseNotImmutable, "FromException").WithArguments("FromException", "System.Exception").WithLocation(8, 26),
                // (9,26): error CS20031: 'FromList' is marked [Immutable], but its base type 'List<int>' is not immutable.
                // [Immutable] sealed class FromList : List<int> { }
                Diagnostic(ErrorCode.ERR_SharperImmutableBaseNotImmutable, "FromList").WithArguments("FromList", "System.Collections.Generic.List<int>").WithLocation(9, 26));
        }

        [Fact]
        public void Immutable_Hierarchy()
        {
            var source = """
                using System.Collections.Generic;
                [Immutable, ImmutableHierarchy] abstract record Shape;
                sealed record Circle(double Radius) : Shape;
                record Polygon(List<int> Points) : Shape;
                record Square(double Side) : Shape;
                record Deeper(double Side) : Square(Side) { public int X; }
                [ImmutableHierarchy] class NotImmutable { }
                [ImmutableHierarchy] interface IValue { }
                sealed class Value : IValue { public int X; }
                struct StructValue : IValue { public List<int> Xs; }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (4,8): error CS20032: 'Polygon' must be immutable, because 'Shape' is marked [ImmutableHierarchy], but 'Polygon.Points' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                // record Polygon(List<int> Points) : Shape;
                Diagnostic(ErrorCode.ERR_SharperImmutableHierarchyViolated, "Polygon").WithArguments("Polygon", "Shape", "'Polygon.Points' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(4, 8),
                // (6,8): error CS20032: 'Deeper' must be immutable, because 'Shape' is marked [ImmutableHierarchy], but 'Deeper.X' is not readonly.
                // record Deeper(double Side) : Square(Side) { public int X; }
                Diagnostic(ErrorCode.ERR_SharperImmutableHierarchyViolated, "Deeper").WithArguments("Deeper", "Shape", "'Deeper.X' is not readonly").WithLocation(6, 8),
                // (6,56): warning CS0649: Field 'Deeper.X' is never assigned to, and will always have its default value 0
                // record Deeper(double Side) : Square(Side) { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("Deeper.X", "0").WithLocation(6, 56),
                // (7,28): error CS20034: 'NotImmutable' is marked [ImmutableHierarchy], so it must also be marked [Immutable].
                // [ImmutableHierarchy] class NotImmutable { }
                Diagnostic(ErrorCode.ERR_SharperImmutableHierarchyWithoutImmutable, "NotImmutable").WithArguments("NotImmutable").WithLocation(7, 28),
                // (9,14): error CS20032: 'Value' must be immutable, because 'IValue' is marked [ImmutableHierarchy], but 'Value.X' is not readonly.
                // sealed class Value : IValue { public int X; }
                Diagnostic(ErrorCode.ERR_SharperImmutableHierarchyViolated, "Value").WithArguments("Value", "IValue", "'Value.X' is not readonly").WithLocation(9, 14),
                // (9,42): warning CS0649: Field 'Value.X' is never assigned to, and will always have its default value 0
                // sealed class Value : IValue { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("Value.X", "0").WithLocation(9, 42),
                // (10,8): error CS20032: 'StructValue' must be immutable, because 'IValue' is marked [ImmutableHierarchy], but 'StructValue.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                // struct StructValue : IValue { public List<int> Xs; }
                Diagnostic(ErrorCode.ERR_SharperImmutableHierarchyViolated, "StructValue").WithArguments("StructValue", "IValue", "'StructValue.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(10, 8),
                // (10,48): warning CS0649: Field 'StructValue.Xs' is never assigned to, and will always have its default value null
                // struct StructValue : IValue { public List<int> Xs; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Xs").WithArguments("StructValue.Xs", "null").WithLocation(10, 48));
        }

        [Fact]
        public void Immutable_Hierarchy_CSharpFile()
        {
            var sharper = """
                [Immutable, ImmutableHierarchy] public abstract record Shape;
                """;
            var csharp = """
                public record Polygon(System.Collections.Generic.List<int> Points) : Shape;
                public sealed record Circle(double Radius) : Shape;
                """;
            var comp = CreateCompilation(
                [CSharpTestSource.Parse(sharper, "a.cx", s_sharperOptions), CSharpTestSource.Parse(csharp, "b.cs", TestOptions.Regular)],
                targetFramework: TargetFramework.Net90);
            comp.VerifyDiagnostics(
                // b.cs(1,15): warning CS20033: 'Polygon' must be immutable, because 'Shape' is marked [ImmutableHierarchy], but 'Polygon.Points' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                // public record Polygon(System.Collections.Generic.List<int> Points) : Shape;
                Diagnostic(ErrorCode.WRN_SharperImmutableHierarchyViolated, "Polygon").WithArguments("Polygon", "Shape", "'Polygon.Points' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(1, 15));
        }

        [Fact]
        public void Immutable_AttributeUses()
        {
            var source = """
                [Immutable] sealed class C
                {
                    [UnobservableWrite] private static int s_a;
                    [UnobservableWrite] private readonly int _b;
                    [UnobservableWrite] private int _ok;
                }
                sealed class NotMarked { [UnobservableWrite] private int _c; }
                [Immutable] struct S { [UnobservableWrite] private int _d; }
                [Immutable] interface I { }
                class G<[Phantom] T> { }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (3,44): error CS20035: [UnobservableWrite] is only valid on a field that is not readonly, of a class marked [Immutable].
                //     [UnobservableWrite] private static int s_a;
                Diagnostic(ErrorCode.ERR_SharperMisplacedUnobservableWrite, "s_a").WithLocation(3, 44),
                // (3,44): warning CS0169: The field 'C.s_a' is never used
                //     [UnobservableWrite] private static int s_a;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "s_a").WithArguments("C.s_a").WithLocation(3, 44),
                // (4,46): error CS20035: [UnobservableWrite] is only valid on a field that is not readonly, of a class marked [Immutable].
                //     [UnobservableWrite] private readonly int _b;
                Diagnostic(ErrorCode.ERR_SharperMisplacedUnobservableWrite, "_b").WithLocation(4, 46),
                // (4,46): warning CS0169: The field 'C._b' is never used
                //     [UnobservableWrite] private readonly int _b;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "_b").WithArguments("C._b").WithLocation(4, 46),
                // (5,37): warning CS0169: The field 'C._ok' is never used
                //     [UnobservableWrite] private int _ok;
                Diagnostic(ErrorCode.WRN_UnreferencedField, "_ok").WithArguments("C._ok").WithLocation(5, 37),
                // (7,58): error CS20035: [UnobservableWrite] is only valid on a field that is not readonly, of a class marked [Immutable].
                // sealed class NotMarked { [UnobservableWrite] private int _c; }
                Diagnostic(ErrorCode.ERR_SharperMisplacedUnobservableWrite, "_c").WithLocation(7, 58),
                // (7,58): warning CS0169: The field 'NotMarked._c' is never used
                // sealed class NotMarked { [UnobservableWrite] private int _c; }
                Diagnostic(ErrorCode.WRN_UnreferencedField, "_c").WithArguments("NotMarked._c").WithLocation(7, 58),
                // (8,56): error CS20035: [UnobservableWrite] is only valid on a field that is not readonly, of a class marked [Immutable].
                // [Immutable] struct S { [UnobservableWrite] private int _d; }
                Diagnostic(ErrorCode.ERR_SharperMisplacedUnobservableWrite, "_d").WithLocation(8, 56),
                // (8,56): warning CS0169: The field 'S._d' is never used
                // [Immutable] struct S { [UnobservableWrite] private int _d; }
                Diagnostic(ErrorCode.WRN_UnreferencedField, "_d").WithArguments("S._d").WithLocation(8, 56),
                // (9,2): error CS0592: Attribute 'Immutable' is not valid on this declaration type. It is only valid on 'class, struct' declarations.
                // [Immutable] interface I { }
                Diagnostic(ErrorCode.ERR_AttributeOnBadSymbolType, "Immutable").WithArguments("Immutable", "class, struct").WithLocation(9, 2));
        }

        [Fact]
        public void Immutable_Attested()
        {
            var source = """
                using System.Collections.Immutable;
                using System.Collections.Frozen;
                using System.Collections.Generic;
                [Immutable] sealed record Ok(string S, ImmutableArray<string> A, ImmutableList<int> L, ImmutableDictionary<string, int> D, FrozenSet<string> F, System.Version V, System.TimeSpan T);
                [Immutable] sealed record NotOk(ImmutableArray<int[]> A);
                [Immutable] sealed record ReadOnlyView(IReadOnlyList<int> Xs);
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics(
                // (5,27): error CS20029: 'NotOk' is marked [Immutable], but it cannot be proven immutable: 'NotOk.A' has type 'System.Collections.Immutable.ImmutableArray<int[]>', which is not immutable.
                // [Immutable] sealed record NotOk(ImmutableArray<int[]> A);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "NotOk").WithArguments("NotOk", "'NotOk.A' has type 'System.Collections.Immutable.ImmutableArray<int[]>', which is not immutable").WithLocation(5, 27),
                // (6,27): error CS20029: 'ReadOnlyView' is marked [Immutable], but it cannot be proven immutable: 'ReadOnlyView.Xs' has type 'System.Collections.Generic.IReadOnlyList<int>', which is neither sealed nor marked [ImmutableHierarchy].
                // [Immutable] sealed record ReadOnlyView(IReadOnlyList<int> Xs);
                Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "ReadOnlyView").WithArguments("ReadOnlyView", "'ReadOnlyView.Xs' has type 'System.Collections.Generic.IReadOnlyList<int>', which is neither sealed nor marked [ImmutableHierarchy]").WithLocation(6, 27));

            Assert.True(comp.SharperImmutability.IsImmutable(comp.GetSpecialType(SpecialType.System_String), exact: false));
            Assert.True(comp.SharperImmutability.IsImmutable(comp.GetWellKnownType(WellKnownType.System_Collections_Immutable_ImmutableArray_T).Construct(comp.GetSpecialType(SpecialType.System_Int32)), exact: false));
            Assert.False(comp.SharperImmutability.IsImmutable(comp.GetWellKnownType(WellKnownType.System_Collections_Generic_List_T).Construct(comp.GetSpecialType(SpecialType.System_Int32)), exact: true));
        }

        [Fact]
        public void Immutable_Metadata()
        {
            var library = """
                [Immutable] public sealed record Money(decimal Amount);
                [Immutable] public record Person(string Name);
                [Immutable, ImmutableHierarchy] public abstract record Shape;
                public sealed record Unmarked(int X);
                [Immutable] public sealed record Tagged<[Phantom] TTag, T>(T Value);
                """;
            var libraryComp = CreateImmutableCompilation(library);
            libraryComp.VerifyDiagnostics();

            foreach (var reference in new[] { libraryComp.ToMetadataReference(), libraryComp.EmitToImageReference() })
            {
                var source = """
                    using System.Collections.Generic;
                    [Immutable] sealed record UsesLibrary(Money M, Shape S, Tagged<List<int>, int> T);
                    [Immutable] sealed record UsesPerson(Person P);
                    [Immutable] sealed record UsesUnmarked(Unmarked U);
                    sealed record Square(double Side) : Shape;
                    record Bad(List<int> Xs) : Shape;
                    """;
                CreateImmutableCompilation(source, references: [reference]).VerifyDiagnostics(
                    // (3,27): error CS20029: 'UsesPerson' is marked [Immutable], but it cannot be proven immutable: 'UsesPerson.P' has type 'Person', which is neither sealed nor marked [ImmutableHierarchy].
                    // [Immutable] sealed record UsesPerson(Person P);
                    Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "UsesPerson").WithArguments("UsesPerson", "'UsesPerson.P' has type 'Person', which is neither sealed nor marked [ImmutableHierarchy]").WithLocation(3, 27),
                    // (4,27): error CS20029: 'UsesUnmarked' is marked [Immutable], but it cannot be proven immutable: 'UsesUnmarked.U' has type 'Unmarked', which comes from another assembly and is not marked [Immutable].
                    // [Immutable] sealed record UsesUnmarked(Unmarked U);
                    Diagnostic(ErrorCode.ERR_SharperImmutableNotProven, "UsesUnmarked").WithArguments("UsesUnmarked", "'UsesUnmarked.U' has type 'Unmarked', which comes from another assembly and is not marked [Immutable]").WithLocation(4, 27),
                    // (6,8): error CS20032: 'Bad' must be immutable, because 'Shape' is marked [ImmutableHierarchy], but 'Bad.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable].
                    // record Bad(List<int> Xs) : Shape;
                    Diagnostic(ErrorCode.ERR_SharperImmutableHierarchyViolated, "Bad").WithArguments("Bad", "Shape", "'Bad.Xs' has type 'System.Collections.Generic.List<int>', which comes from another assembly and is not marked [Immutable]").WithLocation(6, 8));
            }
        }

        [Fact]
        public void Immutable_Emit()
        {
            var source = """
                [Immutable] public sealed record Money(decimal Amount);
                public sealed record Box<[Phantom] T>(int X);
                """;
            var comp = CreateImmutableCompilation(source);
            CompileAndVerify(comp, symbolValidator: module =>
            {
                var attribute = module.GlobalNamespace.GetMember<NamedTypeSymbol>("Sharper.ImmutableAttribute");
                Assert.Equal(Accessibility.Internal, attribute.DeclaredAccessibility);
                Assert.Equal(new[] { "System.Runtime.CompilerServices.CompilerGeneratedAttribute", "Microsoft.CodeAnalysis.EmbeddedAttribute", "System.AttributeUsageAttribute(System.AttributeTargets.Class | System.AttributeTargets.Struct, AllowMultiple = false, Inherited = false)" },
                    attribute.GetAttributes().Select(a => a.ToString()));
                Assert.Contains("Sharper.ImmutableAttribute", module.GlobalNamespace.GetMember<NamedTypeSymbol>("Money").GetAttributes().Select(a => a.ToString()));
                Assert.Empty(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper").GetTypeMembers("UnobservableWriteAttribute"));
                Assert.NotNull(module.GlobalNamespace.GetMember<NamespaceSymbol>("Sharper").GetTypeMember("PhantomAttribute"));
            }).VerifyDiagnostics();
        }

        [Fact]
        public void Immutable_UserDeclaredAttribute()
        {
            var source = """
                namespace Sharper
                {
                    [System.AttributeUsage(System.AttributeTargets.Class)]
                    public sealed class ImmutableAttribute : System.Attribute { }
                }
                [Immutable] sealed class C { public int X; }
                [Sharper.Immutable] sealed class D { public int X; }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics(
                // (6,26): error CS20030: 'C' is marked [Immutable], but it is mutable: 'C.X' is not readonly.
                // [Immutable] sealed class C { public int X; }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "C").WithArguments("C", "'C.X' is not readonly").WithLocation(6, 26),
                // (6,41): warning CS0649: Field 'C.X' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class C { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("C.X", "0").WithLocation(6, 41),
                // (7,34): error CS20030: 'D' is marked [Immutable], but it is mutable: 'D.X' is not readonly.
                // [Sharper.Immutable] sealed class D { public int X; }
                Diagnostic(ErrorCode.ERR_SharperImmutableDisproven, "D").WithArguments("D", "'D.X' is not readonly").WithLocation(7, 34),
                // (7,49): warning CS0649: Field 'D.X' is never assigned to, and will always have its default value 0
                // [Sharper.Immutable] sealed class D { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("D.X", "0").WithLocation(7, 49));
            Assert.Same(comp.GetMember<NamedTypeSymbol>("Sharper.ImmutableAttribute"), comp.GetMember<NamedTypeSymbol>("C").GetAttributes().Single().AttributeClass);

            // In C#, the claims are not checked, and only the qualified name binds.
            CreateImmutableCompilation(source, parseOptions: TestOptions.Regular).VerifyDiagnostics(
                // (6,2): error CS0246: The type or namespace name 'ImmutableAttribute' could not be found (are you missing a using directive or an assembly reference?)
                // [Immutable] sealed class C { public int X; }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Immutable").WithArguments("ImmutableAttribute").WithLocation(6, 2),
                // (6,2): error CS0246: The type or namespace name 'Immutable' could not be found (are you missing a using directive or an assembly reference?)
                // [Immutable] sealed class C { public int X; }
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Immutable").WithArguments("Immutable").WithLocation(6, 2),
                // (6,41): warning CS0649: Field 'C.X' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class C { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("C.X", "0").WithLocation(6, 41),
                // (7,49): warning CS0649: Field 'D.X' is never assigned to, and will always have its default value 0
                // [Sharper.Immutable] sealed class D { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("D.X", "0").WithLocation(7, 49));
        }

        [Fact]
        public void Immutable_TypeNamedImmutableWins()
        {
            var source = """
                class Immutable : System.Attribute { }
                [Immutable] sealed class C { public int X; }
                """;
            CreateImmutableCompilation(source).VerifyDiagnostics(
                // (2,41): warning CS0649: Field 'C.X' is never assigned to, and will always have its default value 0
                // [Immutable] sealed class C { public int X; }
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "X").WithArguments("C.X", "0").WithLocation(2, 41));
        }

        [Fact]
        public void Immutable_SemanticModel()
        {
            var source = """
                [Immutable] sealed class C { }
                [ImmutableAttribute] sealed class D { }
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            var tree = comp.SyntaxTrees[0];
            var model = comp.GetSemanticModel(tree);
            var names = tree.GetRoot().DescendantNodes().OfType<Syntax.AttributeSyntax>().Select(a => a.Name).ToArray();
            var attribute = comp.GetMember<NamedTypeSymbol>("C").GetAttributes().Single().AttributeClass!;
            Assert.Equal("Sharper.ImmutableAttribute", attribute.ToTestDisplayString());
            Assert.True(attribute.IsImplicitlyDeclared);
            foreach (var name in names)
            {
                Assert.Equal("Sharper.ImmutableAttribute..ctor()", model.GetSymbolInfo(name).Symbol.ToTestDisplayString());
                Assert.Equal("Sharper.ImmutableAttribute", model.GetTypeInfo(name).Type.ToTestDisplayString());
            }
        }

        [Fact]
        public void Immutable_ExactInstancesOnly()
        {
            // [Immutable] puts no requirement on subclasses: a mutable subclass simply is not immutable.
            var source = """
                [Immutable] abstract class Base { public abstract int Area { get; } }
                class Mutable : Base { public int Side = 1; public override int Area => Side * Side; }
                [Immutable] record Person(string Name);
                record Employee(string Name, System.Collections.Generic.List<string> Roles) : Person(Name);
                """;
            var comp = CreateImmutableCompilation(source);
            comp.VerifyDiagnostics();
            Assert.True(IsImmutable(comp, "Base", exact: true));
            Assert.False(IsImmutable(comp, "Base", exact: false));
            Assert.False(IsImmutable(comp, "Mutable", exact: true));
            Assert.False(IsImmutable(comp, "Employee", exact: true));
        }

        [Fact]
        public void Immutable_Concurrent()
        {
            // The same results and reasons, whichever thread reaches a cycle first.
            var source = """
                #nullable enable
                using System.Collections.Generic;
                [Immutable] sealed record A(B? B, D? D);
                [Immutable] sealed record B(C? C, A? A);
                [Immutable] sealed record C(A? A, List<int> Xs);
                [Immutable] sealed record D(E? E, A? A);
                [Immutable] sealed record E(D? D, int X);
                """;
            var expected = CreateImmutableCompilation(source).GetDiagnostics().Select(d => d.ToString()).Order().ToArray();
            Assert.Equal(5, expected.Length);
            for (int i = 0; i < 10; i++)
            {
                var comp = CreateImmutableCompilation(source);
                var names = new[] { "A", "B", "C", "D", "E" };
                System.Threading.Tasks.Parallel.ForEach(names, name => IsImmutable(comp, name, exact: false));
                Assert.Equal(expected, comp.GetDiagnostics().Select(d => d.ToString()).Order());
            }
        }
    }
}
