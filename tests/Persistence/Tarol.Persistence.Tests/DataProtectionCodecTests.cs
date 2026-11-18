using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tarol.Primitives;

namespace Tarol.Persistence.Tests;

/// <summary>Step 16A: sealing in the codec (data-protection.md §3, §6.2).</summary>
public sealed class DataProtectionCodecTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(PersonalJson.Default.Options))
        .Add<Customer>()
        .Add<Order>()
        .Add<Account>()
        .Upcast<Customer>(new SignedUpToRegistered());

    private static async ValueTask<KeyRing> Keys(InMemoryKeyStore store, params Guid[] subjects)
        => await store.Keys(Tenant, subjects, create: true, TestContext.Current.CancellationToken);

    private static (EventType Type, byte[] Plain, byte[] Sealed) Seal<T>(EventCodec codec, DomainEvent e, Guid stream, KeyRing keys)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var plain = new ArrayBufferWriter<byte>();
        var type = codec.Encode<T>(e, plain);
        var sealedPayload = new ArrayBufferWriter<byte>();
        codec.Seal<T>(type, plain.WrittenSpan, stream, keys, sealedPayload).ShouldBeTrue();
        return (type, plain.WrittenSpan.ToArray(), sealedPayload.WrittenSpan.ToArray());
    }

    public static TheoryData<string?> Strings => new() { "Ada Lovelace", "quotes \" and \\ backslash", "line\nbreak\ttab", "unicode ✓ Ünïcødé 漢字", "\u0001 control", "", (string?)null };

    [Theory]
    [MemberData(nameof(Strings))]
    public async Task A_String_Round_Trips_Sealed(string? name)
    {
        var codec = Codec();
        var stream = Guid.CreateVersion7();
        var keys = await Keys(InMemoryKeyStore.Create(), stream);
        var e = new Customer.Registered(name, "ada@example.com", new Address("1 Main St", "London"), "GB");

        var (type, _, sealedPayload) = Seal<Customer>(codec, e, stream, keys);

        var json = Encoding.UTF8.GetString(sealedPayload);
        json.ShouldContain("\"country\":\"GB\"");
        json.ShouldNotContain("ada@example.com");
        json.ShouldNotContain("London");
        if (name is { Length: > 0 })
            json.ShouldNotContain(JsonSerializer.Serialize(name, PersonalJson.Default.String)[1..^1]);
        codec.Decode("customer", type, sealedPayload, keys).ShouldBe(e);
    }

    [Fact]
    public async Task Numbers_Lists_Records_And_Null_Round_Trip()
    {
        var codec = Codec();
        var stream = Guid.CreateVersion7();
        var keys = await Keys(InMemoryKeyStore.Create(), stream);

        foreach (var e in new DomainEvent[] { new Customer.Described(1815, ["Ada", "Countess"]), new Customer.Described(null, null), new Customer.Described(0, []) })
        {
            var (type, plain, sealedPayload) = Seal<Customer>(codec, e, stream, keys);
            var decoded = (Customer.Described)codec.Decode("customer", type, sealedPayload, keys);
            decoded.BirthYear.ShouldBe(((Customer.Described)e).BirthYear);
            decoded.Nicknames.ShouldBe(((Customer.Described)e).Nicknames);
            if (((Customer.Described)e).BirthYear is null)
                sealedPayload.ShouldBe(plain); // null stays null: nothing to seal
        }
    }

    [Fact]
    public async Task The_Envelope_Has_The_Documented_Shape()
    {
        var codec = Codec();
        var stream = Guid.CreateVersion7();
        var keys = await Keys(InMemoryKeyStore.Create(), stream);
        var (_, _, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", null, null, "GB"), stream, keys);

        var name = JsonElement.Parse(sealedPayload).GetProperty("name").GetString()!;
        name.ShouldStartWith("tdp1.");
        Envelope.TryReadSubject(Encoding.UTF8.GetBytes(name), out var subject).ShouldBeTrue();
        subject.ShouldBe(stream);
        // "Ada" as JSON is 5 bytes: prefix 5 + base64url(16 + 4 + 12 + 16 + 5 = 53 bytes) = 5 + 71
        name.Length.ShouldBe(5 + 71);
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(sealedPayload, subjects);
        subjects.ShouldBe([stream]);
    }

    [Fact]
    public async Task A_Subject_Member_Seals_For_That_Subject()
    {
        var codec = Codec();
        var store = InMemoryKeyStore.Create();
        var customer = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();
        var e = new Order.Placed(customer, "1 Main St", 12.5m);

        codec.SubjectsOf<Order>(e, order).ShouldBe([customer]);
        var keys = await Keys(store, customer);
        var (type, _, sealedPayload) = Seal<Order>(codec, e, order, keys);

        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(sealedPayload, subjects);
        subjects.ShouldBe([customer]);
        var opening = await store.Keys(Tenant, [customer], create: false, TestContext.Current.CancellationToken);
        codec.Decode("order", type, sealedPayload, opening).ShouldBe(e);
    }

    [Fact]
    public async Task An_Envelope_Relabelled_To_Another_Subject_Does_Not_Open()
    {
        var codec = Codec();
        var store = InMemoryKeyStore.Create();
        var ada = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        var keys = await Keys(store, ada, bob);
        var (type, _, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", null, null, "GB"), ada, keys);

        // swap the subject bytes of the envelope for bob's: the AES-GCM tag binds (tenant, subject)
        var json = Encoding.UTF8.GetString(sealedPayload);
        var envelope = JsonElement.Parse(json).GetProperty("name").GetString()!;
        var raw = System.Buffers.Text.Base64Url.DecodeFromChars(envelope.AsSpan(5));
        bob.TryWriteBytes(raw.AsSpan(0, 16));
        var relabelled = json.Replace(envelope, "tdp1." + System.Buffers.Text.Base64Url.EncodeToString(raw), StringComparison.Ordinal);

        Should.Throw<CryptographicException>(() => codec.Decode("customer", type, Encoding.UTF8.GetBytes(relabelled), keys));
    }

    [Fact]
    public async Task Another_Tenant_Cannot_Open_It()
    {
        var codec = Codec();
        var store = InMemoryKeyStore.Create();
        var stream = Guid.CreateVersion7();
        var (type, _, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", null, null, "GB"), stream, await Keys(store, stream));

        var other = await store.Keys(Guid.CreateVersion7(), [stream], create: true, TestContext.Current.CancellationToken);
        Should.Throw<CryptographicException>(() => codec.Decode("customer", type, sealedPayload, other));
    }

    [Fact]
    public async Task An_Erased_Subject_Reads_As_Null_And_The_Rest_Stays()
    {
        var codec = Codec();
        var store = InMemoryKeyStore.Create();
        var stream = Guid.CreateVersion7();
        var (type, _, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", "ada@example.com", new Address("1 Main St", "London"), "GB"), stream, await Keys(store, stream));

        (await store.EraseSubject(Tenant, stream, TestContext.Current.CancellationToken)).ShouldBeTrue();
        var keys = await store.Keys(Tenant, [stream], create: false, TestContext.Current.CancellationToken);

        keys.TryGet(stream, out var key).ShouldBeTrue();
        key.IsErased.ShouldBeTrue();
        codec.Decode("customer", type, sealedPayload, keys).ShouldBe(new Customer.Registered(null, null, null, "GB"));
        codec.Decode("customer", type, sealedPayload).ShouldBe(new Customer.Registered(null, null, null, "GB")); // no keys: every sealed field reads as null
    }

    [Fact]
    public async Task A_Ring_Without_The_Subject_Throws_Rather_Than_Read_Null()
    {
        var codec = Codec();
        var stream = Guid.CreateVersion7();
        var (type, _, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", null, null, "GB"), stream, await Keys(InMemoryKeyStore.Create(), stream));

        Should.Throw<InvalidOperationException>(() => codec.Decode("customer", type, sealedPayload, new KeyRing(Tenant))).Message.ShouldContain("holds no key for subject");
    }

    [Fact]
    public async Task Sealing_For_An_Erased_Subject_Is_Refused()
    {
        var codec = Codec();
        var store = InMemoryKeyStore.Create();
        var stream = Guid.CreateVersion7();
        await store.EraseSubject(Tenant, stream, TestContext.Current.CancellationToken);
        var keys = await Keys(store, stream); // create: true keeps the tombstone

        var plain = new ArrayBufferWriter<byte>();
        var type = codec.Encode<Customer>(new Customer.Registered("Ada", null, null, "GB"), plain);
        Should.Throw<SubjectErasedException>(() => codec.Seal<Customer>(type, plain.WrittenSpan, stream, keys, new ArrayBufferWriter<byte>())).Subject.ShouldBe(stream);
    }

    [Fact]
    public async Task An_Upcaster_Sees_The_Opened_Field()
    {
        var codec = Codec();
        var stream = Guid.CreateVersion7();
        var keys = await Keys(InMemoryKeyStore.Create(), stream);
        // an old event, sealed the way 16A seals: the payload of "customer.signed_up" v1 with a sealed "fullName"
        var envelope = new byte[Envelope.MaxLength(64)];
        keys.TryGet(stream, out var key).ShouldBeTrue();
        var length = Envelope.Seal(key, "\"Ada Lovelace\""u8, envelope);
        var stored = Encoding.UTF8.GetBytes($$"""{"fullName":"{{Encoding.UTF8.GetString(envelope, 0, length)}}","email":null}""");

        codec.Decode("customer", new EventType("customer.signed_up", 1), stored, keys).ShouldBe(new Customer.Registered("Ada Lovelace", null, null, "DE"));
    }

    [Fact]
    public void Events_Without_Personal_Data_Are_Stored_Byte_Identically()
    {
        var codec = Codec();
        var plainCodec = new EventCodec(new JsonSerializerOptions(PersonalJson.Default.Options)).Add<Account>();
        var e = new Account.Deposited(12.5m);
        var sealedBuffer = new ArrayBufferWriter<byte>();
        var plainBuffer = new ArrayBufferWriter<byte>();

        var type = codec.Encode<Account>(e, sealedBuffer, Guid.CreateVersion7(), new KeyRing(Tenant));
        plainCodec.Encode<Account>(e, plainBuffer);

        sealedBuffer.WrittenSpan.ToArray().ShouldBe(plainBuffer.WrittenSpan.ToArray());
        codec.HasPersonalData<Account>().ShouldBeFalse();
        codec.HasPersonalData<Customer>().ShouldBeTrue();
        codec.Seal<Account>(type, plainBuffer.WrittenSpan, Guid.CreateVersion7(), new KeyRing(Tenant), new ArrayBufferWriter<byte>()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Sealed_Payload_Keeps_Every_Plain_Byte()
    {
        var codec = Codec();
        var stream = Guid.CreateVersion7();
        var keys = await Keys(InMemoryKeyStore.Create(), stream);
        var (_, plain, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", null, null, "Großbritannien"), stream, keys);

        // only the sealed value changes: the text around it is the serializer's, byte for byte
        var plainText = Encoding.UTF8.GetString(plain);
        var sealedText = Encoding.UTF8.GetString(sealedPayload);
        sealedText[..sealedText.IndexOf("\"tdp1.", StringComparison.Ordinal)].ShouldBe(plainText[..plainText.IndexOf("\"Ada\"", StringComparison.Ordinal)]);
        sealedText.ShouldEndWith(plainText[(plainText.IndexOf("\"Ada\"", StringComparison.Ordinal) + 5)..]);
    }

    [Fact]
    public async Task The_State_Seals_Root_And_Entity_Members()
    {
        var codec = Codec();
        var store = InMemoryKeyStore.Create();
        var customer = Customer.Register("Ada", "ada@example.com", new Address("1 Main St", "London"), "GB");
        customer.AddContact("+44 20 7946 0000");
        var state = AggregateState.Write(customer, codec.Options);
        var subjects = new List<Guid>();
        codec.StateSubjectsOf<Customer>(state, customer.Id.Value, subjects);
        subjects.ShouldBe([customer.Id.Value]);
        var keys = await Keys(store, customer.Id.Value);

        var sealedState = new ArrayBufferWriter<byte>();
        codec.SealState<Customer>(state, customer.Id.Value, keys, sealedState).ShouldBeTrue();
        var text = Encoding.UTF8.GetString(sealedState.WrittenSpan);
        text.ShouldNotContain("ada@example.com");
        text.ShouldNotContain("7946");
        text.ShouldContain("\"country\":\"GB\"");

        var opened = new ArrayBufferWriter<byte>();
        EventCodec.OpenState(sealedState.WrittenSpan, keys, opened).ShouldBeTrue();
        opened.WrittenSpan.ToArray().ShouldBe(state);
        var loaded = AggregateState.Read(customer.Id, opened.WrittenSpan, codec.Options);
        loaded.TryGetValue(out Customer? copy).ShouldBeTrue();
        copy.Contacts.Single().Phone.ShouldBe("+44 20 7946 0000");

        await store.EraseSubject(Tenant, customer.Id.Value, TestContext.Current.CancellationToken);
        var erased = new ArrayBufferWriter<byte>();
        var tombstones = await store.Keys(Tenant, [customer.Id.Value], false, TestContext.Current.CancellationToken);
        EventCodec.OpenState(sealedState.WrittenSpan, tombstones, erased);
        AggregateState.Read(customer.Id, erased.WrittenSpan, codec.Options).TryGetValue(out Customer? forgotten).ShouldBeTrue();
        forgotten.Name.ShouldBeNull();
        forgotten.Home.ShouldBeNull();
        forgotten.Country.ShouldBe("GB");
        forgotten.Contacts.Single().Phone.ShouldBeNull();
    }

    [Fact]
    public void A_State_Without_Personal_Members_Is_Left_As_It_Is()
    {
        var codec = Codec();
        var account = Account.Open("DE01");
        var state = AggregateState.Write(account, codec.Options);
        codec.SealState<Account>(state, account.Id.Value, new KeyRing(Tenant), new ArrayBufferWriter<byte>()).ShouldBeFalse();
        EventCodec.OpenState(state, null, new ArrayBufferWriter<byte>()).ShouldBeFalse();
    }

    [Fact]
    public async Task One_Subject_Key_Serves_Four_Threads()
    {
        // AesGcm is not safe for concurrent use (data-protection.md §5 item 1): the key holds it under a lock.
        var stream = Guid.CreateVersion7();
        var keys = await Keys(InMemoryKeyStore.Create(), stream);
        keys.TryGet(stream, out var key).ShouldBeTrue();
        var failures = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            var envelope = new byte[Envelope.MaxLength(64)];
            var opened = new ArrayBufferWriter<byte>(64);
            for (var i = 0; i < 5_000; i++)
            {
                var value = Encoding.UTF8.GetBytes($"\"thread {t} value {i}\"");
                var length = Envelope.Seal(key, value, envelope);
                opened.ResetWrittenCount();
                if (!Envelope.TryOpen(keys, envelope.AsSpan(0, length), opened) || !opened.WrittenSpan.SequenceEqual(value))
                    Interlocked.Increment(ref failures);
            }
        }, TestContext.Current.CancellationToken)));

        failures.ShouldBe(0);
    }

    [Fact]
    public async Task A_Tenant_Erasure_Erases_Every_Subject_Of_The_Tenant()
    {
        var store = InMemoryKeyStore.Create();
        var ada = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        await Keys(store, ada, bob);
        var other = Guid.CreateVersion7();
        await store.Keys(other, [ada], create: true, TestContext.Current.CancellationToken);
        KeyErasedEventArgs? signal = null;
        store.Erased += (_, e) => signal = e;

        (await store.EraseTenant(Tenant, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await store.EraseTenant(Tenant, TestContext.Current.CancellationToken)).ShouldBeFalse();

        signal.ShouldNotBeNull().Subject.ShouldBeNull();
        var keys = await store.Keys(Tenant, [ada, bob], create: true, TestContext.Current.CancellationToken);
        keys.TryGet(ada, out var a).ShouldBeTrue();
        a.IsErased.ShouldBeTrue();
        keys.TryGet(bob, out var b).ShouldBeTrue();
        b.IsErased.ShouldBeTrue();
        (await store.Keys(other, [ada], create: false, TestContext.Current.CancellationToken)).TryGet(ada, out var kept).ShouldBeTrue();
        kept.IsErased.ShouldBeFalse();
    }

    [Fact]
    public async Task A_Master_Key_Rotation_Rewraps_Tenant_Keys_Only()
    {
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        var master = new SwitchableMasterKey(new ConfiguredKeyEncryptionKey("k1", k1));
        var store = new InMemoryKeyStore(master, TimeProvider.System);
        var stream = Guid.CreateVersion7();
        var codec = Codec();
        var (type, _, sealedPayload) = Seal<Customer>(codec, new Customer.Registered("Ada", null, null, "GB"), stream, await Keys(store, stream));

        master.Current = new ConfiguredKeyEncryptionKey("k2", k2, [new("k1", k1)]); // k2 is current, k1 still unwraps
        (await store.Rewrap(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await store.Rewrap(TestContext.Current.CancellationToken)).ShouldBe(0);

        master.Current = new ConfiguredKeyEncryptionKey("k2", k2); // k1 retired: the tenant key was re-wrapped, the data key and the event untouched
        var keys = await store.Keys(Tenant, [stream], false, TestContext.Current.CancellationToken);
        codec.Decode("customer", type, sealedPayload, keys).ShouldBe(new Customer.Registered("Ada", null, null, "GB"));
    }

    [Fact]
    public void An_Unknown_Master_Key_Id_Throws()
        => Should.Throw<CryptographicException>(() => new ConfiguredKeyEncryptionKey("k2", RandomNumberGenerator.GetBytes(32)).Unwrap("k1", new byte[KeyWrap.WrappedSize], [], new byte[32]))
            .Message.ShouldContain("'k1' is not configured");

    private sealed class SwitchableMasterKey(IKeyEncryptionKey current) : IKeyEncryptionKey
    {
        public IKeyEncryptionKey Current { get; set; } = current;

        public string KeyId => Current.KeyId;

        public byte[] Wrap(ReadOnlySpan<byte> tenantKey, ReadOnlySpan<byte> associatedData) => Current.Wrap(tenantKey, associatedData);

        public void Unwrap(string keyId, ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> associatedData, Span<byte> tenantKey)
            => Current.Unwrap(keyId, wrapped, associatedData, tenantKey);
    }
}
