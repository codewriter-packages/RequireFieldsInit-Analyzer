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

        private static readonly DiagnosticDescriptor Rule = new(
            id: "RequireFieldsInit_001",
            title: "Required field not initialized",
            messageFormat: "Required field '{0}' not initialized",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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

            var cache = new Cache
            {
                AttributeTypeSymbol = attributeTypeSymbol,
                RequiredFieldsCache =
                    new ConcurrentDictionary<INamedTypeSymbol, List<string>>(SymbolEqualityComparer.Default),
            };


            context.RegisterSyntaxNodeAction(ctx => CheckObjectCreation(ctx, cache),
                SyntaxKind.ObjectCreationExpression);
            context.RegisterCompilationEndAction(_ => cache.RequiredFieldsCache.Clear());
        }

        private void CheckObjectCreation(SyntaxNodeAnalysisContext context, Cache cache)
        {
            if (context.Node is not ObjectCreationExpressionSyntax creationSyntax)
            {
                return;
            }

            if (creationSyntax.Type is not IdentifierNameSyntax identifierNameSyntax)
            {
                return;
            }

            if (context.SemanticModel.GetSymbolInfo(identifierNameSyntax).Symbol is not INamedTypeSymbol typeSymbol)
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

                context.ReportDiagnostic(Diagnostic.Create(Rule, creationSyntax.Type.GetLocation(), fieldName));
            }
        }

        private static List<string> PopulateRequiredFields(INamedTypeSymbol typeSymbol, Cache cache)
        {
            var requireFieldsInit = typeSymbol
                .GetAttributes()
                .Any(it => SymbolEqualityComparer.Default.Equals(it.AttributeClass, cache.AttributeTypeSymbol));

            if (!requireFieldsInit)
            {
                return null;
            }

            var requiredFields = new List<string>();

            foreach (var memberName in typeSymbol.MemberNames)
            {
                if (typeSymbol.GetMembers(memberName).OfType<IFieldSymbol>().Any())
                {
                    requiredFields.Add(memberName);
                }
            }

            return requiredFields;
        }

        public class Cache
        {
            public INamedTypeSymbol AttributeTypeSymbol;
            public ConcurrentDictionary<INamedTypeSymbol, List<string>> RequiredFieldsCache;
        }
    }
}