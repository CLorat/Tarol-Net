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
