using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

public sealed record Address(string Street, string City);

/// <summary>A customer is its own subject (data-protection.md §2): name, email, address, phone numbers and its contacts' phones are sealed.</summary>
public partial class Customer : AggregateRoot<Customer>
{
    public EntitySet<Contact> Contacts { get; } = [];
    [PersonalData] public string? Name { get; private set; }
    [PersonalData] public string? Email { get; private set; }
    [PersonalData] public Address? Home { get; private set; }
    [PersonalData] public int? BirthYear { get; private set; }
    [PersonalData] public List<string>? Nicknames { get; private set; }
    public string Country { get; private set; } = "";

    public static Customer Register(string name, string email, Address home, string country)
        => Register(Id<Customer>.New(), name, email, home, country);

    public static Customer Register(Id<Customer> id, string name, string email, Address home, string country)
    {
        var customer = new Customer(id);
        customer.Raise(new Registered(name, email, home, country));
        return customer;
    }

    public Success Describe(int? birthYear, List<string>? nicknames) => Raise(new Described(birthYear, nicknames));

    public Contact AddContact(string phone) => Raise(new Contact.Added(phone));

    public Success Move(string country) => Raise(new Moved(country));

    public sealed record Registered([property: PersonalData] string? Name, [property: PersonalData] string? Email, [property: PersonalData] Address? Home, string Country) : Event;
    public sealed record Described([property: PersonalData] int? BirthYear, [property: PersonalData] List<string>? Nicknames) : Event;
    public sealed record Moved(string Country) : Event;

    private void On(Registered e) { Name = e.Name; Email = e.Email; Home = e.Home; Country = e.Country; }
    private void On(Described e) { BirthYear = e.BirthYear; Nicknames = e.Nicknames; }
    private void On(Moved e) => Country = e.Country;
}

public partial class Contact : Entity<Customer, Contact>
{
    [PersonalData] public string? Phone { get; private set; }
    public sealed record Added([property: PersonalData] string? Phone) : Created;
    private void On(Added e) => Phone = e.Phone;
}

/// <summary>An order holds its customer's address: the customer is the subject (Subject = nameof(…)).</summary>
public partial class Order : AggregateRoot<Order>
{
    public Guid Customer { get; private set; }
    [PersonalData(Subject = nameof(Customer))] public string? ShipTo { get; private set; }
    public decimal Total { get; private set; }

    public static Order Place(Guid customer, string shipTo, decimal total)
    {
        var order = new Order(Id<Order>.New());
        order.Raise(new Placed(customer, shipTo, total));
        return order;
    }

    [PersonalData(Subject = nameof(Customer))] public string? Note { get; private set; }

    public Success Readdress(string shipTo, string note) => Raise(new Readdressed(Customer, shipTo, note));

    public sealed record Placed(Guid Customer, [property: PersonalData(Subject = nameof(Placed.Customer))] string? ShipTo, decimal Total) : Event;

    /// <summary>Two personal members with the same subject member (Tatalu FB-F13.3-2).</summary>
    public sealed record Readdressed(
        Guid Customer,
        [property: PersonalData(Subject = nameof(Readdressed.Customer))] string? ShipTo,
        [property: PersonalData(Subject = nameof(Readdressed.Customer))] string? Note) : Event;

    private void On(Placed e) { Customer = e.Customer; ShipTo = e.ShipTo; Total = e.Total; }
    private void On(Readdressed e) { ShipTo = e.ShipTo; Note = e.Note; }
}

/// <summary>"customer.signed_up" v1 named the name "fullName"; it is a sealed field, and the upcaster sees it opened.</summary>
public sealed class SignedUpToRegistered : IEventUpcaster
{
    public EventType From => new("customer.signed_up", 1);
    public EventType Into => new("customer.registered", 1);

    public void Upcast(JsonObject payload)
    {
        payload["name"] = payload["fullName"]?.DeepClone();
        payload.Remove("fullName");
        payload["country"] ??= "DE";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Customer.Registered))]
[JsonSerializable(typeof(Customer.Described))]
[JsonSerializable(typeof(Customer.Moved))]
[JsonSerializable(typeof(Contact.Added))]
[JsonSerializable(typeof(Order.Placed))]
[JsonSerializable(typeof(Order.Readdressed))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Account.Opened))]
[JsonSerializable(typeof(Account.Deposited))]
[JsonSerializable(typeof(Account.Renumbered))]
public partial class PersonalJson : JsonSerializerContext;
