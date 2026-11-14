using System.Collections.Concurrent;
using System.Collections.Generic;
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
///         <item>TPRM015: user code does not call <c>Dispatch</c>, <c>CheckInvariants</c>, the lifecycle hook (<c>ApplyLive</c>,
///         <c>ApplyRaised</c>, <c>ForbiddenStep</c>) or generated <c>__</c> members, and does not override <c>ApplyLive</c>;</item>
///         <item>TPRM027: an <c>On</c> handler does not fill a <c>[Transient]</c> member (it is not stored, so a load from the
///         stored state would lose it); resetting it to <c>null</c> or <c>default</c> is allowed;</item>
///         <item>TPRM022: a state of a <c>[Lifecycle]</c> enum used by a domain type is assigned by some <c>On</c> handler;</item>
///         <item>TPRM023: the <c>On</c> handler of an event with <c>[Transition]</c> assigns its <c>To</c>.</item>
///     </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.StateOutsideApply, Diagnostics.DecisionInApply, Diagnostics.PlumbingUsed, Diagnostics.TransientIsState,
            Diagnostics.NeverAssigned, Diagnostics.TransitionMismatch);

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
            // TPRM022/023: every assignment of a lifecycle enum to a member of a domain type; State is null for a computed value
            var states = new ConcurrentBag<(IMethodSymbol Method, INamedTypeSymbol Enum, string? State)>();
            var lifecycles = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var handlers = new ConcurrentBag<IMethodSymbol>();

            start.RegisterSymbolAction(ctx =>
            {
                switch (ctx.Symbol)
                {
                    case INamedTypeSymbol type when IsDomainType(type):
                        // backing fields are implicitly declared and get no symbol action of their own
                        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
                            if (!field.IsStatic && LifecycleDiscovery.IsLifecycle(field.Type) && field.Type.Locations.Any(static l => l.IsInSource))
                                lifecycles.TryAdd((INamedTypeSymbol)field.Type, 0);
                        break;
                    case IMethodSymbol { IsOverride: true, Name: "ApplyLive" } method when IsDomainType(method.ContainingType) && IsPlumbing(method):
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.PlumbingUsed, method.Locations.FirstOrDefault(), method.Name));
                        break;
                }
            }, SymbolKind.NamedType, SymbolKind.Method);

            start.RegisterOperationBlockStartAction(block =>
            {
                if (block.OwningSymbol is not IMethodSymbol method || !IsDomainType(method.ContainingType))
                    return;
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
                    return;
                var apply = Symbols.IsHandler(method);
                if (apply)
                    handlers.Add(method);

                block.RegisterOperationAction(ctx =>
                {
                    var target = ctx.Operation switch
                    {
                        IAssignmentOperation assignment => assignment.Target,
                        IIncrementOrDecrementOperation increment => increment.Target,
                        _ => null,
                    };
                    if (target is IMemberReferenceOperation { Instance: IInstanceReferenceOperation } && LifecycleDiscovery.IsLifecycle(target.Type))
                        states.Add((method, (INamedTypeSymbol)target.Type!, AssignedState(ctx.Operation, (INamedTypeSymbol)target.Type!)));
                    if (!apply && StateMember(target) is { } member)
                        writes.Add((method, member, ctx.Operation.Syntax.GetLocation()));
                    else if (apply && TransientMember(target) is { } transient && !IsReset(ctx.Operation))
                        ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.TransientIsState, ctx.Operation.Syntax.GetLocation(), transient.Name, method.Parameters[0].Type.Name));
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

                // assignments in On handlers and in the private helpers only they call
                var applied = states.Where(w => Symbols.IsHandler(w.Method) || OnlyFromApply(w.Method, 0)).ToList();

                // TPRM022: a declared state no On handler assigns (the initial state is exempt; a computed assignment disables it)
                foreach (var lifecycle in lifecycles.Keys)
                {
                    var model = LifecycleDiscovery.Model(lifecycle, end.CancellationToken);
                    var assigned = applied.Where(w => SymbolEqualityComparer.Default.Equals(w.Enum, lifecycle)).ToList();
                    if (!model.IsValid || assigned.Any(static w => w.State is null))
                        continue;
                    foreach (var state in model.States.Where(s => s != model.Initial && assigned.All(w => w.State != s)))
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.NeverAssigned,
                            lifecycle.GetMembers(state).FirstOrDefault()?.Locations.FirstOrDefault(), state, lifecycle.Name));
                }

                // TPRM023: an On handler that does not assign the To of its event's [Transition]
                foreach (var handler in handlers)
                {
                    if (!SymbolEqualityComparer.Default.Equals(TargetOf(handler.Parameters[0].Type), handler.ContainingType))
                        continue; // an owner's handler for a child's event applies its own state, not the child's step
                    var reached = Reached(handler);
                    foreach (var attribute in handler.Parameters[0].Type.GetAttributes())
                    {
                        if (attribute.AttributeClass is not { TypeArguments.Length: 1 } transition
                            || !Symbols.Is(transition.OriginalDefinition, "TransitionAttribute`1")
                            || transition.TypeArguments[0] is not INamedTypeSymbol lifecycle
                            || !LifecycleDiscovery.IsLifecycle(lifecycle)
                            || attribute.ConstructorArguments.Length != 2)
                            continue;
                        var from = StateName(lifecycle, attribute.ConstructorArguments[0].Value);
                        var to = StateName(lifecycle, attribute.ConstructorArguments[1].Value);
                        if (from is null || to is null)
                            continue; // TPRM021
                        var assigned = states.Where(w => reached.Contains(w.Method) && SymbolEqualityComparer.Default.Equals(w.Enum, lifecycle)).ToList();
                        if (assigned.Any(w => w.State is null || w.State == to))
                            continue;
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.TransitionMismatch, handler.Locations.FirstOrDefault(),
                            handler.Parameters[0].Type.Name, handler.ContainingType.Name, to, lifecycle.Name, from));
                    }
                }
            });

            // the handler and the methods of its type it calls, transitively
            HashSet<IMethodSymbol> Reached(IMethodSymbol handler)
            {
                var set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { handler.OriginalDefinition };
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var pair in callers)
                        if (!set.Contains(pair.Key) && pair.Value.Any(c => set.Contains(c.OriginalDefinition)))
                            changed |= set.Add(pair.Key);
                }

                return set;
            }

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

    /// <summary>The member name of a lifecycle enum constant assigned by a plain assignment, or <see langword="null"/> for a computed value.</summary>
    private static string? AssignedState(IOperation operation, INamedTypeSymbol lifecycle)
    {
        if (operation is not ISimpleAssignmentOperation { Value: var value })
            return null;
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;
        return value.ConstantValue.HasValue ? StateName(lifecycle, value.ConstantValue.Value) : null;
    }

    /// <summary>The root or entity an event targets.</summary>
    private static INamedTypeSymbol? TargetOf(ITypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (Symbols.LibraryEventBase(b) is { Kind: not EventKind.None } found)
                return found.Target;
        return null;
    }

    private static string? StateName(INamedTypeSymbol lifecycle, object? value)
        => value is null
            ? null
            : lifecycle.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value))?.Name;

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

    /// <summary>A <c>[Transient]</c> field or property of this instance (or the backing field of a <c>[Transient]</c> property).</summary>
    private static ISymbol? TransientMember(IOperation? target)
    {
        var member = target switch
        {
            IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation } f => (ISymbol)f.Field,
            IPropertyReferenceOperation { Property.IsStatic: false, Instance: IInstanceReferenceOperation } p => p.Property,
            _ => null,
        };
        if (member is null)
            return null;
        if (Symbols.HasAttribute(member, "TransientAttribute"))
            return member;
        return member is IFieldSymbol { AssociatedSymbol: { } associated } && Symbols.HasAttribute(associated, "TransientAttribute") ? associated : null;
    }

    /// <summary>A plain assignment of <c>null</c> or <c>default</c>: resetting a cache.</summary>
    private static bool IsReset(IOperation operation)
    {
        if (operation is not ISimpleAssignmentOperation { Value: var value })
            return false;
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;
        return value is IDefaultValueOperation || value is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
    }

    private static bool IsPlumbing(IMethodSymbol method)
    {
        if (method.Name.StartsWith("__", System.StringComparison.Ordinal) && IsDomainType(method.ContainingType))
            return true;
        if (method.Name is not ("Dispatch" or "CheckInvariants" or "ApplyLive" or "ApplyRaised" or "ForbiddenStep"))
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
