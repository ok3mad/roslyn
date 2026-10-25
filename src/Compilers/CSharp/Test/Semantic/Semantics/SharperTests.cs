// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    public sealed class SharperTests : CSharpTestBase
    {
        [Fact]
        public void Gating_DisabledByDefault()
        {
            Assert.False(TestOptions.Regular.IsFeatureEnabled(MessageID.IDS_FeatureSharper));
            Assert.False(TestOptions.RegularPreview.IsFeatureEnabled(MessageID.IDS_FeatureSharper));
        }

        [Fact]
        public void Gating_EnabledByFeatureFlag()
        {
            var options = TestOptions.Regular.WithFeatures([new KeyValuePair<string, string>(Feature.Sharper, "true")]);
            Assert.True(options.IsFeatureEnabled(MessageID.IDS_FeatureSharper));
            Assert.True(TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper).IsFeatureEnabled(MessageID.IDS_FeatureSharper));
        }

        [Fact]
        public void Gating_DisabledDiagnostic()
        {
            var info = MessageID.IDS_FeatureSharper.GetFeatureAvailabilityDiagnosticInfo(TestOptions.RegularPreview);
            Assert.NotNull(info);
            Assert.Equal(ErrorCode.ERR_FeatureIsExperimental, (ErrorCode)info.Code);
            Assert.Equal("Feature 'Sharper' is experimental and unsupported; use '/features:sharper' to enable.", info.GetMessage());

            Assert.Null(MessageID.IDS_FeatureSharper.GetFeatureAvailabilityDiagnosticInfo(TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper)));
        }

        private static readonly CSharpParseOptions s_sharperOptions = TestOptions.Regular.WithExperimental(MessageID.IDS_FeatureSharper);

        [Fact]
        public void YieldReturnSpread_Basic()
        {
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in Spread(new[] { 1, 2, 3 }))
                    Console.Write(x);

                static IEnumerable<int> Spread(IEnumerable<int> xs)
                {
                    yield return 0;
                    yield return ..xs;
                    yield return 4;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "01234" : null).VerifyDiagnostics();
        }

        [Theory]
        [CombinatorialData]
        public void YieldReturnSpread_ExistingRangeMeaningIsKept(bool sharper)
        {
            // In an iterator of Range, `..i` is already a valid range expression in C#.
            // Sharper must keep that meaning; the spread only applies where C# reports an error.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var r in Ranges(2))
                    Console.Write(r);

                static IEnumerable<Range> Ranges(int i)
                {
                    yield return ..i;
                }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0..2" : null).VerifyDiagnostics();
        }

        [Theory]
        [CombinatorialData]
        public void YieldReturnSpread_ExistingRangeMeaningIsKept_ObjectElement(bool sharper)
        {
            // In an iterator of object, `..i` is a valid range that gets boxed, so it stays a range.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var r in Ranges(2))
                    Console.Write(r);

                static IEnumerable<object> Ranges(int i)
                {
                    yield return ..i;
                }
                """;
            CompileAndVerify(source, parseOptions: sharper ? s_sharperOptions : TestOptions.Regular, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "0..2" : null).VerifyDiagnostics();
        }

        [Fact]
        public void YieldReturnSpread_ObjectElement()
        {
            // In an iterator of object, `..xs` is not a valid range, so it spreads,
            // and each element is boxed rather than the sequence being yielded as one object.
            var source = """
                using System;
                using System.Collections.Generic;

                foreach (var x in Spread(new[] { 1, 2, 3 }))
                    Console.Write(x);

                static IEnumerable<object> Spread(IEnumerable<int> xs)
                {
                    yield return ..xs;
                }
                """;
            CompileAndVerify(source, parseOptions: s_sharperOptions, targetFramework: TargetFramework.Net90, expectedOutput: ExecutionConditionUtil.IsCoreClr ? "123" : null).VerifyDiagnostics();
        }
    }
}
