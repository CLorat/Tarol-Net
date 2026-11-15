using Microsoft.CodeAnalysis;

namespace Tarol.Messaging.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Messaging";

    /// <summary>TMSG001: a [Consumes] factory that cannot be an entry.</summary>
    public static readonly DiagnosticDescriptor BadFactory = new(
        "TMSG001", "Consumer factory is not a static From(payload)",
        "[Consumes(\"{0}\", {1})] on '{2}': {3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Consumes] goes on a public static method of a change record (IChange<T> or ICreation<T>) that takes the contract record, or the record and the Message, and returns the change.");

    /// <summary>TMSG002: two factories for one (consumer, contract, version).</summary>
    public static readonly DiagnosticDescriptor Duplicate = new(
        "TMSG002", "Two factories read the same contract version",
        "The consumer '{0}' has two factories for {1} v{2}: '{3}' and '{4}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Each consumer has one factory per contract version; a new version gets a new factory.");

    /// <summary>TMSG003: a contract name or version that is not valid.</summary>
    public static readonly DiagnosticDescriptor BadContract = new(
        "TMSG003", "Contract is not <system>.<name> with a version of 1 or more",
        "[Consumes(\"{0}\", {1})] on '{2}': {3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A contract is prefixed by the system that owns it (lowercase letters, digits and '-', then a dot), e.g. \"billing.invoice-paid\"; the prefix is the source a consumer trusts. Versions start at 1.");

    /// <summary>TMSG006: a contract record without JSON metadata.</summary>
    public static readonly DiagnosticDescriptor NoJson = new(
        "TMSG006", "Contract record is not in a JSON context",
        "'{0}' (the payload of {1} v{2}) is listed in no [JsonSerializable] of this compilation's JsonSerializerContext; the consumer will fail at runtime",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Consumers read the payload through the application's source-generated JSON context (Native AOT): add [JsonSerializable(typeof(TPayload))] to it. Reported only when the compilation declares a context.");

    /// <summary>TMSG007: consumer names that collide.</summary>
    public static readonly DiagnosticDescriptor Name = new(
        "TMSG007", "Consumer names collide",
        "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A consumer's name is its subscription and its key scope: every factory of one change has the same name, and two changes never share one. Pin it with Name = \"...\" before renaming a change.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TMSG001" => BadFactory,
        "TMSG002" => Duplicate,
        "TMSG003" => BadContract,
        "TMSG006" => NoJson,
        _ => Name,
    };
}
