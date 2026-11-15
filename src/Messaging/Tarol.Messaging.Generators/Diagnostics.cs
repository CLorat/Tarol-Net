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
        "{0} on '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A contract is prefixed by the system that owns it (lowercase letters, digits and '-', then a dot), e.g. \"billing.invoice-paid\"; the prefix is the source a consumer trusts. Versions start at 1.");

    /// <summary>TMSG004: a [Publishes&lt;T&gt;] method that cannot map.</summary>
    public static readonly DiagnosticDescriptor BadPublication = new(
        "TMSG004", "Publication is not a static mapping method",
        "[Publishes<{0}>(\"{1}\", {2})] on '{3}': {4}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Publishes<TEvent>] goes on a static (public or internal) method that takes the event, or the event and the RecordedEvent, and returns the contract record (a class); null publishes nothing.");

    /// <summary>TMSG005: publications that collide, or an assembly that publishes for two systems.</summary>
    public static readonly DiagnosticDescriptor PublicationCollision = new(
        "TMSG005", "Publications collide",
        "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "One method per (contract, version) in an assembly: it is the outbox row's name. An assembly publishes under its own system's prefix; two prefixes are reported as a warning (the consumers trust a prefix as its source).");

    /// <summary>TMSG006: a contract record without JSON metadata.</summary>
    public static readonly DiagnosticDescriptor NoJson = new(
        "TMSG006", "Contract record is not in a JSON context",
        "'{0}' (the record of {1} v{2}) is listed in no [JsonSerializable] of this compilation's JsonSerializerContext; it will fail at runtime",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Consumers read and publications write the contract record through the application's source-generated JSON context (Native AOT): add [JsonSerializable(typeof(TPayload))] to it. Reported only when the compilation declares a context.");

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
        "TMSG004" => BadPublication,
        "TMSG005" => PublicationCollision,
        "TMSG006" => NoJson,
        _ => Name,
    };
}
