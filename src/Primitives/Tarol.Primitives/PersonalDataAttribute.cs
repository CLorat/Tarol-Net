namespace Tarol.Primitives;

/// <summary>
/// Marks a member of an event, of an aggregate's or entity's state, or of a projection row as personal data: it leaves the
/// process sealed with its subject's key (AES-256-GCM), and it reads as <see langword="null"/> once the subject is erased.
/// The member must be nullable (TPRM030). Its whole JSON value is sealed, whatever its type.
/// <code>
/// public sealed record Registered([property: PersonalData] string? Email, string Country) : Event;           // subject: the stream id
/// public sealed record Placed(Guid Customer, [property: PersonalData(Subject = nameof(Customer))] string? ShipTo) : Event;
/// [PersonalData] public string? Email { get; private set; }                                                // in the state
/// </code>
/// </summary>
/// <remarks>
/// The subject is the stream id (the aggregate is its own subject) unless <see cref="Subject"/> names a <see cref="Guid"/> or
/// <c>Id&lt;T&gt;</c> member of the same type (TPRM031). On an event with a primary constructor, target the property
/// (<c>[property: PersonalData]</c>); a plain <c>[PersonalData]</c> on the parameter is read as the same.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = false)]
public sealed class PersonalDataAttribute : Attribute
{
    /// <summary>
    /// The name of a <see cref="Guid"/> or <c>Id&lt;T&gt;</c> member of the same type whose value is the subject (use
    /// <c>nameof</c>); <see langword="null"/> = the stream id.
    /// </summary>
    public string? Subject { get; init; }
}
