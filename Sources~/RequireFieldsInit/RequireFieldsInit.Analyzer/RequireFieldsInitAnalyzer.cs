using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace CodeWriter.RequireFieldsInit
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class RequireFieldsInitAnalyzer : DiagnosticAnalyzer
    {
        private const string RequireFieldsInitAttributeName = "RequireFieldsInitAttribute";
        private const string RequireFieldsInitDisableChecksInNamespaceAttributeName = "RequireFieldsInitDisableChecksInNamespaceAttribute";

        private static readonly DiagnosticDescriptor UnhandledErrorRule = new(
            id: "RequireFieldsInit_000",
            title: "Internal error",
            messageFormat: "Internal error occured. Please open a bug report. Message: '{0}'",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor FieldNotInitializedRule = new(
            id: "RequireFieldsInit_001",
            title: "Required field not initialized",
            messageFormat: "Required field '{0}' not initialized",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(UnhandledErrorRule, FieldNotInitializedRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                                   GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            if (context.Compilation.GetTypeByMetadataName(RequireFieldsInitAttributeName)
                is not INamedTypeSymbol attributeTypeSymbol)
            {
                return;
            }

            if (context.Compilation.GetTypeByMetadataName(RequireFieldsInitDisableChecksInNamespaceAttributeName)
                is not INamedTypeSymbol disableChecksInNamespaceAttributeTypeSymbol)
            {
                return;
            }

            var disabledNamespaces = context.Compilation.Assembly.GetAttributes()
                .Where(it => SymbolEqualityComparer.Default.Equals(it.AttributeClass, disableChecksInNamespaceAttributeTypeSymbol))
                .Select(it => (string) it.NamedArguments.Single(a => a.Key == "Namespace").Value.Value)
                .ToList();

            var cache = new Cache
            {
                AttributeTypeSymbol = attributeTypeSymbol,
                DisabledNamespaces = disabledNamespaces,
                RequiredFieldsCache =
                    new ConcurrentDictionary<INamedTypeSymbol, List<string>>(SymbolEqualityComparer.Default),
            };


            context.RegisterSyntaxNodeAction(ctx =>
                {
                    try
                    {
                        CheckObjectCreation(ctx, cache);
                    }
                    catch (Exception ex)
                    {
                        ctx.ReportDiagnostic(
                            Diagnostic.Create(UnhandledErrorRule, ctx.Node.GetLocation(), ex.Message));
                    }
                },
                SyntaxKind.ObjectCreationExpression);
            context.RegisterCompilationEndAction(_ => cache.RequiredFieldsCache.Clear());
        }

        private void CheckObjectCreation(SyntaxNodeAnalysisContext context, Cache cache)
        {
            if (context.Node is not ObjectCreationExpressionSyntax creationSyntax)
            {
                return;
            }

            if (creationSyntax.Type is not NameSyntax nameSyntax)
            {
                return;
            }

            if (context.SemanticModel.GetSymbolInfo(nameSyntax).Symbol is not INamedTypeSymbol typeSymbol)
            {
                return;
            }

            if (!cache.RequiredFieldsCache.TryGetValue(typeSymbol, out var allRequiredFields))
            {
                allRequiredFields = PopulateRequiredFields(typeSymbol, cache);
                cache.RequiredFieldsCache.TryAdd(typeSymbol, allRequiredFields);
            }

            if (allRequiredFields == null || allRequiredFields.Count == 0)
            {
                return;
            }

            if (cache.DisabledNamespaces.Count > 0)
            {
                var ns = creationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>()?.Name?.ToString() ?? "";

                if (cache.DisabledNamespaces.Contains(ns))
                {
                    return;
                }
            }

            Analyze(context, creationSyntax, allRequiredFields);
        }

        private void Analyze(
            SyntaxNodeAnalysisContext context,
            ObjectCreationExpressionSyntax creationSyntax,
            List<string> requiredFields)
        {
            var initializerExpressions =
                (IEnumerable<ExpressionSyntax>) creationSyntax.Initializer?.Expressions ??
                Array.Empty<ExpressionSyntax>();

            var initializedNames = initializerExpressions
                .OfType<AssignmentExpressionSyntax>()
                .Select(it => it.Left is IdentifierNameSyntax idSyntax ? idSyntax.Identifier.Text : null)
                .Where(it => it != null)
                .ToArray();

            foreach (var fieldName in requiredFields)
            {
                if (initializedNames.Contains(fieldName))
                {
                    continue;
                }

                context.ReportDiagnostic(
                    Diagnostic.Create(FieldNotInitializedRule, creationSyntax.Type.GetLocation(), fieldName));
            }
        }

        private static List<string> PopulateRequiredFields(INamedTypeSymbol typeSymbol, Cache cache)
        {
            var requireFieldsInitData = typeSymbol
                .GetAttributes()
                .FirstOrDefault(it =>
                    SymbolEqualityComparer.Default.Equals(it.AttributeClass, cache.AttributeTypeSymbol));

            if (requireFieldsInitData == null)
            {
                return null;
            }

            TypedConstant? optionalArg = null;
            TypedConstant? requiredArg = null;

            foreach (var kvp in requireFieldsInitData.NamedArguments)
            {
                switch (kvp)
                {
                    case { Key: "Optional", Value: { IsNull: false, Kind: TypedConstantKind.Array } }:
                        optionalArg = kvp.Value;
                        break;

                    case { Key: "Required", Value: { IsNull: false, Kind: TypedConstantKind.Array } }:
                        requiredArg = kvp.Value;
                        break;
                }
            }

            var requiredFields = new List<string>();

            if (requiredArg is { Values: var requiredValuesArg })
            {
                foreach (var element in requiredValuesArg)
                {
                    if (element is { IsNull: false, Kind: TypedConstantKind.Primitive, Value: string requiredFieldName })
                    {
                        requiredFields.Add(requiredFieldName);
                    }
                }
            }
            else
            {
                foreach (var memberName in typeSymbol.MemberNames)
                {
                    var fieldSymbol = typeSymbol.GetMembers(memberName).OfType<IFieldSymbol>().FirstOrDefault();

                    if (fieldSymbol == null || fieldSymbol.IsStatic || fieldSymbol.IsReadOnly)
                    {
                        continue;
                    }

                    requiredFields.Add(memberName);
                }
            }

            if (optionalArg is { Values: var optionalValuesArg })
            {
                foreach (var element in optionalValuesArg)
                {
                    if (element is { IsNull: false, Kind: TypedConstantKind.Primitive, Value: string optionalFieldName })
                    {
                        requiredFields.Remove(optionalFieldName);
                    }
                }
            }

            return requiredFields;
        }

        public class Cache
        {
            public List<string> DisabledNamespaces;
            public INamedTypeSymbol AttributeTypeSymbol;
            public ConcurrentDictionary<INamedTypeSymbol, List<string>> RequiredFieldsCache;
        }
    }
}