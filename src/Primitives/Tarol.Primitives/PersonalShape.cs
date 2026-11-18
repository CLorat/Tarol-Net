namespace Tarol.Primitives;

/// <summary>
/// One <c>[PersonalData]</c> member of a stored JSON object, as the generator emits it.
/// </summary>
/// <param name="Name">The C# member name.</param>
/// <param name="JsonName">The pinned JSON name (<c>[JsonPropertyName]</c> on an event, the state name in a state);
/// <see langword="null"/> = <paramref name="Name"/> through the JSON options' naming policy.</param>
/// <param name="Subject">The C# name of the member that holds the subject (a <see cref="Guid"/> or <c>Id&lt;T&gt;</c> of the same
/// object); <see langword="null"/> = the stream id.</param>
/// <param name="SubjectJsonName">The pinned JSON name of <paramref name="Subject"/>; <see langword="null"/> = through the naming policy.</param>
public sealed record PersonalMember(string Name, string? JsonName = null, string? Subject = null, string? SubjectJsonName = null);

/// <summary>A child object (an <c>EntityRef</c>) or array of objects (an <c>EntitySet</c>) of a stored state that holds personal members.</summary>
/// <param name="JsonName">The child member's JSON name in the state.</param>
/// <param name="Shape">The child's personal members.</param>
public sealed record PersonalChild(string JsonName, PersonalShape Shape);

/// <summary>
/// Which members of a stored JSON object are <c>[PersonalData]</c>: an event's payload, or an aggregate's state with its
/// entity children. The generator emits one per event that has personal members (<see cref="EventCatalogEntry.Personal"/>) and
/// one per aggregate whose state has them (<see cref="EventCatalog.State"/>); the event codec seals and opens by it.
/// </summary>
public sealed class PersonalShape
{
    /// <summary>Creates a shape.</summary>
    /// <param name="members">The personal members of the object itself.</param>
    /// <param name="children">The children holding personal members (state only).</param>
    public PersonalShape(PersonalMember[] members, params PersonalChild[] children)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(children);
        Members = members;
        Children = children;
    }

    /// <summary>The personal members of the object itself.</summary>
    public IReadOnlyList<PersonalMember> Members { get; }

    /// <summary>The children holding personal members (an aggregate's entities).</summary>
    public IReadOnlyList<PersonalChild> Children { get; }
}
