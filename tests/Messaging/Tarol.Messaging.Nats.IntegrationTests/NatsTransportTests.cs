using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tarol.Kernel;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Nats.IntegrationTests;

/// <summary>NATS's own rows (docs/migration/messaging.md §3.2): subject permissions, the dedup id, duplicates as success.</summary>
[Trait("Category", "Integration")]
public sealed class NatsTransportTests(Nats nats)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Message Raw(string contract, int version = 1, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        Source = Message.SystemOf(contract),
        Type = contract,
        Version = version,
        Time = TimeProvider.System.GetUtcNow(),
        Correlation = Id<ICorrelation>.New(),
        Data = "{}"u8.ToArray(),
    };

    [Fact]
    public async Task A_user_publishes_only_under_its_own_prefix_and_a_forbidden_publish_is_a_logged_timeout()
    {
        await using (var artflow = new NatsTransport(new NatsTransportOptions { Url = nats.SecuredUrl, Username = "artflow", Password = "artflow" }))
            await artflow.Prepare(["artflow.auction-settled"], [], Ct);
        await using (var owner = new NatsTransport(new NatsTransportOptions { Url = nats.SecuredUrl, Username = "billing", Password = "billing" }))
            await owner.Prepare(["billing.invoice-paid"], [], Ct);
        var log = new ListLogger();
        await using var billing = new NatsTransport(new NatsTransportOptions
        {
            Url = nats.SecuredUrl,
            Username = "billing",
            Password = "billing",
            PublishTimeout = TimeSpan.FromSeconds(2),
        }, log);

        var forbidden = await Should.ThrowAsync<TimeoutException>(() => billing.Publish(Raw("artflow.auction-settled"), Ct));
        forbidden.Message.ShouldContain("may this user publish on it?");
        await billing.Publish(Raw("billing.invoice-paid"), Ct);                                       // its own prefix

        log.Lines.ShouldContain(l => l.Contains("NATS refused user billing", StringComparison.Ordinal) && l.Contains("Permissions Violation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task One_event_in_two_contract_versions_is_two_messages_and_a_republish_is_a_counted_duplicate()
    {
        var prefix = "n" + Guid.NewGuid().ToString("N")[..8];
        await using var transport = new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = prefix });
        await transport.Prepare(["artflow.auction-settled"], [], Ct);
        var v1 = Raw("artflow.auction-settled");

        await transport.Publish(v1, Ct);
        await transport.Publish(v1 with { Version = 2 }, Ct);                                         // Nats-Msg-Id = (event, contract, version)
        await transport.Publish(v1, Ct);                                                              // the same again: a duplicate, no exception

        transport.Duplicates.ShouldBe(1);
        transport.StreamOf("artflow.auction-settled").ShouldBe(prefix.ToUpperInvariant() + "_ARTFLOW");
    }

    [Fact]
    public async Task A_subscription_reads_one_stream()
    {
        await using var transport = new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = "n" + Guid.NewGuid().ToString("N")[..8] });

        await Should.ThrowAsync<ArgumentException>(() => transport.Prepare([], [new Subscription("x.Both", ["artflow.a", "billing.b"])], Ct));
        Should.Throw<ArgumentException>(() => new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = "bad.prefix" }));
    }

    [Fact]
    public async Task A_stream_keeps_messages_for_the_bus_retention_by_default_and_as_configured()
    {
        // step 16C (DP-5): JetStream streams used to keep messages for ever
        var prefix = "n" + Guid.NewGuid().ToString("N")[..8];
        await using (var transport = new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = prefix }))
            await transport.Prepare(["shop.ordered"], [], Ct);
        await using (var transport = new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = prefix + "b", MaxAge = TimeSpan.FromHours(12) }))
            await transport.Prepare(["shop.ordered"], [], Ct);
        await using var connection = new NATS.Client.Core.NatsConnection(new NATS.Client.Core.NatsOpts { Url = nats.Url });
        var js = new NATS.Client.JetStream.NatsJSContext(connection);

        (await js.GetStreamAsync($"{prefix}_SHOP".ToUpperInvariant(), cancellationToken: Ct)).Info.Config.MaxAge.ShouldBe(TimeSpan.FromDays(7));
        (await js.GetStreamAsync($"{prefix}b_SHOP".ToUpperInvariant(), cancellationToken: Ct)).Info.Config.MaxAge.ShouldBe(TimeSpan.FromHours(12));
        Should.Throw<ArgumentOutOfRangeException>(() => new NatsTransport(new NatsTransportOptions { Url = nats.Url, Prefix = prefix, MaxAge = TimeSpan.FromSeconds(1) }));
    }

    private sealed class ListLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }
}
