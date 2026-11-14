// Candidate c: user code (what a Tarol user writes).
using C.Core;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace C.Domain;

[AttributeUsage(AttributeTargets.Method)] public sealed class InvariantAttribute : Attribute;

public partial class Gallery : AggregateRoot<Gallery>
{
    public EntitySet<Room> Rooms { get; } = [];
    public string Name { get; private set; } = "";
    public List<string> Tags { get; private set; } = [];

    public static Gallery Open(string name)
    {
        var gallery = new Gallery(Id<Gallery>.New());
        gallery.Raise(new Opened(name));
        return gallery;
    }

    public Room AddRoom(string title) => Raise(new Room.Added(title));
    public Result Tag(string tag) => Tags.Contains(tag) ? Error.Conflict("Gallery.AlreadyTagged") : Raise(new Tagged(tag));

    public sealed record Opened(string Name) : Event;
    public sealed record Tagged(string Tag) : Event;

    private void On(Opened e) => Name = e.Name;
    private void On(Tagged e) => Tags = [.. Tags, e.Tag];
}

public partial class Room : Entity<Gallery, Room>
{
    public EntitySet<Artwork> Artworks { get; } = [];
    public string Title { get; private set; } = "";
    public decimal TotalValue { get; private set; }

    public Artwork Hang(string name) => Raise(new Artwork.Hung(name));
    public Success Close() => Raise(new Closed());

    public sealed record Added(string Title) : Created;
    public sealed record Closed : Deleted;

    private void On(Added e) => Title = e.Title;
    private void On(Artwork.Repriced e) => TotalValue = Artworks.Sum(a => a.Price);   // ancestor handler
}

public partial class Artwork : Entity<Gallery, Artwork>
{
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }

    public Success Reprice(decimal price) => Raise(new Repriced(price));

    public sealed record Hung(string Name) : Created;
    public sealed record Repriced(decimal Price) : Event;

    private void On(Hung e) => Name = e.Name;
    private void On(Repriced e) => Price = e.Price;

    [Invariant] private Result PriceNotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Artwork.NegativePrice");
}
