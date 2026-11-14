using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Galleries;

/// <summary>Root → Room → Artwork, an ancestor handler (Room.TotalValue) and invariants on the root and an entity.</summary>
public partial class Gallery : AggregateRoot<Gallery>
{
    public EntitySet<Room> Rooms { get; } = [];
    public string Name { get; private set; } = "";
    public List<string> Tags { get; private set; } = [];
    public int ArtworkCount { get; private set; }

    public static Gallery Open(string name)
    {
        var gallery = new Gallery(Id<Gallery>.New());
        gallery.Raise(new Opened(name));
        return gallery;
    }

    public Room AddRoom(string title) => Raise(new Room.Added(title));

    public Room AddRoom(string title, Id<Room> id) => Raise(new Room.Added(title), id);

    public Result Tag(string tag) => Tags.Contains(tag) ? Error.Conflict("Gallery.AlreadyTagged") : Raise(new Tagged(tag));

    public Result CloseRoom(Id<Room> id) => Rooms.TryGet(id, out var room) ? room.Close() : Error.NotFound("Room.NotFound");

    public sealed record Opened(string Name) : Event;
    public sealed record Tagged(string Tag) : Event;

    private void On(Opened e) => Name = e.Name;
    private void On(Tagged e) => Tags = [.. Tags, e.Tag];
    private void On(Artwork.Hung e) => ArtworkCount++;

    [Invariant]
    private Result AtMostTenTags() => Tags.Count <= 10 ? Result.Success() : Error.Conflict("Gallery.TooManyTags");
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
    private void On(Artwork.Repriced e) => TotalValue = Artworks.Sum(static a => a.Price);
}

public partial class Artwork : Entity<Gallery, Artwork>
{
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }

    public Success Reprice(decimal price) => Raise(new Repriced(price));

    public Success Remove() => Raise(new Removed());

    public sealed record Hung(string Name) : Created;
    public sealed record Repriced(decimal Price) : Event;
    public sealed record Removed : Deleted;

    private void On(Hung e) => Name = e.Name;
    private void On(Repriced e) => Price = e.Price;

    [Invariant]
    private Result PriceNotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Artwork.NegativePrice");
}

/// <summary>A top-level event that targets an entity, with a pinned alias.</summary>
[EventType("gallery.artwork.renamed", 2)]
public sealed record ArtworkRenamed(string Name) : Artwork.Event;

public partial class Artwork
{
    public Success Rename(string name) => Raise(new ArtworkRenamed(name));

    private void On(ArtworkRenamed e) => Name = e.Name;
}
