using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Contract;

/// <summary>One delivery a <see cref="ContractHarness"/> saw: the consumer, what arrived, what the consumer answered.</summary>
public sealed record SeenDelivery(string Consumer, Incoming Incoming, Handled Handled);

/// <summary>
/// The consumers of one system on a real transport, in real time: an in-memory store and dead letters, the
/// <see cref="MessageConsumers"/> of the contract domain, one consume loop per subscription.
/// </summary>
public sealed class ContractHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly ConcurrentQueue<SeenDelivery> _seen = new();

    public ContractHarness(IMessageTransport transport, ConsumerSet consumers, string application, DeliveryPolicy policy, int concurrency = 1)
    {
        Transport = transport;
        Store = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions { StateOptions = ContractJson.Default.Options });
        Pipeline = new ChangePipeline(Store, TimeProvider.System);
        DeadLetters = new InMemoryMessageDeadLetters(TimeProvider.System);
        Options = new MessagingOptions { Application = application, Policy = policy, Concurrency = concurrency, Lease = TimeSpan.FromSeconds(10) };
        Options.Trust("artflow", t => t.AnyTenant());
        Options.Trust("billing", t => t.Tenants(TransportContract.TenantA));
        Consumers = new MessageConsumers(consumers, Pipeline, DeadLetters, Options, ContractJson.Default.Options);
    }

    public IMessageTransport Transport { get; }

    public InMemoryAggregateStore Store { get; }

    public ChangePipeline Pipeline { get; }

    public InMemoryMessageDeadLetters DeadLetters { get; }

    public MessagingOptions Options { get; }

    public MessageConsumers Consumers { get; }

    public IReadOnlyList<SeenDelivery> Seen => [.. _seen];

    /// <summary>Changes the answer the transport gets (e.g. a lost acknowledgement); <see langword="null"/> = the consumer's own.</summary>
    public Func<SeenDelivery, Handled>? Answer { get; set; }

    /// <summary>Declares the subscriptions and starts consuming.</summary>
    public async Task<ContractHarness> Start()
    {
        await Transport.Prepare([], Consumers.Subscriptions, _stop.Token);
        foreach (var subscription in Consumers.Subscriptions)
        {
            var consumer = subscription.Name[(Options.Application.Length + 1)..];
            _loops.Add(Task.Run(() => Transport.Consume(subscription, async (incoming, ct) =>
            {
                var handled = await Consumers.Handle(consumer, incoming, ct);
                var seen = new SeenDelivery(consumer, incoming, handled);
                _seen.Enqueue(seen);
                return Answer?.Invoke(seen) ?? handled;
            }, _stop.Token)));
        }
        return this;
    }

    public void Given<T>(T aggregate)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        if (AggregateChanges.Prepare(aggregate).TryGetValue(out Violation? broken))
            throw new InvalidOperationException(broken.ToString());
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;
        var appended = Store.Append(aggregate, expected, OperationContext.Start(Actor.User("test"), TransportContract.TenantA, TimeProvider.System), Option.None,
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (appended.TryGetValue(out Violation? refused))
            throw new InvalidOperationException(refused.ToString());
        AggregateChanges.Committed(aggregate);
    }

    public T? Load<T>(Guid id)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => Store.Load((Id<T>)id, OperationContext.Start(Actor.User("test"), TransportContract.TenantA, TimeProvider.System), CancellationToken.None).AsTask()
            .GetAwaiter().GetResult().TryGetValue(out T? found) ? found : null;

    public IEnumerable<SeenDelivery> Of(string consumer, Guid messageId)
        => Seen.Where(s => s.Consumer == consumer && s.Incoming.Message?.Id == messageId);

    /// <summary>Waits until <paramref name="condition"/> holds; fails with <paramref name="what"/> and the deliveries seen.</summary>
    public async Task Until(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException($"{what} did not happen; seen: {string.Join("; ", Seen.Select(s => $"{s.Consumer} {s.Incoming.Topic} #{s.Incoming.Attempt} {s.Handled.Ack}"))}");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}

/// <summary>
/// The transport contract (docs/migration/messaging.md §3.1), written once and run against every <see cref="IMessageTransport"/>:
/// the consumers of the contract domain through a real transport, in real time. Each test gets a transport of its own
/// (a schema, a namespace or a vhost), so no test sees another's messages.
/// </summary>
public abstract class TransportContract
{
    public static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
    public static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000b");

    /// <summary>Short retries: 3 attempts, 100 ms, 200 ms.</summary>
    protected static readonly DeliveryPolicy Quick = new() { MaxAttempts = 3, FirstDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(400) };

    /// <summary>The generated consumers of the test project (<c>GeneratedConsumers.Set</c>).</summary>
    protected abstract ConsumerSet Consumers { get; }

    /// <summary>A transport on an isolated namespace of its own; every call with the same <paramref name="isolation"/> reaches the same messages.</summary>
    protected abstract Task<IMessageTransport> CreateTransport(string isolation);

    /// <summary>What the transport authenticates a publisher of <paramref name="source"/> as (<see cref="Incoming.AuthenticatedAs"/>); <see langword="null"/> = none.</summary>
    protected virtual string? AuthenticatedAs(string source) => null;

    /// <summary>Whether the transport drops a republished message by its dedup id (else the consumer's key replays it).</summary>
    protected abstract bool DropsDuplicates { get; }

    private static string NewIsolation() => "t" + Guid.NewGuid().ToString("N")[..10];

    private async Task<(ContractHarness Harness, IMessageTransport Publisher)> Start(DeliveryPolicy? policy = null, int concurrency = 1)
    {
        var isolation = NewIsolation();
        var harness = await new ContractHarness(await CreateTransport(isolation), Consumers, "billing", policy ?? Quick, concurrency).Start();
        var publisher = await CreateTransport(isolation);
        await publisher.Prepare(["artflow.auction-settled", "artflow.auction-noted", "billing.invoice-paid"], [], CancellationToken.None);
        return (harness, publisher);
    }

    protected static Message Envelope<T>(string contract, int version, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, Option<Id<ITenant>> tenant = default,
        string actor = "User:alice", Guid? id = null)
        => new()
        {
            Id = id ?? Guid.CreateVersion7(),
            Source = Message.SystemOf(contract),
            Type = contract,
            Version = version,
            Time = TimeProvider.System.GetUtcNow(),
            Subject = Guid.NewGuid().ToString("D"),
            Tenant = tenant,
            Correlation = Id<ICorrelation>.New(),
            Actor = actor,
            Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            Data = JsonSerializer.SerializeToUtf8Bytes(payload, type),
        };

    protected static Message Settled(Guid auction, decimal price = 150m, int version = 1)
        => Envelope("artflow.auction-settled", version, new AuctionSettledV1(auction, price), ContractJson.Default.AuctionSettledV1, TenantA);

    protected static Message Paid(Guid auction, Option<Id<ITenant>>? tenant = null)
        => Envelope("billing.invoice-paid", 1, new InvoicePaidV1(Guid.NewGuid(), auction, 150m), ContractJson.Default.InvoicePaidV1, tenant ?? TenantA);

    private static int Invoices(ContractHarness h, Guid auction) => h.Store.Recorded.Count(r => r.Event.Event is Invoice.Issued i && i.Auction == auction);

    private static async Task<IReadOnlyList<DeadMessage>> Dead(ContractHarness h) => await h.DeadLetters.List(100, CancellationToken.None);

    [Fact]
    public async Task A_valid_message_reaches_both_consumers_of_its_contract()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var auction = Guid.NewGuid();

        await publisher.Publish(Settled(auction), CancellationToken.None);

        await h.Until(() => Invoices(h, auction) == 1 && h.Load<Ledger>(auction)?.Sales == 1, "an invoice and a sale");
        h.Consumers.Subscriptions.Select(s => s.Name).ShouldBe(["billing.IssueInvoice", "billing.RecordPayment", "billing.Sales", "billing.TrackNote"]);
    }

    [Fact]
    public async Task The_consumer_acts_for_the_senders_actor_in_the_envelopes_tenant_and_conversation()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var message = Settled(Guid.NewGuid());

        await publisher.Publish(message, CancellationToken.None);

        await h.Until(() => h.Store.Recorded.Any(r => r.Event.Event is Invoice.Issued), "an invoice");
        var recorded = h.Store.Recorded.Single(r => r.Event.Event is Invoice.Issued);
        recorded.Context.Actor.ToString().ShouldBe("System:Consumers.IssueInvoice (for alice)");
        recorded.Context.Tenant.ShouldBe((Option<Id<ITenant>>)TenantA);
        recorded.Context.CorrelationId.ShouldBe(message.Correlation);
        recorded.Context.CausationId.ShouldBe((Option<Id<ICausation>>)(Id<ICausation>)message.Id);
        var delivery = h.Of("IssueInvoice", message.Id).First().Incoming;
        delivery.Topic.ShouldBe("artflow.auction-settled");
        delivery.AuthenticatedAs.ShouldBe(AuthenticatedAs("artflow"));
        delivery.Message.ShouldBe(message with { Data = delivery.Message!.Data });                     // every header survived
        delivery.Message.Data.ToArray().ShouldBe(message.Data.ToArray());
    }

    [Fact]
    public async Task A_lost_acknowledgement_redelivers_and_the_key_replays()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var lost = 0;
        h.Answer = seen => seen.Consumer == "IssueInvoice" && Interlocked.Increment(ref lost) == 1 ? new Handled(Ack.Retry, TimeSpan.FromMilliseconds(50)) : seen.Handled;
        var message = Settled(Guid.NewGuid());

        await publisher.Publish(message, CancellationToken.None);

        await h.Until(() => h.Of("IssueInvoice", message.Id).Count() == 2, "a redelivery");
        h.Of("IssueInvoice", message.Id).Select(s => s.Incoming.Attempt).ShouldBe([1, 2]);
        h.Store.Recorded.Count(r => r.Event.Event is Invoice.Issued).ShouldBe(1);
    }

    [Fact]
    public async Task A_republished_message_commits_once()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var auction = Guid.NewGuid();
        var message = Settled(auction);

        await publisher.Publish(message, CancellationToken.None);
        await publisher.Publish(message, CancellationToken.None);                                // a crash after publish, before complete
        await h.Until(() => Invoices(h, auction) == 1, "an invoice");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Invoices(h, auction).ShouldBe(1);
        h.Of("IssueInvoice", message.Id).Count().ShouldBe(DropsDuplicates ? 1 : 2);
    }

    [Fact]
    public async Task A_contract_without_a_subscription_is_published_and_not_delivered()
    {
        var (h, publisher) = await Start();
        await using var running = h;

        await publisher.Prepare(["artflow.unheard-of"], [], CancellationToken.None);
        await publisher.Publish(Envelope("artflow.unheard-of", 1, new AuctionNotedV1(Guid.NewGuid(), 1), ContractJson.Default.AuctionNotedV1), CancellationToken.None);
        var marker = Settled(Guid.NewGuid());
        await publisher.Publish(marker, CancellationToken.None);

        await h.Until(() => h.Of("Sales", marker.Id).Any(), "the marker");
        h.Seen.ShouldNotContain(s => s.Incoming.Topic == "artflow.unheard-of");
    }

    [Fact]
    public async Task An_unknown_version_and_an_invalid_mapping_are_buried_at_once()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var v3 = Settled(Guid.NewGuid(), version: 3);
        var zero = Settled(Guid.NewGuid(), price: 0m);

        await publisher.Publish(v3, CancellationToken.None);
        await publisher.Publish(zero, CancellationToken.None);

        await h.Until(() => Dead(h).Result.Count == 3 && h.Of("Sales", zero.Id).Any(), "three dead letters and a sale");
        var dead = await Dead(h);
        dead.Where(d => d.MessageId == v3.Id).Select(d => d.Reason).ShouldAllBe(r => r == "Messaging.UnknownVersion: artflow.auction-settled v3");
        dead.Where(d => d.MessageId == v3.Id).Select(d => d.Subscription).Order().ShouldBe(["billing.IssueInvoice", "billing.Sales"]);
        var invalid = dead.Single(d => d.MessageId == zero.Id);
        invalid.Subscription.ShouldBe("billing.IssueInvoice");
        invalid.Attempts.ShouldBe(1);
        invalid.Reason.ShouldStartWith("Invalid");
        h.Of("Sales", zero.Id).Single().Handled.Ack.ShouldBe(Ack.Ack);                           // the other consumer commits
    }

    [Fact]
    public async Task A_conflict_is_retried_until_its_precondition_commits()
    {
        var (h, publisher) = await Start(Quick with { MaxAttempts = 50 });
        await using var running = h;
        var auction = Auction.Open(Guid.NewGuid());
        h.Given(auction);
        var paid = Paid(auction.Id.Value);

        await publisher.Publish(paid, CancellationToken.None);
        await h.Until(() => h.Of("RecordPayment", paid.Id).Count() >= 2, "two attempts");
        var stored = h.Load<Auction>(auction.Id.Value)!;
        _ = stored.Settle();
        h.Given(stored);

        await h.Until(() => h.Load<Auction>(auction.Id.Value)!.Paid == 150m, "the payment");
        h.Of("RecordPayment", paid.Id).SkipLast(1).ShouldAllBe(s => s.Handled.Ack == Ack.Retry);
        h.Of("RecordPayment", paid.Id).Select(s => s.Incoming.Attempt).ShouldBe(Enumerable.Range(1, h.Of("RecordPayment", paid.Id).Count()));
    }

    [Fact]
    public async Task A_missing_target_is_retried_then_buried_and_re_driven_after_the_fix()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var auction = Guid.NewGuid();
        var paid = Paid(auction);

        await publisher.Publish(paid, CancellationToken.None);
        await h.Until(() => Dead(h).Result.Count == 1, "a dead letter");
        h.Of("RecordPayment", paid.Id).Select(s => s.Incoming.Attempt).ShouldBe([1, 2, 3]);
        var dead = (await Dead(h)).Single();
        dead.Attempts.ShouldBe(3);
        dead.Reason.ShouldContain("NotFound");

        var opened = Auction.Open(auction);
        _ = opened.Settle();
        h.Given(opened);
        var redriven = await h.Consumers.Redrive(dead.Id, CancellationToken.None);

        redriven!.Verdict.ShouldBe(Verdict.Done);
        h.Load<Auction>(auction)!.Paid.ShouldBe(150m);
    }

    [Fact]
    public async Task Poison_data_is_buried_with_its_bytes()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var poison = Settled(Guid.NewGuid()) with { Data = "not json"u8.ToArray() };

        await publisher.Publish(poison, CancellationToken.None);

        await h.Until(() => Dead(h).Result.Count == 2, "two dead letters");
        (await Dead(h)).ShouldAllBe(d => d.Reason.StartsWith("Messaging.Malformed", StringComparison.Ordinal) && d.Attempts == 1
            && System.Text.Encoding.UTF8.GetString(d.Data.ToArray()) == "not json" && d.Headers["ce-id"] == poison.Id.ToString("D"));
    }

    [Fact]
    public async Task A_forged_tenant_or_source_is_buried_and_a_forged_actor_is_never_the_actor()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var opened = Auction.Open(Guid.NewGuid());
        _ = opened.Settle();
        h.Given(opened);
        var otherTenant = Paid(opened.Id.Value, TenantB);                                               // billing may name tenant A only
        var wrongSource = Settled(Guid.NewGuid()) with { Source = "billing" };                            // ce-source disagrees with the prefix
        var admin = Settled(Guid.NewGuid()) with { Actor = "User:admin" };

        await publisher.Publish(otherTenant, CancellationToken.None);
        await publisher.Publish(wrongSource, CancellationToken.None);
        await publisher.Publish(admin, CancellationToken.None);

        await h.Until(() => Dead(h).Result.Count == 3 && h.Store.Recorded.Any(r => r.Event.Event is Invoice.Issued), "three dead letters and an invoice");
        var dead = await Dead(h);
        dead.Single(d => d.MessageId == otherTenant.Id).Reason.ShouldStartWith("Messaging.TenantNotAllowed");
        dead.Where(d => d.MessageId == wrongSource.Id).ShouldAllBe(d => d.Reason.StartsWith("Messaging.SourceMismatch", StringComparison.Ordinal));
        h.Store.Recorded.Single(r => r.Event.Event is Invoice.Issued).Context.Actor.ToString().ShouldBe("System:Consumers.IssueInvoice (for admin)");   // recorded, not trusted
    }

    [Fact]
    public async Task A_dual_publish_keeps_both_versions_and_the_v1_consumer_buries_v2()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var auction = Guid.NewGuid();
        var v1 = Settled(auction);
        var v2 = v1 with { Version = 2 };                                                               // one event, two contract versions

        await publisher.Publish(v1, CancellationToken.None);
        await publisher.Publish(v2, CancellationToken.None);

        await h.Until(() => Invoices(h, auction) == 1 && Dead(h).Result.Count == 2, "an invoice and two dead letters for v2");
        h.Of("IssueInvoice", v1.Id).Select(s => s.Incoming.Message!.Version).Order().ShouldBe([1, 2]);   // the transport kept v2
    }

    [Fact]
    public async Task One_stream_is_consumed_in_publish_order()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var auction = Guid.NewGuid();

        for (var n = 1; n <= 30; n++)
            await publisher.Publish(Envelope("artflow.auction-noted", 1, new AuctionNotedV1(auction, n), ContractJson.Default.AuctionNotedV1, TenantA), CancellationToken.None);

        await h.Until(() => h.Load<NoteLog>(auction)?.Notes.Count == 30, "30 notes");
        h.Load<NoteLog>(auction)!.Notes.ShouldBe(Enumerable.Range(1, 30));
    }

    [Fact]
    public async Task Messages_published_before_the_consumer_runs_wait_for_it()
    {
        var isolation = NewIsolation();
        var transport = await CreateTransport(isolation);
        var declare = new ContractHarness(transport, Consumers, "billing", Quick);
        await transport.Prepare([], declare.Consumers.Subscriptions, CancellationToken.None);            // the subscription exists, nobody consumes
        var publisher = await CreateTransport(isolation);
        await publisher.Prepare(["artflow.auction-settled"], [], CancellationToken.None);
        var auction = Guid.NewGuid();
        await publisher.Publish(Settled(auction), CancellationToken.None);

        await using var h = await new ContractHarness(await CreateTransport(isolation), Consumers, "billing", Quick).Start();

        await h.Until(() => Invoices(h, auction) == 1, "the waiting message");
    }

    [Fact]
    public async Task A_retry_waits_for_its_delay()
    {
        var (h, publisher) = await Start();
        await using var running = h;
        var times = new ConcurrentQueue<DateTimeOffset>();
        h.Answer = seen =>
        {
            if (seen.Consumer == "IssueInvoice")
                times.Enqueue(TimeProvider.System.GetUtcNow());
            return seen.Consumer == "IssueInvoice" && times.Count == 1 ? new Handled(Ack.Retry, TimeSpan.FromSeconds(1)) : seen.Handled;
        };

        await publisher.Publish(Settled(Guid.NewGuid()), CancellationToken.None);

        await h.Until(() => times.Count == 2, "the retry");
        var both = times.ToArray();
        (both[1] - both[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    }
}
