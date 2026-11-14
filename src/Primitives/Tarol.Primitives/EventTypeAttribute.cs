namespace Tarol.Primitives;

/// <summary>
/// Pins the stored alias and schema version of an event. Without it the alias is
/// <c>&lt;aggregate&gt;.&lt;containing types&gt;.&lt;event&gt;</c> in snake case (<c>gallery.artwork.hung</c>) and the version is 1.
/// Pin the alias before you rename an event that is already stored.
/// </summary>
/// <param name="alias">The alias; unique within the aggregate (TPRM010).</param>
/// <param name="version">The schema version of the payload.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventTypeAttribute(string alias, int version = 1) : Attribute
{
    /// <summary>The alias.</summary>
    public string Alias { get; } = alias;

    /// <summary>The schema version.</summary>
    public int Version { get; } = version;
}
