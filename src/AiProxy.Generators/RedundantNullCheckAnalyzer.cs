using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AiProxy.Generators;

/// <summary>Reports <c>ArgumentNullException.ThrowIfNull</c> on a value whose declared type is a non-nullable reference type.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantNullCheckAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Redundant = new(
        "APGEN003", "Null check on a non-nullable value", "'{0}' is declared non-nullable; remove ArgumentNullException.ThrowIfNull",
        "Nullability", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Redundant];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 1)
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                Name: "ThrowIfNull", ContainingType: { Name: "ArgumentNullException", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } }
            })
        {
            return;
        }

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        var symbol = context.SemanticModel.GetSymbolInfo(argument, context.CancellationToken).Symbol;
        var type = symbol switch
        {
            IParameterSymbol parameter => parameter.Type,
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => context.SemanticModel.GetTypeInfo(argument, context.CancellationToken).Type,
        };

        if (type is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.NotAnnotated })
        {
            context.ReportDiagnostic(Diagnostic.Create(Redundant, invocation.GetLocation(), argument.ToString()));
        }
    }
}
