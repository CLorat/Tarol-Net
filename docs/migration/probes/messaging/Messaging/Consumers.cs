using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>
/// On a static factory of a change (candidate K-A): the change consumes <paramref name="contract"/> in
/// <paramref name="version"/>. The factory is the anti-corruption layer, <c>(TPayload)</c> or <c>(TPayload, Message)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ConsumesAttribute(string contract, int version) : Attribute
{
    /// <summary>The contract, prefixed by its owning system.</summary>
    public string Contract { get; } = contract;
    /// <summary>The contract version this factory reads.</summary>
    public int Version { get; } = version;
    /// <summary>The consumer's stable name (subscription and key scope); default: the change's type name.</summary>
    public string? Name { get; init; }
}

/// <summary>One consumer of an application: its name and the contracts it receives.</summary>
public sealed record ConsumerInfo(string Name, IReadOnlyList<string> Topics);

/// <summary>What the generated switch did with a message.</summary>
public enum Mapped
{
    /// <summary>The factory ran and the pipeline answered.</summary>
    Ran,
    /// <summary>The data does not match the contract record (422 in a webhook).</summary>
    Malformed,
    /// <summary>A known contract in a version no factory reads (R-10: buried by default).</summary>
    UnknownVersion,
    /// <summary>A contract this consumer does not read (acknowledged and dropped).</summary>
    UnknownContract,
}

/// <summary>The switch's answer.</summary>
public readonly record struct ConsumerRun(Mapped Mapped, Result<ChangeOutcome> Result)
{
    /// <summary>The switch did not run the pipeline.</summary>
    public static ConsumerRun Not(Mapped mapped) => new(mapped, Error.Internal("Messaging.NotRun"));
}

/// <summary>The scope a consumer's change runs in, built by the host from the envelope and the trust rules.</summary>
public sealed class MessageScope(OperationContext operation, ChangeOptions options, JsonSerializerOptions json)
{
    /// <summary>The operation: <c>Actor.Service(source)</c> on behalf of the envelope's actor, the envelope's tenant and correlation, caused by the message.</summary>
    public OperationContext Operation { get; } = operation;
    /// <summary>The key (tenant, <c>consumer:&lt;name&gt;</c>, message id).</summary>
    public ChangeOptions Options { get; } = options;

    /// <summary>The contract record from the data, through the application's JSON context; <see langword="null"/> when it does not match.</summary>
    public T? Payload<T>(ReadOnlyMemory<byte> data)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(data.Span, (JsonTypeInfo<T>)json.GetTypeInfo(typeof(T)));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The generated switch (consumer, contract, version) → payload → factory → pipeline.</summary>
public delegate ValueTask<ConsumerRun> ConsumerRunner(string consumer, ChangePipeline pipeline, Message message, MessageScope scope, CancellationToken cancellationToken);

/// <summary>The consumers of one assembly (generated <c>GeneratedConsumers.Set</c>).</summary>
public sealed class ConsumerSet(IReadOnlyList<ConsumerInfo> consumers, ConsumerRunner runner)
{
    /// <summary>The consumers.</summary>
    public IReadOnlyList<ConsumerInfo> Consumers { get; } = consumers;
    /// <summary>The switch.</summary>
    public ConsumerRunner Runner { get; } = runner;
}

/// <summary>What a consumer trusts of one source system's envelopes.</summary>
public sealed class SourceTrust
{
    /// <summary>Any tenant the envelope names (a system that serves every tenant, e.g. a shared billing).</summary>
    public bool AnyTenant { get; set; }
    /// <summary>Otherwise the tenants it may name.</summary>
    public HashSet<Guid> Tenants { get; } = [];
}

/// <summary>Probe switch for the key scope.</summary>
public enum KeyScope
{
    /// <summary><c>consumer:&lt;consumer name&gt;</c> (proposed).</summary>
    Consumer,
    /// <summary><c>consumer:&lt;contract&gt;</c> (the plan's first wording).</summary>
    Contract,
}

/// <summary>Which actor a consumed message runs as (the Kernel allows <c>OnBehalfOf</c> only for a System actor).</summary>
public enum ActorRule
{
    /// <summary><c>Actor.Service(source)</c>; the envelope's actor is not recorded.</summary>
    Service,
    /// <summary><c>Actor.System("Consumers.&lt;name&gt;", onBehalfOf: the envelope's actor id)</c>, like a reaction.</summary>
    SystemOnBehalf,
}

/// <summary>Consumer configuration.</summary>
public sealed class ConsumerOptions
{
    /// <summary>This application's name: the prefix of its subscriptions (<c>billing.IssueInvoice</c>).</summary>
    public required string Application { get; init; }
    /// <summary>The source systems this application accepts, by the prefix of their contracts.</summary>
    public Dictionary<string, SourceTrust> Sources { get; } = new(StringComparer.Ordinal);
    /// <summary>Attempts and backoff (the reaction worker's policy).</summary>
    public DeliveryPolicy Policy { get; set; } = new();
    /// <summary>Probe: the key scope.</summary>
    public KeyScope KeyScope { get; set; } = KeyScope.Consumer;
    /// <summary>Which actor the change runs as.</summary>
    public ActorRule ActorRule { get; set; } = ActorRule.Service;
    /// <summary>Probe: take the envelope's actor as the actor (what a forged actor would get).</summary>
    public bool TrustEnvelopeActor { get; set; }
}

/// <summary>A message given up by a consumer (DL-1: kept by the consumer, transport-independent).</summary>
public sealed record DeadMessage(long Id, string Subscription, string Topic, Guid? MessageId, int Attempts, string Reason, byte[] Headers, byte[] Data, DateTimeOffset DeadAt);

/// <summary>Where a consumer keeps its dead letters.</summary>
public interface IMessageDeadLetters
{
    /// <summary>Adds one.</summary>
    Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken);
    /// <summary>The oldest ones.</summary>
    Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken);
    /// <summary>Removes one and returns it (to re-drive it).</summary>
    Task<DeadMessage?> Take(long id, CancellationToken cancellationToken);
}

