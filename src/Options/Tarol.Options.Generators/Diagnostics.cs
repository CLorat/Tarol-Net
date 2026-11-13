using Microsoft.CodeAnalysis;

namespace Tarol.Options.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Options";

    /// <summary>TOPT001: the union protocol's object Value is read in user code (was TRES003 for Option).</summary>
    public static readonly DiagnosticDescriptor ObjectValue = new(
        id: "TOPT001",
        title: "Value of an Option is the untyped union member",
        messageFormat: "'{0}.Value' is the union protocol member: it is typed object and boxes value types. Use a pattern ('o is T value'), TryGetValue or GetValueOrThrow().",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "C# requires a union type to expose 'object? Value'. It is not the typed value: None reads as a boxed None, and an int is boxed.");

    /// <summary>TOPT002: a null or property pattern tests the Option struct itself, not its value (was TRES004).</summary>
    public static readonly DiagnosticDescriptor NullTest = new(
        id: "TOPT002",
        title: "Null test on an Option is constant",
        messageFormat: "'{0}' tests the {1} itself, which is never null, so it is always {2}. Use 'is T value', 'is None', IsSome or IsNone.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An Option<T> is a struct whose absent case is None, not null. 'o is { } x', 'o is not null' and 'o != null' are always true (x is the Option, not the value); 'o is null' and 'o == null' are always false. Code written for T? keeps compiling after a change to Option<T>, but no longer checks anything.");

    /// <summary>TOPT003: TryGetValue is called as a statement, so its result is ignored (was TRES005 for Option).</summary>
    public static readonly DiagnosticDescriptor IgnoredTryGetValue = new(
        id: "TOPT003",
        title: "TryGetValue result is ignored",
        messageFormat: "The result of '{0}.TryGetValue' is ignored, so the out value may be default ({1}). Test it ('if (o.TryGetValue(out var v))') or use a pattern.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TryGetValue sets the out value to default when the option is None. For a value type such as int that is 0, with no nullable warning.");

    /// <summary>TOPT004: a default literal of T is converted to an Option, so it becomes Some (was TRES006 for Option).</summary>
    public static readonly DiagnosticDescriptor DefaultBecomesSome = new(
        id: "TOPT004",
        title: "'default' becomes a present value, not None",
        messageFormat: "This 'default' is default({0}) and converts to {1}, a present value (Some). Write Option.None instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "In 'found ? value : default' and 'value ?? default' the conditional has the natural type T, so 'default' is default(T) (0 for an int) and is then wrapped: Some(0). For reference types it throws ArgumentNullException. 'found ? value : Option.None' is target-typed and correct.");
}
