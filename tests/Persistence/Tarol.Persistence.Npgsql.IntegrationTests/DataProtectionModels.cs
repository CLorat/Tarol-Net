using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>The probe's customer (data-protection.md §3): name, email and street sealed; country and city plain.</summary>
public partial class Patron : AggregateRoot<Patron>
{
    [PersonalData] public string? Name { get; private set; }
    [PersonalData] public string? Email { get; private set; }
    [PersonalData] public string? Street { get; private set; }
    public string Country { get; private set; } = "";
    public string City { get; private set; } = "";

    public static Patron Register(Id<Patron> id, string name, string email, string street, string city, string country)
    {
        var patron = new Patron(id);
        patron.Raise(new Registered(name, email, street, city, country));
        return patron;
    }

    public Success ChangeEmail(string email) => Raise(new EmailChanged(email));

    public Success Relocate(string city) => Raise(new Relocated(city));

    public sealed record Registered([property: PersonalData] string? Name, [property: PersonalData] string? Email, [property: PersonalData] string? Street, string City, string Country) : Event;
    public sealed record EmailChanged([property: PersonalData] string? Email) : Event;
    public sealed record Relocated(string City) : Event;

    private void On(Registered e) { Name = e.Name; Email = e.Email; Street = e.Street; City = e.City; Country = e.Country; }
    private void On(EmailChanged e) => Email = e.Email;
    private void On(Relocated e) => City = e.City;
}

/// <summary>An order holding its customer's address, sealed for the customer.</summary>
public partial class Purchase : AggregateRoot<Purchase>
{
    public Guid Patron { get; private set; }
    [PersonalData(Subject = nameof(Patron))] public string? ShipTo { get; private set; }
    public decimal Total { get; private set; }

    public static Purchase Place(Id<Purchase> id, Guid patron, string shipTo, decimal total)
    {
        var purchase = new Purchase(id);
        purchase.Raise(new Placed(patron, shipTo, total));
        return purchase;
    }

    public sealed record Placed(Guid Patron, [property: PersonalData(Subject = nameof(Placed.Patron))] string? ShipTo, decimal Total) : Event;

    private void On(Placed e) { Patron = e.Patron; ShipTo = e.ShipTo; Total = e.Total; }
}

/// <summary>"patron.signed_up" v1 held the name as "fullName" (sealed): the upcaster sees it opened.</summary>
public sealed class SignedUpToRegistered : IEventUpcaster
{
    public EventType From => new("patron.signed_up", 1);
    public EventType Into => new("patron.registered", 1);

    public void Upcast(JsonObject payload)
    {
        payload["name"] = payload["fullName"]?.DeepClone();
        payload.Remove("fullName");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Patron.Registered))]
[JsonSerializable(typeof(Patron.EmailChanged))]
[JsonSerializable(typeof(Patron.Relocated))]
[JsonSerializable(typeof(Purchase.Placed))]
[JsonSerializable(typeof(decimal))]
public partial class PrivacyJson : JsonSerializerContext;
