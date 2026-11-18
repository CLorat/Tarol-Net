using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tarol.Caching;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.DataProtection.Tests;

[Retention(Days = 30)]
public partial class Customer : AggregateRoot<Customer>
{
    [PersonalData] public string? Name { get; private set; }
    [PersonalData] public string? Email { get; private set; }
    public string Country { get; private set; } = "";

    public static Customer Register(Id<Customer> id, string name, string email, string country)
    {
        var customer = new Customer(id);
        customer.Raise(new Registered(name, email, country));
        return customer;
    }

    public Success Rename(string name) => Raise(new Renamed(name));

    public sealed record Registered([property: PersonalData] string? Name, [property: PersonalData] string? Email, string Country) : Event;
    public sealed record Renamed([property: PersonalData] string? Name) : Event;

    private void On(Registered e) { Name = e.Name; Email = e.Email; Country = e.Country; }
    private void On(Renamed e) => Name = e.Name;
}

public partial class Order : AggregateRoot<Order>
{
    public Guid Customer { get; private set; }
    [PersonalData(Subject = nameof(Customer))] public string? ShipTo { get; private set; }
    public decimal Total { get; private set; }

    public static Order Place(Id<Order> id, Guid customer, string shipTo, decimal total)
    {
        var order = new Order(id);
        order.Raise(new Placed(customer, shipTo, total));
        return order;
    }

    public sealed record Placed(Guid Customer, [property: PersonalData(Subject = nameof(Placed.Customer))] string? ShipTo, decimal Total) : Event;

    private void On(Placed e) { Customer = e.Customer; ShipTo = e.ShipTo; Total = e.Total; }
}

[Projection("customers", Version = 1)]
public sealed partial record CustomerRow(Guid Id, string? Name, string? Email, [Sort] string Country)
{
    public static CustomerRow When(Customer.Registered e, RecordedEvent r) => new(r.StreamId, e.Name, e.Email, e.Country);

    public CustomerRow When(Customer.Renamed e) => this with { Name = e.Name };
}

[Projection("orders", Version = 1)]
public sealed partial record OrderRow(Guid Id, Guid Customer, string? ShipTo, decimal Total)
{
    public static OrderRow When(Order.Placed e, RecordedEvent r) => new(r.StreamId, e.Customer, e.ShipTo, e.Total);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Customer.Registered))]
[JsonSerializable(typeof(Customer.Renamed))]
[JsonSerializable(typeof(Order.Placed))]
[JsonSerializable(typeof(CustomerRow))]
[JsonSerializable(typeof(OrderRow))]
[JsonSerializable(typeof(decimal))]
public partial class TestJson : JsonSerializerContext;

/// <summary>An application in memory: the store with keys, two projections behind the page cache, data protection.</summary>
public sealed class App : IAsyncDisposable
{
    public static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public App(Action<DataProtectionOptions>? configure = null, IDictionary<string, string?>? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(configuration ?? new Dictionary<string, string?> { ["Tarol:DataProtection:MasterKey"] = MasterKey })
            .Build());
        var json = new JsonSerializerOptions(TestJson.Default.Options);
        var codec = new EventCodec(json).Add<Customer>().Add<Order>();
        _ = new TarolBuilder(services, json, codec)
            .UseInMemoryStore(new StoreLookups().DefinePersonal<Customer>("email", static c => c.Email?.ToLowerInvariant()))
            .UseInMemoryKeyStore()
            .UseInMemoryReadModels()
            .AddProjectionPositions()
            .AddProjections(p => p.Add<CustomerRow>().Add<OrderRow>())
            .AddCaching()
            .AddDataProtection(configure);
        Services = services.BuildServiceProvider();
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    public ServiceProvider Services { get; }

    public IAggregateStore Store => Services.GetRequiredService<IAggregateStore>();

    public InMemoryAggregateStore Memory => Services.GetRequiredService<InMemoryAggregateStore>();

    public IDataSubjects Subjects => Services.GetRequiredService<IDataSubjects>();

    public IReadModels ReadModels => Services.GetRequiredService<IReadModels>();

    public OperationContext Context { get; } = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);

    public Guid Tenant => Context.Tenant.GetValueOrThrow().Value;

    public async Task Commit<T>(T aggregate, long expected = 0)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        (await Store.Append(aggregate, expected, Context, Option.None, TestContext.Current.CancellationToken)).ToResult().ShouldBe(Result.Success());
        AggregateChanges.Committed(aggregate);
    }

    public async Task Project()
    {
        foreach (var host in Services.GetServices<ProjectionHost>())
            await host.CatchUp(TestContext.Current.CancellationToken);
    }

    public async Task<T?> Row<T>(Guid id)
        where T : class, IProjection<T>
        => (await ReadModels.Store<T>().Find(new RowKey(Tenant, id), TestContext.Current.CancellationToken))?.Row;

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
