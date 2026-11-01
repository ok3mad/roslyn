// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    internal static class SyntaxBindingUtilities
    {
        public static bool BindsToResumableStateMachineState(SyntaxNode node)
            => node.IsKind(SyntaxKind.YieldReturnStatement) ||
               node.IsKind(SyntaxKind.AwaitExpression) ||
               node is CommonForEachStatementSyntax { AwaitKeyword.RawKind: not 0 }
                    or VariableDeclaratorSyntax { Parent.Parent: UsingStatementSyntax { AwaitKeyword.RawKind: not 0 } or LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } }
                    or UsingStatementSyntax { Expression: not null, AwaitKeyword.RawKind: not 0 };

        public static bool BindsToTryStatement(SyntaxNode node)
            => node is VariableDeclaratorSyntax { Parent.Parent: UsingStatementSyntax { } or LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } }
                    or UsingStatementSyntax { Expression: not null }
                    or CommonForEachStatementSyntax
                    or TryStatementSyntax
                    or LockStatementSyntax
                    // Sharper: a spread `yield return ..xs` lowers to a foreach whose try/finally has the yield's syntax.
                    or YieldStatementSyntax { RawKind: (int)SyntaxKind.YieldReturnStatement, Expression: RangeExpressionSyntax { LeftOperand: null, RightOperand: not null } }
                    // Sharper: a try expression lowers to a try statement with its syntax.
                    or TryExpressionSyntax;
    }
}
