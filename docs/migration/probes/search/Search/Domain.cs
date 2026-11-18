using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Search;

/// <summary>
/// The write side of the feeder prototype. What a user would mark (the probe hand-writes what a generator would emit, see
/// <see cref="ArtworkSearch"/>): Title [SearchTitle, Searchable(Weight A)], Description [Searchable(B)], the owner's name
/// [PersonalData] (subject = the stream) searchable only as an exact blind token; an edition is found by its own label and
/// shown through its artwork.
/// </summary>
public partial class Artwork : AggregateRoot<Artwork>
{
    public EntitySet<Edition> Editions { get; } = [];
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    [PersonalData] public string? Owner { get; private set; }

    public static Artwork Create(string title, string description, string owner, DateTimeOffset now)
    {
        var a = new Artwork(Id<Artwork>.New(now));
        a.Raise(new Created(title, description, owner));
        return a;
    }

    public Success Rename(string title) => Raise(new Renamed(title));

    public Edition AddEdition(string label) => Raise(new Edition.Added(label));

    public sealed record Created(string Title, string Description, [property: PersonalData] string? Owner) : Event;
    public sealed record Renamed(string Title) : Event;

    private void On(Created e) => (Title, Description, Owner) = (e.Title, e.Description, e.Owner);
    private void On(Renamed e) => Title = e.Title;
}

public partial class Edition : Entity<Artwork, Edition>
{
    public string Label { get; private set; } = "";
    public sealed record Added(string Label) : Created;
    private void On(Added e) => Label = e.Label;
}

/// <summary>A stream type no search map covers: the feeder skips it.</summary>
public partial class Note : AggregateRoot<Note>
{
    public string Text { get; private set; } = "";
    public static Note Write(string text, DateTimeOffset now) { var n = new Note(Id<Note>.New(now)); n.Raise(new Written(text)); return n; }
    public sealed record Written(string Text) : Event;
    private void On(Written e) => Text = e.Text;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Artwork.Created))]
[JsonSerializable(typeof(Artwork.Renamed))]
[JsonSerializable(typeof(Edition.Added))]
[JsonSerializable(typeof(Note.Written))]
[JsonSerializable(typeof(string))]
internal sealed partial class ProbeJson : JsonSerializerContext;
