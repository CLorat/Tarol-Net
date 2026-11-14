using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace C.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApplyAnalyzer : DiagnosticAnalyzer
{
    // P1: state changed outside an apply method (bypass of events).
    static readonly DiagnosticDescriptor StateOutsideApply = new("TPRM900", "State changed outside an apply method",
        "'{0}' is assigned in '{1}', which is not an On handler; raise an event and assign it in its On handler", "Tarol", DiagnosticSeverity.Warning, true, customTags: WellKnownDiagnosticTags.CompilationEnd);
    // P2: rule, clock, raise or throw inside an apply method (replay safety).
    static readonly DiagnosticDescriptor RuleInApply = new("TPRM901", "Apply method must not decide",
        "'{0}' in apply method '{1}': {2}; apply only assigns the event's data", "Tarol", DiagnosticSeverity.Warning, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(StateOutsideApply, RuleInApply);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var root = start.Compilation.GetTypeByMetadataName("C.Core.AggregateRoot`1");
            var entity = start.Compilation.GetTypeByMetadataName("C.Core.Entity`2");
            var domainEvent = start.Compilation.GetTypeByMetadataName("C.Core.DomainEvent");
            var result = start.Compilation.GetTypeByMetadataName("Tarol.Results.Result");
            var resultT = start.Compilation.GetTypeByMetadataName("Tarol.Results.Result`1");
            var timeProvider = start.Compilation.GetTypeByMetadataName("System.TimeProvider");
            if (root is null || entity is null || domainEvent is null) return;

            bool IsDomainType(INamedTypeSymbol? t)
            {
                for (var b = t?.BaseType; b is not null; b = b.BaseType)
                    if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, root) || SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, entity)) return true;
                return false;
            }
            bool IsEvent(ITypeSymbol t) { for (var b = t; b is not null; b = b.BaseType) if (SymbolEqualityComparer.Default.Equals(b, domainEvent)) return true; return false; }
            bool IsApply(IMethodSymbol m) => m.Name == "On" && m.Parameters.Length == 1 && IsEvent(m.Parameters[0].Type) && m.ReturnsVoid;

            // private helpers called only from apply methods count as apply (call graph inside the type).
            var callers = new ConcurrentDictionary<IMethodSymbol, ConcurrentBag<IMethodSymbol>>(SymbolEqualityComparer.Default);
            var writes = new ConcurrentBag<(IMethodSymbol Method, ISymbol Member, Location Location)>();

            start.RegisterOperationBlockStartAction(block =>
            {
                if (block.OwningSymbol is not IMethodSymbol method || !IsDomainType(method.ContainingType)) return;
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) return;
                var apply = IsApply(method);

                block.RegisterOperationAction(ctx =>
                {
                    var target = ((IAssignmentOperation)ctx.Operation).Target;
                    var member = target switch { IFieldReferenceOperation f when !f.Field.IsStatic && f.Instance is IInstanceReferenceOperation => (ISymbol)f.Field,
                                                 IPropertyReferenceOperation p when !p.Property.IsStatic && p.Instance is IInstanceReferenceOperation => p.Property, _ => null };
                    if (member is not null && !apply) writes.Add((method, member, ctx.Operation.Syntax.GetLocation()));
                }, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment);
                block.RegisterOperationAction(ctx =>
                {
                    var target = ((IIncrementOrDecrementOperation)ctx.Operation).Target;
                    if (!apply && target is IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } or IFieldReferenceOperation { Instance: IInstanceReferenceOperation })
                        writes.Add((method, ((IMemberReferenceOperation)target).Member, ctx.Operation.Syntax.GetLocation()));
                }, OperationKind.Increment, OperationKind.Decrement);

                block.RegisterOperationAction(ctx =>
                {
                    var call = (IInvocationOperation)ctx.Operation;
                    if (SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, method.ContainingType))
                        callers.GetOrAdd(call.TargetMethod.OriginalDefinition, _ => new()).Add(method);
                    if (!apply) return;
                    var ret = call.TargetMethod.ReturnType.OriginalDefinition;
                    string? why = SymbolEqualityComparer.Default.Equals(ret, result) || SymbolEqualityComparer.Default.Equals(ret, resultT) ? "a call returning Result is a rule or a guard"
                        : call.TargetMethod.Name == "Raise" ? "raising is decided by behaviour methods"
                        : SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, timeProvider) ? "the clock; use the time in the event" : null;
                    if (why is not null) ctx.ReportDiagnostic(Diagnostic.Create(RuleInApply, ctx.Operation.Syntax.GetLocation(), call.TargetMethod.Name, method.Name, why));
                }, OperationKind.Invocation);
                if (apply)
                    block.RegisterOperationAction(ctx => ctx.ReportDiagnostic(Diagnostic.Create(RuleInApply, ctx.Operation.Syntax.GetLocation(), "throw", method.Name, "a throw rejects history on replay")), OperationKind.Throw);
            });

            start.RegisterCompilationEndAction(end =>
            {
                bool OnlyFromApply(IMethodSymbol m, int depth)
                {
                    if (depth > 8 || m.DeclaredAccessibility != Accessibility.Private) return false;
                    if (!callers.TryGetValue(m.OriginalDefinition, out var from) || from.IsEmpty) return false;
                    return from.All(c => IsApply(c) || OnlyFromApply(c, depth + 1));
                }
                foreach (var w in writes)
                    if (!OnlyFromApply(w.Method, 0))
                        end.ReportDiagnostic(Diagnostic.Create(StateOutsideApply, w.Location, w.Member.Name, w.Method.Name));
            });
        });
    }
}
