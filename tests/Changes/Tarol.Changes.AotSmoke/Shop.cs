using System.Text.Json.Serialization;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Changes.AotSmoke;

public readonly partial record struct Quantity([Range(1, 10)] int Value);

public partial class Order : AggregateRoot<Order>
{
    public string Number { get; private set; } = "";
    public int Items { get; private set; }
    public bool Closed { get; private set; }

    public static Result<Order> Open(string number, DateTimeOffset now)
    {
        var order = new Order(Id<Order>.New(now));
        order.Raise(new Opened(number));
        return order;
    }

    public Result Add(Quantity quantity) => Closed ? Error.Conflict("Order.Closed") : Raise(new Added(quantity.Value));
    public Result Close(DateTimeOffset now) => Closed ? Error.Conflict("Order.Closed") : Raise(new WasClosed(now));

    public sealed record Opened(string Number) : Event;
    public sealed record Added(int Items) : Event;
    public sealed record WasClosed(DateTimeOffset At) : Event;
    private void On(Opened e) => Number = e.Number;
    private void On(Added e) => Items += e.Items;
    private void On(WasClosed e) => Closed = true;
}

public partial class Customer : AggregateRoot<Customer>
{
    public bool Blocked { get; private set; }

    public static Customer Register(bool blocked)
    {
        var customer = new Customer(Id<Customer>.New());
        customer.Raise(new Registered(blocked));
        return customer;
    }

    public sealed record Registered(bool Blocked) : Event;
    private void On(Registered e) => Blocked = e.Blocked;
}

public sealed partial record OpenOrder(string Number) : ICreation<Order>
{
    public Result<Order> Execute(ChangeContext context) => Order.Open(Number, context.Now);
}

public sealed partial record AddItems(Id<Order> Order, Quantity Quantity, [Read] Id<Customer> Customer) : IChange<Order>, IValidatable
{
    public Result Execute(Order order, ChangeContext context)
        => context.Read<Customer>().Blocked ? Error.Conflict("Customer.Blocked") : order.Add(Quantity);
}

public sealed partial record CloseByNumber(string Number) : ILoads<Order>
{
    public async ValueTask<Result<Order>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Find<Order>("number", Number, context.Operation, cancellationToken)).TryGetValue(out Id<Order> id)
           && (await reader.Load(id, context.Operation, cancellationToken)).TryGetValue(out Order? order)
            ? order
            : Error.NotFound("Order.NotFound");

    public Result Execute(Order order, ChangeContext context) => order.Close(context.Now);
}

/// <summary>A reaction (step 9A): a closed order opens a follow-up, due at the closing time.</summary>
public sealed partial record OpenFollowUp(string Number) : ICreation<Order>
{
    [OnEvent<Order.WasClosed>(DueAt = nameof(Order.WasClosed.At))]
    public static OpenFollowUp From(Order.WasClosed e, RecordedEvent recorded) => new("follow-up-" + recorded.StreamId.ToString("N")[..8]);

    public Result<Order> Execute(ChangeContext context) => Order.Open(Number, context.Now);
}

/// <summary>A scheduled change (step 14A): a nightly order at 02:00 Vienna time, numbered by the local day.</summary>
public sealed partial record OpenNightlyOrder(string Number) : ICreation<Order>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static OpenNightlyOrder From(JobOccurrence o) => new("nightly-" + o.Local.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));

    public Result<Order> Execute(ChangeContext context) => Order.Open(Number, context.Now);
}

[JsonSerializable(typeof(int))]
internal sealed partial class SmokeJson : JsonSerializerContext;
