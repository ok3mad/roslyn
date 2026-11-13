// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>What a value's refinement says about a parameter's refinement (Feature 11, step 4).</summary>
    internal enum SharperRefinementProof
    {
        /// <summary>Every value the value can have satisfies the parameter's refinement.</summary>
        Proven,

        /// <summary>No value the value can have satisfies it.</summary>
        Disproven,

        /// <summary>Some values do and some do not.</summary>
        NotProven,
    }

    internal sealed partial class SharperRefinement
    {
        /// <summary>
        /// Compares the values this refinement allows, a value's, with those <paramref name="target"/> allows, a
        /// parameter's: proven if <c>A ∩ ¬B</c> is empty, disproven if <c>A ∩ B</c> is empty, with null counted on its
        /// own (<see cref="MatchesNull"/>).
        /// </summary>
        internal SharperRefinementProof Prove(SharperRefinement target)
        {
            Debug.Assert(!HasErrors && !target.HasErrors);
            if (Outside(target).IsEmpty && !(MatchesNull && !target.MatchesNull))
            {
                return SharperRefinementProof.Proven;
            }

            return ((IConstantValueSet)Values!.Intersect(target.Values!)).IsEmpty && !(MatchesNull && target.MatchesNull)
                ? SharperRefinementProof.Disproven
                : SharperRefinementProof.NotProven;
        }

        /// <summary>
        /// A value this refinement allows and <paramref name="target"/> does not, as a literal of the type, for a
        /// diagnostic: <c>null</c> if that is the only one.
        /// </summary>
        internal string SampleOutside(SharperRefinement target)
        {
            var outside = Outside(target);
            return !outside.IsEmpty && outside.Sample is { } sample
                ? FormatConstant(sample, Pattern.InputType)
                : "null";
        }

        private IConstantValueSet Outside(SharperRefinement target)
            => (IConstantValueSet)Values!.Intersect(target.Values!.Complement());
    }
}
