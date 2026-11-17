using System.Diagnostics;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence;

/// <summary>
/// The outbox of an <see cref="InMemoryAggregateStore"/>: the same rules as the Npgsql outbox (heads per (tenant, type,
/// stream, reaction), scheduled reactions by due time, leases with claim ids, dead letters moved out, re-drive behind),
/// so tests see the full chain event → reaction → change without a database.
/// </summary>
internal sealed class InMemoryOutbox(TimeProvider time, IOutboxReactions reactions) : IOutbox, IDeadLetterSource
{
    private sealed class Row
    {
        public required long Id { get; init; }
        public required string Reaction { get; init; }
        public required PositionedEvent Event { get; init; }
        public required bool Scheduled { get; init; }
        public required Option<string> Traceparent { get; init; }
        public DateTimeOffset DueAt { get; set; }
        public int Attempts { get; set; }
        public Guid? Claim { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public Option<string> LastError { get; set; }

        public (Guid Tenant, string Type, Guid Stream, string Reaction) Key
            => (Event.Event.Context.Tenant.TryGetValue(out Id<ITenant> tenant) ? tenant.Value : Guid.Empty, Event.StreamType, Event.Event.StreamId, Reaction);
    }

    private readonly Lock _gate = new();
    private readonly List<Row> _rows = [];
    private readonly List<DeadLetter> _dead = [];
    private readonly Dictionary<long, Row> _buried = [];
    private TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _next;

    /// <summary>Refuses a commit whose event lists a reaction twice, before the store changes anything.</summary>
    public void Check(StreamEvent[] changes)
    {
        foreach (var change in changes)
        {
            var names = reactions.ReactionsTo(change.Event);
            for (var i = 0; i < names.Length; i++)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(names[i]);
                for (var j = 0; j < i; j++)
                    if (names[i] == names[j])
                        throw new InvalidOperationException($"The reaction '{names[i]}' is listed twice for one event.");
            }
        }
    }

    /// <summary>Writes the rows of a commit (called by the store inside its commit, after <see cref="Check"/>).</summary>
    public void Write(IReadOnlyList<PositionedEvent> committed)
    {
        var traceparent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? Option.Some(activity.Id!) : Option.None;
        var wrote = false;
        lock (_gate)
        {
            foreach (var positioned in committed)
            {
                var domainEvent = positioned.Event.Event.Event;
                foreach (var reaction in reactions.ReactionsTo(domainEvent))
                {
                    var due = reactions.DueAt(domainEvent, reaction) is { } at && at > positioned.Event.RecordedAt ? at : positioned.Event.RecordedAt;
                    _rows.Add(new Row
                    {
                        Id = ++_next,
                        Reaction = reaction,
                        Event = positioned,
                        Scheduled = reactions.IsScheduled(reaction),
                        Traceparent = traceparent,
                        DueAt = due,
                    });
                    wrote = true;
                }
            }
        }
        if (wrote)
            Interlocked.Exchange(ref _written, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    public Task<IReadOnlyList<OutboxRow>> Claim(int max, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var seen = new HashSet<(Guid, string, Guid, string)>();
            var heads = new List<Row>();
            foreach (var row in _rows)                                        // in id order
                if (row.Scheduled || seen.Add(row.Key))
                    heads.Add(row);
            var claimed = new List<OutboxRow>();
            foreach (var row in heads.Where(r => r.DueAt <= now && (r.LeaseUntil is null || r.LeaseUntil <= now)).OrderBy(r => r.DueAt).ThenBy(r => r.Id).Take(max))
            {
                row.Claim = Guid.NewGuid();
                row.LeaseUntil = now + lease;
                row.Attempts++;
                claimed.Add(new OutboxRow(row.Id, row.Claim.Value, row.Reaction, row.Event.Event.Context.Tenant, row.Attempts, row.LastError,
                    row.Event, row.Traceparent) { DueAt = row.DueAt });
            }
            return Task.FromResult<IReadOnlyList<OutboxRow>>([.. claimed.OrderBy(r => r.Id)]);
        }
    }

    private Row? Owned(OutboxRow claimed) => _rows.Find(r => r.Id == claimed.Id && r.Claim == claimed.ClaimId);

    public Task<bool> Complete(OutboxRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
            return Task.FromResult(Owned(row) is { } owned && _rows.Remove(owned));
    }

    public Task<bool> Retry(OutboxRow row, DateTimeOffset retryAt, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_gate)
        {
            if (Owned(row) is not { } owned)
                return Task.FromResult(false);
            (owned.DueAt, owned.Claim, owned.LeaseUntil, owned.LastError) = (retryAt, null, null, reason);
            return Task.FromResult(true);
        }
    }

