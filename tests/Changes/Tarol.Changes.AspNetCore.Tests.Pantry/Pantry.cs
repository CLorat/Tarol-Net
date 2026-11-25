using Tarol.Kernel;
using Tarol.Validations;

namespace Tarol.Changes.AspNetCore.Tests.Pantry;

/// <summary>A label of 1 to 120 characters; the garden has its own <c>Label</c> of 1 to 80 (Tatalu FB-INT-1).</summary>
[OpenApiSchemaName("PantryLabel")]
public readonly partial record struct Label([Length(1, 120)] string Value);

/// <summary>A postal code: a value object of an assembly without endpoints (Tatalu FB-F01.1-3, FB-INT-3).</summary>
public readonly partial record struct PostalCode([Pattern(@"^\d{5}$")] string Value);

/// <summary>A shade; the garden has its own <c>Shade</c> with other values (Tatalu FB-INT2-1).</summary>
[OpenApiSchemaName("PantryShade")]
public enum Shade
{
    Light,
    Dark,
}