/// <summary>In-memory dead letters.</summary>
public sealed class InMemoryDeadLetters(TimeProvider time) : IMessageDeadLetters
{
    private readonly List<DeadMessage> _dead = [];
    private long _next;

    /// <inheritdoc />
    public Task Add(string subscription, Incoming incoming, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (_dead)
            _dead.Add(new DeadMessage(++_next, subscription, incoming.Topic, incoming.Message?.Id, incoming.Attempt, reason,
                JsonSerializer.SerializeToUtf8Bytes(incoming.Message is null ? [] : incoming.Message.Headers().ToDictionary(), MessagingJson.Default.DictionaryStringString),
                incoming.Raw.ToArray(), time.GetUtcNow()));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadMessage>> List(int max, CancellationToken cancellationToken)
    {
        lock (_dead)
            return Task.FromResult<IReadOnlyList<DeadMessage>>([.. _dead.Take(max)]);
    }

    /// <inheritdoc />
    public Task<DeadMessage?> Take(long id, CancellationToken cancellationToken)
    {
        lock (_dead)
        {
            var found = _dead.Find(d => d.Id == id);
            if (found is not null)
                _dead.Remove(found);
            return Task.FromResult(found);
        }
    }
}

/// <summary>What happened to one delivery (for tests and the probe).</summary>
public sealed record ConsumerAttempt(string Consumer, Incoming Incoming, Verdict Verdict, string? Reason, ChangeOutcome? Outcome);

/// <summary>
/// Runs an application's consumers: one durable subscription per consumer, the envelope checked against the trust rules,
/// the context built, the generated switch called, the answer classified with <see cref="Delivery.Classify"/>, dead letters kept.
/// </summary>
public sealed partial class ConsumerHost(ConsumerSet set, ChangePipeline pipeline, IMessageDeadLetters dead, ConsumerOptions options,
    JsonSerializerOptions json, TimeProvider time)
{
    /// <summary>The source of consumer activities.</summary>
    public const string ActivitySourceName = "Tarol.Messaging";
    private static readonly ActivitySource Source = new(ActivitySourceName);
    private readonly TimeProvider _time = time;

    /// <summary>The clock (backoff is computed by the policy; the transport schedules the redelivery).</summary>
    public TimeProvider Time => _time;

    /// <summary>Observes every attempt.</summary>
    public Action<ConsumerAttempt>? Observed { get; set; }

    /// <summary>Probe: after a commit, answer Retry instead of Ack (the ack was lost: the transport redelivers).</summary>
    public Func<string, Message, bool>? LoseAck { get; set; }

    /// <summary>The subscriptions of the consumers.</summary>
    public IReadOnlyList<Subscription> Subscriptions(int concurrency = 1)
        => [.. set.Consumers.Select(c => new Subscription(options.Application + "." + c.Name, c.Topics) { Concurrency = concurrency })];

    /// <summary>Consumes until cancelled.</summary>
    public Task Run(IMessageTransport transport, CancellationToken cancellationToken, int concurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Task.WhenAll(set.Consumers.Select(c => transport.Consume(
            new Subscription(options.Application + "." + c.Name, c.Topics) { Concurrency = concurrency },
            (incoming, ct) => Handle(c.Name, incoming, ct), cancellationToken)));
    }

    /// <summary>Re-drives a dead letter: runs it again in process, as attempt 1; buried again if it fails again.</summary>
    public async Task<Verdict?> Redrive(long id, CancellationToken cancellationToken)
    {
        if (await dead.Take(id, cancellationToken).ConfigureAwait(false) is not { } letter)
            return null;
        var headers = JsonSerializer.Deserialize(letter.Headers, MessagingJson.Default.DictionaryStringString) ?? [];
        var message = Message.From(h => headers.TryGetValue(h, out var v) ? v : null, letter.Data);
        var consumer = letter.Subscription[(options.Application.Length + 1)..];
        Verdict? verdict = null;
        var observed = Observed;
        Observed = a => { verdict = a.Verdict; observed?.Invoke(a); };
        try
        {
            _ = await Handle(consumer, new Incoming(message, 1, null, letter.Data, letter.Topic), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Observed = observed;
        }
        return verdict;
    }

    /// <summary>Handles one delivery for <paramref name="consumer"/>; never throws (but for cancellation): an exception is a retry.</summary>
    public async ValueTask<Handled> Handle(string consumer, Incoming incoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        try
        {
            return await HandleCore(consumer, incoming, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return await Retry(consumer, options.Application + "." + consumer, incoming, "host: " + e.GetType().Name + ": " + e.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Handled> HandleCore(string consumer, Incoming incoming, CancellationToken cancellationToken)
    {
        var subscription = options.Application + "." + consumer;
        if (incoming.Message is not { } message)
            return await Bury(consumer, subscription, incoming, "Messaging.Malformed: no CloudEvents headers", null, cancellationToken).ConfigureAwait(false);

        ActivityLink[]? links = message.Traceparent is { } tp && ActivityContext.TryParse(tp, null, out var origin) ? [new ActivityLink(origin)] : null;
        using var activity = Source.StartActivity(ActivityKind.Consumer, default(ActivityContext), links: links, name: "consume " + message.Type);
        activity?.SetTag("messaging.destination.name", message.Type);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("messaging.consumer.group.name", subscription);
        activity?.SetTag("tarol.attempt", incoming.Attempt);

        // trust: the source is the contract's prefix; the envelope and the transport must agree with it
        var dot = message.Type.IndexOf('.', StringComparison.Ordinal);
        var source = dot > 0 ? message.Type[..dot] : "";
        if (source.Length == 0 || !string.Equals(message.Source, source, StringComparison.Ordinal)
            || (incoming.AuthenticatedAs is { } who && !string.Equals(who, source, StringComparison.Ordinal)))
            return await Bury(consumer, subscription, incoming, $"Messaging.SourceMismatch: type {message.Type}, ce-source {message.Source}, authenticated {incoming.AuthenticatedAs ?? "-"}", null, cancellationToken).ConfigureAwait(false);
        if (!options.Sources.TryGetValue(source, out var trust))
            return await Bury(consumer, subscription, incoming, "Messaging.UnknownSource: " + source, null, cancellationToken).ConfigureAwait(false);
        if (message.Tenant.TryGetValue(out Id<ITenant> tenant) && !trust.AnyTenant && !trust.Tenants.Contains(tenant.Value))
            return await Bury(consumer, subscription, incoming, $"Messaging.TenantNotAllowed: {source} may not act for tenant {tenant.Value}", null, cancellationToken).ConfigureAwait(false);

        var claimed = message.Actor?.Split(':');
        var actor = options.TrustEnvelopeActor && claimed is [var kind, var id, ..]
            ? new Actor(Enum.Parse<ActorKind>(kind), id)
            : options.ActorRule == ActorRule.SystemOnBehalf
                ? Actor.System("Consumers." + consumer, claimed is [_, var behalf, ..] ? behalf : Option.None)
                : Actor.Service(source);
        var operation = new OperationContext(actor, message.Tenant, message.Correlation, (Id<ICausation>)message.Id);
        var key = new IdempotencyKey(message.Tenant, "consumer:" + (options.KeyScope == KeyScope.Consumer ? consumer : message.Type),
            message.Id.ToString("N", CultureInfo.InvariantCulture));
        var scope = new MessageScope(operation, new ChangeOptions(key), json);

        ConsumerRun run;
        try
        {
            run = await set.Runner(consumer, pipeline, message, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return await Retry(consumer, subscription, incoming, e.GetType().Name + ": " + e.Message, activity, cancellationToken).ConfigureAwait(false);
        }
        switch (run.Mapped)
        {
            case Mapped.Malformed:
                return await Bury(consumer, subscription, incoming, "Messaging.Malformed: the data does not match the contract", activity, cancellationToken).ConfigureAwait(false);
            case Mapped.UnknownVersion:
                return await Bury(consumer, subscription, incoming, $"Messaging.UnknownVersion: {message.Type} v{message.Version}", activity, cancellationToken).ConfigureAwait(false);
            case Mapped.UnknownContract:
                Observed?.Invoke(new ConsumerAttempt(consumer, incoming, Verdict.Done, "Messaging.UnknownContract (dropped)", null));
                return new Handled(Ack.Ack);
        }
        var verdict = Delivery.Classify(run.Result);
        var reason = run.Result.TryGetValue(out Violation? violation) ? Describe(violation) : null;
        if (verdict == Verdict.Done)
        {
            activity?.SetTag("tarol.verdict", "Done");
            Observed?.Invoke(new ConsumerAttempt(consumer, incoming, Verdict.Done, null, run.Result.GetValueOrThrow()));
            return LoseAck?.Invoke(consumer, message) == true ? new Handled(Ack.Retry, TimeSpan.FromMilliseconds(100)) : new Handled(Ack.Ack);
        }
        return verdict == Verdict.Retry
            ? await Retry(consumer, subscription, incoming, reason!, activity, cancellationToken).ConfigureAwait(false)
            : await Bury(consumer, subscription, incoming, reason!, activity, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Handled> Retry(string consumer, string subscription, Incoming incoming, string reason, Activity? activity, CancellationToken cancellationToken)
    {
        if (incoming.Attempt >= options.Policy.MaxAttempts)
            return await Bury(consumer, subscription, incoming, reason, activity, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("tarol.verdict", "Retry");
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        Observed?.Invoke(new ConsumerAttempt(consumer, incoming, Verdict.Retry, reason, null));
        var jitterKey = incoming.Message is { } m ? BitConverter.ToInt64(m.Id.ToByteArray(), 8) : 0;
        return new Handled(Ack.Retry, options.Policy.Backoff(jitterKey, incoming.Attempt));
    }

    private async ValueTask<Handled> Bury(string consumer, string subscription, Incoming incoming, string reason, Activity? activity, CancellationToken cancellationToken)
    {
        activity?.SetTag("tarol.verdict", "Bury");
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        await dead.Add(subscription, incoming, reason, cancellationToken).ConfigureAwait(false);
        Observed?.Invoke(new ConsumerAttempt(consumer, incoming, Verdict.Bury, reason, null));
        return new Handled(Ack.Ack);                                  // DL-1: the dead letter is ours, the transport forgets it
    }

    private static string Describe(Violation violation) => violation switch
    {
        Error e => $"{e.Type} {e.Code}",
        Invalid i => "Invalid " + string.Join(", ", i.Errors.Select(static f => f.Field + " " + f.Code)),
        _ => violation.ToString(),
    };

    internal static string Now(TimeProvider t) => t.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>The library's own JSON metadata.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class MessagingJson : System.Text.Json.Serialization.JsonSerializerContext;
