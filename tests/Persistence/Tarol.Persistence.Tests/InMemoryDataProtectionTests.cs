using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

/// <summary>Step 16A: the in-memory store seals and opens through the codec, so tests see erasure (data-protection.md §5).</summary>
public sealed class InMemoryDataProtectionTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly InMemoryKeyStore _keys;
    private readonly InMemoryAggregateStore _store;
    private readonly OperationContext _alice;

    public InMemoryDataProtectionTests()
    {
        _keys = InMemoryKeyStore.Create(_time);
        var codec = new EventCodec(new JsonSerializerOptions(PersonalJson.Default.Options)).Add<Customer>().Add<Order>().Add<Account>();
        _store = new InMemoryAggregateStore(_time, new InMemoryStoreOptions
        {
            Codec = codec,
            Keys = _keys,
            Lookups = new StoreLookups().DefinePersonal<Customer>("email", static c => c.Email?.ToLowerInvariant()),
        });
        _alice = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(_time), _time);
    }

    private Guid Tenant => _alice.Tenant.TryGetValue(out Id<ITenant> id) ? id.Value : Guid.Empty;

    private async Task<Customer> Registered(string name = "Ada")
    {
        var customer = Customer.Register(name, $"{name.ToLowerInvariant()}@example.com", new Address("1 Main St", "London"), "GB");
        customer.AddContact("+44 20 7946 0000");
        (await _store.Append(customer, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();
        return customer;
    }

    private async Task<Customer> Load(Id<Customer> id) => (await _store.Load(id, _alice, _ct)).TryGetValue(out Customer? c) ? c : throw new InvalidOperationException("not found");

    [Fact]
    public async Task A_Customer_Round_Trips_Through_State_And_Replay()
    {
        var customer = await Registered();

        var loaded = await Load(customer.Id);
        loaded.Name.ShouldBe("Ada");
        loaded.Home.ShouldBe(new Address("1 Main St", "London"));
        loaded.Contacts.Single().Phone.ShouldBe("+44 20 7946 0000");
        _store.StateLoads.ShouldBe(1);
        _store.History(customer.Id, _alice.Tenant).Select(e => e.Event).OfType<Customer.Registered>().Single().Email.ShouldBe("ada@example.com");
        var stored = Encoding.UTF8.GetString(_store.StoredStateForTests(customer.Id, _alice.Tenant)!);
        stored.ShouldNotContain("\"Ada\"", Case.Sensitive);                         // quoted: a hex id may hold "ada" (case-insensitive)
        stored.ShouldNotContain("+44 20 7946 0000");                                  // the whole number: a random id or nonce may hold "7946"
        stored.ShouldContain("\"country\":\"GB\"");
    }

    [Fact]
    public async Task After_Erasure_The_Customer_Still_Loads_With_Its_Personal_Fields_Null()
    {
        var customer = await Registered();
        var order = Order.Place(customer.Id.Value, "1 Main St, London", 42m);
        (await _store.Append(order, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();
        var bob = await Registered("Bob");

        (await _keys.EraseSubject(Tenant, customer.Id.Value, _ct)).ShouldBeTrue();

        var forgotten = await Load(customer.Id);
        forgotten.Name.ShouldBeNull();
        forgotten.Email.ShouldBeNull();
        forgotten.Home.ShouldBeNull();
        forgotten.Country.ShouldBe("GB");
        forgotten.Contacts.Single().Phone.ShouldBeNull();
        var replayed = Customer.Rehydrate(customer.Id, _store.History(customer.Id, _alice.Tenant));
        replayed.Name.ShouldBeNull();
        replayed.Country.ShouldBe("GB");

        var placed = (await _store.Load(order.Id, _alice, _ct)).TryGetValue(out Order? o) ? o : null;
        placed.ShouldNotBeNull().Total.ShouldBe(42m);
        placed.ShouldNotBeNull().ShipTo.ShouldBeNull();
        (await Load(bob.Id)).Name.ShouldBe("Bob");

        var feed = new List<PositionedEvent>();
        await foreach (var e in _store.ReadAll(GlobalPosition.Start, 100, _ct))
            feed.Add(e);
        feed.Select(e => e.Event.Event.Event).OfType<Customer.Registered>().Select(e => e.Name).ShouldBe([null, "Bob"]);
        _store.Recorded.Select(e => e.Event.Event).OfType<Order.Placed>().Single().ShipTo.ShouldBeNull();
    }

    [Fact]
    public async Task Two_State_Members_With_The_Same_Subject_Round_Trip_Sealed()
    {
        // Tatalu FB-F13.3-2 (stored state): ShipTo and Note both name Customer as their subject.
        var customer = await Registered();
        var order = Order.Place(customer.Id.Value, "1 Main St, London", 42m);
        order.Readdress("2 Side St, Leeds", "leave at the door");
        (await _store.Append(order, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        var loaded = (await _store.Load(order.Id, _alice, _ct)).TryGetValue(out Order? o) ? o : null;
        loaded.ShouldNotBeNull().ShipTo.ShouldBe("2 Side St, Leeds");
        loaded.Note.ShouldBe("leave at the door");
        _store.AssertNoPlaintext("2 Side St, Leeds");
        _store.AssertNoPlaintext("leave at the door");
    }

    [Fact]
    public async Task AssertNoPlaintext_Finds_A_Value_The_Store_Holds_Unsealed()
    {
        var customer = Customer.Register("Ada", "ada@example.com", new Address("1 Main St", "London"), "Atlantis");
        (await _store.Append(customer, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        _store.AssertNoPlaintext("ada@example.com");
        Should.Throw<InvalidOperationException>(() => _store.AssertNoPlaintext("Atlantis")).Message.ShouldContain("customer"); // the unsealed country
        Should.Throw<ArgumentException>(() => _store.AssertNoPlaintext("GB"));
    }

    [Fact]
    public async Task New_Personal_Data_For_An_Erased_Subject_Is_Refused()
    {
        var customer = await Registered();
        await _keys.EraseSubject(Tenant, customer.Id.Value, _ct);

        var loaded = await Load(customer.Id);
        loaded.Describe(1815, ["Ada"]);
        var refused = await _store.Append(loaded, loaded.Version - 1, _alice, Option.None, _ct);
        refused.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Error>().Code.ShouldBe(DataProtectionErrors.SubjectErasedCode);

        var order = Order.Place(customer.Id.Value, "elsewhere", 1m);
        (await _store.Append(order, 0, _alice, Option.None, _ct)).TryGetValue(out Violation? orderViolation).ShouldBeTrue();
        orderViolation.ShouldBeOfType<Error>().Code.ShouldBe(DataProtectionErrors.SubjectErasedCode);

        var moved = await Load(customer.Id);
        moved.Move("FR");
        (await _store.Append(moved, moved.Version - 1, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue(); // nothing personal: accepted
    }

    [Fact]
    public async Task A_Tenant_Erasure_Forgets_Every_Subject_Of_The_Tenant()
    {
        var ada = await Registered();
        var bob = await Registered("Bob");
        var other = OperationContext.Start(Actor.User("carol"), Id<ITenant>.New(_time), _time);
        var carol = Customer.Register("Carol", "carol@example.com", new Address("2 Side St", "Leeds"), "GB");
        (await _store.Append(carol, 0, other, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        await _keys.EraseTenant(Tenant, _ct);

        (await Load(ada.Id)).Name.ShouldBeNull();
        (await Load(bob.Id)).Name.ShouldBeNull();
        ((await _store.Load(carol.Id, other, _ct)).TryGetValue(out Customer? kept) ? kept.Name : null).ShouldBe("Carol");
    }

    [Fact]
    public async Task Aggregates_Without_Personal_Data_Are_Stored_As_Before()
    {
        var withKeys = Account.Open("DE01");
        withKeys.Deposit(5).IsSuccess.ShouldBeTrue();
        var plain = new InMemoryAggregateStore(_time, new InMemoryStoreOptions { Codec = new EventCodec(new JsonSerializerOptions(PersonalJson.Default.Options)).Add<Account>() });
        var copy = Account.Open(withKeys.Id, "DE01");
        copy.Deposit(5).IsSuccess.ShouldBeTrue();

        (await _store.Append(withKeys, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();
        (await plain.Append(copy, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue();

        _store.History(withKeys.Id, _alice.Tenant).ShouldBe(plain.History(copy.Id, _alice.Tenant));
        _store.StoredStateForTests(withKeys.Id, _alice.Tenant).ShouldBe(plain.StoredStateForTests(copy.Id, _alice.Tenant));
    }

    [Fact]
    public void A_Key_Store_Needs_A_Codec()
        => Should.Throw<ArgumentException>(() => new InMemoryAggregateStore(_time, new InMemoryStoreOptions { Keys = _keys }));

    [Fact]
    public async Task A_Personal_Lookup_Finds_By_Its_Blind_Index_And_Stores_No_Plaintext()
    {
        var ada = await Registered();

        ((await _store.Find<Customer>("email", "ada@example.com", _alice, _ct)).TryGetValue(out Id<Customer> found) ? found : default).ShouldBe(ada.Id);
        (await _store.Find<Customer>("email", "nobody@example.com", _alice, _ct)).IsNone.ShouldBeTrue();
        var other = OperationContext.Start(Actor.User("carol"), Id<ITenant>.New(_time), _time);
        (await _store.Find<Customer>("email", "ada@example.com", other, _ct)).IsNone.ShouldBeTrue();

        var index = await _keys.BlindIndex(Tenant, "email", "ada@example.com", create: false, _ct);
        index.ShouldNotBeNull().Length.ShouldBe(43);
        index.ShouldNotContain("ada@", Case.Sensitive);                                 // '@' is not base64url: random bytes never match
        (await _keys.BlindIndex(Tenant, "email", "ada@example.com", create: false, _ct)).ShouldBe(index);
        (await _keys.BlindIndex(Guid.CreateVersion7(), "email", "ada@example.com", create: true, _ct)).ShouldNotBe(index);
        (await _keys.BlindIndex(Tenant, "phone", "ada@example.com", create: false, _ct)).ShouldNotBe(index);
    }

    [Fact]
    public async Task A_Personal_Lookup_Stays_Unique()
    {
        await Registered();
        var twin = Customer.Register("Ada", "ADA@example.com", new Address("3 Other St", "York"), "GB");
        (await _store.Append(twin, 0, _alice, Option.None, _ct)).TryGetValue(out Violation? taken).ShouldBeTrue();
        taken.ShouldBeOfType<Error>().Code.ShouldBe(StoreErrors.LookupTakenCode);
    }

    [Fact]
    public async Task Erasure_Deletes_The_Personal_Lookups_Of_The_Subjects_Streams()
    {
        var ada = await Registered();

        _store.DeletePersonalLookups(Tenant, [new StreamRef(Tenant, "customer", ada.Id.Value)]).ShouldBe(1);

        (await _store.Find<Customer>("email", "ada@example.com", _alice, _ct)).IsNone.ShouldBeTrue();
        var again = Customer.Register("Ada", "ada@example.com", new Address("1 Main St", "London"), "GB");
        (await _store.Append(again, 0, _alice, Option.None, _ct)).IsSuccess.ShouldBeTrue(); // the index is free again
    }

    [Fact]
    public async Task A_Tenant_Erasure_Makes_Personal_Lookups_Unfindable()
    {
        await Registered();
        await _keys.EraseTenant(Tenant, _ct);
        (await _store.Find<Customer>("email", "ada@example.com", _alice, _ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_Personal_Lookup_Needs_A_Key_Store()
    {
        var store = new InMemoryAggregateStore(_time, new InMemoryStoreOptions { Lookups = new StoreLookups().DefinePersonal<Account>("number", static a => a.Number) });
        await Should.ThrowAsync<InvalidOperationException>(async () => _ = await store.Append(Account.Open("DE01"), 0, _alice, Option.None, _ct));
    }
}
