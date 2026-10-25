// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
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
    }
}
