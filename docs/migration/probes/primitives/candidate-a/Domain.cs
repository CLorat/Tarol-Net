using Tarol.Primitives;
using Tarol.StronglyTypedId;
namespace Eval.A;
public partial class Gallery : AggregateRoot<Gallery>
{
    public EntitySet<Room> Rooms { get; } = new();
    public string Name { get; private set; } = "";
    public List<string> Tags { get; private set; } = [];
    public static Gallery Open(string name) { var g = new Gallery(Id<Gallery>.New()); g.Raise(new Opened(name)); return g; }
    public Room AddRoom(string title) => Raise(new Room.Added(title));
    public void CloseRoom(Id<Room> id) => Raise(new Room.Closed(id));
    public void Tag(string tag) => Raise(new Tagged(tag));
    public sealed record Opened(string Name) : Event;
    public sealed record Tagged(string Tag) : Event;
    private void On(Opened e) => Name = e.Name;
    private void On(Tagged e) => Tags = [.. Tags, e.Tag];
}
public partial class Room : Entity<Gallery, Room>
{
    public EntitySet<Artwork> Artworks { get; } = new();
    public string Title { get; private set; } = "";
    public decimal TotalValue { get; private set; }
    public Artwork Hang(string name) => Raise(new Artwork.Hung(name));
    public sealed record Added(string Title) : Created;
    public sealed record Closed(Id<Room> RoomId) : Deleted(RoomId);
    private void On(Added e) => Title = e.Title;
    private void On(Artwork.Repriced e) => TotalValue = Artworks.Sum(a => a.Price);
}
public partial class Artwork : Entity<Gallery, Artwork>
{
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }
    public void Reprice(decimal price) => Raise(new Repriced(price));
    public void RepriceDirect(decimal price) => Price = price;   // bypass probe: compiles, no event
    public sealed record Hung(string Name) : Created;
    public sealed record Repriced(decimal Price) : Event;
    private void On(Hung e) => Name = e.Name;
    private void On(Repriced e) => Price = e.Price;
}