    public Task<bool> Bury(OutboxRow row, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_gate)
        {
            if (Owned(row) is not { } owned)
                return Task.FromResult(false);
            _ = _rows.Remove(owned);
            _dead.Add(new DeadLetter(owned.Id, owned.Reaction, owned.Event.Event.Context.Tenant, owned.Event.StreamType, owned.Event.Event.StreamId,
                owned.Event.Event.EventId, owned.Attempts, reason, time.GetUtcNow()));
            _buried[owned.Id] = owned;                                        // keeps the event for a re-drive
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLetters(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeadLetter>>([.. _dead.OrderBy(d => d.Id).Take(max)]);
    }

    public Task<bool> Redrive(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _dead.FindIndex(d => d.Id == id);
            if (index < 0 || !_buried.Remove(id, out var buried))
                return Task.FromResult(false);
            _dead.RemoveAt(index);
            _rows.Add(new Row
            {
                Id = ++_next,
                Reaction = buried.Reaction,
                Event = buried.Event,
                Scheduled = buried.Scheduled,
                Traceparent = buried.Traceparent,
                DueAt = time.GetUtcNow(),
            });
        }
        Interlocked.Exchange(ref _written, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLetters(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeadLetter>>([.. _dead
                .Where(d => d.Id > query.After && (query.Name is null || d.Reaction == query.Name) && (query.Tenant.IsNone || d.Tenant == query.Tenant))
                .OrderBy(d => d.Id).Take(query.Limit)]);
    }

    public Task<bool> Discard(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _buried.Remove(id);
            return Task.FromResult(_dead.RemoveAll(d => d.Id == id) == 1);
        }
    }

    public Task<Option<DateTimeOffset>> OldestDue(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var seen = new HashSet<(Guid, string, Guid, string)>();
            DateTimeOffset? oldest = null;
            foreach (var row in _rows)                                        // in id order: the first of a key is its head
                if ((row.Scheduled || seen.Add(row.Key)) && row.DueAt <= now && (oldest is null || row.DueAt < oldest))
                    oldest = row.DueAt;
            return Task.FromResult(oldest is { } at ? Option.Some(at) : Option.None);
        }
    }

    /// <summary>The dead letters (for the in-memory snapshot signals).</summary>
    internal int DeadCount
    {
        get
        {
            lock (_gate)
                return _dead.Count;
        }
    }

    string IDeadLetterSource.Kind => DeadLetterKinds.Reactions;

    async Task<DeadLetterPage> IDeadLetterSource.List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        var rows = await DeadLetters(query, cancellationToken).ConfigureAwait(false);
        return new DeadLetterPage([.. rows.Select(static d => d.ToItem())], rows.Count == query.Limit ? rows[^1].Id : null);
    }

    Task<bool> IDeadLetterSource.Redrive(long id, string actor, CancellationToken cancellationToken) => Redrive(id, cancellationToken);

    Task<bool> IDeadLetterSource.Discard(long id, string actor, CancellationToken cancellationToken) => Discard(id, cancellationToken);

    public Task<long> Pending(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult((long)_rows.Count);
    }

    public Task WaitForRows(TimeSpan timeout, CancellationToken cancellationToken)
        => Task.WhenAny(Volatile.Read(ref _written).Task, Task.Delay(timeout, time, cancellationToken));
}
