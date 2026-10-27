// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.CSharp.UnitTests;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.CSharp.EditAndContinue.UnitTests
{
    /// <summary>
    /// Edit and Continue tests for Sharper features (docs/sharper).
    /// </summary>
    [CompilerTrait(CompilerFeature.Iterator)]
    public class EditAndContinueSharperTests(ITestOutputHelper logger) : EditAndContinueTestBase
    {
        private static readonly CSharpParseOptions s_sharperParseOptions = TestOptions.Regular.WithNoRefSafetyRulesAttribute().WithExperimental(MessageID.IDS_FeatureSharper);

        private EditAndContinueTest CreateTest() => new EditAndContinueTest(logger, parseOptions: s_sharperParseOptions);

        /// <summary>
        /// The yield state and the finalize state of a spread share its syntax offset and are told apart by their
        /// relative ordinal, also across a second generation (which maps states from the first one, not from the PDB).
        /// </summary>
        [Fact]
        public void YieldReturnSpread_EditBeforeSpread()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 1;</N:0>
                            <N:1>yield return ..xs;</N:1>
                            <N:2>yield return 3;</N:2>
                        }
                    }
                    """,
                    validator: g =>
                    {
                        g.VerifyCustomDebugInformation("C.F", """
                            <symbols>
                              <methods>
                                <method containingType="C" name="F" parameterNames="xs">
                                  <customDebugInfo>
                                    <encLocalSlotMap>
                                      <slot kind="5" offset="52" />
                                    </encLocalSlotMap>
                                    <encStateMachineStateMap>
                                      <state number="1" offset="16" />
                                      <state number="2" offset="52" />
                                      <state number="-3" offset="52" />
                                      <state number="3" offset="91" />
                                    </encStateMachineStateMap>
                                  </customDebugInfo>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyMethodBody("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      216 (0xd8)
                              .maxstack  2
                              .locals init (bool V_0,
                                            int V_1,
                                            int V_2)
                              .try
                              {
                                // sequence point: <hidden>
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  switch    (
                                    IL_001f,
                                    IL_0021,
                                    IL_0023,
                                    IL_0025)
                                IL_001d:  br.s       IL_002a
                                IL_001f:  br.s       IL_0031
                                IL_0021:  br.s       IL_004e
                                IL_0023:  br.s       IL_008e
                                IL_0025:  br         IL_00c3
                                IL_002a:  ldc.i4.0
                                IL_002b:  stloc.0
                                IL_002c:  leave      IL_00d6
                                IL_0031:  ldarg.0
                                IL_0032:  ldc.i4.m1
                                IL_0033:  stfld      "int C.<F>d__0.<>1__state"
                                // sequence point: {
                                IL_0038:  nop
                                // sequence point: yield return 1;
                                IL_0039:  ldarg.0
                                IL_003a:  ldc.i4.1
                                IL_003b:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0040:  ldarg.0
                                IL_0041:  ldc.i4.1
                                IL_0042:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0047:  ldc.i4.1
                                IL_0048:  stloc.0
                                IL_0049:  leave      IL_00d6
                                // sequence point: <hidden>
                                IL_004e:  ldarg.0
                                IL_004f:  ldc.i4.m1
                                IL_0050:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0055:  ldarg.0
                                IL_0056:  ldarg.0
                                IL_0057:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_005c:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_0061:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0066:  ldarg.0
                                IL_0067:  ldc.i4.s   -3
                                IL_0069:  stfld      "int C.<F>d__0.<>1__state"
                                // sequence point: <hidden>
                                IL_006e:  br.s       IL_0096
                                IL_0070:  ldarg.0
                                IL_0071:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0076:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_007b:  stloc.2
                                // sequence point: yield return ..xs;
                                IL_007c:  ldarg.0
                                IL_007d:  ldloc.2
                                IL_007e:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0083:  ldarg.0
                                IL_0084:  ldc.i4.2
                                IL_0085:  stfld      "int C.<F>d__0.<>1__state"
                                IL_008a:  ldc.i4.1
                                IL_008b:  stloc.0
                                IL_008c:  leave.s    IL_00d6
                                // sequence point: <hidden>
                                IL_008e:  ldarg.0
                                IL_008f:  ldc.i4.s   -3
                                IL_0091:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0096:  ldarg.0
                                IL_0097:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_009c:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_00a1:  brtrue.s   IL_0070
                                IL_00a3:  ldarg.0
                                IL_00a4:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_00a9:  nop
                                IL_00aa:  ldarg.0
                                IL_00ab:  ldnull
                                IL_00ac:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                // sequence point: yield return 3;
                                IL_00b1:  ldarg.0
                                IL_00b2:  ldc.i4.3
                                IL_00b3:  stfld      "int C.<F>d__0.<>2__current"
                                IL_00b8:  ldarg.0
                                IL_00b9:  ldc.i4.3
                                IL_00ba:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00bf:  ldc.i4.1
                                IL_00c0:  stloc.0
                                IL_00c1:  leave.s    IL_00d6
                                // sequence point: <hidden>
                                IL_00c3:  ldarg.0
                                IL_00c4:  ldc.i4.m1
                                IL_00c5:  stfld      "int C.<F>d__0.<>1__state"
                                // sequence point: }
                                IL_00ca:  ldc.i4.0
                                IL_00cb:  stloc.0
                                IL_00cc:  leave.s    IL_00d6
                              }
                              fault
                              {
                                // sequence point: <hidden>
                                IL_00ce:  ldarg.0
                                IL_00cf:  call       "void C.<F>d__0.Dispose()"
                                IL_00d4:  nop
                                IL_00d5:  endfinally
                              }
                              IL_00d6:  ldloc.0
                              IL_00d7:  ret
                            }
                            """);
                    })
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 10;</N:0>
                            <N:1>yield return ..xs;</N:1>
                            <N:2>yield return 3;</N:2>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x38" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x39" startLine="7" startColumn="14" endLine="7" endColumn="30" document="1" />
                                    <entry offset="0x4f" hidden="true" document="1" />
                                    <entry offset="0x6f" hidden="true" document="1" />
                                    <entry offset="0x7d" startLine="8" startColumn="14" endLine="8" endColumn="32" document="1" />
                                    <entry offset="0x8f" hidden="true" document="1" />
                                    <entry offset="0xb2" startLine="9" startColumn="14" endLine="9" endColumn="29" document="1" />
                                    <entry offset="0xc4" hidden="true" document="1" />
                                    <entry offset="0xcb" startLine="10" startColumn="5" endLine="10" endColumn="6" document="1" />
                                    <entry offset="0xcf" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      217 (0xd9)
                              .maxstack  2
                              .locals init (bool V_0,
                                            int V_1,
                                            int V_2)
                              .try
                              {
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  switch    (
                                    IL_001f,
                                    IL_0021,
                                    IL_0023,
                                    IL_0025)
                                IL_001d:  br.s       IL_002a
                                IL_001f:  br.s       IL_0031
                                IL_0021:  br.s       IL_004f
                                IL_0023:  br.s       IL_008f
                                IL_0025:  br         IL_00c4
                                IL_002a:  ldc.i4.0
                                IL_002b:  stloc.0
                                IL_002c:  leave      IL_00d7
                                IL_0031:  ldarg.0
                                IL_0032:  ldc.i4.m1
                                IL_0033:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0038:  nop
                                IL_0039:  ldarg.0
                                IL_003a:  ldc.i4.s   10
                                IL_003c:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0041:  ldarg.0
                                IL_0042:  ldc.i4.1
                                IL_0043:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0048:  ldc.i4.1
                                IL_0049:  stloc.0
                                IL_004a:  leave      IL_00d7
                                IL_004f:  ldarg.0
                                IL_0050:  ldc.i4.m1
                                IL_0051:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0056:  ldarg.0
                                IL_0057:  ldarg.0
                                IL_0058:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_005d:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_0062:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0067:  ldarg.0
                                IL_0068:  ldc.i4.s   -3
                                IL_006a:  stfld      "int C.<F>d__0.<>1__state"
                                IL_006f:  br.s       IL_0097
                                IL_0071:  ldarg.0
                                IL_0072:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0077:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_007c:  stloc.2
                                IL_007d:  ldarg.0
                                IL_007e:  ldloc.2
                                IL_007f:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0084:  ldarg.0
                                IL_0085:  ldc.i4.2
                                IL_0086:  stfld      "int C.<F>d__0.<>1__state"
                                IL_008b:  ldc.i4.1
                                IL_008c:  stloc.0
                                IL_008d:  leave.s    IL_00d7
                                IL_008f:  ldarg.0
                                IL_0090:  ldc.i4.s   -3
                                IL_0092:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0097:  ldarg.0
                                IL_0098:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_009d:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_00a2:  brtrue.s   IL_0071
                                IL_00a4:  ldarg.0
                                IL_00a5:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_00aa:  nop
                                IL_00ab:  ldarg.0
                                IL_00ac:  ldnull
                                IL_00ad:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00b2:  ldarg.0
                                IL_00b3:  ldc.i4.3
                                IL_00b4:  stfld      "int C.<F>d__0.<>2__current"
                                IL_00b9:  ldarg.0
                                IL_00ba:  ldc.i4.3
                                IL_00bb:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00c0:  ldc.i4.1
                                IL_00c1:  stloc.0
                                IL_00c2:  leave.s    IL_00d7
                                IL_00c4:  ldarg.0
                                IL_00c5:  ldc.i4.m1
                                IL_00c6:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00cb:  ldc.i4.0
                                IL_00cc:  stloc.0
                                IL_00cd:  leave.s    IL_00d7
                              }
                              fault
                              {
                                IL_00cf:  ldarg.0
                                IL_00d0:  call       "void C.<F>d__0.Dispose()"
                                IL_00d5:  nop
                                IL_00d6:  endfinally
                              }
                              IL_00d7:  ldloc.0
                              IL_00d8:  ret
                            }
                            """);
                    })
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 100;</N:0>
                            <N:1>yield return ..xs;</N:1>
                            <N:2>yield return 3;</N:2>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x38" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x39" startLine="7" startColumn="14" endLine="7" endColumn="31" document="1" />
                                    <entry offset="0x4f" hidden="true" document="1" />
                                    <entry offset="0x6f" hidden="true" document="1" />
                                    <entry offset="0x7d" startLine="8" startColumn="14" endLine="8" endColumn="32" document="1" />
                                    <entry offset="0x8f" hidden="true" document="1" />
                                    <entry offset="0xb2" startLine="9" startColumn="14" endLine="9" endColumn="29" document="1" />
                                    <entry offset="0xc4" hidden="true" document="1" />
                                    <entry offset="0xcb" startLine="10" startColumn="5" endLine="10" endColumn="6" document="1" />
                                    <entry offset="0xcf" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      217 (0xd9)
                              .maxstack  2
                              .locals init (bool V_0,
                                            int V_1,
                                            int V_2)
                              .try
                              {
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  switch    (
                                    IL_001f,
                                    IL_0021,
                                    IL_0023,
                                    IL_0025)
                                IL_001d:  br.s       IL_002a
                                IL_001f:  br.s       IL_0031
                                IL_0021:  br.s       IL_004f
                                IL_0023:  br.s       IL_008f
                                IL_0025:  br         IL_00c4
                                IL_002a:  ldc.i4.0
                                IL_002b:  stloc.0
                                IL_002c:  leave      IL_00d7
                                IL_0031:  ldarg.0
                                IL_0032:  ldc.i4.m1
                                IL_0033:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0038:  nop
                                IL_0039:  ldarg.0
                                IL_003a:  ldc.i4.s   100
                                IL_003c:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0041:  ldarg.0
                                IL_0042:  ldc.i4.1
                                IL_0043:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0048:  ldc.i4.1
                                IL_0049:  stloc.0
                                IL_004a:  leave      IL_00d7
                                IL_004f:  ldarg.0
                                IL_0050:  ldc.i4.m1
                                IL_0051:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0056:  ldarg.0
                                IL_0057:  ldarg.0
                                IL_0058:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_005d:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_0062:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0067:  ldarg.0
                                IL_0068:  ldc.i4.s   -3
                                IL_006a:  stfld      "int C.<F>d__0.<>1__state"
                                IL_006f:  br.s       IL_0097
                                IL_0071:  ldarg.0
                                IL_0072:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0077:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_007c:  stloc.2
                                IL_007d:  ldarg.0
                                IL_007e:  ldloc.2
                                IL_007f:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0084:  ldarg.0
                                IL_0085:  ldc.i4.2
                                IL_0086:  stfld      "int C.<F>d__0.<>1__state"
                                IL_008b:  ldc.i4.1
                                IL_008c:  stloc.0
                                IL_008d:  leave.s    IL_00d7
                                IL_008f:  ldarg.0
                                IL_0090:  ldc.i4.s   -3
                                IL_0092:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0097:  ldarg.0
                                IL_0098:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_009d:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_00a2:  brtrue.s   IL_0071
                                IL_00a4:  ldarg.0
                                IL_00a5:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_00aa:  nop
                                IL_00ab:  ldarg.0
                                IL_00ac:  ldnull
                                IL_00ad:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00b2:  ldarg.0
                                IL_00b3:  ldc.i4.3
                                IL_00b4:  stfld      "int C.<F>d__0.<>2__current"
                                IL_00b9:  ldarg.0
                                IL_00ba:  ldc.i4.3
                                IL_00bb:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00c0:  ldc.i4.1
                                IL_00c1:  stloc.0
                                IL_00c2:  leave.s    IL_00d7
                                IL_00c4:  ldarg.0
                                IL_00c5:  ldc.i4.m1
                                IL_00c6:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00cb:  ldc.i4.0
                                IL_00cc:  stloc.0
                                IL_00cd:  leave.s    IL_00d7
                              }
                              fault
                              {
                                IL_00cf:  ldarg.0
                                IL_00d0:  call       "void C.<F>d__0.Dispose()"
                                IL_00d5:  nop
                                IL_00d6:  endfinally
                              }
                              IL_00d7:  ldloc.0
                              IL_00d8:  ret
                            }
                            """);
                    })
                .Verify();
        }

        [Fact]
        public void YieldReturnSpread_EditAfterSpread()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return ..xs;</N:0>
                            <N:1>yield return 2;</N:1>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return ..xs;</N:0>
                            <N:1>yield return 20;</N:1>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x2f" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x49" hidden="true" document="1" />
                                    <entry offset="0x57" startLine="7" startColumn="14" endLine="7" endColumn="32" document="1" />
                                    <entry offset="0x69" hidden="true" document="1" />
                                    <entry offset="0x8c" startLine="8" startColumn="14" endLine="8" endColumn="30" document="1" />
                                    <entry offset="0x9f" hidden="true" document="1" />
                                    <entry offset="0xa6" startLine="9" startColumn="5" endLine="9" endColumn="6" document="1" />
                                    <entry offset="0xaa" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                    })
                .Verify();
        }

        /// <summary>
        /// The states and the hoisted enumerator are reused, so a suspended iterator continues the old enumeration.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_EditSource()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs, IEnumerable<int> ys)
                        {
                            <N:0>yield return ..xs;</N:0>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs, IEnumerable<int> ys)
                        {
                            <N:0>yield return ..ys;</N:0>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "ys: System.Collections.Generic.IEnumerable<int>",
                            "<>3__ys: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x21" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x3b" hidden="true" document="1" />
                                    <entry offset="0x49" startLine="7" startColumn="14" endLine="7" endColumn="32" document="1" />
                                    <entry offset="0x5b" hidden="true" document="1" />
                                    <entry offset="0x7e" startLine="8" startColumn="5" endLine="8" endColumn="6" document="1" />
                                    <entry offset="0x82" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      140 (0x8c)
                              .maxstack  2
                              .locals init (bool V_0,
                                            int V_1,
                                            int V_2)
                              .try
                              {
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  brfalse.s  IL_0012
                                IL_000a:  br.s       IL_000c
                                IL_000c:  ldloc.1
                                IL_000d:  ldc.i4.1
                                IL_000e:  beq.s      IL_0014
                                IL_0010:  br.s       IL_0016
                                IL_0012:  br.s       IL_001a
                                IL_0014:  br.s       IL_005b
                                IL_0016:  ldc.i4.0
                                IL_0017:  stloc.0
                                IL_0018:  leave.s    IL_008a
                                IL_001a:  ldarg.0
                                IL_001b:  ldc.i4.m1
                                IL_001c:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0021:  nop
                                IL_0022:  ldarg.0
                                IL_0023:  ldarg.0
                                IL_0024:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.ys"
                                IL_0029:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_002e:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0033:  ldarg.0
                                IL_0034:  ldc.i4.s   -3
                                IL_0036:  stfld      "int C.<F>d__0.<>1__state"
                                IL_003b:  br.s       IL_0063
                                IL_003d:  ldarg.0
                                IL_003e:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0043:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_0048:  stloc.2
                                IL_0049:  ldarg.0
                                IL_004a:  ldloc.2
                                IL_004b:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0050:  ldarg.0
                                IL_0051:  ldc.i4.1
                                IL_0052:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0057:  ldc.i4.1
                                IL_0058:  stloc.0
                                IL_0059:  leave.s    IL_008a
                                IL_005b:  ldarg.0
                                IL_005c:  ldc.i4.s   -3
                                IL_005e:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0063:  ldarg.0
                                IL_0064:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0069:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_006e:  brtrue.s   IL_003d
                                IL_0070:  ldarg.0
                                IL_0071:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_0076:  nop
                                IL_0077:  ldarg.0
                                IL_0078:  ldnull
                                IL_0079:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_007e:  ldc.i4.0
                                IL_007f:  stloc.0
                                IL_0080:  leave.s    IL_008a
                              }
                              fault
                              {
                                IL_0082:  ldarg.0
                                IL_0083:  call       "void C.<F>d__0.Dispose()"
                                IL_0088:  nop
                                IL_0089:  endfinally
                              }
                              IL_008a:  ldloc.0
                              IL_008b:  ret
                            }
                            """);
                    })
                .Verify();
        }

        /// <summary>
        /// As for a foreach whose collection changes type, the hoisted enumerator is not reused: the array spread gets
        /// new hoisted fields and keeps the yield state.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_EditSource_ChangeType()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs, int[] ys)
                        {
                            <N:0>yield return ..xs;</N:0>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs, int[] ys)
                        {
                            <N:0>yield return ..ys;</N:0>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "ys: int[]",
                            "<>3__ys: int[]",
                            "<>s__2: int[]",
                            "<>s__3: int");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x1f" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x33" hidden="true" document="1" />
                                    <entry offset="0x43" startLine="7" startColumn="14" endLine="7" endColumn="32" document="1" />
                                    <entry offset="0x53" hidden="true" document="1" />
                                    <entry offset="0x5a" hidden="true" document="1" />
                                    <entry offset="0x7f" startLine="8" startColumn="5" endLine="8" endColumn="6" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      129 (0x81)
                              .maxstack  3
                              .locals init (int V_0,
                                            int V_1)
                              IL_0000:  ldarg.0
                              IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                              IL_0006:  stloc.0
                              IL_0007:  ldloc.0
                              IL_0008:  brfalse.s  IL_0012
                              IL_000a:  br.s       IL_000c
                              IL_000c:  ldloc.0
                              IL_000d:  ldc.i4.1
                              IL_000e:  beq.s      IL_0014
                              IL_0010:  br.s       IL_0016
                              IL_0012:  br.s       IL_0018
                              IL_0014:  br.s       IL_0053
                              IL_0016:  ldc.i4.0
                              IL_0017:  ret
                              IL_0018:  ldarg.0
                              IL_0019:  ldc.i4.m1
                              IL_001a:  stfld      "int C.<F>d__0.<>1__state"
                              IL_001f:  nop
                              IL_0020:  ldarg.0
                              IL_0021:  ldarg.0
                              IL_0022:  ldfld      "int[] C.<F>d__0.ys"
                              IL_0027:  stfld      "int[] C.<F>d__0.<>s__2"
                              IL_002c:  ldarg.0
                              IL_002d:  ldc.i4.0
                              IL_002e:  stfld      "int C.<F>d__0.<>s__3"
                              IL_0033:  br.s       IL_0068
                              IL_0035:  ldarg.0
                              IL_0036:  ldfld      "int[] C.<F>d__0.<>s__2"
                              IL_003b:  ldarg.0
                              IL_003c:  ldfld      "int C.<F>d__0.<>s__3"
                              IL_0041:  ldelem.i4
                              IL_0042:  stloc.1
                              IL_0043:  ldarg.0
                              IL_0044:  ldloc.1
                              IL_0045:  stfld      "int C.<F>d__0.<>2__current"
                              IL_004a:  ldarg.0
                              IL_004b:  ldc.i4.1
                              IL_004c:  stfld      "int C.<F>d__0.<>1__state"
                              IL_0051:  ldc.i4.1
                              IL_0052:  ret
                              IL_0053:  ldarg.0
                              IL_0054:  ldc.i4.m1
                              IL_0055:  stfld      "int C.<F>d__0.<>1__state"
                              IL_005a:  ldarg.0
                              IL_005b:  ldarg.0
                              IL_005c:  ldfld      "int C.<F>d__0.<>s__3"
                              IL_0061:  ldc.i4.1
                              IL_0062:  add
                              IL_0063:  stfld      "int C.<F>d__0.<>s__3"
                              IL_0068:  ldarg.0
                              IL_0069:  ldfld      "int C.<F>d__0.<>s__3"
                              IL_006e:  ldarg.0
                              IL_006f:  ldfld      "int[] C.<F>d__0.<>s__2"
                              IL_0074:  ldlen
                              IL_0075:  conv.i4
                              IL_0076:  blt.s      IL_0035
                              IL_0078:  ldarg.0
                              IL_0079:  ldnull
                              IL_007a:  stfld      "int[] C.<F>d__0.<>s__2"
                              IL_007f:  ldc.i4.0
                              IL_0080:  ret
                            }
                            """);
                    })
                .Verify();
        }

        [Fact]
        public void YieldReturnSpread_Add()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 1;</N:0>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 1;</N:0>
                            yield return ..xs;
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x2f" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x30" startLine="7" startColumn="14" endLine="7" endColumn="29" document="1" />
                                    <entry offset="0x42" hidden="true" document="1" />
                                    <entry offset="0x62" hidden="true" document="1" />
                                    <entry offset="0x70" startLine="8" startColumn="9" endLine="8" endColumn="27" document="1" />
                                    <entry offset="0x82" hidden="true" document="1" />
                                    <entry offset="0xa5" startLine="9" startColumn="5" endLine="9" endColumn="6" document="1" />
                                    <entry offset="0xa9" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      179 (0xb3)
                              .maxstack  2
                              .locals init (bool V_0,
                                            int V_1,
                                            int V_2)
                              .try
                              {
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  switch    (
                                    IL_001b,
                                    IL_001d,
                                    IL_001f)
                                IL_0019:  br.s       IL_0021
                                IL_001b:  br.s       IL_0028
                                IL_001d:  br.s       IL_0042
                                IL_001f:  br.s       IL_0082
                                IL_0021:  ldc.i4.0
                                IL_0022:  stloc.0
                                IL_0023:  leave      IL_00b1
                                IL_0028:  ldarg.0
                                IL_0029:  ldc.i4.m1
                                IL_002a:  stfld      "int C.<F>d__0.<>1__state"
                                IL_002f:  nop
                                IL_0030:  ldarg.0
                                IL_0031:  ldc.i4.1
                                IL_0032:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0037:  ldarg.0
                                IL_0038:  ldc.i4.1
                                IL_0039:  stfld      "int C.<F>d__0.<>1__state"
                                IL_003e:  ldc.i4.1
                                IL_003f:  stloc.0
                                IL_0040:  leave.s    IL_00b1
                                IL_0042:  ldarg.0
                                IL_0043:  ldc.i4.m1
                                IL_0044:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0049:  ldarg.0
                                IL_004a:  ldarg.0
                                IL_004b:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_0050:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_0055:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_005a:  ldarg.0
                                IL_005b:  ldc.i4.s   -3
                                IL_005d:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0062:  br.s       IL_008a
                                IL_0064:  ldarg.0
                                IL_0065:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_006a:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_006f:  stloc.2
                                IL_0070:  ldarg.0
                                IL_0071:  ldloc.2
                                IL_0072:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0077:  ldarg.0
                                IL_0078:  ldc.i4.2
                                IL_0079:  stfld      "int C.<F>d__0.<>1__state"
                                IL_007e:  ldc.i4.1
                                IL_007f:  stloc.0
                                IL_0080:  leave.s    IL_00b1
                                IL_0082:  ldarg.0
                                IL_0083:  ldc.i4.s   -3
                                IL_0085:  stfld      "int C.<F>d__0.<>1__state"
                                IL_008a:  ldarg.0
                                IL_008b:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0090:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_0095:  brtrue.s   IL_0064
                                IL_0097:  ldarg.0
                                IL_0098:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_009d:  nop
                                IL_009e:  ldarg.0
                                IL_009f:  ldnull
                                IL_00a0:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00a5:  ldc.i4.0
                                IL_00a6:  stloc.0
                                IL_00a7:  leave.s    IL_00b1
                              }
                              fault
                              {
                                IL_00a9:  ldarg.0
                                IL_00aa:  call       "void C.<F>d__0.Dispose()"
                                IL_00af:  nop
                                IL_00b0:  endfinally
                              }
                              IL_00b1:  ldloc.0
                              IL_00b2:  ret
                            }
                            """);
                    })
                .Verify();
        }

        /// <summary>
        /// The C# that <see cref="YieldReturnSpread_Add"/> stands for. The baseline has no finalize state, so the first
        /// finalize state of the next generation is the first iterator finalize state (-3).
        /// </summary>
        [Fact]
        public void ForEach_Add()
        {
            using var _ = new EditAndContinueTest(logger)
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 1;</N:0>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 1;</N:0>
                            foreach (var x in xs)
                                yield return x;
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>",
                            "<x>5__2: int");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <customDebugInfo>
                                    <hoistedLocalScopes>
                                      <slot />
                                      <slot startOffset="0x65" endOffset="0x95" />
                                    </hoistedLocalScopes>
                                  </customDebugInfo>
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x2f" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x30" startLine="7" startColumn="14" endLine="7" endColumn="29" document="1" />
                                    <entry offset="0x42" hidden="true" document="1" />
                                    <entry offset="0x49" startLine="8" startColumn="9" endLine="8" endColumn="16" document="1" />
                                    <entry offset="0x4a" startLine="8" startColumn="27" endLine="8" endColumn="29" document="1" />
                                    <entry offset="0x63" hidden="true" document="1" />
                                    <entry offset="0x65" startLine="8" startColumn="18" endLine="8" endColumn="23" document="1" />
                                    <entry offset="0x76" startLine="9" startColumn="13" endLine="9" endColumn="28" document="1" />
                                    <entry offset="0x8d" hidden="true" document="1" />
                                    <entry offset="0x95" startLine="8" startColumn="24" endLine="8" endColumn="26" document="1" />
                                    <entry offset="0xb0" startLine="10" startColumn="5" endLine="10" endColumn="6" document="1" />
                                    <entry offset="0xb4" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      190 (0xbe)
                              .maxstack  2
                              .locals init (bool V_0,
                                            int V_1)
                              .try
                              {
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  switch    (
                                    IL_001b,
                                    IL_001d,
                                    IL_001f)
                                IL_0019:  br.s       IL_0021
                                IL_001b:  br.s       IL_0028
                                IL_001d:  br.s       IL_0042
                                IL_001f:  br.s       IL_008d
                                IL_0021:  ldc.i4.0
                                IL_0022:  stloc.0
                                IL_0023:  leave      IL_00bc
                                IL_0028:  ldarg.0
                                IL_0029:  ldc.i4.m1
                                IL_002a:  stfld      "int C.<F>d__0.<>1__state"
                                IL_002f:  nop
                                IL_0030:  ldarg.0
                                IL_0031:  ldc.i4.1
                                IL_0032:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0037:  ldarg.0
                                IL_0038:  ldc.i4.1
                                IL_0039:  stfld      "int C.<F>d__0.<>1__state"
                                IL_003e:  ldc.i4.1
                                IL_003f:  stloc.0
                                IL_0040:  leave.s    IL_00bc
                                IL_0042:  ldarg.0
                                IL_0043:  ldc.i4.m1
                                IL_0044:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0049:  nop
                                IL_004a:  ldarg.0
                                IL_004b:  ldarg.0
                                IL_004c:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_0051:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_0056:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_005b:  ldarg.0
                                IL_005c:  ldc.i4.s   -3
                                IL_005e:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0063:  br.s       IL_0095
                                IL_0065:  ldarg.0
                                IL_0066:  ldarg.0
                                IL_0067:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_006c:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_0071:  stfld      "int C.<F>d__0.<x>5__2"
                                IL_0076:  ldarg.0
                                IL_0077:  ldarg.0
                                IL_0078:  ldfld      "int C.<F>d__0.<x>5__2"
                                IL_007d:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0082:  ldarg.0
                                IL_0083:  ldc.i4.2
                                IL_0084:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0089:  ldc.i4.1
                                IL_008a:  stloc.0
                                IL_008b:  leave.s    IL_00bc
                                IL_008d:  ldarg.0
                                IL_008e:  ldc.i4.s   -3
                                IL_0090:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0095:  ldarg.0
                                IL_0096:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_009b:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_00a0:  brtrue.s   IL_0065
                                IL_00a2:  ldarg.0
                                IL_00a3:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_00a8:  nop
                                IL_00a9:  ldarg.0
                                IL_00aa:  ldnull
                                IL_00ab:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00b0:  ldc.i4.0
                                IL_00b1:  stloc.0
                                IL_00b2:  leave.s    IL_00bc
                              }
                              fault
                              {
                                IL_00b4:  ldarg.0
                                IL_00b5:  call       "void C.<F>d__0.Dispose()"
                                IL_00ba:  nop
                                IL_00bb:  endfinally
                              }
                              IL_00bc:  ldloc.0
                              IL_00bd:  ret
                            }
                            """);
                    })
                .Verify();
        }

        /// <summary>
        /// An iterator suspended in the deleted spread throws when resumed.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_Remove()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            yield return ..xs;
                            <N:0>yield return 2;</N:0>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>yield return 2;</N:0>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x30" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x31" startLine="7" startColumn="14" endLine="7" endColumn="29" document="1" />
                                    <entry offset="0x41" hidden="true" document="1" />
                                    <entry offset="0x48" startLine="8" startColumn="5" endLine="8" endColumn="6" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size       74 (0x4a)
                              .maxstack  2
                              .locals init (int V_0)
                              IL_0000:  ldarg.0
                              IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                              IL_0006:  stloc.0
                              IL_0007:  ldloc.0
                              IL_0008:  brfalse.s  IL_0012
                              IL_000a:  br.s       IL_000c
                              IL_000c:  ldloc.0
                              IL_000d:  ldc.i4.2
                              IL_000e:  beq.s      IL_0014
                              IL_0010:  br.s       IL_0016
                              IL_0012:  br.s       IL_0029
                              IL_0014:  br.s       IL_0041
                              IL_0016:  ldloc.0
                              IL_0017:  ldc.i4.1
                              IL_0018:  blt.s      IL_0027
                              IL_001a:  ldstr      "Edit and Continue can't resume suspended iterator since the corresponding yield return statement has been deleted"
                              IL_001f:  ldc.i4.s   -3
                              IL_0021:  newobj     "System.Runtime.CompilerServices.HotReloadException..ctor(string, int)"
                              IL_0026:  throw
                              IL_0027:  ldc.i4.0
                              IL_0028:  ret
                              IL_0029:  ldarg.0
                              IL_002a:  ldc.i4.m1
                              IL_002b:  stfld      "int C.<F>d__0.<>1__state"
                              IL_0030:  nop
                              IL_0031:  ldarg.0
                              IL_0032:  ldc.i4.2
                              IL_0033:  stfld      "int C.<F>d__0.<>2__current"
                              IL_0038:  ldarg.0
                              IL_0039:  ldc.i4.2
                              IL_003a:  stfld      "int C.<F>d__0.<>1__state"
                              IL_003f:  ldc.i4.1
                              IL_0040:  ret
                              IL_0041:  ldarg.0
                              IL_0042:  ldc.i4.m1
                              IL_0043:  stfld      "int C.<F>d__0.<>1__state"
                              IL_0048:  ldc.i4.0
                              IL_0049:  ret
                            }
                            """);
                    })
                .Verify();
        }

        /// <summary>
        /// Each spread keeps its own states and hoisted variables, also an array spread, which has no finalize state.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_TwoSpreads()
        {
            using var _ = CreateTest()
                .AddBaseline("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs, int[] ys)
                        {
                            <N:0>yield return ..xs;</N:0>
                            <N:1>yield return ..ys;</N:1>
                            <N:2>yield return ..xs;</N:2>
                        }
                    }
                    """,
                    validator: g =>
                    {
                        g.VerifyCustomDebugInformation("C.F", """
                            <symbols>
                              <methods>
                                <method containingType="C" name="F" parameterNames="xs, ys">
                                  <customDebugInfo>
                                    <encLocalSlotMap>
                                      <slot kind="5" offset="16" />
                                      <slot kind="6" offset="55" />
                                      <slot kind="8" offset="55" />
                                      <slot kind="5" offset="94" />
                                    </encLocalSlotMap>
                                    <encStateMachineStateMap>
                                      <state number="1" offset="16" />
                                      <state number="-3" offset="16" />
                                      <state number="2" offset="55" />
                                      <state number="3" offset="94" />
                                      <state number="-4" offset="94" />
                                    </encStateMachineStateMap>
                                  </customDebugInfo>
                                </method>
                              </methods>
                            </symbols>
                            """);
                    })
                .AddGeneration("""
                    using System.Collections.Generic;

                    class C
                    {
                        static IEnumerable<int> F(IEnumerable<int> xs, int[] ys)
                        {
                            yield return 0;
                            <N:0>yield return ..xs;</N:0>
                            <N:1>yield return ..ys;</N:1>
                            <N:2>yield return ..xs;</N:2>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>2__current: int",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "ys: int[]",
                            "<>3__ys: int[]",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>",
                            "<>s__2: int[]",
                            "<>s__3: int",
                            "<>s__4: System.Collections.Generic.IEnumerator<int>");
                        g.VerifyPdb([0x06000001, 0x06000005], """
                            <symbols>
                              <files>
                                <file id="1" name="" language="C#" />
                              </files>
                              <methods>
                                <method token="0x6000005">
                                  <sequencePoints>
                                    <entry offset="0x0" hidden="true" document="1" />
                                    <entry offset="0x41" startLine="6" startColumn="5" endLine="6" endColumn="6" document="1" />
                                    <entry offset="0x42" startLine="7" startColumn="9" endLine="7" endColumn="24" document="1" />
                                    <entry offset="0x57" hidden="true" document="1" />
                                    <entry offset="0x77" hidden="true" document="1" />
                                    <entry offset="0x85" startLine="8" startColumn="14" endLine="8" endColumn="32" document="1" />
                                    <entry offset="0x9a" hidden="true" document="1" />
                                    <entry offset="0xd0" hidden="true" document="1" />
                                    <entry offset="0xe0" startLine="9" startColumn="14" endLine="9" endColumn="32" document="1" />
                                    <entry offset="0xf5" hidden="true" document="1" />
                                    <entry offset="0xfc" hidden="true" document="1" />
                                    <entry offset="0x13a" hidden="true" document="1" />
                                    <entry offset="0x148" startLine="10" startColumn="14" endLine="10" endColumn="32" document="1" />
                                    <entry offset="0x15a" hidden="true" document="1" />
                                    <entry offset="0x17d" startLine="11" startColumn="5" endLine="11" endColumn="6" document="1" />
                                    <entry offset="0x181" hidden="true" document="1" />
                                  </sequencePoints>
                                </method>
                              </methods>
                            </symbols>
                            """);
                        g.VerifyIL("C.<F>d__0.System.Collections.IEnumerator.MoveNext", """
                            {
                              // Code size      395 (0x18b)
                              .maxstack  3
                              .locals init (bool V_0,
                                            int V_1,
                                            int V_2)
                              .try
                              {
                                IL_0000:  ldarg.0
                                IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                                IL_0006:  stloc.1
                                IL_0007:  ldloc.1
                                IL_0008:  switch    (
                                    IL_0023,
                                    IL_0025,
                                    IL_0027,
                                    IL_002c,
                                    IL_0031)
                                IL_0021:  br.s       IL_0033
                                IL_0023:  br.s       IL_003a
                                IL_0025:  br.s       IL_009a
                                IL_0027:  br         IL_00f5
                                IL_002c:  br         IL_015a
                                IL_0031:  br.s       IL_0057
                                IL_0033:  ldc.i4.0
                                IL_0034:  stloc.0
                                IL_0035:  leave      IL_0189
                                IL_003a:  ldarg.0
                                IL_003b:  ldc.i4.m1
                                IL_003c:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0041:  nop
                                IL_0042:  ldarg.0
                                IL_0043:  ldc.i4.0
                                IL_0044:  stfld      "int C.<F>d__0.<>2__current"
                                IL_0049:  ldarg.0
                                IL_004a:  ldc.i4.4
                                IL_004b:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0050:  ldc.i4.1
                                IL_0051:  stloc.0
                                IL_0052:  leave      IL_0189
                                IL_0057:  ldarg.0
                                IL_0058:  ldc.i4.m1
                                IL_0059:  stfld      "int C.<F>d__0.<>1__state"
                                IL_005e:  ldarg.0
                                IL_005f:  ldarg.0
                                IL_0060:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_0065:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_006a:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_006f:  ldarg.0
                                IL_0070:  ldc.i4.s   -3
                                IL_0072:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0077:  br.s       IL_00a2
                                IL_0079:  ldarg.0
                                IL_007a:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_007f:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_0084:  stloc.2
                                IL_0085:  ldarg.0
                                IL_0086:  ldloc.2
                                IL_0087:  stfld      "int C.<F>d__0.<>2__current"
                                IL_008c:  ldarg.0
                                IL_008d:  ldc.i4.1
                                IL_008e:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0093:  ldc.i4.1
                                IL_0094:  stloc.0
                                IL_0095:  leave      IL_0189
                                IL_009a:  ldarg.0
                                IL_009b:  ldc.i4.s   -3
                                IL_009d:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00a2:  ldarg.0
                                IL_00a3:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00a8:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_00ad:  brtrue.s   IL_0079
                                IL_00af:  ldarg.0
                                IL_00b0:  call       "void C.<F>d__0.<>m__Finally1()"
                                IL_00b5:  nop
                                IL_00b6:  ldarg.0
                                IL_00b7:  ldnull
                                IL_00b8:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00bd:  ldarg.0
                                IL_00be:  ldarg.0
                                IL_00bf:  ldfld      "int[] C.<F>d__0.ys"
                                IL_00c4:  stfld      "int[] C.<F>d__0.<>s__2"
                                IL_00c9:  ldarg.0
                                IL_00ca:  ldc.i4.0
                                IL_00cb:  stfld      "int C.<F>d__0.<>s__3"
                                IL_00d0:  br.s       IL_010a
                                IL_00d2:  ldarg.0
                                IL_00d3:  ldfld      "int[] C.<F>d__0.<>s__2"
                                IL_00d8:  ldarg.0
                                IL_00d9:  ldfld      "int C.<F>d__0.<>s__3"
                                IL_00de:  ldelem.i4
                                IL_00df:  stloc.2
                                IL_00e0:  ldarg.0
                                IL_00e1:  ldloc.2
                                IL_00e2:  stfld      "int C.<F>d__0.<>2__current"
                                IL_00e7:  ldarg.0
                                IL_00e8:  ldc.i4.2
                                IL_00e9:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00ee:  ldc.i4.1
                                IL_00ef:  stloc.0
                                IL_00f0:  leave      IL_0189
                                IL_00f5:  ldarg.0
                                IL_00f6:  ldc.i4.m1
                                IL_00f7:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00fc:  ldarg.0
                                IL_00fd:  ldarg.0
                                IL_00fe:  ldfld      "int C.<F>d__0.<>s__3"
                                IL_0103:  ldc.i4.1
                                IL_0104:  add
                                IL_0105:  stfld      "int C.<F>d__0.<>s__3"
                                IL_010a:  ldarg.0
                                IL_010b:  ldfld      "int C.<F>d__0.<>s__3"
                                IL_0110:  ldarg.0
                                IL_0111:  ldfld      "int[] C.<F>d__0.<>s__2"
                                IL_0116:  ldlen
                                IL_0117:  conv.i4
                                IL_0118:  blt.s      IL_00d2
                                IL_011a:  ldarg.0
                                IL_011b:  ldnull
                                IL_011c:  stfld      "int[] C.<F>d__0.<>s__2"
                                IL_0121:  ldarg.0
                                IL_0122:  ldarg.0
                                IL_0123:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.xs"
                                IL_0128:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_012d:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__4"
                                IL_0132:  ldarg.0
                                IL_0133:  ldc.i4.s   -4
                                IL_0135:  stfld      "int C.<F>d__0.<>1__state"
                                IL_013a:  br.s       IL_0162
                                IL_013c:  ldarg.0
                                IL_013d:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__4"
                                IL_0142:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                IL_0147:  stloc.2
                                IL_0148:  ldarg.0
                                IL_0149:  ldloc.2
                                IL_014a:  stfld      "int C.<F>d__0.<>2__current"
                                IL_014f:  ldarg.0
                                IL_0150:  ldc.i4.3
                                IL_0151:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0156:  ldc.i4.1
                                IL_0157:  stloc.0
                                IL_0158:  leave.s    IL_0189
                                IL_015a:  ldarg.0
                                IL_015b:  ldc.i4.s   -4
                                IL_015d:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0162:  ldarg.0
                                IL_0163:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__4"
                                IL_0168:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                IL_016d:  brtrue.s   IL_013c
                                IL_016f:  ldarg.0
                                IL_0170:  call       "void C.<F>d__0.<>m__Finally2()"
                                IL_0175:  nop
                                IL_0176:  ldarg.0
                                IL_0177:  ldnull
                                IL_0178:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__4"
                                IL_017d:  ldc.i4.0
                                IL_017e:  stloc.0
                                IL_017f:  leave.s    IL_0189
                              }
                              fault
                              {
                                IL_0181:  ldarg.0
                                IL_0182:  call       "void C.<F>d__0.Dispose()"
                                IL_0187:  nop
                                IL_0188:  endfinally
                              }
                              IL_0189:  ldloc.0
                              IL_018a:  ret
                            }
                            """);
                    })
                .Verify();
        }

        private EditAndContinueTest CreateAsyncTest() => new EditAndContinueTest(logger, parseOptions: s_sharperParseOptions, targetFramework: TargetFramework.Net90);

        /// <summary>
        /// In an async iterator, a spread has only its yield state: the async rewriter turns the try/finally that disposes
        /// the enumerator into disposal-mode jumps, with no finalize state. Edits around and inside the spread keep that
        /// state and the hoisted enumerator.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_AsyncIterator_EditBeforeSpread()
        {
            using var _ = CreateAsyncTest()
                .AddBaseline("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            <N:1>yield return ..xs;</N:1>
                            <N:2>yield return 3;</N:2>
                        }
                    }
                    """,
                    validator: g =>
                    {
                        g.VerifyCustomDebugInformation("C.F", """
                            <symbols>
                              <methods>
                                <method containingType="C" name="F" parameterNames="xs">
                                  <customDebugInfo>
                                    <encLocalSlotMap>
                                      <slot kind="5" offset="56" />
                                    </encLocalSlotMap>
                                    <encStateMachineStateMap>
                                      <state number="0" offset="16" />
                                      <state number="-4" offset="56" />
                                      <state number="-5" offset="95" />
                                    </encStateMachineStateMap>
                                  </customDebugInfo>
                                </method>
                              </methods>
                            </symbols>
                            """);
                    })
                .AddGeneration("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            System.Console.Write(1);
                            <N:1>yield return ..xs;</N:1>
                            <N:2>yield return 3;</N:2>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>t__builder: System.Runtime.CompilerServices.AsyncIteratorMethodBuilder",
                            "<>v__promiseOfValueOrEnd: System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>",
                            "<>2__current: int",
                            "<>w__disposeMode: bool",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>",
                            "<>u__1: System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter");
                    })
                .Verify();
        }

        /// <summary>
        /// The state and the hoisted enumerator are reused, so a suspended iterator continues the old enumeration.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_AsyncIterator_EditSource()
        {
            using var _ = CreateAsyncTest()
                .AddBaseline("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs, IEnumerable<int> ys)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            <N:1>yield return ..xs;</N:1>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs, IEnumerable<int> ys)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            <N:1>yield return ..ys;</N:1>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>t__builder: System.Runtime.CompilerServices.AsyncIteratorMethodBuilder",
                            "<>v__promiseOfValueOrEnd: System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>",
                            "<>2__current: int",
                            "<>w__disposeMode: bool",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "ys: System.Collections.Generic.IEnumerable<int>",
                            "<>3__ys: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>",
                            "<>u__1: System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter");
                        g.VerifyIL("C.<F>d__0.System.Runtime.CompilerServices.IAsyncStateMachine.MoveNext", """
                            {
                              // Code size      425 (0x1a9)
                              .maxstack  3
                              .locals init (int V_0,
                                            System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter V_1,
                                            System.Runtime.CompilerServices.YieldAwaitable V_2,
                                            C.<F>d__0 V_3,
                                            int V_4,
                                            System.Exception V_5)
                              IL_0000:  ldarg.0
                              IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                              IL_0006:  stloc.0
                              .try
                              {
                                IL_0007:  ldloc.0
                                IL_0008:  ldc.i4.s   -4
                                IL_000a:  sub
                                IL_000b:  switch    (
                                    IL_0026,
                                    IL_002b,
                                    IL_002f,
                                    IL_002f,
                                    IL_002d)
                                IL_0024:  br.s       IL_002f
                                IL_0026:  br         IL_00b9
                                IL_002b:  br.s       IL_002f
                                IL_002d:  br.s       IL_0084
                                IL_002f:  ldarg.0
                                IL_0030:  ldfld      "bool C.<F>d__0.<>w__disposeMode"
                                IL_0035:  brfalse.s  IL_003c
                                IL_0037:  leave      IL_016b
                                IL_003c:  ldarg.0
                                IL_003d:  ldc.i4.m1
                                IL_003e:  dup
                                IL_003f:  stloc.0
                                IL_0040:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0045:  nop
                                IL_0046:  call       "System.Runtime.CompilerServices.YieldAwaitable System.Threading.Tasks.Task.Yield()"
                                IL_004b:  stloc.2
                                IL_004c:  ldloca.s   V_2
                                IL_004e:  call       "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter System.Runtime.CompilerServices.YieldAwaitable.GetAwaiter()"
                                IL_0053:  stloc.1
                                IL_0054:  ldloca.s   V_1
                                IL_0056:  call       "bool System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter.IsCompleted.get"
                                IL_005b:  brtrue.s   IL_00a0
                                IL_005d:  ldarg.0
                                IL_005e:  ldc.i4.0
                                IL_005f:  dup
                                IL_0060:  stloc.0
                                IL_0061:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0066:  ldarg.0
                                IL_0067:  ldloc.1
                                IL_0068:  stfld      "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter C.<F>d__0.<>u__1"
                                IL_006d:  ldarg.0
                                IL_006e:  stloc.3
                                IL_006f:  ldarg.0
                                IL_0070:  ldflda     "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder C.<F>d__0.<>t__builder"
                                IL_0075:  ldloca.s   V_1
                                IL_0077:  ldloca.s   V_3
                                IL_0079:  call       "void System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter, C.<F>d__0>(ref System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter, ref C.<F>d__0)"
                                IL_007e:  nop
                                IL_007f:  leave      IL_01a8
                                IL_0084:  ldarg.0
                                IL_0085:  ldfld      "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter C.<F>d__0.<>u__1"
                                IL_008a:  stloc.1
                                IL_008b:  ldarg.0
                                IL_008c:  ldflda     "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter C.<F>d__0.<>u__1"
                                IL_0091:  initobj    "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter"
                                IL_0097:  ldarg.0
                                IL_0098:  ldc.i4.m1
                                IL_0099:  dup
                                IL_009a:  stloc.0
                                IL_009b:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00a0:  ldloca.s   V_1
                                IL_00a2:  call       "void System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter.GetResult()"
                                IL_00a7:  nop
                                IL_00a8:  ldarg.0
                                IL_00a9:  ldarg.0
                                IL_00aa:  ldfld      "System.Collections.Generic.IEnumerable<int> C.<F>d__0.ys"
                                IL_00af:  callvirt   "System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator()"
                                IL_00b4:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_00b9:  nop
                                .try
                                {
                                  IL_00ba:  ldloc.0
                                  IL_00bb:  ldc.i4.s   -4
                                  IL_00bd:  beq.s      IL_00c1
                                  IL_00bf:  br.s       IL_00c3
                                  IL_00c1:  br.s       IL_00e9
                                  IL_00c3:  br.s       IL_00fc
                                  IL_00c5:  ldarg.0
                                  IL_00c6:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                  IL_00cb:  callvirt   "int System.Collections.Generic.IEnumerator<int>.Current.get"
                                  IL_00d0:  stloc.s    V_4
                                  IL_00d2:  ldarg.0
                                  IL_00d3:  ldloc.s    V_4
                                  IL_00d5:  stfld      "int C.<F>d__0.<>2__current"
                                  IL_00da:  ldarg.0
                                  IL_00db:  ldc.i4.s   -4
                                  IL_00dd:  dup
                                  IL_00de:  stloc.0
                                  IL_00df:  stfld      "int C.<F>d__0.<>1__state"
                                  IL_00e4:  leave      IL_019b
                                  IL_00e9:  ldarg.0
                                  IL_00ea:  ldc.i4.m1
                                  IL_00eb:  dup
                                  IL_00ec:  stloc.0
                                  IL_00ed:  stfld      "int C.<F>d__0.<>1__state"
                                  IL_00f2:  ldarg.0
                                  IL_00f3:  ldfld      "bool C.<F>d__0.<>w__disposeMode"
                                  IL_00f8:  brfalse.s  IL_00fc
                                  IL_00fa:  leave.s    IL_0124
                                  IL_00fc:  ldarg.0
                                  IL_00fd:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                  IL_0102:  callvirt   "bool System.Collections.IEnumerator.MoveNext()"
                                  IL_0107:  brtrue.s   IL_00c5
                                  IL_0109:  leave.s    IL_0124
                                }
                                finally
                                {
                                  IL_010b:  ldloc.0
                                  IL_010c:  ldc.i4.m1
                                  IL_010d:  bne.un.s   IL_0123
                                  IL_010f:  ldarg.0
                                  IL_0110:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                  IL_0115:  brfalse.s  IL_0123
                                  IL_0117:  ldarg.0
                                  IL_0118:  ldfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                  IL_011d:  callvirt   "void System.IDisposable.Dispose()"
                                  IL_0122:  nop
                                  IL_0123:  endfinally
                                }
                                IL_0124:  ldarg.0
                                IL_0125:  ldfld      "bool C.<F>d__0.<>w__disposeMode"
                                IL_012a:  brfalse.s  IL_012e
                                IL_012c:  leave.s    IL_016b
                                IL_012e:  ldarg.0
                                IL_012f:  ldnull
                                IL_0130:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0135:  leave.s    IL_016b
                              }
                              catch System.Exception
                              {
                                IL_0137:  stloc.s    V_5
                                IL_0139:  ldarg.0
                                IL_013a:  ldc.i4.s   -2
                                IL_013c:  stfld      "int C.<F>d__0.<>1__state"
                                IL_0141:  ldarg.0
                                IL_0142:  ldnull
                                IL_0143:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                                IL_0148:  ldarg.0
                                IL_0149:  ldc.i4.0
                                IL_014a:  stfld      "int C.<F>d__0.<>2__current"
                                IL_014f:  ldarg.0
                                IL_0150:  ldflda     "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder C.<F>d__0.<>t__builder"
                                IL_0155:  call       "void System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.Complete()"
                                IL_015a:  nop
                                IL_015b:  ldarg.0
                                IL_015c:  ldflda     "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> C.<F>d__0.<>v__promiseOfValueOrEnd"
                                IL_0161:  ldloc.s    V_5
                                IL_0163:  call       "void System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>.SetException(System.Exception)"
                                IL_0168:  nop
                                IL_0169:  leave.s    IL_01a8
                              }
                              IL_016b:  ldarg.0
                              IL_016c:  ldc.i4.s   -2
                              IL_016e:  stfld      "int C.<F>d__0.<>1__state"
                              IL_0173:  ldarg.0
                              IL_0174:  ldnull
                              IL_0175:  stfld      "System.Collections.Generic.IEnumerator<int> C.<F>d__0.<>s__1"
                              IL_017a:  ldarg.0
                              IL_017b:  ldc.i4.0
                              IL_017c:  stfld      "int C.<F>d__0.<>2__current"
                              IL_0181:  ldarg.0
                              IL_0182:  ldflda     "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder C.<F>d__0.<>t__builder"
                              IL_0187:  call       "void System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.Complete()"
                              IL_018c:  nop
                              IL_018d:  ldarg.0
                              IL_018e:  ldflda     "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> C.<F>d__0.<>v__promiseOfValueOrEnd"
                              IL_0193:  ldc.i4.0
                              IL_0194:  call       "void System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>.SetResult(bool)"
                              IL_0199:  nop
                              IL_019a:  ret
                              IL_019b:  ldarg.0
                              IL_019c:  ldflda     "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> C.<F>d__0.<>v__promiseOfValueOrEnd"
                              IL_01a1:  ldc.i4.1
                              IL_01a2:  call       "void System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>.SetResult(bool)"
                              IL_01a7:  nop
                              IL_01a8:  ret
                            }
                            """);
                    })
                .Verify();
        }

        [Fact]
        public void YieldReturnSpread_AsyncIterator_Add()
        {
            using var _ = CreateAsyncTest()
                .AddBaseline("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            <N:1>yield return 1;</N:1>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            <N:1>yield return 1;</N:1>
                            yield return ..xs;
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>t__builder: System.Runtime.CompilerServices.AsyncIteratorMethodBuilder",
                            "<>v__promiseOfValueOrEnd: System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>",
                            "<>2__current: int",
                            "<>w__disposeMode: bool",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>s__1: System.Collections.Generic.IEnumerator<int>",
                            "<>u__1: System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter");
                    })
                .Verify();
        }

        /// <summary>
        /// An iterator suspended inside a removed spread throws when resumed, as for a removed yield return.
        /// </summary>
        [Fact]
        public void YieldReturnSpread_AsyncIterator_Remove()
        {
            using var _ = CreateAsyncTest()
                .AddBaseline("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            yield return ..xs;
                            <N:1>yield return 2;</N:1>
                        }
                    }
                    """)
                .AddGeneration("""
                    using System.Collections.Generic;
                    using System.Threading.Tasks;

                    class C
                    {
                        static async IAsyncEnumerable<int> F(IEnumerable<int> xs)
                        {
                            <N:0>await Task.Yield()</N:0>;
                            <N:1>yield return 2;</N:1>
                        }
                    }
                    """,
                    edits: [Edit(SemanticEditKind.Update, c => c.GetMember("C.F"), preserveLocalVariables: true)],
                    validator: g =>
                    {
                        g.VerifySynthesizedFields("C.<F>d__0",
                            "<>1__state: int",
                            "<>t__builder: System.Runtime.CompilerServices.AsyncIteratorMethodBuilder",
                            "<>v__promiseOfValueOrEnd: System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>",
                            "<>2__current: int",
                            "<>w__disposeMode: bool",
                            "<>l__initialThreadId: int",
                            "xs: System.Collections.Generic.IEnumerable<int>",
                            "<>3__xs: System.Collections.Generic.IEnumerable<int>",
                            "<>u__1: System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter");
                        g.VerifyIL("C.<F>d__0.System.Runtime.CompilerServices.IAsyncStateMachine.MoveNext", """
                            {
                              // Code size      314 (0x13a)
                              .maxstack  3
                              .locals init (int V_0,
                                            System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter V_1,
                                            System.Runtime.CompilerServices.YieldAwaitable V_2,
                                            C.<F>d__0 V_3,
                                            System.Exception V_4)
                              IL_0000:  ldarg.0
                              IL_0001:  ldfld      "int C.<F>d__0.<>1__state"
                              IL_0006:  stloc.0
                              .try
                              {
                                IL_0007:  ldloc.0
                                IL_0008:  ldc.i4.s   -5
                                IL_000a:  beq.s      IL_001a
                                IL_000c:  br.s       IL_000e
                                IL_000e:  ldloc.0
                                IL_000f:  ldc.i4.s   -3
                                IL_0011:  beq.s      IL_001f
                                IL_0013:  br.s       IL_0015
                                IL_0015:  ldloc.0
                                IL_0016:  brfalse.s  IL_0021
                                IL_0018:  br.s       IL_0023
                                IL_001a:  br         IL_00c1
                                IL_001f:  br.s       IL_0035
                                IL_0021:  br.s       IL_008a
                                IL_0023:  ldloc.0
                                IL_0024:  ldc.i4.s   -4
                                IL_0026:  bgt.s      IL_0035
                                IL_0028:  ldstr      "Edit and Continue can't resume suspended iterator since the corresponding yield return statement has been deleted"
                                IL_002d:  ldc.i4.s   -3
                                IL_002f:  newobj     "System.Runtime.CompilerServices.HotReloadException..ctor(string, int)"
                                IL_0034:  throw
                                IL_0035:  ldarg.0
                                IL_0036:  ldfld      "bool C.<F>d__0.<>w__disposeMode"
                                IL_003b:  brfalse.s  IL_0042
                                IL_003d:  leave      IL_0103
                                IL_0042:  ldarg.0
                                IL_0043:  ldc.i4.m1
                                IL_0044:  dup
                                IL_0045:  stloc.0
                                IL_0046:  stfld      "int C.<F>d__0.<>1__state"
                                IL_004b:  nop
                                IL_004c:  call       "System.Runtime.CompilerServices.YieldAwaitable System.Threading.Tasks.Task.Yield()"
                                IL_0051:  stloc.2
                                IL_0052:  ldloca.s   V_2
                                IL_0054:  call       "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter System.Runtime.CompilerServices.YieldAwaitable.GetAwaiter()"
                                IL_0059:  stloc.1
                                IL_005a:  ldloca.s   V_1
                                IL_005c:  call       "bool System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter.IsCompleted.get"
                                IL_0061:  brtrue.s   IL_00a6
                                IL_0063:  ldarg.0
                                IL_0064:  ldc.i4.0
                                IL_0065:  dup
                                IL_0066:  stloc.0
                                IL_0067:  stfld      "int C.<F>d__0.<>1__state"
                                IL_006c:  ldarg.0
                                IL_006d:  ldloc.1
                                IL_006e:  stfld      "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter C.<F>d__0.<>u__1"
                                IL_0073:  ldarg.0
                                IL_0074:  stloc.3
                                IL_0075:  ldarg.0
                                IL_0076:  ldflda     "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder C.<F>d__0.<>t__builder"
                                IL_007b:  ldloca.s   V_1
                                IL_007d:  ldloca.s   V_3
                                IL_007f:  call       "void System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.AwaitUnsafeOnCompleted<System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter, C.<F>d__0>(ref System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter, ref C.<F>d__0)"
                                IL_0084:  nop
                                IL_0085:  leave      IL_0139
                                IL_008a:  ldarg.0
                                IL_008b:  ldfld      "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter C.<F>d__0.<>u__1"
                                IL_0090:  stloc.1
                                IL_0091:  ldarg.0
                                IL_0092:  ldflda     "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter C.<F>d__0.<>u__1"
                                IL_0097:  initobj    "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter"
                                IL_009d:  ldarg.0
                                IL_009e:  ldc.i4.m1
                                IL_009f:  dup
                                IL_00a0:  stloc.0
                                IL_00a1:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00a6:  ldloca.s   V_1
                                IL_00a8:  call       "void System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter.GetResult()"
                                IL_00ad:  nop
                                IL_00ae:  ldarg.0
                                IL_00af:  ldc.i4.2
                                IL_00b0:  stfld      "int C.<F>d__0.<>2__current"
                                IL_00b5:  ldarg.0
                                IL_00b6:  ldc.i4.s   -5
                                IL_00b8:  dup
                                IL_00b9:  stloc.0
                                IL_00ba:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00bf:  leave.s    IL_012c
                                IL_00c1:  ldarg.0
                                IL_00c2:  ldc.i4.m1
                                IL_00c3:  dup
                                IL_00c4:  stloc.0
                                IL_00c5:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00ca:  ldarg.0
                                IL_00cb:  ldfld      "bool C.<F>d__0.<>w__disposeMode"
                                IL_00d0:  brfalse.s  IL_00d4
                                IL_00d2:  leave.s    IL_0103
                                IL_00d4:  leave.s    IL_0103
                              }
                              catch System.Exception
                              {
                                IL_00d6:  stloc.s    V_4
                                IL_00d8:  ldarg.0
                                IL_00d9:  ldc.i4.s   -2
                                IL_00db:  stfld      "int C.<F>d__0.<>1__state"
                                IL_00e0:  ldarg.0
                                IL_00e1:  ldc.i4.0
                                IL_00e2:  stfld      "int C.<F>d__0.<>2__current"
                                IL_00e7:  ldarg.0
                                IL_00e8:  ldflda     "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder C.<F>d__0.<>t__builder"
                                IL_00ed:  call       "void System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.Complete()"
                                IL_00f2:  nop
                                IL_00f3:  ldarg.0
                                IL_00f4:  ldflda     "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> C.<F>d__0.<>v__promiseOfValueOrEnd"
                                IL_00f9:  ldloc.s    V_4
                                IL_00fb:  call       "void System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>.SetException(System.Exception)"
                                IL_0100:  nop
                                IL_0101:  leave.s    IL_0139
                              }
                              IL_0103:  ldarg.0
                              IL_0104:  ldc.i4.s   -2
                              IL_0106:  stfld      "int C.<F>d__0.<>1__state"
                              IL_010b:  ldarg.0
                              IL_010c:  ldc.i4.0
                              IL_010d:  stfld      "int C.<F>d__0.<>2__current"
                              IL_0112:  ldarg.0
                              IL_0113:  ldflda     "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder C.<F>d__0.<>t__builder"
                              IL_0118:  call       "void System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.Complete()"
                              IL_011d:  nop
                              IL_011e:  ldarg.0
                              IL_011f:  ldflda     "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> C.<F>d__0.<>v__promiseOfValueOrEnd"
                              IL_0124:  ldc.i4.0
                              IL_0125:  call       "void System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>.SetResult(bool)"
                              IL_012a:  nop
                              IL_012b:  ret
                              IL_012c:  ldarg.0
                              IL_012d:  ldflda     "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> C.<F>d__0.<>v__promiseOfValueOrEnd"
                              IL_0132:  ldc.i4.1
                              IL_0133:  call       "void System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool>.SetResult(bool)"
                              IL_0138:  nop
                              IL_0139:  ret
                            }
                            """);
                    })
                .Verify();
        }
    }
}
