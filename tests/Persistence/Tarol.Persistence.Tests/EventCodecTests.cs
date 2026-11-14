using System.Buffers;
using System.Text;
using System.Text.Json;
using Tarol.Primitives;

namespace Tarol.Persistence.Tests;

public sealed class EventCodecTests
{
    private static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(CodecJson.Default.Options))
        .Add<Subscription>()
        .Upcast<Subscription>(new ShutToCancelled())
        .Upcast<Subscription>(new CancelledV1ToV2());

    private static DomainEvent Decode(EventCodec codec, string alias, int version, string json)
        => codec.Decode("subscription", new EventType(alias, version), Encoding.UTF8.GetBytes(json));

    [Fact]
    public void An_event_round_trips_with_its_alias_and_version()
    {
        var codec = Codec();
        var buffer = new ArrayBufferWriter<byte>();

        var type = codec.Encode<Subscription>(new Subscription.Cancelled("price"), buffer);

        type.ShouldBe(new EventType("subscription.cancelled", 2));
        Encoding.UTF8.GetString(buffer.WrittenSpan).ShouldBe("""{"reason":"price"}""");
        codec.Decode("subscription", type, buffer.WrittenSpan).ShouldBe(new Subscription.Cancelled("price"));
    }

    [Fact]
    public void An_old_version_is_upcast()
        => Decode(Codec(), "subscription.cancelled", 1, "{}").ShouldBe(new Subscription.Cancelled("unknown"));

    [Fact]
    public void Upcasters_chain_across_a_rename()
        => Decode(Codec(), "subscription.shut", 1, "{}").ShouldBe(new Subscription.Cancelled("unknown"));

    [Fact]
    public void An_unknown_alias_throws_with_the_fix()
        => Should.Throw<InvalidOperationException>(() => Decode(Codec(), "subscription.paused", 1, "{}"))
            .Message.ShouldBe("No event of 'subscription' is stored as 'subscription.paused' v1 and no upcaster moves it: pin the old alias with [EventType] or register an IEventUpcaster.");

    [Fact]
    public void An_old_version_without_an_upcaster_throws()
    {
        var codec = new EventCodec(new JsonSerializerOptions(CodecJson.Default.Options)).Add<Subscription>();
        Should.Throw<InvalidOperationException>(() => Decode(codec, "subscription.cancelled", 1, "{}")).Message.ShouldContain("'subscription.cancelled' v1");
    }

    [Fact]
    public void An_unregistered_stream_type_throws()
        => Should.Throw<InvalidOperationException>(() => Codec().Decode("account", new EventType("account.opened", 1), "{}"u8));

    [Fact]
    public void A_malformed_payload_is_a_JsonException()
        => Should.Throw<JsonException>(() => Decode(Codec(), "subscription.cancelled", 2, "{\"reason\":"));

    [Fact]
    public void Registration_fails_when_an_event_has_no_metadata()
        => Should.Throw<InvalidOperationException>(() => new EventCodec(new JsonSerializerOptions(TestJson.Default.Options)).Add<Account>())
            .Message.ShouldBe("The JSON options of the event codec have no metadata for Deposited, Opened, Renumbered (Account): add [JsonSerializable(typeof(…))] for each to your JsonSerializerContext.");

    [Fact]
    public void Registering_twice_is_harmless_and_the_stream_type_comes_from_the_aggregate()
    {
        var codec = Codec().Add<Subscription>();
        codec.IsRegistered<Subscription>().ShouldBeTrue();
        codec.IsRegistered<Account>().ShouldBeFalse();
        codec.StreamTypeOf<Subscription>().ShouldBe(Subscription.StreamType);
        Should.Throw<InvalidOperationException>(() => codec.StreamTypeOf<Account>());
    }

    [Fact]
    public void Upcasters_are_unique_per_stored_type_and_must_move()
    {
        Should.Throw<InvalidOperationException>(() => Codec().Upcast<Subscription>(new CancelledV1ToV2()));
        Should.Throw<ArgumentException>(() => Codec().Upcast<Subscription>(new Standstill()));
        Should.Throw<InvalidOperationException>(() => new EventCodec(new JsonSerializerOptions(CodecJson.Default.Options)).Upcast<Subscription>(new CancelledV1ToV2()));
    }

    [Fact]
    public void Encoding_an_unregistered_aggregate_throws()
        => Should.Throw<InvalidOperationException>(() => Codec().Encode<Account>(new Account.Deposited(1), new ArrayBufferWriter<byte>()));

    [Fact]
    public void The_options_are_frozen()
        => Codec().Options.IsReadOnly.ShouldBeTrue();

    private sealed class Standstill : IEventUpcaster
    {
        public EventType From => new("subscription.cancelled", 2);
        public EventType Into => From;
        public void Upcast(System.Text.Json.Nodes.JsonObject payload) { }
    }
}
