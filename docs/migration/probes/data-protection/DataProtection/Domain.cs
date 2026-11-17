using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.DataProtection;

/// <summary>
/// A data subject: the customer is its own subject (its stream id is the subject id). Personal: <c>Name</c>, <c>Email</c>,
/// <c>Street</c>. Not personal: <c>Country</c>, <c>City</c>, <c>Tier</c>. In the design these carry <c>[PersonalData]</c>;
/// the probe lists them in <see cref="Personal"/> as the generator would.
/// </summary>
public partial class Customer : AggregateRoot<Customer>
{
    public string? Name { get; private set; }
    public string? Email { get; private set; }
    public string? Street { get; private set; }
    public string Country { get; private set; } = "";
    public string City { get; private set; } = "";
    public int Tier { get; private set; }

    public static Customer Register(DateTimeOffset now, string name, string email, string country)
    {
        var c = new Customer(Id<Customer>.New(now));
        c.Raise(new Registered(name, email, country));
        return c;
    }

    public Result Move(string street, string city) => Raise(new Moved(street, city));
    public Result Promote() => Raise(new Promoted(Tier + 1));

    public sealed record Registered(string? Name, string? Email, string Country) : Event;
    public sealed record Moved(string? Street, string City) : Event;
    public sealed record Promoted(int Tier) : Event;

    private void On(Registered e) { Name = e.Name; Email = e.Email; Country = e.Country; }
    private void On(Moved e) { Street = e.Street; City = e.City; }
    private void On(Promoted e) => Tier = e.Tier;
}

/// <summary>An aggregate that holds another subject's data: <c>ShipTo</c> belongs to the customer named by <c>Customer</c>.</summary>
public partial class Order : AggregateRoot<Order>
{
    public Guid Customer { get; private set; }
    public string? ShipTo { get; private set; }
    public decimal Total { get; private set; }

    public static Order Place(DateTimeOffset now, Guid customer, string shipTo, decimal total)
    {
        var o = new Order(Id<Order>.New(now));
        o.Raise(new Placed(customer, shipTo, total));
        return o;
    }

    public sealed record Placed(Guid Customer, string? ShipTo, decimal Total) : Event;

    private void On(Placed e) { Customer = e.Customer; ShipTo = e.ShipTo; Total = e.Total; }
}

/// <summary>Candidate D's event: the same shape with the converter on the personal properties.</summary>
public sealed record RegisteredD(
    [property: JsonConverter(typeof(PersonalString))] string? Name,
    [property: JsonConverter(typeof(PersonalString))] string? Email,
    string Country);

/// <summary>A list projection that copies personal data (the case erasure must reach).</summary>
[Projection("customer-list")]
public sealed partial record CustomerRow(Guid Id, string? Name, string? Email, [Sort] string Country, int Tier)
{
    public static CustomerRow When(Customer.Registered e, RecordedEvent r) => new(r.StreamId, e.Name, e.Email, e.Country, 0);
    public CustomerRow When(Customer.Promoted e) => this with { Tier = e.Tier };
}

/// <summary>What a generator would emit from <c>[PersonalData]</c>: the JSON names per event and state, and whose subject.</summary>
public static class Personal
{
    public static PersonalField[] Of(DomainEvent e, Guid stream) => e switch
    {
        Customer.Registered => [new("name", stream), new("email", stream)],
        Customer.Moved => [new("street", stream)],
        Order.Placed p => [new("shipTo", p.Customer)],
        _ => [],
    };

    public static PersonalField[] State(Customer c) => [new("name", c.Id.Value), new("email", c.Id.Value), new("street", c.Id.Value)];
    public static PersonalField[] State(Order o) => [new("shipTo", o.Customer)];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Customer.Registered))]
[JsonSerializable(typeof(Customer.Moved))]
[JsonSerializable(typeof(Customer.Promoted))]
[JsonSerializable(typeof(Order.Placed))]
[JsonSerializable(typeof(RegisteredD))]
[JsonSerializable(typeof(CustomerRow))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ProbeJson : JsonSerializerContext;
