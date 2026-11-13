// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Sharper: a function type that no <c>System.Func</c> or <c>System.Action</c> can express, such as
    /// <c>(ref int) -> void</c>, is the anonymous delegate of a lambda's natural type, which is internal to its
    /// compilation (https://github.com/ok3mad/roslyn/issues/56). It is allowed wherever an internal type is, so the
    /// boundary is the inconsistent accessibility errors' (CS0050 to CS0061, CS0703), and so is that of an <c>infer</c>
    /// member whose inferred type is such a delegate. Where one of those errors would name an anonymous delegate,
    /// CS20075 says to declare a named delegate instead.
    /// </summary>
    internal static class SharperFunctionTypeVisibility
    {
        /// <summary>
        /// Reports the inconsistent accessibility error <paramref name="code"/> for <paramref name="type"/>, which is
        /// less visible than <paramref name="member"/>, or CS20075 when the type that makes it less visible is an
        /// anonymous delegate and the member is declared in Sharper.
        /// </summary>
        internal static void AddInconsistentAccessibility(this BindingDiagnosticBag diagnostics, ErrorCode code, Location location, Symbol member, TypeSymbol type)
        {
            if (location.SourceTree?.Options is CSharpParseOptions options &&
                options.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
            {
                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                if (type.FindTypeLessVisibleThan(member, ref useSiteInfo) is NamedTypeSymbol { IsAnonymousType: true, TypeKind: TypeKind.Delegate } anonymousDelegate)
                {
                    // '{0}' is visible outside this assembly, but uses the function type '{1}', ... Declare a named delegate instead.
                    diagnostics.Add(ErrorCode.ERR_SharperFunctionTypeNotVisible, location, member, FunctionTypeDisplay(anonymousDelegate));
                    return;
                }
            }

            diagnostics.Add(code, location, member, type);
        }

        /// <summary>
        /// The anonymous delegate as the function type that stands for it, <c>(ref int, out string) -> bool</c>, since
        /// the symbol display of an anonymous delegate is <c>&lt;anonymous delegate&gt;</c>.
        /// </summary>
        private static string FunctionTypeDisplay(NamedTypeSymbol anonymousDelegate)
        {
            var invoke = anonymousDelegate.DelegateInvokeMethod;
            if (invoke is null)
            {
                return anonymousDelegate.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            }

            var builder = PooledStringBuilder.GetInstance();
            var text = builder.Builder;
            text.Append('(');
            for (int i = 0; i < invoke.ParameterCount; i++)
            {
                var parameter = invoke.Parameters[i];
                if (i > 0)
                {
                    text.Append(", ");
                }

                if (parameter.EffectiveScope != ScopedKind.None && !ParameterHelpers.IsRefScopedByDefault(parameter))
                {
                    text.Append("scoped ");
                }

                text.Append(parameter.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    RefKind.RefReadOnlyParameter => "ref readonly ",
                    _ => "",
                });
                text.Append(parameter.TypeWithAnnotations.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            }

            text.Append(") -> ");
            text.Append(invoke.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.RefReadOnly => "ref readonly ",
                _ => "",
            });
            text.Append(invoke.ReturnTypeWithAnnotations.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            return builder.ToStringAndFree();
        }
    }
}
