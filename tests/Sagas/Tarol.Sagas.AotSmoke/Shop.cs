using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Sagas.AotSmoke;

public partial class Order : AggregateRoot<Order>
{
    public bool Shipped { get; private set; }
    public bool Cancelled { get; private set; }

    public static Order Place(DateTimeOffset now)
    {
        var order = new Order(Id<Order>.New(now));
        order.Raise(new Placed(now.AddHours(1)));
        return order;
    }

    public Result Ship() => Cancelled || Shipped ? Result.Success() : Raise(new WasShipped());
    public Result CancelIfUnshipped(DateTimeOffset now) => Shipped || Cancelled ? Result.Success() : Raise(new WasCancelled());

    public sealed record Placed(DateTimeOffset ShipBy) : Event;
    public sealed record WasShipped : Event;
    public sealed record WasCancelled : Event;
    private void On(Placed e) => Shipped = false;
    private void On(WasShipped e) => Shipped = true;
    private void On(WasCancelled e) => Cancelled = true;
}

/// <summary>A reaction: placing an order ships it.</summary>
public sealed partial record ShipOrder(Id<Order> Order) : IChange<Order>
{
    [OnEvent<Order.Placed>]
    public static ShipOrder From(Order.Placed e, RecordedEvent recorded) => new((Id<Order>)recorded.StreamId);

    public Result Execute(Order order, ChangeContext context) => order.Ship();
}

/// <summary>A scheduled reaction: an hour later, cancel what did not ship.</summary>
public sealed partial record CancelUnshipped(Id<Order> Order) : IChange<Order>
{
    [OnEvent<Order.Placed>(DueAt = nameof(AotSmoke.Order.Placed.ShipBy))]
    public static CancelUnshipped From(Order.Placed e, RecordedEvent recorded) => new((Id<Order>)recorded.StreamId);

    public Result Execute(Order order, ChangeContext context) => order.CancelIfUnshipped(context.Now);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Order.Placed))]
[JsonSerializable(typeof(Order.WasShipped))]
[JsonSerializable(typeof(Order.WasCancelled))]
internal sealed partial class SmokeJson : JsonSerializerContext;
