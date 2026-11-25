namespace Tarol.Kernel;

/// <summary>
/// The name of a contract type's schema in the OpenAPI document (and so of its type in a generated client), instead of its
/// plain type name. Two different types of one document may not share a schema name: the document fails with an
/// <see cref="InvalidOperationException"/> naming both, and this attribute on one of them resolves it (Tatalu FB-INT-1).
/// </summary>
/// <param name="name">The schema name: a valid identifier, unique in the document (e.g. <c>"ChildLabel"</c>).</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class OpenApiSchemaNameAttribute(string name) : Attribute
{
    /// <summary>The schema name.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// A type the client needs although no operation references it (an enum a domain function returns, Tatalu FB-F02.6-2): the
/// generated <c>AddTarolEndpoints</c> of its assembly puts its schema into the OpenAPI document's components and its name into
/// <c>x-tarol-types</c>, so <c>tarol-codegen</c> exports it. It must be in the application's JSON contexts.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class ContractTypeAttribute : Attribute;

/// <summary>
/// A constant the client needs (the current version of a document, a limit, Tatalu FB-F13.5-1): the generated
/// <c>AddTarolEndpoints</c> writes it into <c>x-tarol-constants</c> (name → value) and <c>tarol-codegen</c> exports it as
/// <c>export const Name = value as const</c>. On a public <c>const</c> field of type string, bool or a number.
/// </summary>
/// <param name="name">The exported name; <see langword="null"/>: the containing type's name and the field's (<c>PrivacyDocumentsAgreementVersion</c>).</param>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ContractConstantAttribute(string? name = null) : Attribute
{
    /// <summary>The exported name, or <see langword="null"/> for the type's and the field's name.</summary>
    public string? Name { get; } = name;
}
