using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Results.Generators;

/// <summary>Reports the Result pitfalls the compiler does not: TRES001, TRES002, TRES003.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResultAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.AmbiguousValueType, Diagnostics.DiscardedResult, Diagnostics.ObjectValue);

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
        if (reference.Property.Name == "Value" && types.IsResult(reference.Property.ContainingType)
            && reference.Syntax is MemberAccessExpressionSyntax access)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ObjectValue, access.Name.GetLocation(),
                reference.Property.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private sealed class KnownTypes
    {
        private readonly INamedTypeSymbol _resultOfT;
        private readonly INamedTypeSymbol _result;
        private readonly INamedTypeSymbol _violation;
        private readonly INamedTypeSymbol? _task;
        private readonly INamedTypeSymbol? _valueTask;

        private KnownTypes(INamedTypeSymbol resultOfT, INamedTypeSymbol result, INamedTypeSymbol violation, Compilation compilation)
        {
            _resultOfT = resultOfT;
            _result = result;
            _violation = violation;
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
