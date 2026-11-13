using Microsoft.CodeAnalysis;

namespace Tarol.Results.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Results";

    /// <summary>TRES001: Result&lt;T&gt; whose T makes the union cases ambiguous.</summary>
    public static readonly DiagnosticDescriptor AmbiguousValueType = new(
        id: "TRES001",
        title: "Result value type is ambiguous",
        messageFormat: "Result<{0}> is ambiguous: {1}. Wrap the value in a type of its own.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A Result<T> is a union of T and Violation. When T is a Violation, object or another result, a value and a failure cannot be told apart: new(violation) silently becomes a failure, an object holding a Violation silently becomes a success, and implicit conversions are ambiguous (CS0457).");

    /// <summary>TRES002: a result is computed and thrown away.</summary>
    public static readonly DiagnosticDescriptor DiscardedResult = new(
        id: "TRES002",
        title: "Result is discarded",
        messageFormat: "The {0} returned by '{1}' is discarded, so a failure goes unnoticed. Handle it, return it, or discard it explicitly with '_ = …'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Results replace exceptions; ignoring one is the equivalent of an empty catch block.");

    /// <summary>TRES003: the union protocol's object Value is read in user code.</summary>
    public static readonly DiagnosticDescriptor ObjectValue = new(
        id: "TRES003",
        title: "Value of a Result is the untyped union member",
        messageFormat: "'{0}.Value' is the union protocol member: it is typed object and boxes value types. Use a pattern ('x is T value'), TryGetValue or GetValueOrThrow().",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "C# requires a union type to expose 'object? Value'. It is not the typed value Tarol.Results 0.x returned.");

    /// <summary>TRES005: TryGetValue is called as a statement, so its result is ignored.</summary>
    public static readonly DiagnosticDescriptor IgnoredTryGetValue = new(
        id: "TRES005",
        title: "TryGetValue result is ignored",
        messageFormat: "The result of '{0}.TryGetValue' is ignored, so the out value may be default ({1}). Test it ('if (x.TryGetValue(out var v))') or use a pattern.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Result.TryGetValue sets the out value to default when the case is not active. For a value type such as int that is 0, with no nullable warning.");

    /// <summary>TRES006: a default literal of T is converted to a Result.</summary>
    public static readonly DiagnosticDescriptor DefaultBecomesValue = new(
        id: "TRES006",
        title: "'default' becomes a success, not a failure",
        messageFormat: "This 'default' is default({0}) and converts to {1}, a success. Return a Violation instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "In 'found ? value : default' and 'value ?? default' the conditional has the natural type T, so 'default' is default(T) (0 for an int) and is then wrapped: Success(0). For reference types it throws ArgumentNullException. 'found ? value : Error.NotFound(...)' is target-typed and correct. The Option counterpart is TOPT004 in Tarol.Options.");
}
