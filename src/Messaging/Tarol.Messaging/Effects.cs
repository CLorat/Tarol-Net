using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Messaging;

/// <summary>
/// An <b>effect</b> (S-2): a call to the outside world (an e-mail, a payment provider, an HTTP API) when
/// <typeparamref name="TEvent"/> commits. The store writes an outbox row <c>effect:&lt;name&gt;</c> in the commit; the
/// reaction worker runs it at least once, so the receiver deduplicates by <see cref="EffectContext.IdempotencyKey"/>
/// (the event id). Register it with <c>AddMessaging(…, m =&gt; m.Effect&lt;TEvent, THandler&gt;("name"))</c>.
/// </summary>
/// <typeparam name="TEvent">The committed event.</typeparam>
/// <example>
/// <code>
/// public sealed class SendReceipt(IMailer mailer) : IEffect&lt;Auction.PaymentRecorded&gt;
/// {
///     public async ValueTask&lt;Result&gt; Run(Auction.PaymentRecorded e, EffectContext context, CancellationToken cancellationToken)
///     {
///         await mailer.Send(e.Buyer, "Receipt " + e.Invoice, context.IdempotencyKey, cancellationToken);
///         return Result.Success();
///     }
/// }
/// </code>
/// </example>
/// <remarks>
/// The result is classified like a change's (<see cref="Delivery.Classify"/>): <c>Error.Unavailable</c>, a timeout or a
/// conflict retry with the worker's backoff; <c>Invalid</c>, Forbidden or Gone bury the row in <c>outbox_dead</c>. An
/// exception is a retry. A handler that commits a change instead belongs in a reaction (<c>[OnEvent&lt;T&gt;]</c>).
/// </remarks>
public interface IEffect<in TEvent>
    where TEvent : DomainEvent
{
    /// <summary>Runs the effect once more (it may have run before: use the key).</summary>
    /// <param name="domainEvent">The committed event.</param>
    /// <param name="context">The effect's name, the event with its commit facts, and the idempotency key.</param>
    /// <param name="cancellationToken">Cancels the call (the row comes back after its lease).</param>
    /// <returns>Success, or the violation to retry or bury.</returns>
    ValueTask<Result> Run(TEvent domainEvent, EffectContext context, CancellationToken cancellationToken);
}

/// <summary>What an effect knows about its row.</summary>
/// <param name="Name">The effect's name (the row is <c>effect:&lt;name&gt;</c>).</param>
/// <param name="Recorded">The committed event with its stream, id, time and operation context.</param>
/// <param name="IdempotencyKey">The event id (<c>N</c> format): the same on every attempt; send it to the receiver.</param>
public sealed record EffectContext(string Name, RecordedEvent Recorded, string IdempotencyKey);

/// <summary>
/// The effects of an application: the store's table of <c>effect:&lt;name&gt;</c> rows and their runner. Each handler is
/// resolved from a fresh service scope per row. Built by <c>AddMessaging(…).Effect&lt;TEvent, THandler&gt;(name)</c>, or by
/// hand with <see cref="Add{TEvent}"/> (tests).
/// </summary>
public sealed class EffectSet : IOutboxRowHandler
{
    private readonly Entry[] _entries;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<Type, string[]> _rows;

    /// <summary>Creates a set without effects; <see cref="Add{TEvent}"/> returns sets with them.</summary>
    /// <param name="services">Where the handlers come from (a scope per row); <see langword="null"/> = no services.</param>
    public EffectSet(IServiceProvider? services = null)
        : this([], services ?? NoServices.Instance)
    {
    }

