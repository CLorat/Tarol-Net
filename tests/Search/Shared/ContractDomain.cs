using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Search.Contract;

/// <summary>The contract suite's domain: an artwork with editions (entities), a sealed owner, an audience, a language.</summary>
[Searchable("Artwork")]
public partial class Artwork : AggregateRoot<Artwork>
{
    public EntitySet<Edition> Editions { get; } = [];
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";
    [PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; }
    [SearchAudience] public string? Seller { get; private set; }
    [SearchLanguage] public string Language { get; private set; } = "en";

    public static Artwork Create(string title, string description, string? owner = null, string? seller = null, string language = "en")
    {
        var a = new Artwork(Id<Artwork>.New());
        a.Raise(new Created(title, description, owner, seller, language));
        return a;
    }

    public Success Rename(string title) => Raise(new Renamed(title));

    public Edition AddEdition(string label) => Raise(new Edition.Added(label));

    public sealed record Created(string Title, string Description, [property: PersonalData] string? Owner, string? Seller, string Language) : Event;
    public sealed record Renamed(string Title) : Event;

    private void On(Created e) => (Title, Description, Owner, Seller, Language) = (e.Title, e.Description, e.Owner, e.Seller, e.Language);
    private void On(Renamed e) => Title = e.Title;
}

public partial class Edition : Entity<Artwork, Edition>
{
    [Searchable(SearchWeight.A)] public string Label { get; private set; } = "";
    public sealed record Added(string Label) : Created;
    private void On(Added e) => Label = e.Label;
}

/// <summary>A second searchable type (for the type filter and counts).</summary>
[Searchable("Artist")]
public partial class Artist : AggregateRoot<Artist>
{
    [SearchTitle, Searchable(SearchWeight.A)] public string Name { get; private set; } = "";
    [Searchable] public string Bio { get; private set; } = "";

    public static Artist Create(string name, string bio)
    {
        var a = new Artist(Id<Artist>.New());
        a.Raise(new Registered(name, bio));
        return a;
    }

    public sealed record Registered(string Name, string Bio) : Event;
    private void On(Registered e) => (Name, Bio) = (e.Name, e.Bio);
}

/// <summary>A stream type no search covers.</summary>
public partial class Note : AggregateRoot<Note>
{
    public string Text { get; private set; } = "";
    public static Note Write(string text) { var n = new Note(Id<Note>.New()); n.Raise(new Written(text)); return n; }
    public sealed record Written(string Text) : Event;
    private void On(Written e) => Text = e.Text;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Artwork.Created))]
[JsonSerializable(typeof(Artwork.Renamed))]
[JsonSerializable(typeof(Edition.Added))]
[JsonSerializable(typeof(Artist.Registered))]
[JsonSerializable(typeof(Note.Written))]
[JsonSerializable(typeof(string))]
public sealed partial class ContractJson : JsonSerializerContext
{
    public static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(Default.Options)).Add<Artwork>().Add<Artist>().Add<Note>();
}
