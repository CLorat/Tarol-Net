namespace Tarol.Primitives.Generators.Tests;

internal static class Domains
{
    public const string Gallery = """
        using System.Linq;
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;

        namespace Museum;

        public partial class Gallery : AggregateRoot<Gallery>
        {
            public EntitySet<Room> Rooms { get; } = [];
            public string Name { get; private set; } = "";
            public static Gallery Open(string name) { var g = new Gallery(Id<Gallery>.New()); g.Raise(new Opened(name)); return g; }
            public Room AddRoom(string title) => Raise(new Room.Added(title));
            public sealed record Opened(string Name) : Event;
            private void On(Opened e) => Name = e.Name;
            [Invariant] private Result HasName() => Name.Length > 0 ? Result.Success() : Error.Conflict("Gallery.NoName");
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
            public decimal Price { get; private set; }
            public Success Reprice(decimal price) => Raise(new Repriced(price));
            public sealed record Hung(string Name) : Created;
            public sealed record Repriced(decimal Price) : Event;
            private void On(Repriced e) => Price = e.Price;
            [Invariant] private Result NotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Artwork.Negative");
        }
        """;

    public const string Orders = """
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.StronglyTypedId;

        namespace Shop;

        public partial class Order : AggregateRoot<Order>
        {
            public EntitySet<Line> Lines { get; } = [];
            public EntityRef<Shipping> Shipping { get; } = new();
            private readonly EntitySet<Note> _notes = [];
            public Line Add(string sku) => Raise(new Line.Added(sku));
            public Shipping ShipTo(string address) => Raise(new Shipping.Assigned(address));
            public Note Annotate(string text) => Raise(new Note.OnOrder(text));
        }

        public partial class Line : Entity<Order, Line>
        {
            private Line(Id<Line> id, Order root) : base(id, root) { }
            public EntitySet<Note> Notes { get; } = [];
            public string Sku { get; private set; } = "";
            public sealed record Added(string Sku) : Created;
            [EventType("shop.order.line_removed", 2)] public sealed record Removed : Deleted;
            private void On(Added e) => Sku = e.Sku;
        }

        public partial class Shipping : Entity<Order, Shipping>
        {
            public string Address { get; private set; } = "";
            public sealed record Assigned(string Address) : Created;
            public sealed record Cancelled : Deleted;
            private void On(Assigned e) => Address = e.Address;
        }

        public partial class Note : Entity<Order, Note>
        {
            public sealed record OnOrder(string Text) : CreatedIn<Order>;
            public sealed record OnLine(string Text) : CreatedIn<Line>;
        }
        """;

    public const string Nested = """
        using Tarol.Primitives;

        namespace Outer.Space
        {
            public static partial class Domain
            {
                public partial class Station : AggregateRoot<Station>
                {
                    public EntitySet<Module> Modules { get; } = [];
                    public int Count { get; private set; }
                    private void On(Module.Docked e) => Count++;
                }

                public partial class Module : Entity<Station, Module>
                {
                    public sealed record Docked : Created;
                }
            }
        }

        namespace Elsewhere
        {
            public abstract record ModuleEvent : Outer.Space.Domain.Module.Event;
            public sealed record Repaired(int Hours) : ModuleEvent;
        }

        namespace Outer.Space
        {
            public static partial class Domain
            {
                public partial class Module
                {
                    public int Hours { get; private set; }
                    private void On(Elsewhere.ModuleEvent e) => Hours++;
                }
            }
        }
        """;
}