    private EffectSet(Entry[] entries, IServiceProvider services)
    {
        _entries = entries;
        _services = services;
        _rows = new();
        Names = [.. entries.Select(static e => e.Row)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Names { get; }

    /// <summary>The row name of an effect: <c>effect:&lt;name&gt;</c>.</summary>
    /// <param name="name">The effect's name.</param>
    /// <returns>The row name.</returns>
    public static string RowOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "effect:" + name;
    }

    /// <summary>A set with one more effect.</summary>
    /// <typeparam name="TEvent">The event it runs for (and every event derived from it).</typeparam>
    /// <param name="name">
    /// Its stable name, stored in every row: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>. Rows of a name that no longer exists
    /// are dead-lettered, so keep it when renaming the handler.
    /// </param>
    /// <param name="handler">Creates the handler from the row's service scope.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">The name is not valid, or taken.</exception>
    public EffectSet Add<TEvent>(string name, Func<IServiceProvider, IEffect<TEvent>> handler)
        where TEvent : DomainEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (name.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException($"The effect name '{name}' is letters, digits, '-', '_' and '.'.", nameof(name));
        var row = RowOf(name);
        if (_entries.Any(e => e.Row == row))
            throw new ArgumentException($"The effect '{name}' is declared twice.", nameof(name));
        return new([.. _entries, new Entry<TEvent>(name, row, handler)], _services);
    }

    /// <summary>The same effects, with handlers from <paramref name="services"/>.</summary>
    /// <param name="services">The application's services.</param>
    /// <returns>The bound set.</returns>
    public EffectSet WithServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new(_entries, services);
    }

    /// <inheritdoc />
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (_entries.Length == 0)
            return ReadOnlySpan<string>.Empty;
        return _rows.GetOrAdd(domainEvent.GetType(), static (_, state) =>
            [.. state.Entries.Where(e => e.Matches(state.Event)).Select(static e => e.Row)], (Entries: _entries, Event: domainEvent));
    }

    /// <inheritdoc />
    /// <remarks>Effects run at the commit.</remarks>
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => null;

    /// <inheritdoc />
    public bool IsScheduled(string reaction) => false;

    /// <inheritdoc />
    public string ActivityName(string reaction)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        return reaction.StartsWith("effect:", StringComparison.Ordinal) ? "effect " + reaction[7..] : "effect " + reaction;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No effect has that name, or the row's event is not its event type.</exception>
    public async ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        ArgumentNullException.ThrowIfNull(recorded);
        var entry = _entries.FirstOrDefault(e => e.Row == reaction)
            ?? throw new InvalidOperationException($"No effect runs the row '{reaction}' (removed, or renamed).");
        var scope = _services.GetService<IServiceScopeFactory>()?.CreateAsyncScope();
        try
        {
            var context = new EffectContext(entry.Name, recorded, recorded.EventId.ToString("N", CultureInfo.InvariantCulture));
            var result = await entry.Run(scope?.ServiceProvider ?? _services, recorded, context, cancellationToken).ConfigureAwait(false);
            return result.TryGetValue(out Violation? violation)
                ? violation
                : new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0);
        }
        finally
        {
            if (scope is { } s)
                await s.DisposeAsync().ConfigureAwait(false);
        }
    }

    private abstract class Entry(string name, string row)
    {
        public string Name { get; } = name;

        public string Row { get; } = row;

        public abstract bool Matches(DomainEvent domainEvent);

        public abstract ValueTask<Result> Run(IServiceProvider services, RecordedEvent recorded, EffectContext context, CancellationToken cancellationToken);
    }

    private sealed class Entry<TEvent>(string name, string row, Func<IServiceProvider, IEffect<TEvent>> create) : Entry(name, row)
        where TEvent : DomainEvent
    {
        public override bool Matches(DomainEvent domainEvent) => domainEvent is TEvent;

        public override ValueTask<Result> Run(IServiceProvider services, RecordedEvent recorded, EffectContext context, CancellationToken cancellationToken)
            => recorded.Event.Event is TEvent e
                ? create(services).Run(e, context, cancellationToken)
                : throw new InvalidOperationException($"The effect '{Name}' runs for {typeof(TEvent).Name}, not {recorded.Event.Event.GetType().Name}.");
    }

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
