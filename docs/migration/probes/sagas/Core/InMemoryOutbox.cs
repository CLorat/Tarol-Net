using Tarol.Persistence;

namespace Tarol.Sagas;

/// <summary>
/// In-memory parity (question 5): the same claim rules as the SQL (due, lease, head per (tenant, type, stream, reaction),
/// dead letters out of the order). The probe derives rows from the store's feed; the decided implementation writes them in
/// <c>InMemoryAggregateStore.Append</c>, like the Npgsql commit.
/// </summary>
public sealed class InMemoryOutbox(IEventFeed feed, ReactionSet reactions, TimeProvider time) : IReactionOutbox
{
    private sealed class Row
    {
        public required long Id;
        public required string Reaction;
        public required PositionedEvent Event;
        public DateTimeOffset DueAt;
        public int Attempts;
        public Guid? Claim;
        public DateTimeOffset? LeaseUntil;
        public string? LastError;
        public DateTimeOffset? DeadAt;
        public (Guid, string, Guid, string) Head => (Event.Event.Context.Tenant.TryGetValue(out Tarol.StronglyTypedId.Id<Tarol.Kernel.ITenant> t) ? t.Value : Guid.Empty, Event.StreamType, Event.Event.StreamId, Reaction);
    }

    private readonly Lock _gate = new();
    private readonly List<Row> _rows = [];
    private GlobalPosition _checkpoint = GlobalPosition.Start;
    private long _next;

    private async Task Sync(CancellationToken ct)
    {
        var fresh = new List<PositionedEvent>();
        await foreach (var e in feed.ReadAll(_checkpoint, 1000, ct))
            fresh.Add(e);
        lock (_gate)
        {
            foreach (var e in fresh)
            {
                if (e.Position <= _checkpoint)
                    continue;
                _checkpoint = e.Position;
                foreach (var name in reactions.Table(e.Event.Event.Event))
                    _rows.Add(new Row
                    {
                        Id = ++_next, Reaction = name, Event = e,
                        DueAt = reactions.DueAt(e.Event.Event.Event, name) is { } due && due > e.Event.RecordedAt ? due : e.Event.RecordedAt,
                    });
            }
        }
    }

    public async Task<IReadOnlyList<ClaimedRow>> Claim(int max, TimeSpan lease, CancellationToken ct)
    {
        await Sync(ct);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var seen = new HashSet<(Guid, string, Guid, string)>();
            var claimed = new List<ClaimedRow>();
            foreach (var row in _rows.Where(r => r.DeadAt is null).OrderBy(r => r.Id))
            {
                if (!seen.Add(row.Head))
                    continue;                                                     // not the head of its (stream, reaction)
                if (row.DueAt > now || row.LeaseUntil > now || claimed.Count == max)
                    continue;
                row.Claim = Guid.NewGuid();
                row.LeaseUntil = now + lease;
                row.Attempts++;
                claimed.Add(new ClaimedRow(row.Id, row.Claim.Value, row.Reaction, row.Attempts, row.LastError, row.Event.StreamType,
                    row.Event.Event, null));
            }
            return claimed;
        }
    }

    private bool Owned(ClaimedRow c, out Row row)
    {
        row = _rows.FirstOrDefault(r => r.Id == c.Id)!;
        return row is not null && row.Claim == c.ClaimId;
    }

    public Task<bool> Complete(ClaimedRow row, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(Owned(row, out var r) && _rows.Remove(r));
    }

    public Task<bool> Retry(ClaimedRow row, DateTimeOffset at, string error, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Owned(row, out var r))
                return Task.FromResult(false);
            (r.DueAt, r.Claim, r.LeaseUntil, r.LastError) = (at, null, null, error);
            return Task.FromResult(true);
        }
    }

    public Task<bool> Bury(ClaimedRow row, string error, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Owned(row, out var r))
                return Task.FromResult(false);
            (r.DeadAt, r.Claim, r.LeaseUntil, r.LastError) = (time.GetUtcNow(), null, null, error);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLetters(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DeadLetter>>([.. _rows.Where(r => r.DeadAt is not null)
                .Select(r => new DeadLetter(r.Id, r.Reaction, r.Event.Event.EventId, r.Event.Event.StreamId, r.Attempts, r.LastError, r.DeadAt!.Value))]);
    }

    public Task<bool> Redrive(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            var r = _rows.FirstOrDefault(x => x.Id == id && x.DeadAt is not null);
            if (r is null)
                return Task.FromResult(false);
            _ = _rows.Remove(r);
            _rows.Add(new Row { Id = ++_next, Reaction = r.Reaction, Event = r.Event, DueAt = time.GetUtcNow() });   // DL-B semantics
            return Task.FromResult(true);
        }
    }

    public async Task<long> Pending(CancellationToken ct)
    {
        await Sync(ct);
        lock (_gate)
            return _rows.Count(r => r.DeadAt is null);
    }
}
