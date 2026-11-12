// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Collections;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed class CSDiagnosticInfo : DiagnosticInfoWithSymbols
    {
        public static readonly DiagnosticInfo EmptyErrorInfo = new CSDiagnosticInfo(0);
        public static readonly DiagnosticInfo VoidDiagnosticInfo = new CSDiagnosticInfo(ErrorCode.Void);

        private readonly IReadOnlyList<Location> _additionalLocations;
        private readonly ImmutableDictionary<string, string> _properties;

        internal CSDiagnosticInfo(ErrorCode code)
            : this(code, Array.Empty<object>(), ImmutableArray<Symbol>.Empty, ImmutableArray<Location>.Empty)
        {
        }

        internal CSDiagnosticInfo(ErrorCode code, params object[] args)
            : this(code, args, ImmutableArray<Symbol>.Empty, ImmutableArray<Location>.Empty)
        {
        }

        internal CSDiagnosticInfo(ErrorCode code, ImmutableArray<Symbol> symbols, object[] args)
            : this(code, args, symbols, ImmutableArray<Location>.Empty)
        {
        }

        internal CSDiagnosticInfo(ErrorCode code, object[] args, ImmutableArray<Symbol> symbols, ImmutableArray<Location> additionalLocations, ImmutableDictionary<string, string> properties = null)
            : base(code, args, symbols)
        {
            _properties = properties ?? ImmutableDictionary<string, string>.Empty;
            // Internal errors are abnormal and should not occur except where there are bugs in the compiler.
            Debug.Assert(code != ErrorCode.ERR_InternalError);
            _additionalLocations = additionalLocations.IsDefaultOrEmpty ? SpecializedCollections.EmptyReadOnlyList<Location>() : additionalLocations;
        }

        private CSDiagnosticInfo(CSDiagnosticInfo original, DiagnosticSeverity severity) : base(original, severity)
        {
            _additionalLocations = original._additionalLocations;
            _properties = original._properties;
        }

        protected override DiagnosticInfo GetInstanceWithSeverityCore(DiagnosticSeverity severity)
        {
            return new CSDiagnosticInfo(this, severity);
        }

        public override IReadOnlyList<Location> AdditionalLocations => _additionalLocations;

        /// <summary>Sharper: the properties a fixer reads, such as the types CS20027 lists.</summary>
        internal override ImmutableDictionary<string, string> Properties => _properties;

        internal new ErrorCode Code => (ErrorCode)base.Code;

        internal static bool IsEmpty(DiagnosticInfo info) => (object)info == EmptyErrorInfo;
    }
}
