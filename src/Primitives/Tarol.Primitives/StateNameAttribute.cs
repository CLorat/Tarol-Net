namespace Tarol.Primitives;

/// <summary>
/// Pins the JSON name a field, an auto-property (its backing field) or a child collection is stored under in the
/// aggregate's state, so a C# rename keeps the stored shape. Without it the name is the property name (or the field name
/// without leading <c>_</c>) in camelCase. The name is used verbatim.
/// <code>[StateName("title")] public string Name { get; private set; } = "";</code>
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class StateNameAttribute : Attribute
{
    /// <summary>Pins the stored name.</summary>
    /// <param name="name">The JSON name, used verbatim.</param>
    public StateNameAttribute(string name) => Name = name;

    /// <summary>The JSON name.</summary>
    public string Name { get; }
}
