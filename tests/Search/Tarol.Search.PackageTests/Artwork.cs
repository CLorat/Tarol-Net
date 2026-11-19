using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Search.PackageTests;

[Searchable("Artwork")]
public partial class Artwork : AggregateRoot<Artwork>
{
    public EntitySet<Edition> Editions { get; } = [];
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";

    public static Artwork Create(string title, string description)
    {
        var a = new Artwork(Id<Artwork>.New());
        a.Raise(new Created(title, description));
        return a;
    }

    public Edition AddEdition(string label) => Raise(new Edition.Added(label));

    public sealed record Created(string Title, string Description) : Event;

    private void On(Created e) => (Title, Description) = (e.Title, e.Description);
}

public partial class Edition : Entity<Artwork, Edition>
{
    [Searchable(SearchWeight.A)] public string Label { get; private set; } = "";
    public sealed record Added(string Label) : Created;
    private void On(Added e) => Label = e.Label;
}
