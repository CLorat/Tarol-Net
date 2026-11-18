using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>What happened to one delivery.</summary>
/// <param name="Consumer">The consumer.</param>
/// <param name="Incoming">The delivery.</param>
/// <param name="Verdict">Done (also dropped), retried or buried.</param>
/// <param name="Reason">Why it was not done, or why it was dropped.</param>
/// <param name="Outcome">The commit (or the replayed one) when it was done.</param>
public sealed record ConsumerAttempt(string Consumer, Incoming Incoming, Verdict Verdict, Option<string> Reason, ChangeOutcome? Outcome);

/// <summary>
/// The consumers of an application (docs/architecture/messaging.md §3): for each delivery the trust rules (M-13), the
/// context (M-6) and the key (M-12), the generated switch, <see cref="Delivery.Classify"/> and the
/// <see cref="Sagas.DeliveryPolicy"/>, dead letters (M-9). Every exception but cancellation becomes a retry, so a
/// transport's loop never sees one.
/// </summary>
/// <remarks>
/// Each delivery runs in an <see cref="ActivityKind.Consumer"/> activity <c>consume &lt;contract&gt;</c> of
/// <see cref="ActivitySourceName"/>, linked to the message's <c>traceparent</c> (never its child).
/// </remarks>
public sealed partial class MessageConsumers
{
    /// <summary>The name of the <see cref="ActivitySource"/> of consumed messages: add it to your tracing.</summary>
    public const string ActivitySourceName = "Tarol.Messaging";

    /// <summary>The name of the messaging meter (step 15): <c>tarol.messages.consumed</c>, <c>.published</c>, <c>.duration</c>.</summary>
    public const string MeterName = "Tarol.Messaging";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    internal static readonly Meter SharedMeter = new(MeterName);

    private readonly ConsumerSet _set;
    private readonly Counter<long> _consumed;
    private readonly Histogram<double> _duration;
    private readonly ChangePipeline _pipeline;
    private readonly IMessageDeadLetters _dead;
    private readonly MessagingOptions _options;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger _log;

    /// <summary>Creates the consumers.</summary>
    /// <param name="set">The application's consumers (<c>GeneratedConsumers.Set</c>, or <see cref="ConsumerSet.Combine"/>).</param>
    /// <param name="pipeline">The pipeline the changes run through.</param>
    /// <param name="dead">Where given-up messages go.</param>
    /// <param name="options">Application name, trust, policy.</param>
    /// <param name="json">The application's JSON options (its source-generated context) for the contract records.</param>
    /// <param name="logger">Logs retries, dead letters and dropped messages; <see langword="null"/> = none.</param>
    /// <param name="meters">Creates the meter <see cref="MeterName"/>; <see langword="null"/> = a shared meter.</param>
    public MessageConsumers(ConsumerSet set, ChangePipeline pipeline, IMessageDeadLetters dead, MessagingOptions options, JsonSerializerOptions json,
        ILogger? logger = null, IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(dead);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(json);
        options.Validate();
        _set = set;
        _pipeline = pipeline;
        _dead = dead;
        _options = options;
        _json = json;
        _log = logger ?? NullLogger.Instance;
        var meter = meters?.Create(MeterName) ?? SharedMeter;
        _consumed = meter.CreateCounter<long>("tarol.messages.consumed", "{message}", "Deliveries handled, by consumer and outcome (completed, dropped, retried, buried).");
        _duration = meter.CreateHistogram<double>("tarol.messages.duration", "s", "Duration of handling a delivery, its change included.");
        Subscriptions = [.. set.Consumers.Select(c => new Subscription(SubscriptionOf(c.Name), c.Topics) { Concurrency = options.Concurrency, Lease = options.Lease })];
    }

    /// <summary>One subscription per consumer: <c>&lt;application&gt;.&lt;consumer&gt;</c>.</summary>
    public IReadOnlyList<Subscription> Subscriptions { get; }

    /// <summary>The dead letters.</summary>
    public IMessageDeadLetters DeadLetters => _dead;

    internal Action<ConsumerAttempt>? Observed { get; set; }

