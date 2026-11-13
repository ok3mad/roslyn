// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Sharper lambda to single-method interface (#29): a lambda or method group converts to an interface with exactly one
    /// abstract member, a non-generic instance method, where C# reports an error. The function is converted to a delegate
    /// for that method (<c>Func</c>, <c>Action</c> or a synthesized delegate), and lowering wraps the delegate in a
    /// generated class that implements the interface.
    /// <para>
    /// Valid C# keeps its meaning (design rule 2): C# binds first, and the conversion is only classified in a retry after
    /// C# found no conversion (assignments, returns, initializers) or no applicable candidate (overload resolution), with
    /// <see cref="Conversions.WithSharperFunctionToInterface"/>. So a delegate overload still wins, and so does an
    /// interface that every delegate implements (<c>ICloneable</c>), which a lambda with a natural type converts to.
    /// </para>
    /// </summary>
    internal partial class Binder
    {
        /// <summary>
        /// The one abstract member of an interface, if it is a non-generic instance method: the interface's own members and
        /// those of its base interfaces count, except a base member that an interface in between implements with a default.
        /// Any abstract property, event, indexer or static member makes the interface ineligible.
        /// </summary>
        internal static MethodSymbol? GetSharperSingleAbstractMethod(NamedTypeSymbol interfaceType)
        {
            if (!interfaceType.IsInterfaceType() || interfaceType.IsErrorType())
            {
                return null;
            }

            MethodSymbol? result = null;
            if (!addAbstractMembers(interfaceType, interfaceType, ref result))
            {
                return null;
            }

            foreach (var baseInterface in interfaceType.AllInterfacesNoUseSiteDiagnostics)
            {
                if (!addAbstractMembers(interfaceType, baseInterface, ref result))
                {
                    return null;
                }
            }

            return result;

            // Returns false when the interface is ineligible.
            static bool addAbstractMembers(NamedTypeSymbol interfaceType, NamedTypeSymbol declaringInterface, ref MethodSymbol? result)
            {
                foreach (var member in declaringInterface.GetMembersUnordered())
                {
                    if (member.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event) || !member.IsAbstract)
                    {
                        continue;
                    }

                    if (member.IsStatic)
                    {
                        // A static abstract member cannot be implemented by a lambda.
                        return false;
                    }

                    if (member is MethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation } ||
                        member is MethodSymbol { AssociatedSymbol: not null })
                    {
                        // A reabstraction, or a property or event accessor: the base member, or the property or event,
                        // decides.
                        continue;
                    }

                    if (!ReferenceEquals(declaringInterface, interfaceType) &&
                        interfaceType.FindImplementationForInterfaceMember(member) is { IsAbstract: false })
                    {
                        // An interface in between implements it with a default.
                        continue;
                    }

                    if (member is not MethodSymbol { MethodKind: MethodKind.Ordinary, IsGenericMethod: false, IsVararg: false } method ||
                        result is not null)
                    {
                        return false;
                    }

                    result = method;
                }

                return true;
            }
        }

        /// <summary>
        /// Whether the type is an interface a lambda or method group converts to, and if so, its method and the delegate
        /// type the function converts to first. The delegate type is computed from the interface's definition, so that the
        /// generated class can store it over the definition's type parameters (<paramref name="delegateTypeOverDefinition"/>),
        /// and then substituted with the type's arguments.
        /// </summary>
        internal bool TryGetSharperFunctionToInterface(
            TypeSymbol destination,
            SyntaxNode syntax,
            [NotNullWhen(true)] out MethodSymbol? interfaceMethod,
            [NotNullWhen(true)] out NamedTypeSymbol? delegateType,
            [NotNullWhen(true)] out NamedTypeSymbol? delegateTypeOverDefinition)
        {
            interfaceMethod = null;
            delegateType = null;
            delegateTypeOverDefinition = null;

            if (destination is not NamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceType ||
                interfaceType.IsErrorType() ||
                IsInterfaceOfEveryDelegate(interfaceType))
            {
                return false;
            }

            var definition = interfaceType.OriginalDefinition;
            var methodOverDefinition = GetSharperSingleAbstractMethod(definition);
            if (methodOverDefinition is null)
            {
                return false;
            }

            delegateTypeOverDefinition = GetMethodGroupOrLambdaDelegateType(syntax, methodOverDefinition, hasParams: false);
            if (delegateTypeOverDefinition is null)
            {
                return false;
            }

            if (ReferenceEquals(definition, interfaceType))
            {
                interfaceMethod = methodOverDefinition;
                delegateType = delegateTypeOverDefinition;
                return true;
            }

            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            var typeMap = new TypeMap(definition.GetAllTypeParameters(), interfaceType.GetAllTypeArguments(ref discardedUseSiteInfo));
            var declaringInterface = typeMap.SubstituteNamedType(methodOverDefinition.ContainingType);
            interfaceMethod = methodOverDefinition.OriginalDefinition.AsMember(declaringInterface);
            delegateType = typeMap.SubstituteNamedType(delegateTypeOverDefinition);
            return true;
        }

        /// <summary>
        /// <c>ICloneable</c> and <c>ISerializable</c>: a lambda with a natural type already converts to them in C#, and
        /// the result is the delegate itself.
        /// </summary>
        private bool IsInterfaceOfEveryDelegate(NamedTypeSymbol interfaceType)
        {
            var multicastDelegate = Compilation.GetSpecialType(SpecialType.System_MulticastDelegate);
            foreach (var implemented in multicastDelegate.AllInterfacesNoUseSiteDiagnostics)
            {
                if (implemented.Equals(interfaceType, TypeCompareKind.AllIgnoreOptions))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The conversion of a lambda or method group to a single-method interface, classified only in the retry after a C#
        /// error (<see cref="Conversions.WithSharperFunctionToInterface"/>): the function must convert to the delegate for
        /// the interface's method.
        /// </summary>
        internal Conversion ClassifySharperFunctionToInterface(BoundExpression source, TypeSymbol destination, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (source.Kind is not (BoundKind.UnboundLambda or BoundKind.MethodGroup) ||
                !source.Syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper) ||
                !TryGetSharperFunctionToInterface(destination, source.Syntax, out _, out var delegateType, out _))
            {
                return Conversion.NoConversion;
            }

            if (source is UnboundLambda lambda)
            {
                // A lambda whose signature matches converts even when its body has errors, unlike a lambda to a delegate
                // in C# (BindingFailed). Creating the conversion then reports the body's own errors, where C# would report
                // that the interface is not a delegate type.
                return ConversionsBase.IsAnonymousFunctionCompatibleWithType(lambda, delegateType, Compilation) is LambdaConversionResult.Success or LambdaConversionResult.BindingFailed
                    ? Conversion.MakeSharperFunctionToInterface(Conversion.AnonymousFunction)
                    : Conversion.NoConversion;
            }

            // The plain conversions, so that this does not recurse.
            Conversion functionConversion = Conversions.ClassifyImplicitConversionFromExpression(source, delegateType, ref useSiteInfo);
            return functionConversion.Kind is ConversionKind.MethodGroup
                ? Conversion.MakeSharperFunctionToInterface(functionConversion)
                : Conversion.NoConversion;
        }

        /// <summary>
        /// The delegate a lambda or method group converts to on its way to a single-method interface, for type inference
        /// in the retry (<see cref="ConversionsBase.GetSharperFunctionToInterfaceDelegateType"/>): an interface over the
        /// method's type parameters gives a delegate over them, so the lambda is inferred as for that delegate.
        /// </summary>
        internal NamedTypeSymbol? GetSharperFunctionToInterfaceDelegateType(BoundExpression source, TypeSymbol destination)
        {
            return source.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup &&
                source.Syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper) &&
                TryGetSharperFunctionToInterface(destination, source.Syntax, out _, out var delegateType, out _)
                ? delegateType
                : null;
        }

        /// <summary>
        /// The retry of an assignment, return or initializer that C# found no conversion for.
        /// </summary>
        private Conversion ClassifySharperFunctionToInterfaceAfterError(BoundExpression source, TypeSymbol destination, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
            => source.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup
                ? ClassifySharperFunctionToInterface(source, destination, ref useSiteInfo)
                : Conversion.NoConversion;

        private BoundExpression CreateSharperFunctionToInterfaceConversion(
            SyntaxNode syntax,
            BoundExpression source,
            Conversion conversion,
            TypeSymbol destination,
            BindingDiagnosticBag diagnostics)
        {
            Debug.Assert(conversion.Kind == ConversionKind.SharperFunctionToInterface);

            if (!TryGetSharperFunctionToInterface(destination, source.Syntax, out var interfaceMethod, out var delegateType, out var delegateTypeOverDefinition))
            {
                throw ExceptionUtilities.Unreachable();
            }

            var function = CreateConversion(
                syntax,
                source,
                conversion.UnderlyingConversions[0],
                isCast: false,
                conversionGroupOpt: null,
                InConversionGroupFlags.Unspecified,
                wasCompilerGenerated: true,
                delegateType,
                diagnostics);

            // PROTOTYPE(sharper): the IOperation tree shows a None operation over the delegate creation; an
            // IConversionOperation to the interface would need a public conversion kind.
            return new BoundSharperFunctionToInterface(syntax, function, interfaceMethod, delegateTypeOverDefinition, destination, function.HasErrors)
            {
                WasCompilerGenerated = source.WasCompilerGenerated,
            };
        }

        /// <summary>
        /// Whether to retry an overload resolution that found no applicable candidate with the conversion to a
        /// single-method interface: some argument is a lambda or method group in Sharper code.
        /// </summary>
        /// <remarks>
        /// PROTOTYPE(sharper): delegate invocations, indexers, casts, conditional and switch expressions do not retry yet.
        /// </remarks>
        private static bool ShouldRetrySharperFunctionToInterface(AnalyzedArguments analyzedArguments)
        {
            foreach (var argument in analyzedArguments.Arguments)
            {
                if (argument.Kind is BoundKind.UnboundLambda or BoundKind.MethodGroup &&
                    argument.Syntax.IsFeatureEnabled(MessageID.IDS_FeatureSharper))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A binder whose conversions include the conversion to a single-method interface, for the retry of an overload
        /// resolution. Lookups go to this binder.
        /// </summary>
        private Binder GetSharperFunctionToInterfaceBinder()
            => new Binder(this, Conversions.WithSharperFunctionToInterface());

        /// <summary>
        /// The retry of a method group's overload resolution (instance and extension methods together, so that an extension
        /// method that takes a delegate still wins over an instance method that takes an interface).
        /// </summary>
        private MethodGroupResolution RetryResolveMethodGroupWithSharperFunctionToInterface(
            MethodGroupResolution resolution,
            BoundMethodGroup methodGroup,
            SyntaxNode expression,
            string methodName,
            AnalyzedArguments analyzedArguments,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            OverloadResolution.Options options,
            bool acceptOnlyMethods)
        {
            if (resolution.HasAnyApplicableMethod ||
                resolution.OverloadResolutionResult is null ||
                resolution.IsNonMethodExtensionMember(out _) ||
                !ShouldRetrySharperFunctionToInterface(analyzedArguments))
            {
                return resolution;
            }

            var retryUseSiteInfo = new CompoundUseSiteInfo<AssemblySymbol>(useSiteInfo);
            var retry = GetSharperFunctionToInterfaceBinder().ResolveMethodGroup(
                methodGroup, expression, methodName, analyzedArguments,
                useSiteInfo: ref retryUseSiteInfo,
                options: options,
                acceptOnlyMethods: acceptOnlyMethods);

            // With an applicable candidate, the retry's errors (an ambiguity between two interfaces) are the better ones.
            if (retry.HasAnyApplicableMethod)
            {
                resolution.Free();
                useSiteInfo.MergeAndClear(ref retryUseSiteInfo);
                return retry;
            }

            retry.Free();
            return resolution;
        }
    }
}
