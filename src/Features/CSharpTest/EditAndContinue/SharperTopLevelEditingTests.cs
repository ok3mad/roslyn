// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.EditAndContinue.UnitTests;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.EditAndContinue.UnitTests;

/// <summary>
/// Edits of declarations with Sharper syntax (docs/sharper).
/// </summary>
[UseExportProvider]
public sealed class SharperTopLevelEditingTests : EditingTestBase
{
    private static readonly CSharpParseOptions s_sharperParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithFeatures([new("sharper", "true")]);

    [Fact]
    public void InferredReturnType_BodyEditChangesType()
    {
        // A body edit that changes an inferred return type ('infer') changes the signature that callers were compiled
        // against, so it needs a restart. An edit that keeps the type is an ordinary body update.
        var src1 = """
            class C
            {
                public static infer F() => 1;
                public static infer G() => 1;
                static void M() { var x = F(); var y = G(); }
            }
            """;
        var src2 = """
            class C
            {
                public static infer F() => "one";
                public static infer G() => 2;
                static void M() { var x = F(); var y = G(); }
            }
            """;

        var edits = GetTopEdits(src1, src2, parseOptions: s_sharperParseOptions);

        edits.VerifySemanticDiagnostics(
            s_sharperParseOptions,
            Diagnostic(RudeEditKind.ChangeSharperInferredReturnType, "public static infer F()", GetResource("method")));
    }
}