    /// <summary>
    /// Consumes every subscription on <paramref name="transport"/> until cancelled (after <see cref="IMessageTransport.Prepare"/>).
    /// When one subscription stops (its transport failed), the others are stopped too and the failure is thrown, so the caller
    /// (the <see cref="MessagingHost"/>) declares and consumes all of them again; a subscription never stays dead alone.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">Stops consuming.</param>
    /// <returns>A task that completes when every subscription stopped.</returns>
    /// <exception cref="InvalidOperationException">A subscription stopped without a failure and without being cancelled.</exception>
    public async Task Run(IMessageTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (_set.Consumers.Count == 0)
            return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = _set.Consumers.Select((c, i) => transport.Consume(Subscriptions[i], (incoming, ct) => Handle(c.Name, incoming, ct), stop.Token)).ToArray();
        var first = await Task.WhenAny(running).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            await Task.WhenAll(running).ConfigureAwait(false);                              // the caller stops them all
            return;
        }
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the first stop is the cause; the others were stopped because of it
        }
        await first.ConfigureAwait(false);                                                  // throws its failure
        throw new InvalidOperationException($"The transport stopped consuming {Subscriptions[Array.IndexOf(running, first)].Name} without a failure.");
    }

    /// <summary>
    /// Re-drives a dead letter: runs it again in this process as attempt 1, through the same checks; given up again (a new
    /// dead letter) if it fails again, transiently or not, or if the run throws. A letter given up for
    /// <c>Messaging.SourceMismatch</c> is given up again without a run: the transport's authentication is not kept with it.
    /// The key still holds, so a message whose change did commit is not repeated.
    /// </summary>
    /// <param name="id">The dead letter's id.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What happened, or <see langword="null"/> when there is no such dead letter.</returns>
    public Task<ConsumerAttempt?> Redrive(long id, CancellationToken cancellationToken = default) => Redrive(id, null, cancellationToken);

    /// <summary>As <see cref="Redrive(long, CancellationToken)"/>, on behalf of <paramref name="actor"/> (the store audits the take; step 15).</summary>
    /// <param name="id">The dead letter's id.</param>
    /// <param name="actor">Who re-drives it; <see langword="null"/> = unaudited.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What happened, or <see langword="null"/> when there is no such dead letter.</returns>
    public async Task<ConsumerAttempt?> Redrive(long id, string? actor, CancellationToken cancellationToken = default)
    {
        var taken = actor is null ? _dead.Take(id, cancellationToken) : _dead.Take(id, actor, cancellationToken);
        if (await taken.ConfigureAwait(false) is not { } letter)
            return null;
        var consumer = letter.Subscription.StartsWith(_options.Application + ".", StringComparison.Ordinal)
            ? letter.Subscription[(_options.Application.Length + 1)..]
            : letter.Subscription;
        var message = Message.From(h => letter.Headers.TryGetValue(h, out var v) ? v : null, letter.Data);
        var incoming = new Incoming(message, 1, null, letter.Data, letter.Topic);

        // the transport's authentication is not kept with the letter, so a source mismatch cannot be checked again: it stays
        // dead (re-driving it would skip the check, M-13); nothing about a mismatch changes between the attempts
        if (letter.Reason.StartsWith(SourceMismatch, StringComparison.Ordinal))
            return (await Bury(consumer, letter.Subscription, incoming, letter.Reason, null, CancellationToken.None).ConfigureAwait(false)).Item2;

        // the letter was taken (deleted) above: whatever happens now, it must end done or dead again, never lost
        ConsumerAttempt attempt;
        try
        {
            (_, attempt) = await HandleCore(consumer, incoming, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var reason = e is OperationCanceledException ? letter.Reason : "Messaging.HostFailed: " + e.GetType().Name + ": " + e.Message;
            await _dead.Add(letter.Subscription, incoming, reason, CancellationToken.None).ConfigureAwait(false);
            Buried(_log, consumer, message?.Id, incoming.Attempt, reason);
            throw;
        }
        if (attempt.Verdict != Verdict.Retry)
            return attempt;
        var again = attempt.Reason.TryGetValue(out string? why) ? why : letter.Reason;   // no transport to retry it: dead again
        return (await Bury(consumer, letter.Subscription, incoming, again, null, CancellationToken.None).ConfigureAwait(false)).Item2;
    }

    private const string SourceMismatch = "Messaging.SourceMismatch";

    /// <summary>Handles one delivery for <paramref name="consumer"/>; throws only when <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="consumer">The consumer's name.</param>
    /// <param name="incoming">The delivery.</param>
    /// <param name="cancellationToken">Cancels the run (the transport then delivers it again after the lease).</param>
    /// <returns>Ack (done, dropped or buried) or retry with the policy's backoff.</returns>
    public async ValueTask<Handled> Handle(string consumer, Incoming incoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(incoming);
        try
        {
            return (await HandleCore(consumer, incoming, cancellationToken).ConfigureAwait(false)).Handled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a dead-letter store that is down, or a bug outside the change: the delivery comes back
            var reason = "Messaging.HostFailed: " + e.GetType().Name + ": " + e.Message;
            Retrying(_log, consumer, incoming.Message?.Id, incoming.Attempt, reason);
            return new Handled(Ack.Retry, _options.Policy.Backoff(JitterKey(incoming), incoming.Attempt));
        }
    }

    private async ValueTask<(Handled Handled, ConsumerAttempt Attempt)> HandleCore(string consumer, Incoming incoming, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var handled = await HandleInner(consumer, incoming, cancellationToken).ConfigureAwait(false);
        var attempt = handled.Attempt;
        var outcome = attempt.Verdict switch
        {
            Verdict.Done => attempt.Reason.IsSome ? "dropped" : "completed",
            Verdict.Retry => "retried",
            _ => "buried",
        };
        var tags = new TagList { { "tarol.consumer", consumer }, { "tarol.outcome", outcome } };
        _consumed.Add(1, tags);
        _duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        return handled;
    }

    private async ValueTask<(Handled Handled, ConsumerAttempt Attempt)> HandleInner(string consumer, Incoming incoming, CancellationToken cancellationToken)
    {
        var subscription = SubscriptionOf(consumer);
        if (incoming.Message is not { } message)
            return await Bury(consumer, subscription, incoming, "Messaging.Malformed: the envelope has no valid CloudEvents headers", null, cancellationToken).ConfigureAwait(false);

        ActivityLink[]? links = message.Traceparent is { } traceparent && ActivityContext.TryParse(traceparent, null, out var origin)
            ? [new ActivityLink(origin)]
            : null;
        using var activity = Source.StartActivity(ActivityKind.Consumer, default(ActivityContext), links: links, name: "consume " + message.Type);
        activity?.SetTag("messaging.destination.name", message.Type);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("messaging.consumer.group.name", subscription);
        activity?.SetTag("tarol.consumer", consumer);
        activity?.SetTag("tarol.attempt", incoming.Attempt);

        // M-13: the contract's prefix is the source; the envelope and the transport must agree with it
        var source = Message.SystemOf(message.Type);
        if (source.Length == 0 || !string.Equals(message.Source, source, StringComparison.Ordinal)
            || (incoming.AuthenticatedAs is { } who && !string.Equals(who, source, StringComparison.Ordinal)))
            return await Bury(consumer, subscription, incoming,
                $"{SourceMismatch}: contract {message.Type}, ce-source {message.Source}, authenticated {incoming.AuthenticatedAs ?? "-"}", activity, cancellationToken)
                .ConfigureAwait(false);
        if (!_options.Sources.TryGetValue(source, out var trust))
            return await Bury(consumer, subscription, incoming, "Messaging.UnknownSource: " + source, activity, cancellationToken).ConfigureAwait(false);
        if (!trust.Allows(message.Tenant))
            return await Bury(consumer, subscription, incoming,
                $"Messaging.TenantNotAllowed: {source} may not act for tenant {(message.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty)}", activity, cancellationToken)
                .ConfigureAwait(false);

        // M-6: the system consuming on behalf of the other system's actor; M-12: the key is the consumer's
        var actor = Actor.System("Consumers." + consumer, message.ActorSubject is { } subject ? subject : Option.None);
        var operation = new OperationContext(actor, message.Tenant, message.Correlation, (Id<ICausation>)message.Id);
        var key = new IdempotencyKey(message.Tenant, "consumer:" + consumer, message.Id.ToString("N", CultureInfo.InvariantCulture));
        var scope = new MessageScope(operation, new ChangeOptions(key), _json);

        ConsumerRun run;
        try
        {
            run = await _set.Run(consumer, _pipeline, message, scope, cancellationToken).ConfigureAwait(false);
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
                return await Bury(consumer, subscription, incoming, $"Messaging.UnknownVersion: {message.Type} v{message.Version}", activity, cancellationToken)
                    .ConfigureAwait(false);
            case Mapped.UnknownContract:
                Dropped(_log, consumer, message.Type, message.Id);
                activity?.SetTag("tarol.verdict", "Dropped");
                return Done(consumer, incoming, "Messaging.UnknownContract", null);
        }

        var verdict = Delivery.Classify(run.Result);
        if (verdict == Verdict.Done)
        {
            activity?.SetTag("tarol.verdict", Verdict.Done.ToString());
            return Done(consumer, incoming, null, run.Result.GetValueOrThrow());
        }
        var reason = run.Result.TryGetValue(out Violation? violation) ? Describe(violation) : "unknown";
        return verdict == Verdict.Retry
            ? await Retry(consumer, subscription, incoming, reason, activity, cancellationToken).ConfigureAwait(false)
            : await Bury(consumer, subscription, incoming, reason, activity, cancellationToken).ConfigureAwait(false);
    }

    private (Handled, ConsumerAttempt) Done(string consumer, Incoming incoming, string? reason, ChangeOutcome? outcome)
    {
        var attempt = new ConsumerAttempt(consumer, incoming, Verdict.Done, reason is null ? Option.None : reason, outcome);
        Observed?.Invoke(attempt);
        return (new Handled(Ack.Ack), attempt);
    }

    private async ValueTask<(Handled, ConsumerAttempt)> Retry(string consumer, string subscription, Incoming incoming, string reason, Activity? activity,
        CancellationToken cancellationToken)
    {
        if (incoming.Attempt >= _options.Policy.MaxAttempts)
            return await Bury(consumer, subscription, incoming, reason, activity, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("tarol.verdict", Verdict.Retry.ToString());
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        Retrying(_log, consumer, incoming.Message?.Id, incoming.Attempt, reason);
        var attempt = new ConsumerAttempt(consumer, incoming, Verdict.Retry, reason, null);
        Observed?.Invoke(attempt);
        return (new Handled(Ack.Retry, _options.Policy.Backoff(JitterKey(incoming), incoming.Attempt)), attempt);
    }

    private async ValueTask<(Handled, ConsumerAttempt)> Bury(string consumer, string subscription, Incoming incoming, string reason, Activity? activity,
        CancellationToken cancellationToken)
    {
        activity?.SetTag("tarol.verdict", Verdict.Bury.ToString());
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        await _dead.Add(subscription, incoming, reason, cancellationToken).ConfigureAwait(false);
        Buried(_log, consumer, incoming.Message?.Id, incoming.Attempt, reason);
        var attempt = new ConsumerAttempt(consumer, incoming, Verdict.Bury, reason, null);
        Observed?.Invoke(attempt);
        return (new Handled(Ack.Ack), attempt);                        // M-9: the dead letter is the consumer's, the transport forgets it
    }

    private string SubscriptionOf(string consumer) => _options.Application + "." + consumer;

    private static long JitterKey(Incoming incoming)
        => incoming.Message is { } message ? BitConverter.ToInt64(message.Id.ToByteArray(), 8) : incoming.Raw.Length;

    private static string Describe(Violation violation) => violation switch
    {
        Error e => $"{e.Type} {e.Code}" + (e.Detail is null ? "" : ": " + e.Detail),
        Invalid i => "Invalid " + string.Join(", ", i.Errors.Select(static f => f.Field + " " + f.Code)),
        _ => violation.ToString(),
    };

    [LoggerMessage(EventId = 11001, Level = LogLevel.Warning, Message = "Consumer {Consumer} failed message {MessageId} at attempt {Attempt}; it will be delivered again: {Reason}")]
    private static partial void Retrying(ILogger logger, string consumer, Guid? messageId, int attempt, string reason);

    [LoggerMessage(EventId = 11002, Level = LogLevel.Error, Message = "Consumer {Consumer} gave up message {MessageId} at attempt {Attempt}; it is a dead letter: {Reason}")]
    private static partial void Buried(ILogger logger, string consumer, Guid? messageId, int attempt, string reason);

    [LoggerMessage(EventId = 11003, Level = LogLevel.Warning, Message = "Consumer {Consumer} does not read contract {Contract}; message {MessageId} was dropped (check the subscription's topics)")]
    private static partial void Dropped(ILogger logger, string consumer, string contract, Guid messageId);
}
