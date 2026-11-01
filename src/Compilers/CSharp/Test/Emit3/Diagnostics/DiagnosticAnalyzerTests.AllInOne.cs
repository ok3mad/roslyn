// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    public partial class DiagnosticAnalyzerTests
    {
        [Fact]
        public void DiagnosticAnalyzerAllInOne()
        {
            var source = TestResource.AllInOneCSharpCode;

            // AllInOneCSharpCode has no properties with initializers/attributes.
            var symbolKindsWithNoCodeBlocks = new HashSet<SymbolKind>();
            symbolKindsWithNoCodeBlocks.Add(SymbolKind.Property);

            var analyzer = new CSharpTrackingDiagnosticAnalyzer();
            var options = new AnalyzerOptions(new[] { new TestAdditionalText() }.ToImmutableArray<AdditionalText>());
            CreateCompilationWithMscorlib461(source).VerifyAnalyzerDiagnostics(new[] { analyzer }, options);
            analyzer.VerifyAllAnalyzerMembersWereCalled();
            analyzer.VerifyAnalyzeSymbolCalledForAllSymbolKinds();
            // Sharper's node kinds (9900 and up, see SyntaxKind.FunctionType) need /features:sharper, which this C# source does
            // not use. DiagnosticAnalyzerAllInOne_Sharper covers them.
            analyzer.VerifyAnalyzeNodeCalledForAllSyntaxKinds([.. s_sharperSyntaxKinds]);
            analyzer.VerifyOnCodeBlockCalledForAllSymbolAndMethodKinds(symbolKindsWithNoCodeBlocks);
        }

        private static readonly ImmutableArray<SyntaxKind> s_sharperSyntaxKinds =
            [.. Enum.GetValues<SyntaxKind>().Where(k => (int)k >= (int)SyntaxKind.FunctionType)];

        [Fact]
        public void DiagnosticAnalyzerAllInOne_Sharper()
        {
            // Uses every Sharper syntax node: a function type, a task type, a parenthesized function type and a try expression.
            var source = """
                #nullable enable
                using System.Threading.Tasks;

                class C
                {
                    (int, string) -> bool _predicate = (i, s) => s.Length == i;
                    void Notify(((int) -> void)? callback) => callback?.Invoke(_predicate(1, "a") ? 1 : 0);

                    async int~ GetAsync() { await Task.Yield(); return 1; }
                    async void~ RunAsync() => await GetAsync();
                    int Parse(string s) => try int.Parse(s) catch { System.FormatException => -1 };
                }
                """;

            var analyzer = new CSharpTrackingDiagnosticAnalyzer();
            var options = TestOptions.Regular.WithFeatures([new KeyValuePair<string, string>(Feature.Sharper, "true")]);
            var comp = CreateCompilation(source, parseOptions: options);
            comp.VerifyDiagnostics();
            comp.VerifyAnalyzerDiagnostics([analyzer]);

            var analyzedKinds = analyzer.CallLog.Where(e => e.AbstractMemberName == "SyntaxNode").Select(e => e.SyntaxKind).ToHashSet();
            // A new Sharper node kind fails here until the source above uses it.
            var missing = s_sharperSyntaxKinds.Where(k => !analyzedKinds.Contains(k));
            Assert.True(!missing.Any(), "Missing: " + string.Join(", ", missing));
        }

        [WorkItem(896075, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/896075")]
        [Fact]
        public void DiagnosticAnalyzerIndexerDeclaration()
        {
            var source = @"
public class C
{
    public string this[int index]
    {
        get { return string.Empty; }
        set { value = value + string.Empty; }
    }
}
";
            CreateCompilationWithMscorlib461(source).VerifyAnalyzerDiagnostics(new[] { new CSharpTrackingDiagnosticAnalyzer() });
        }

        // AllInOne does not include experimental features.
        #region Experimental Features

        [Fact]
        public void DiagnosticAnalyzerConditionalAccess()
        {
            var source = @"
public class C
{
    public string this[int index]
    {
        get { return string.Empty ?. ToString() ?[1] .ToString() ; }
        set { value = value + string.Empty; }
    }
}
";
            CreateCompilationWithMscorlib461(source).VerifyAnalyzerDiagnostics(new[] { new CSharpTrackingDiagnosticAnalyzer() });
        }

        [Fact]
        public void DiagnosticAnalyzerExpressionBodiedProperty()
        {
            var comp = CreateCompilationWithMscorlib461(@"
public class C
{
    public int P => 10;
}").VerifyAnalyzerDiagnostics(new[] { new CSharpTrackingDiagnosticAnalyzer() });
        }

        #endregion

        [Fact]
        [WorkItem(759, "https://github.com/dotnet/roslyn/issues/759")]
        public void AnalyzerDriverIsSafeAgainstAnalyzerExceptions()
        {
            var compilation = CreateCompilationWithMscorlib461(TestResource.AllInOneCSharpCode);
            var options = new AnalyzerOptions(new[] { new TestAdditionalText() }.ToImmutableArray<AdditionalText>());

            ThrowingDiagnosticAnalyzer<SyntaxKind>.VerifyAnalyzerEngineIsSafeAgainstExceptions(analyzer =>
                compilation.GetAnalyzerDiagnostics(new[] { analyzer }, options));
        }

        [Fact]
        public void AnalyzerOptionsArePassedToAllAnalyzers()
        {
            var text = new StringText(string.Empty, encodingOpt: null);
            AnalyzerOptions options = new AnalyzerOptions
            (
                new[] { new TestAdditionalText("myfilepath", text) }.ToImmutableArray<AdditionalText>()
            );

            var compilation = CreateCompilationWithMscorlib461(TestResource.AllInOneCSharpCode);
            var analyzer = new OptionsDiagnosticAnalyzer<SyntaxKind>(options);
            compilation.GetAnalyzerDiagnostics(new[] { analyzer }, options);
            analyzer.VerifyAnalyzerOptions();
        }
    }
}
