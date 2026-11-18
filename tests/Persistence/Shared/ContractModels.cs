using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Contract;

/// <summary>A stored-state aggregate with a child entity and a unique email (lookup "email").</summary>
public partial class Member : AggregateRoot<Member>
{
    public EntitySet<Badge> Badges { get; } = [];
    public string Email { get; private set; } = "";
    public int Points { get; private set; }

    public static Member Join(Id<Member> id, string email)
    {
        var member = new Member(id);
        member.Raise(new Joined(email));
        return member;
    }

    public Result Earn(int points) => points <= 0 ? Error.Conflict("Member.Points") : Raise(new Earned(points));

    public Success ChangeEmail(string email) => Raise(new EmailChanged(email));

    public Badge Award(string name) => Raise(new Badge.Awarded(name));

    public Success Leave(string reason) => Raise(new Left(reason));

    public sealed record Joined(string Email) : Event;
    public sealed record Earned(int Points) : Event;
    public sealed record EmailChanged(string Email) : Event;

    /// <summary>v1 had no reason (the integration tests upcast a stored v1 payload).</summary>
    [EventType("member.left", 2)]
    public sealed record Left(string Reason) : Event;

    private void On(Joined e) => Email = e.Email;
    private void On(Earned e) => Points += e.Points;
    private void On(EmailChanged e) => Email = e.Email;
    private void On(Left e) => LeftFor = e.Reason;

    public string? LeftFor { get; private set; }
}

/// <summary>A child of <see cref="Member"/>; its name is personal data, so a store with keys seals the member (step 16).</summary>
public partial class Badge : Entity<Member, Badge>
{
    [PersonalData] public string? Name { get; private set; }

    public sealed record Awarded([property: PersonalData] string? Name) : Created;

    private void On(Awarded e) => Name = e.Name;
}

/// <summary>An aggregate without stored state.</summary>
[LoadByReplay]
public partial class Tally : AggregateRoot<Tally>
{
    public int Count { get; private set; }

    public static Tally Start(Id<Tally> id) { var t = new Tally(id); t.Raise(new Started()); return t; }

    public Success Bump() => Raise(new Bumped());

    public sealed record Started : Event;
    public sealed record Bumped : Event;

    private void On(Started e) => Count = 0;
    private void On(Bumped e) => Count++;
}

/// <summary>JSON metadata of the contract models' events and state.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Member.Joined))]
[JsonSerializable(typeof(Member.Earned))]
[JsonSerializable(typeof(Member.EmailChanged))]
[JsonSerializable(typeof(Member.Left))]
[JsonSerializable(typeof(Badge.Awarded))]
[JsonSerializable(typeof(Tally.Started))]
[JsonSerializable(typeof(Tally.Bumped))]
public partial class ContractJson : JsonSerializerContext;
