// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal static partial class ValueSetFactory
    {
        /// <summary>
        /// Sharper: the parts of <paramref name="set"/>, so that it can be written as a pattern (refined types, slice 2):
        /// ranges of values, where a null bound reaches the end of the type and equal bounds are one value. Returns false
        /// for sets that cannot be read this way (<c>nint</c>, <c>nuint</c>, type unions). <paramref name="excluded"/> is
        /// true when the ranges are the values the set excludes (a set of strings that excludes a few).
        /// </summary>
        internal static bool TryGetSharperRanges(IValueSet set, ArrayBuilder<(ConstantValue? Low, ConstantValue? High)> ranges, out bool excluded)
        {
            excluded = false;
            switch (set)
            {
                case BoolValueSet boolSet:
                    boolSet.AddSharperRanges(ranges);
                    return true;
                case EnumeratedValueSet<string> strings:
                    excluded = !strings.AddSharperMembers(ranges);
                    return true;
                case FloatingValueSet<float> floats:
                    floats.AddSharperRanges(ranges);
                    return true;
                case FloatingValueSet<double> doubles:
                    doubles.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<sbyte> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<byte> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<short> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<ushort> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<int> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<uint> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<long> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<ulong> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<char> s:
                    s.AddSharperRanges(ranges);
                    return true;
                case NumericValueSet<decimal> s:
                    s.AddSharperRanges(ranges);
                    return true;
                default:
                    return false;
            }
        }
    }
}
