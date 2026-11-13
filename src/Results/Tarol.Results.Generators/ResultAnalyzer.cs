using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Results.Generators;

/// <summary>Reports the Result and Option pitfalls the compiler does not: TRES001 … TRES006.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResultAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.AmbiguousValueType, Diagnostics.DiscardedResult, Diagnostics.ObjectValue,
            Diagnostics.OptionNullTest, Diagnostics.IgnoredTryGetValue, Diagnostics.DefaultBecomesValue);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var types = KnownTypes.Create(start.Compilation);
            if (types is null)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(c => AnalyzeGenericName(c, types), SyntaxKind.GenericName);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, types), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeExpressionStatement(c, types), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(c => AnalyzePropertyReference(c, types), OperationKind.PropertyReference);
            start.RegisterOperationAction(c => AnalyzePattern(c, types), OperationKind.ConstantPattern, OperationKind.NegatedPattern, OperationKind.RecursivePattern);
            start.RegisterOperationAction(c => AnalyzeNullComparison(c, types), OperationKind.Binary);
            start.RegisterOperationAction(c => AnalyzeConversion(c, types), OperationKind.Conversion);
        });
    }

    // TRES001 on a spelled type: Result<Violation>, Result<object>, Result<Result<int>>.
    private static void AnalyzeGenericName(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is INamedTypeSymbol type
            && types.AmbiguityOf(type) is { } reason)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AmbiguousValueType, context.Node.GetLocation(),
                type.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), reason));
        }
    }

    // TRES001 on an inferred type argument: Result.Success(violation), Result.Success(new object()).
    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownTypes types)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!method.IsGenericMethod
            || invocation.Syntax is not InvocationExpressionSyntax { Expression: var callee }
            || callee is GenericNameSyntax or MemberAccessExpressionSyntax { Name: GenericNameSyntax })
        {
            return; // explicit type arguments are reported by AnalyzeGenericName
        }

        if (method.ReturnType is INamedTypeSymbol returned && types.AmbiguityOf(returned) is { } reason)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AmbiguousValueType, invocation.Syntax.GetLocation(),
                returned.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), reason));
        }
    }

    // TRES002: `repository.Save(x);`, `await unitOfWork.Commit(ct);`, `Validate(x);`.
    private static void AnalyzeExpressionStatement(OperationAnalysisContext context, KnownTypes types)
    {
        var statement = (IExpressionStatementOperation)context.Operation;
        var expression = statement.Operation;
        var called = expression is IAwaitOperation awaited ? awaited.Operation : expression;
        while (called is IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } inner })
        {
            called = inner;
        }

        // TRES005: `option.TryGetValue(out var v);`
        if (called is IInvocationOperation { TargetMethod.Name: "TryGetValue", Instance: { Type: INamedTypeSymbol receiver } } tryGet
            && types.IsUnion(receiver))
        {
            var outType = tryGet.TargetMethod.Parameters.Length == 1 ? tryGet.TargetMethod.Parameters[0].Type : null;
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.IgnoredTryGetValue, statement.Syntax.GetLocation(),
                receiver.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                outType is { IsValueType: true } ? $"default({outType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)})" : "null"));
            return;
        }

        if (called is not IInvocationOperation invocation || !types.IsResultLike(expression.Type))
        {
            return;
        }

        // Tap/TapFailure/TapAsync exist for their side effect and hand back the result they were called on.
        if (invocation.TargetMethod.Name is "Tap" or "TapFailure" or "TapAsync" && invocation.Instance is not null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DiscardedResult, statement.Syntax.GetLocation(),
            expression.Type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), invocation.TargetMethod.Name));
    }

    // TRES003: result.Value.
    private static void AnalyzePropertyReference(OperationAnalysisContext context, KnownTypes types)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        if (reference.Property.Name == "Value" && types.IsUnion(reference.Property.ContainingType)
            && reference.Syntax is MemberAccessExpressionSyntax access)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ObjectValue, access.Name.GetLocation(),
                reference.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    // TRES004: `o is { } x`, `o is not null`, `o is null` on an Option.
    private static void AnalyzePattern(OperationAnalysisContext context, KnownTypes types)
    {
        var pattern = (IPatternOperation)context.Operation;
        if (!types.IsOption(pattern.InputType) || pattern.Parent is IPatternOperation)
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

    // TRES004: `o == null`, `o != null` on an Option.
    private static void AnalyzeNullComparison(OperationAnalysisContext context, KnownTypes types)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
        {
            return;
        }

        var left = Unwrap(binary.LeftOperand);
        var right = Unwrap(binary.RightOperand);
        var option = IsNullLiteral(right) ? left : IsNullLiteral(left) ? right : null;
        if (option is not null && types.IsOption(option.Type))
        {
            ReportNullTest(context, binary.Syntax, option.Type!, binary.OperatorKind == BinaryOperatorKind.NotEquals);
        }
    }

    // TRES006: `found ? value : default`, `value ?? default`, `return default(int);` converted to Option<T> / Result<T>.
    private static void AnalyzeConversion(OperationAnalysisContext context, KnownTypes types)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.IsImplicit is false && conversion.Syntax is CastExpressionSyntax
            || conversion.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } target
            || !(types.IsOption(target) || types.IsGenericResult(target))
            || !SymbolEqualityComparer.Default.Equals(conversion.Operand.Type, target.TypeArguments[0]))
        {
            return;
        }

        foreach (var found in Defaults(conversion.Operand))
        {
            var isOption = types.IsOption(target);
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.DefaultBecomesValue, found.Syntax.GetLocation(),
                target.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                isOption ? "present value (Some)" : "success",
                isOption ? "Option.None" : "a Violation"));
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
        => context.ReportDiagnostic(Diagnostic.Create(Diagnostics.OptionNullTest, syntax.GetLocation(),
            syntax.ToString(), option.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), always ? "true" : "false"));

    private sealed class KnownTypes
    {
        private readonly INamedTypeSymbol _resultOfT;
        private readonly INamedTypeSymbol _result;
        private readonly INamedTypeSymbol _violation;
        private readonly INamedTypeSymbol? _option;
        private readonly INamedTypeSymbol? _task;
        private readonly INamedTypeSymbol? _valueTask;

        private KnownTypes(INamedTypeSymbol resultOfT, INamedTypeSymbol result, INamedTypeSymbol violation, Compilation compilation)
        {
            _resultOfT = resultOfT;
            _result = result;
            _violation = violation;
            _option = compilation.GetTypeByMetadataName("Tarol.Results.Option`1");
            _task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            _valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        }

        public static KnownTypes? Create(Compilation compilation)
        {
            var resultOfT = compilation.GetTypeByMetadataName("Tarol.Results.Result`1");
            var result = compilation.GetTypeByMetadataName("Tarol.Results.Result");
            var violation = compilation.GetTypeByMetadataName("Tarol.Results.Violation");
            return resultOfT is null || result is null || violation is null ? null : new KnownTypes(resultOfT, result, violation, compilation);
        }

        public bool IsResult(INamedTypeSymbol? type)
            => type is not null && (SymbolEqualityComparer.Default.Equals(type, _result) || SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, _resultOfT));

        public bool IsGenericResult(INamedTypeSymbol type) => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, _resultOfT);

        public bool IsOption(ITypeSymbol? type)
            => _option is not null && type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _option);

        public bool IsUnion(INamedTypeSymbol? type) => IsResult(type) || IsOption(type);

        public bool IsResultLike(ITypeSymbol? type)
        {
            if (type is not INamedTypeSymbol named)
            {
                return false;
            }

            if (IsResult(named))
            {
                return true;
            }

            var definition = named.OriginalDefinition;
            return named.TypeArguments.Length == 1
                && (SymbolEqualityComparer.Default.Equals(definition, _task) || SymbolEqualityComparer.Default.Equals(definition, _valueTask))
                && named.TypeArguments[0] is INamedTypeSymbol inner && IsResult(inner);
        }

        public string? AmbiguityOf(INamedTypeSymbol type)
        {
            if (!SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, _resultOfT) || type.TypeArguments.Length != 1)
            {
                return null;
            }

            var argument = type.TypeArguments[0];
            if (argument.SpecialType == SpecialType.System_Object)
            {
                return "an object can hold a Violation, which then reads as a success";
            }

            if (argument is INamedTypeSymbol named && IsResult(named))
            {
                return "a nested result hides the inner failure behind an outer success";
            }

            for (var current = argument; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, _violation))
                {
                    return "the value would be a Violation, so value and failure cannot be told apart";
                }
            }

            return null;
        }
    }
}
