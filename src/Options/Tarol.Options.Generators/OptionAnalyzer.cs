using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Options.Generators;

/// <summary>Reports the Option pitfalls the compiler does not: TOPT001 … TOPT004.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OptionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.ObjectValue, Diagnostics.NullTest, Diagnostics.IgnoredTryGetValue, Diagnostics.DefaultBecomesSome);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Tarol.Options.Option`1") is not { } option)
            {
                return;
            }

            start.RegisterOperationAction(c => AnalyzeExpressionStatement(c, option), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(c => AnalyzePropertyReference(c, option), OperationKind.PropertyReference);
            start.RegisterOperationAction(c => AnalyzePattern(c, option), OperationKind.ConstantPattern, OperationKind.NegatedPattern, OperationKind.RecursivePattern);
            start.RegisterOperationAction(c => AnalyzeNullComparison(c, option), OperationKind.Binary);
            start.RegisterOperationAction(c => AnalyzeConversion(c, option), OperationKind.Conversion);
        });
    }

    // TOPT003: `option.TryGetValue(out var v);`
    private static void AnalyzeExpressionStatement(OperationAnalysisContext context, INamedTypeSymbol option)
    {
        var statement = (IExpressionStatementOperation)context.Operation;
        if (statement.Operation is IInvocationOperation { TargetMethod.Name: "TryGetValue", Instance.Type: INamedTypeSymbol receiver } tryGet
            && IsOption(receiver, option))
        {
            var outType = tryGet.TargetMethod.Parameters.Length == 1 ? tryGet.TargetMethod.Parameters[0].Type : null;
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.IgnoredTryGetValue, statement.Syntax.GetLocation(),
                receiver.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                outType is { IsValueType: true } ? $"default({outType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)})" : "null"));
        }
    }

    // TOPT001: option.Value.
    private static void AnalyzePropertyReference(OperationAnalysisContext context, INamedTypeSymbol option)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        if (reference.Property.Name == "Value" && IsOption(reference.Property.ContainingType, option)
            && reference.Syntax is MemberAccessExpressionSyntax access)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ObjectValue, access.Name.GetLocation(),
                reference.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    // TOPT002: `o is { } x`, `o is not null`, `o is null` on an Option.
    private static void AnalyzePattern(OperationAnalysisContext context, INamedTypeSymbol option)
    {
        var pattern = (IPatternOperation)context.Operation;
        if (!IsOption(pattern.InputType, option) || pattern.Parent is IPatternOperation)
        {
            return; // nested patterns are checked through their parent
        }

        bool? always = pattern switch
        {
            IConstantPatternOperation constant when IsNullLiteral(constant.Value) => false,
            INegatedPatternOperation { Pattern: IConstantPatternOperation constant } when IsNullLiteral(constant.Value) => true,
            IRecursivePatternOperation { DeconstructionSubpatterns.Length: 0, PropertySubpatterns.Length: 0 } recursive
                when SymbolEqualityComparer.Default.Equals(recursive.MatchedType, pattern.InputType) => true,
            _ => null,
        };

        if (always is { } value)
        {
            ReportNullTest(context, pattern.Syntax, pattern.InputType, value);
        }
    }

    // TOPT002: `o == null`, `o != null` on an Option.
    private static void AnalyzeNullComparison(OperationAnalysisContext context, INamedTypeSymbol option)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
        {
            return;
        }

        var left = Unwrap(binary.LeftOperand);
        var right = Unwrap(binary.RightOperand);
        var tested = IsNullLiteral(right) ? left : IsNullLiteral(left) ? right : null;
        if (tested is not null && IsOption(tested.Type, option))
        {
            ReportNullTest(context, binary.Syntax, tested.Type!, binary.OperatorKind == BinaryOperatorKind.NotEquals);
        }
    }

    // TOPT004: `found ? value : default`, `value ?? default`, `return default(int);` converted to Option<T>.
    private static void AnalyzeConversion(OperationAnalysisContext context, INamedTypeSymbol option)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.IsImplicit is false && conversion.Syntax is CastExpressionSyntax
            || conversion.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } target
            || !IsOption(target, option)
            || !SymbolEqualityComparer.Default.Equals(conversion.Operand.Type, target.TypeArguments[0]))
        {
            return;
        }

        foreach (var found in Defaults(conversion.Operand))
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DefaultBecomesSome, found.Syntax.GetLocation(),
                target.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private static IEnumerable<IOperation> Defaults(IOperation operation)
    {
        operation = Unwrap(operation);
        switch (operation)
        {
            case IDefaultValueOperation:
                yield return operation;
                break;
            case IConditionalOperation { WhenFalse: not null } conditional:
                foreach (var found in Defaults(conditional.WhenTrue))
                {
                    yield return found;
                }

                foreach (var found in Defaults(conditional.WhenFalse))
                {
                    yield return found;
                }

                break;
            case ICoalesceOperation coalesce:
                foreach (var found in Defaults(coalesce.WhenNull))
                {
                    yield return found;
                }

                break;
        }
    }

    private static bool IsOption(ITypeSymbol? type, INamedTypeSymbol option)
        => type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, option);

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } implicitConversion)
        {
            operation = implicitConversion.Operand;
        }

        return operation;
    }

    private static bool IsNullLiteral(IOperation operation)
        => Unwrap(operation) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };

    private static void ReportNullTest(OperationAnalysisContext context, SyntaxNode syntax, ITypeSymbol option, bool always)
        => context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NullTest, syntax.GetLocation(),
            syntax.ToString(), option.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), always ? "true" : "false"));
}
