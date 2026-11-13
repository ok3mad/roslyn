// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper: the call of an extractor pattern (Feature 13, slice 3). Its outputs are the return value (index -1) and
    /// the values of its <c>out</c> arguments (indices 0 to n-1, in the order of the arguments).
    /// </summary>
    internal sealed partial class BoundDagExtractorEvaluation
    {
        public const int ReturnValueIndex = -1;

        public override BoundDagTemp MakeResultTemp()
        {
            Debug.Assert(Call.Type is not null);
            return new BoundDagTemp(Syntax, Call.Type, this, ReturnValueIndex);
        }

        public ArrayBuilder<BoundDagTemp> MakeOutParameterTemps()
        {
            var builder = ArrayBuilder<BoundDagTemp>.GetInstance();
            if (!Call.ArgumentRefKindsOpt.IsDefaultOrEmpty)
            {
                for (int i = 0; i < Call.Arguments.Length; i++)
                {
                    if (Call.ArgumentRefKindsOpt[i] == RefKind.Out)
                    {
                        Debug.Assert(Call.Arguments[i].Type is not null);
                        builder.Add(new BoundDagTemp(Syntax, Call.Arguments[i].Type!, this, builder.Count));
                    }
                }
            }

            return builder;
        }

        /// <summary>The parameter whose argument gives the output with the given index, or null for the return value.</summary>
        public ParameterSymbol? GetOutParameter(int index)
        {
            if (index == ReturnValueIndex)
            {
                return null;
            }

            int count = 0;
            for (int i = 0; i < Call.Arguments.Length; i++)
            {
                if (Call.ArgumentRefKindsOpt[i] == RefKind.Out && count++ == index)
                {
                    int parameterIndex = Call.ArgsToParamsOpt.IsDefault ? i : Call.ArgsToParamsOpt[i];
                    return Call.Method.Parameters[parameterIndex];
                }
            }

            throw ExceptionUtilities.UnexpectedValue(index);
        }

        public override OneOrMany<BoundDagTemp> AllOutputs()
        {
            var builder = MakeOutParameterTemps();
            builder.Add(MakeResultTemp());
            if (builder is [var one])
            {
                builder.Free();
                return new OneOrMany<BoundDagTemp>(one);
            }

            return new OneOrMany<BoundDagTemp>(builder.ToImmutableAndFree());
        }

        /// <summary>Two extractors make the same call, written the same way, with the input as the argument.</summary>
        public override bool IsEquivalentTo(BoundDagEvaluation other)
            => this == other ||
                other is BoundDagExtractorEvaluation e &&
                BoundDagOpaqueTest.IsSameValue(Call, e.Call, InputPlaceholder, e.InputPlaceholder);

        public override BoundDagEvaluation UpdateEvaluationImpl(BoundDagTemp input) => Update(input);
        public new BoundDagExtractorEvaluation Update(BoundDagTemp input)
        {
            return Update(Call, InputPlaceholder, input);
        }
    }
}
