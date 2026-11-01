// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// The value of a Sharper pipe, <c>x |&gt; stage</c> (#25): a read-only local holding <c>x</c>. A stage that uses
    /// <c>it</c> finds it by name; a bare stage passes it as the argument. Codegen and closures treat it as a user
    /// variable named <c>it</c>, declared at its first use, so the debugger shows it and a lambda can capture it.
    /// </summary>
    internal sealed class SharperPipeItLocalSymbol : LocalSymbol
    {
        private readonly Symbol _containingSymbol;
        private readonly TypeWithAnnotations _type;
        private readonly SyntaxNode _syntax;

        /// <param name="syntax">The first <c>it</c> of the stage, or the stage itself when it is bare.</param>
        internal SharperPipeItLocalSymbol(Symbol containingSymbol, TypeWithAnnotations type, SyntaxNode syntax)
        {
            _containingSymbol = containingSymbol;
            _type = type;
            _syntax = syntax;
        }

        public override string Name => Binder.SharperItName;

        public override Symbol ContainingSymbol => _containingSymbol;

        public override TypeWithAnnotations TypeWithAnnotations => _type;

        public override RefKind RefKind => RefKind.None;

        internal override LocalDeclarationKind DeclarationKind => LocalDeclarationKind.None;

        internal override SynthesizedLocalKind SynthesizedKind => SynthesizedLocalKind.UserDefined;

        internal override SyntaxNode? ScopeDesignatorOpt => null;

        internal override LocalSymbol WithSynthesizedLocalKindAndSyntax(
            SynthesizedLocalKind kind, SyntaxNode syntax
#if DEBUG
            ,
            [CallerLineNumber] int createdAtLineNumber = 0,
            [CallerFilePath] string? createdAtFilePath = null
#endif
            )
        {
            return new SynthesizedLocal(_containingSymbol as MethodSymbol, _type, kind, syntax);
        }

        internal override bool IsImportedFromMetadata => false;

        internal override SyntaxToken IdentifierToken => default;

        internal override bool IsPinned => false;

        internal override bool IsKnownToReferToTempIfReferenceType => false;

        internal override ScopedKind Scope => ScopedKind.None;

        internal override SyntaxNode GetDeclaratorSyntax() => _syntax;

        internal override bool HasSourceLocation => true;

        /// <summary>Like the <c>it</c> of an implicit lambda, the piped value cannot be assigned.</summary>
        internal override bool IsWritableVariable => false;

        internal override bool IsCompilerGenerated => false;

        public override bool IsImplicitlyDeclared => true;

        public override ImmutableArray<Location> Locations => ImmutableArray.Create(_syntax.Location);

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => ImmutableArray<SyntaxReference>.Empty;

        internal override ConstantValue? GetConstantValue(SyntaxNode node, LocalSymbol inProgress, BindingDiagnosticBag? diagnostics = null) => null;

        internal override ReadOnlyBindingDiagnostic<AssemblySymbol> GetConstantValueDiagnostics(BoundExpression boundInitValue)
            => ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty;
    }
}
