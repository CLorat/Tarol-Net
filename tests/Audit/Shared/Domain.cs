using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Audit.Tests;

// A day-care's child, reduced to what the change log needs: a personal name, a group, a device time, an entity, a removal.

public partial class Child : AggregateRoot<Child>
{
    public EntitySet<Allergy> Allergies { get; } = [];
    public string? FirstName { get; private set; }
    public string Group { get; private set; } = "";
    public bool Gone { get; private set; }
    public int Looks { get; private set; }

    public static Child Register(Id<Child> id, string firstName, string group)
    {
        var child = new Child(id);
        child.Raise(new Registered(firstName, group, "sum"));
        return child;
    }

    public Result Rename(string firstName) => Raise(new Renamed(firstName));

    public Result Move(string group, DateTimeOffset at) => Raise(new Moved(group, at));

    public Result Look() => Raise(new Looked());

    public Allergy Note(string substance) => Raise(new Allergy.Noted(substance));

    public Result Leave() => Raise(new Left());

    public sealed record Registered([property: PersonalData] string? FirstName, string Group,
        [property: NotAudited(Reason = "a checksum, no fact")] string Checksum) : Event;

    public sealed record Renamed([property: PersonalData] string? FirstName) : Event;

    public sealed record Moved(string Group, [property: AuditTime] DateTimeOffset At) : Event;

    [NotAudited(Reason = "a heartbeat")]
    public sealed record Looked : Event;

    [AuditRemoved]
    public sealed record Left : Event;

    private void On(Registered e) => (FirstName, Group) = (e.FirstName, e.Group);

    private void On(Renamed e) => FirstName = e.FirstName;

    private void On(Moved e) => Group = e.Group;

    private void On(Looked e) => Looks++;

    private void On(Left e) => Gone = true;
}

public partial class Allergy : Entity<Child, Allergy>
{
    public string Substance { get; private set; } = "";

    public Success Drop() => Raise(new Dropped());

    public sealed record Noted(string Substance) : Created;

    public sealed record Dropped : Deleted;

    private void On(Noted e) => Substance = e.Substance;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Child.Registered))]
[JsonSerializable(typeof(Child.Renamed))]
[JsonSerializable(typeof(Child.Moved))]
[JsonSerializable(typeof(Child.Looked))]
[JsonSerializable(typeof(Child.Left))]
[JsonSerializable(typeof(Allergy.Noted))]
[JsonSerializable(typeof(Allergy.Dropped))]
public partial class AuditJson : JsonSerializerContext;
