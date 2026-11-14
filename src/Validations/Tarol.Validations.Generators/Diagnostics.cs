using Microsoft.CodeAnalysis;

namespace Tarol.Validations.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Validations";

    /// <summary>TVAL001: a value object or an input (or a type containing it) is not partial.</summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        "TVAL001",
        "Validated type must be partial",
        "'{0}' must be declared partial so that its validation can be generated{1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The generator adds Create, Check and the JSON converter to a value object, and Validate to an IValidatable record, as a partial declaration. Every containing type must be partial too.");

    /// <summary>TVAL002: a declaration shape the generator does not support.</summary>
    public static readonly DiagnosticDescriptor Unsupported = new(
        "TVAL002",
        "Unsupported validated type",
        "'{0}' cannot be validated by the generator: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Generic value objects, file-local types and IValidatable records that inherit IValidatable from a base record are not supported. Write the rule by hand (a static Check method, or implement IValidatable.Validate).");

    /// <summary>TVAL003: a rule attribute in a place the generator never reads.</summary>
    public static readonly DiagnosticDescriptor WrongPlace = new(
        "TVAL003",
        "Validation rule has no effect here",
        "[{0}] has no effect on parameter '{1}' of '{2}': rules belong on primary-constructor parameters of a record (a value object or an IValidatable input)",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Rule attributes are read only from the primary constructor of a record or record struct. On a method, a class or struct primary constructor or a normal constructor they would be silently ignored.");

    /// <summary>TVAL004: a rule that does not apply to the parameter's type.</summary>
    public static readonly DiagnosticDescriptor NotApplicable = new(
        "TVAL004",
        "Validation rule does not apply to the type",
        "[{0}] cannot be applied to '{1}' of type '{2}': {3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Range applies to numbers, Length, Pattern and Email to strings, NotEmpty to strings, Guid and Id<T>, Count to arrays and IReadOnlyList<T>. A member whose type is a value object or an IValidatable input gets its rules from that type.");

    /// <summary>TVAL005: invalid rule arguments.</summary>
    public static readonly DiagnosticDescriptor InvalidArguments = new(
        "TVAL005",
        "Invalid validation rule arguments",
        "[{0}] on '{1}' has invalid arguments: {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>TVAL006: constructing a value object without its factory.</summary>
    public static readonly DiagnosticDescriptor BypassesCreate = new(
        "TVAL006",
        "Value object constructed without its rules",
        "'{0}' has validation rules; {1} skips them. Use {0}.Create(…), which returns a Result<{0}>.",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "The constructor of a value object trusts its input, because deserialization and replay must load old values that today's rules reject. Live code calls Create. Tests that build invalid fixtures on purpose may suppress this warning per file.");

    /// <summary>TVAL007: a DataAnnotations validation attribute on a value object or an input.</summary>
    public static readonly DiagnosticDescriptor DataAnnotations = new(
        "TVAL007",
        "DataAnnotations attribute has no effect",
        "[{0}] from System.ComponentModel.DataAnnotations has no effect on '{1}' of '{2}': Tarol does not read it. Use {3}.",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Tarol.Validations matches its own attributes by full name, so DataAnnotations attributes are ignored (the old generator turned [MaxLength] into an exact length, finding 3).");

    /// <summary>TVAL008: a member of an input whose type the generator cannot validate.</summary>
    public static readonly DiagnosticDescriptor MemberNotValidated = new(
        "TVAL008",
        "Input member is not validated",
        "Member '{0}' of '{1}' has type '{2}', which is neither a value object, an IValidatable input, an id, a primitive nor a supported collection; only null is checked",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Make the member type a value object (a record with rule attributes), an IValidatable record, or a supported collection (array or IReadOnlyList<T>) of those.");

    /// <summary>TVAL009: a same-assembly source-generated context used directly writes value objects in their object shape.</summary>
    public static readonly DiagnosticDescriptor ContextWithoutResolver = new(
        "TVAL009",
        "JSON context needs the value-object resolver",
        "'{0}.Default' serializes the value object '{1}' as an object ({{\"value\":…}}), because the JSON source generator cannot see generated converters in the same assembly. Use ValueObjectJson.Options({0}.Default).",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Source generators do not see each other's output, so a JsonSerializerContext in the assembly that declares a value object ignores its generated [JsonConverter]. ValueObjectJson.Options(context) puts the value-object resolver first. Contexts in another assembly, and options that already contain ValueObjectJson.Resolver, are not affected.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TVAL001" => NotPartial,
        "TVAL002" => Unsupported,
        "TVAL004" => NotApplicable,
        "TVAL005" => InvalidArguments,
        _ => MemberNotValidated
    };
}
