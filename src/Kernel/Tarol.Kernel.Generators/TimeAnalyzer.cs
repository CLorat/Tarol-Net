using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Kernel.Generators;

/// <summary>Reports reads of DateTime.Now/UtcNow/Today and DateTimeOffset.Now/UtcNow (TKRN001).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TimeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Diagnostics.SystemClock);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var dateTime = start.Compilation.GetSpecialType(SpecialType.System_DateTime);
            var dateTimeOffset = start.Compilation.GetTypeByMetadataName("System.DateTimeOffset");
            start.RegisterOperationAction(c => Analyze(c, dateTime, dateTimeOffset), OperationKind.PropertyReference);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol dateTime, INamedTypeSymbol? dateTimeOffset)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        if (!property.IsStatic)
        {
            return;
        }

        var type = property.ContainingType;
        var hit = SymbolEqualityComparer.Default.Equals(type, dateTime)
            ? property.Name is "Now" or "UtcNow" or "Today"
            : SymbolEqualityComparer.Default.Equals(type, dateTimeOffset) && property.Name is "Now" or "UtcNow";
        if (hit)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.SystemClock, context.Operation.Syntax.GetLocation(), $"{type.Name}.{property.Name}"));
        }
    }
}
