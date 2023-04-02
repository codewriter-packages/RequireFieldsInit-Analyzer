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
            id: "RequireFieldsInit001",
            title: "Missing field initialization",
            messageFormat: "Missing field initialization: {0}",
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
            var requiredFieldsCache =
                new ConcurrentDictionary<INamedTypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
            context.RegisterSyntaxNodeAction(ctx => CheckObjectCreation(ctx, requiredFieldsCache),
                SyntaxKind.ObjectCreationExpression);
            context.RegisterCompilationEndAction(_ => requiredFieldsCache.Clear());
        }

        private void CheckObjectCreation(
            SyntaxNodeAnalysisContext context,
            ConcurrentDictionary<INamedTypeSymbol, HashSet<string>> requiredFieldsCache)
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

            if (!requiredFieldsCache.TryGetValue(typeSymbol, out var allRequiredFields))
            {
                allRequiredFields = PopulateRequiredFields(typeSymbol);
                requiredFieldsCache.TryAdd(typeSymbol, allRequiredFields);
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
            HashSet<string> requiredFields)
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

        private static HashSet<string> PopulateRequiredFields(INamedTypeSymbol typeSymbol)
        {
            var requireFieldsInit = typeSymbol
                .GetAttributes()
                .Any(it => it.AttributeClass?.Name.Equals(RequireFieldsInitAttributeName, StringComparison.Ordinal) ??
                           false);

            if (!requireFieldsInit)
            {
                return null;
            }

            var requiredFields = new HashSet<string>();

            foreach (var memberName in typeSymbol.MemberNames)
            {
                if (typeSymbol.GetMembers(memberName).OfType<IFieldSymbol>().Any())
                {
                    requiredFields.Add(memberName);
                }
            }

            return requiredFields;
        }
    }
}