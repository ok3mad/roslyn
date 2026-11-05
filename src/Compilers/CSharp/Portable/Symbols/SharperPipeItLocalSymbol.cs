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
    /// A lambda stage, <c>x |&gt; (y =&gt; y + 1)</c> (#186), names the value: the local is then named after the lambda's
    /// parameter and declared by it.
    /// </summary>
    internal sealed class SharperPipeItLocalSymbol : LocalSymbol
    {
        private readonly Symbol _containingSymbol;
        private readonly TypeWithAnnotations _type;
        private readonly SyntaxNode _syntax;
        private readonly ParameterSyntax? _lambdaParameter;

        /// <param name="syntax">The first <c>it</c> of the stage, or the stage itself when it is bare.</param>
        internal SharperPipeItLocalSymbol(Symbol containingSymbol, TypeWithAnnotations type, SyntaxNode syntax)
        {
            _containingSymbol = containingSymbol;
            _type = type;
            _syntax = syntax;
        }

        /// <param name="lambdaParameter">The parameter of a lambda stage, which names the value.</param>
        internal SharperPipeItLocalSymbol(Symbol containingSymbol, TypeWithAnnotations type, ParameterSyntax lambdaParameter, bool isExplicitlyTyped)
            : this(containingSymbol, type, lambdaParameter)
        {
            _lambdaParameter = lambdaParameter;
            IsExplicitlyTyped = isExplicitlyTyped;
        }

        /// <summary>The parameter of a lambda stage (<c>x |&gt; (y =&gt; y + 1)</c>) that names the value, if any.</summary>
        internal ParameterSyntax? LambdaParameter => _lambdaParameter;

        /// <summary>
        /// The lambda stage's parameter has a type, to which the left side converts; the nullable walker keeps that type
        /// instead of inferring one.
        /// </summary>
        internal bool IsExplicitlyTyped { get; }

        public override string Name => _lambdaParameter?.Identifier.ValueText ?? Binder.SharperItName;

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

        internal override SyntaxToken IdentifierToken => _lambdaParameter?.Identifier ?? default;

        internal override bool IsPinned => false;

        internal override bool IsKnownToReferToTempIfReferenceType => false;

        internal override ScopedKind Scope => ScopedKind.None;

        internal override SyntaxNode GetDeclaratorSyntax() => _syntax;

        internal override bool HasSourceLocation => true;

        /// <summary>Like the <c>it</c> of an implicit lambda, the piped value cannot be assigned.</summary>
        internal override bool IsWritableVariable => false;

        internal override bool IsCompilerGenerated => false;

        public override bool IsImplicitlyDeclared => _lambdaParameter is null;

        public override ImmutableArray<Location> Locations
            => ImmutableArray.Create(_lambdaParameter is null ? _syntax.Location : _lambdaParameter.Identifier.GetLocation());

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
            => _lambdaParameter is null ? ImmutableArray<SyntaxReference>.Empty : ImmutableArray.Create(_lambdaParameter.GetReference());

        internal override ConstantValue? GetConstantValue(SyntaxNode node, LocalSymbol inProgress, BindingDiagnosticBag? diagnostics = null) => null;

        internal override ReadOnlyBindingDiagnostic<AssemblySymbol> GetConstantValueDiagnostics(BoundExpression boundInitValue)
            => ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty;
    }
}
