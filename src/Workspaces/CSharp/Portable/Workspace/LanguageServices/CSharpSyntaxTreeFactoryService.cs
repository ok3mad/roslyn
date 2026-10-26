// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp;

internal sealed partial class CSharpSyntaxTreeFactoryService : AbstractSyntaxTreeFactoryService
{
    private static readonly CSharpParseOptions _parseOptionWithLatestLanguageVersion = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpSyntaxTreeFactoryService(ISyntaxTreeCacheService syntaxTreeCache)
        : base(syntaxTreeCache)
    {
    }

    public override ParseOptions GetDefaultParseOptions()
        => CSharpParseOptions.Default;

    public override ParseOptions GetDefaultParseOptionsWithLatestLanguageVersion()
        => _parseOptionWithLatestLanguageVersion;

    public override ParseOptions TryParsePdbParseOptions(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("language-version", out var langVersionString) ||
            !LanguageVersionFacts.TryParse(langVersionString, out var langVersion))
        {
            langVersion = LanguageVersion.Default;
        }

        return new CSharpParseOptions(
            languageVersion: langVersion,
            preprocessorSymbols: metadata.TryGetValue("define", out var defines) ? defines.Split(',') : null);
    }

    public override bool OptionsDifferOnlyByPreprocessorDirectives(ParseOptions options1, ParseOptions options2)
    {
        var csharpOptions1 = (CSharpParseOptions)options1;
        var csharpOptions2 = (CSharpParseOptions)options2;

        // The easy way to figure out if these only differ by a single field is to update one with the preprocessor symbols of the
        // other, and then do an equality check from there; this is future proofed if another value is ever added.
        return csharpOptions1.WithPreprocessorSymbols(csharpOptions2.PreprocessorSymbolNames) == csharpOptions2;
    }

    public override SyntaxTree CreateSyntaxTree(string filePath, ParseOptions options, SourceText text, Encoding encoding, SourceHashAlgorithm checksumAlgorithm, SyntaxNode root)
    {
        options = GetParseOptionsForFile(filePath, options ?? GetDefaultParseOptions());
        return new ParsedSyntaxTree(text, (CSharpSyntaxNode)root, (CSharpParseOptions)options, filePath, encoding, checksumAlgorithm);
    }

    protected override SyntaxTree ParseSyntaxTreeCore(string filePath, ParseOptions options, SourceText text, CancellationToken cancellationToken)
        => SyntaxFactory.ParseSyntaxTree(text, options, filePath, cancellationToken: cancellationToken);

    /// <summary>
    /// Sharper files (.cx) are parsed with the Sharper feature turned on, as the command-line compiler does
    /// (<c>CSharpCompiler.IsSharperFile</c>; the extension and feature name are internal to the compiler).
    /// </summary>
    protected override ParseOptions GetParseOptionsForFile(string filePath, ParseOptions options)
    {
        if (filePath is null
            || !filePath.EndsWith(SharperFileExtension, StringComparison.OrdinalIgnoreCase)
            || options.Features.ContainsKey(SharperFeature))
        {
            return options;
        }

        return options.WithFeatures(options.Features.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase).SetItem(SharperFeature, "true"));
    }

    private const string SharperFileExtension = ".cx";
    private const string SharperFeature = "sharper";
}

[ExportLanguageServiceFactory(typeof(ISyntaxTreeFactoryService), LanguageNames.CSharp), Shared]
internal sealed class CSharpSyntaxTreeFactoryServiceFactory : ILanguageServiceFactory
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public CSharpSyntaxTreeFactoryServiceFactory()
    {
    }

    public ILanguageService CreateLanguageService(HostLanguageServices languageServices)
        => new CSharpSyntaxTreeFactoryService(languageServices.WorkspaceServices.GetService<ISyntaxTreeCacheService>());
}
