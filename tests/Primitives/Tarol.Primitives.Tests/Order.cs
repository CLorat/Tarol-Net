using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Orders;

/// <summary>A single child (EntityRef), a child with two owner types (CreatedIn), and a user-declared entity constructor.</summary>
public partial class Order : AggregateRoot<Order>
{
    public EntitySet<Line> Lines { get; } = [];
    public EntityRef<Shipping> Shipping { get; } = new();
    public EntitySet<Note> Notes { get; } = [];
    public bool Placed { get; private set; }

    public static Order Place()
    {
        var order = new Order(Id<Order>.New());
        order.Raise(new OrderPlaced());
        return order;
    }

    public Line Add(string sku, int quantity) => Raise(new Line.Added(sku, quantity));

    public Shipping ShipTo(string address) => Raise(new Shipping.Assigned(address));

    public Note Annotate(string text) => Raise(new Note.AddedToOrder(text));

    public sealed record OrderPlaced : Event;

    private void On(OrderPlaced e) => Placed = true;
}

public partial class Line : Entity<Order, Line>
{
    // a user-declared (Id, Root) constructor: the generator calls it, so it may be private
    private Line(Id<Line> id, Order root) : base(id, root) => ConstructedByUser = true;

    public bool ConstructedByUser { get; }
    public EntitySet<Note> Notes { get; } = [];
    public string Sku { get; private set; } = "";
    public int Quantity { get; private set; }

    public Note Annotate(string text) => Raise(new Note.AddedToLine(text));

    public Success Remove() => Raise(new Removed());

    public sealed record Added(string Sku, int Quantity) : Created;
    public sealed record Removed : Deleted;

    private void On(Added e) => (Sku, Quantity) = (e.Sku, e.Quantity);
}

public partial class Shipping : Entity<Order, Shipping>
{
    public string Address { get; private set; } = "";

    public Success Cancel() => Raise(new Cancelled());

    public sealed record Assigned(string Address) : Created;
    public sealed record Cancelled : Deleted;

    private void On(Assigned e) => Address = e.Address;
}

public partial class Note : Entity<Order, Note>
{
    public string Text { get; private set; } = "";

    public Success Delete() => Raise(new Deleted());

    public sealed record AddedToOrder(string Text) : CreatedIn<Order>;
    public sealed record AddedToLine(string Text) : CreatedIn<Line>;
    public new sealed record Deleted : Entity<Order, Note>.Deleted;

    private void On(AddedToOrder e) => Text = e.Text;
    private void On(AddedToLine e) => Text = e.Text;
}
