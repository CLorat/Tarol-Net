using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

public sealed class EnvelopeTests
{
    private static Message Sample() => new()
    {
        Id = Guid.Parse("01a1dfee-4770-7f95-b718-26961f92af46"),
        Source = "artflow",
        Type = "artflow.auction-settled",
        Version = 2,
        Time = new DateTimeOffset(2026, 11, 15, 16, 1, 59, TimeSpan.Zero),
        Subject = "01a1dfee47707f95b71826961f92af00",
        Tenant = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a"),
        Correlation = (Id<ICorrelation>)Guid.Parse("0193a000-0000-7000-8000-0000000000c0"),
        Causation = (Id<ICausation>)Guid.Parse("0193a000-0000-7000-8000-0000000000c1"),
        Actor = "System:Reactions.SettleAuction:alice",
        Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        Data = "{\"auction\":\"x\"}"u8.ToArray(),
    };

    [Fact]
    public void The_headers_are_CloudEvents_binary_mode()
    {
        var headers = Sample().Headers().ToDictionary();

        headers.ShouldBe(new Dictionary<string, string>
        {
            ["ce-specversion"] = "1.0",
            ["ce-id"] = "01a1dfee-4770-7f95-b718-26961f92af46",
            ["ce-source"] = "artflow",
            ["ce-type"] = "artflow.auction-settled",
            ["ce-contractversion"] = "2",
            ["ce-time"] = "2026-11-15T16:01:59.0000000+00:00",
            ["content-type"] = "application/json",
            ["ce-subject"] = "01a1dfee47707f95b71826961f92af00",
            ["ce-tenant"] = "0193a000-0000-7000-8000-00000000000a",
            ["ce-correlation"] = "0193a000-0000-7000-8000-0000000000c0",
            ["ce-causation"] = "0193a000-0000-7000-8000-0000000000c1",
            ["ce-actor"] = "System:Reactions.SettleAuction:alice",
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        });
    }

    [Fact]
    public void Headers_read_back_into_the_same_message()
    {
        var sample = Sample();
        var headers = sample.Headers().ToDictionary();

        var read = Message.From(h => headers.TryGetValue(h, out var v) ? v : null, sample.Data);

        read.ShouldNotBeNull();
        (read with { Data = sample.Data }).ShouldBe(sample);
    }

    [Theory]
    [InlineData("ce-specversion", "0.3")]
    [InlineData("ce-id", "not-a-guid")]
    [InlineData("ce-source", "")]
    [InlineData("ce-contractversion", "0")]
    [InlineData("ce-contractversion", "v1")]
    [InlineData("ce-time", "yesterday")]
    [InlineData("ce-correlation", null)]
    [InlineData("ce-tenant", "tenant-a")]
    [InlineData("ce-causation", "nope")]
    public void A_missing_or_malformed_required_header_is_a_poison_message(string name, string? value)
    {
        var headers = Sample().Headers().ToDictionary();
        headers[name] = value!;
        if (value is null)
            headers.Remove(name);

        Message.From(h => headers.TryGetValue(h, out var v) ? v : null, ReadOnlyMemory<byte>.Empty).ShouldBeNull();
    }

    [Fact]
    public void Optional_headers_may_be_absent()
    {
        var minimal = new Dictionary<string, string>
        {
            ["ce-specversion"] = "1.0",
            ["ce-id"] = Guid.NewGuid().ToString(),
            ["ce-source"] = "billing",
            ["ce-type"] = "billing.invoice-paid",
            ["ce-contractversion"] = "1",
            ["ce-time"] = "2026-11-15T16:01:59Z",
            ["ce-correlation"] = Guid.NewGuid().ToString(),
        };

        var read = Message.From(h => minimal.TryGetValue(h, out var v) ? v : null, ReadOnlyMemory<byte>.Empty)!;

        read.Tenant.ShouldBe((Option<Id<ITenant>>)Option.None);
        read.Causation.ShouldBe((Option<Id<ICausation>>)Option.None);
        (read.Actor, read.Subject, read.Traceparent).ShouldBe((null, null, null));
    }

    [Fact]
    public void The_broker_dedup_id_is_per_event_contract_and_version()
    {
        var v2 = Sample();
        var v1 = v2 with { Version = 1 };

        v2.DedupId.ShouldBe("01a1dfee47707f95b71826961f92af46:artflow.auction-settled@2");
        v1.DedupId.ShouldNotBe(v2.DedupId);
        v1.Id.ShouldBe(v2.Id);                                                    // one message id for every version (the consumer's key)
    }

    [Theory]
    [InlineData("User:alice", "alice")]
    [InlineData("System:Reactions.SettleAuction:alice", "alice")]
    [InlineData("Service:psp", "psp")]
    [InlineData("System:Jobs.Nightly:", "Jobs.Nightly")]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void The_actor_subject_is_the_original_identity(string? actor, string? subject)
        => (Sample() with { Actor = actor }).ActorSubject.ShouldBe(subject);

    [Theory]
    [InlineData("billing.invoice-paid", "billing")]
    [InlineData("artflow.auction.settled", "artflow")]
    [InlineData("noprefix", "")]
    [InlineData(".x", "")]
    public void The_system_of_a_contract_is_its_prefix(string contract, string system) => Message.SystemOf(contract).ShouldBe(system);
}
