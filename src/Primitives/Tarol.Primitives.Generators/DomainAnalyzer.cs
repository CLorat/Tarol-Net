using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tarol.Primitives.Generators;

/// <summary>
///     Keeps decide and apply apart in aggregates and entities:
///     <list type="bullet">
///         <item>TPRM013: state is assigned only in <c>On</c> handlers (and constructors, private helpers called only from
///         <c>On</c> handlers, <c>[Transient]</c> members);</item>
///         <item>TPRM014: an <c>On</c> handler does not call a rule or guard (a method returning <c>Result</c>), throw,
///         read a <c>TimeProvider</c> or raise;</item>
///         <item>TPRM015: user code does not call <c>Dispatch</c>, <c>CheckInvariants</c> or generated <c>__</c> members.</item>
///     </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.StateOutsideApply, Diagnostics.DecisionInApply, Diagnostics.PlumbingUsed);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var result = start.Compilation.GetTypeByMetadataName("Tarol.Results.Result");
            var resultOfT = start.Compilation.GetTypeByMetadataName("Tarol.Results.Result`1");
            var timeProvider = start.Compilation.GetTypeByMetadataName("System.TimeProvider");
            if (start.Compilation.GetTypeByMetadataName("Tarol.Primitives.DomainEvent") is null)
                return;

            var callers = new ConcurrentDictionary<IMethodSymbol, ConcurrentBag<IMethodSymbol>>(SymbolEqualityComparer.Default);
            var writes = new ConcurrentBag<(IMethodSymbol Method, ISymbol Member, Location Location)>();

            start.RegisterOperationBlockStartAction(block =>
            {
                if (block.OwningSymbol is not IMethodSymbol method || !IsDomainType(method.ContainingType))
                    return;
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
                    return;
                var apply = Symbols.IsHandler(method);

                block.RegisterOperationAction(ctx =>
                {
                    var target = ctx.Operation switch
                    {
                        IAssignmentOperation assignment => assignment.Target,
                        IIncrementOrDecrementOperation increment => increment.Target,
                        _ => null,
                    };
                    if (!apply && StateMember(target) is { } member)
                        writes.Add((method, member, ctx.Operation.Syntax.GetLocation()));
                }, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment,
                    OperationKind.Increment, OperationKind.Decrement);

                block.RegisterOperationAction(ctx =>
                {
                    var call = (IInvocationOperation)ctx.Operation;
                    var called = call.TargetMethod;
                    if (SymbolEqualityComparer.Default.Equals(called.ContainingType, method.ContainingType))
                        callers.GetOrAdd(called.OriginalDefinition, static _ => new ConcurrentBag<IMethodSymbol>()).Add(method);

                    if (IsPlumbing(called))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.PlumbingUsed, call.Syntax.GetLocation(), called.Name));
                        return;
                    }

                    if (!apply)
                        return;
                    var returned = called.ReturnType.OriginalDefinition;
                    var why = SymbolEqualityComparer.Default.Equals(returned, result) || SymbolEqualityComparer.Default.Equals(returned, resultOfT)
                        ? "a call returning Result is a rule or a guard"
                        : called.Name == "Raise" && IsLibraryBase(called.ContainingType)
                            ? "raising is decided by behaviour methods"
                            : timeProvider is not null && SymbolEqualityComparer.Default.Equals(called.ContainingType, timeProvider)
                                ? "the clock; use the time the event carries"
                                : null;
                    if (why is not null)
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DecisionInApply, call.Syntax.GetLocation(), $"'{called.Name}'", method.Parameters[0].Type.Name, why));
                }, OperationKind.Invocation);

                block.RegisterOperationAction(ctx =>
                {
                    var reference = (IMemberReferenceOperation)ctx.Operation;
                    if (reference.Member.Name.StartsWith("__", System.StringComparison.Ordinal) && IsDomainType(reference.Member.ContainingType))
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.PlumbingUsed, reference.Syntax.GetLocation(), reference.Member.Name));
                }, OperationKind.FieldReference, OperationKind.PropertyReference);

                if (apply)
                {
                    block.RegisterOperationAction(ctx =>
                    {
                        if (((IThrowOperation)ctx.Operation).Exception is null)
                            return; // a bare rethrow in a catch block
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.DecisionInApply, ctx.Operation.Syntax.GetLocation(), "A throw", method.Parameters[0].Type.Name, "it rejects stored history on replay"));
                    }, OperationKind.Throw);
                }
            });

            start.RegisterCompilationEndAction(end =>
            {
                foreach (var write in writes)
                    if (!OnlyFromApply(write.Method, 0))
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.StateOutsideApply, write.Location, write.Member.Name, write.Method.Name));
            });

            bool OnlyFromApply(IMethodSymbol method, int depth)
            {
                if (depth > 8 || method.DeclaredAccessibility != Accessibility.Private)
                    return false;
                if (!callers.TryGetValue(method.OriginalDefinition, out var from) || from.IsEmpty)
                    return false;
                return from.All(c => Symbols.IsHandler(c) || OnlyFromApply(c, depth + 1));
            }
        });
    }

    private static ISymbol? StateMember(IOperation? target)
    {
        var member = target switch
        {
            IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation } f => (ISymbol)f.Field,
            IPropertyReferenceOperation { Property.IsStatic: false, Instance: IInstanceReferenceOperation } p => p.Property,
            _ => null,
        };
        if (member is null || Symbols.HasAttribute(member, "TransientAttribute"))
            return null;
        // the backing field of a [Transient] property, or [field: Transient]
        if (member is IFieldSymbol { AssociatedSymbol: { } associated } && Symbols.HasAttribute(associated, "TransientAttribute"))
            return null;
        return member;
    }

    private static bool IsPlumbing(IMethodSymbol method)
    {
        if (method.Name.StartsWith("__", System.StringComparison.Ordinal) && IsDomainType(method.ContainingType))
            return true;
        if (method.Name is not ("Dispatch" or "CheckInvariants"))
            return false;
        for (var m = method; m is not null; m = m.OverriddenMethod)
            if (IsLibraryBase(m.ContainingType))
                return true;
        return false;
    }

    private static bool IsLibraryBase(INamedTypeSymbol? type)
        => type is not null && (Symbols.Is(type.OriginalDefinition, "AggregateRoot`1") || Symbols.Is(type.OriginalDefinition, "Entity`2"));

    private static bool IsDomainType(INamedTypeSymbol? type)
    {
        for (var b = type?.BaseType; b is not null; b = b.BaseType)
            if (IsLibraryBase(b))
                return true;
        return false;
    }
}
