using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tarol.Persistence;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// <see cref="IProjectionPositions"/> with pushed checkpoints (<see cref="NpgsqlReadModelOptions.PushCheckpoints"/>, CA-2):
/// the projection's batch transaction sends <c>NOTIFY &lt;schema&gt;_checkpoint</c>, delivered after its commit; this class
/// holds one <c>LISTEN</c> connection per process (opened on the first call) and keeps the checkpoint of every projection
/// version in memory. <see cref="Checkpoint{TRow}"/> answers from memory once a checkpoint was seen (else one read, then
/// remembered); <see cref="WaitFor{TRow}"/> is woken by the notification. A poll of the checkpoints every
/// <see cref="PollInterval"/>, and after every reconnect, covers a lost notification or connection. Positions only move forward.
/// </summary>
public sealed partial class NpgsqlProjectionPositions : IProjectionPositions, IAsyncDisposable
{
    /// <summary>The suffix of the channel after the schema (<c>tarol_checkpoint</c>).</summary>
    public const string ChannelSuffix = "_checkpoint";

    private readonly NpgsqlDataSource _db;
    private readonly IReadOnlyList<ProjectionHost> _hosts;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string _readOne;
    private readonly string _readAll;
    private readonly ConcurrentDictionary<(string Name, int Version), Entry> _entries = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private volatile bool _listening;
    private int _disposed;

    /// <summary>Creates the positions.</summary>
    /// <param name="dataSource">The data source of the read tables.</param>
    /// <param name="schema">The schema of the read tables (<see cref="NpgsqlReadModels.Schema"/>); the channel is <c>&lt;schema&gt;_checkpoint</c>.</param>
    /// <param name="hosts">The projection hosts of this process (the worker): their <c>WaitFor</c> answers for their projection.</param>
    /// <param name="time">The clock of the waits and the poll.</param>
    /// <param name="logger">The log (connection lost, fallback poll used).</param>
    public NpgsqlProjectionPositions(NpgsqlDataSource dataSource, string schema = NpgsqlReadModelSchema.DefaultSchema,
        IEnumerable<ProjectionHost>? hosts = null, TimeProvider? time = null, ILogger<NpgsqlProjectionPositions>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        NpgsqlReadModelSchema.Validate(schema);
        Channel = schema + ChannelSuffix;
        NpgsqlReadModelSchema.Validate(Channel);
        _db = dataSource;
        _hosts = [.. hosts ?? []];
        _time = time ?? TimeProvider.System;
        _log = logger ?? (ILogger)NullLogger.Instance;
        _readOne = $"SELECT tx::text, pos, state FROM {schema}.projections WHERE name = $1 AND version = $2";
        _readAll = $"SELECT name, version, tx::text, pos, state FROM {schema}.projections";
    }

    /// <summary>The channel (<c>&lt;schema&gt;_checkpoint</c>).</summary>
    public string Channel { get; }

    /// <summary>How often every checkpoint is read as the fallback of a lost notification (default 1 s; also after every reconnect).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The first pause before a reconnect (default 250 ms); it doubles up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest pause before a reconnect (default 10 s).</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether the <c>LISTEN</c> connection is up (for a health check; <see langword="false"/> before the first call).</summary>
    public bool IsListening => _listening;

    /// <summary>Whether the <c>LISTEN</c> connection was started (by the first checkpoint read or wait); it starts lazily.</summary>
    internal bool IsStarted => _listener is not null;

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> Checkpoint<TRow>(CancellationToken cancellationToken)
        where TRow : class, IProjection<TRow>
    {
        Start();
        var entry = EntryOf(TRow.Name, TRow.Version);
        if (entry.TryGet(out var checkpoint, out _))
            return checkpoint;
        entry.Advance(await Read(TRow.Name, TRow.Version, cancellationToken).ConfigureAwait(false));
        entry.TryGet(out checkpoint, out _);
        return checkpoint;
    }

    /// <inheritdoc />
    public async ValueTask<bool> WaitFor<TRow>(GlobalPosition position, TimeSpan timeout, CancellationToken cancellationToken)
        where TRow : class, IProjection<TRow>
    {
        foreach (var host in _hosts)
            if (host.Name == TRow.Name && host.Version == TRow.Version)
                return await host.WaitFor(position, timeout, cancellationToken).ConfigureAwait(false);
        Start();
        var entry = EntryOf(TRow.Name, TRow.Version);
        var end = _time.GetTimestamp() + (long)(timeout.TotalSeconds * _time.TimestampFrequency);
        while (true)
        {
            if (!entry.TryGet(out var checkpoint, out var moved))
            {
                entry.Advance(await Read(TRow.Name, TRow.Version, cancellationToken).ConfigureAwait(false));
                entry.TryGet(out checkpoint, out moved);
            }
            if (checkpoint.Position >= position)
                return true;
            var left = end - _time.GetTimestamp();
            if (left <= 0)
                return false;
            try
            {
                await moved.WaitAsync(TimeSpan.FromSeconds((double)left / _time.TimestampFrequency), _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                entry.TryGet(out checkpoint, out _);
                return checkpoint.Position >= position;
            }
        }
    }

    /// <summary>Stops the <c>LISTEN</c> connection and the poll.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? listener;
        lock (_gate)
            listener = _listener;
        if (listener is not null)
            await listener.ConfigureAwait(false);
        _stop.Dispose();
    }

    private Entry EntryOf(string name, int version) => _entries.GetOrAdd((name, version), static _ => new Entry());

    private void Start()
    {
        if (_listener is not null || Volatile.Read(ref _disposed) == 1)
            return;
        lock (_gate)
            _listener ??= Task.Run(Listen, CancellationToken.None);
    }

    private async ValueTask<ProjectionCheckpoint> Read(string name, int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(_readOne);
        command.Parameters.Add(new NpgsqlParameter { Value = name });
        command.Parameters.Add(new NpgsqlParameter { Value = version });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? CheckpointSql.Read(reader, 0) : default;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return default;                                         // no worker has prepared a projection yet: building, at the start
        }
    }

    // every checkpoint of the schema in one read (a small table: one row per projection version)
    private async Task Poll(NpgsqlConnection? connection, CancellationToken cancellationToken)
    {
        await using var command = connection is null ? _db.CreateCommand(_readAll) : new NpgsqlCommand(_readAll, connection);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                EntryOf(reader.GetString(0), reader.GetInt32(1)).Advance(CheckpointSql.Read(reader, 2));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // no projection prepared yet
        }
    }

    private async Task Listen()
    {
        var failures = 0;
        var lost = false;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var connection = await _db.OpenConnectionAsync(_stop.Token).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    connection.Notification += (_, e) => Notified(e.Payload);
                    await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                        await listen.ExecuteNonQueryAsync(_stop.Token).ConfigureAwait(false);
                    _listening = true;
                    if (lost)
                        Reconnected(_log, Channel);
                    (failures, lost) = (0, false);
                    var polled = _time.GetTimestamp();
                    await Poll(connection, _stop.Token).ConfigureAwait(false);   // batches may have landed before LISTEN
                    while (!_stop.IsCancellationRequested)
                    {
                        var left = PollInterval - _time.GetElapsedTime(polled);
                        if (left > TimeSpan.Zero
                            && await connection.WaitAsync((int)Math.Ceiling(left.TotalMilliseconds), _stop.Token).ConfigureAwait(false))
                            continue;                                                // a notification: handled by the event
                        polled = _time.GetTimestamp();
                        await Poll(connection, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // the connection broke (or the database is down): poll without LISTEN until it is back
                _listening = false;
                failures++;
                var wait = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, Backoff.Ticks * (1L << Math.Min(failures - 1, 20))));
                ConnectionLost(_log, e, Channel, failures, wait);
                if (await FallbackPoll(wait, first: !lost).ConfigureAwait(false))
                    break;
                lost = true;
            }
        }
        _listening = false;
    }

    // polls through the data source every PollInterval during the backoff; true when stopped
    private async Task<bool> FallbackPoll(TimeSpan wait, bool first)
    {
        var end = _time.GetTimestamp() + (long)(wait.TotalSeconds * _time.TimestampFrequency);
        while (true)
        {
            try
            {
                await Poll(null, _stop.Token).ConfigureAwait(false);
                if (first)
                {
                    FallbackPollUsed(_log, Channel, PollInterval);
                    first = false;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // the database is not reachable either: the positions stay as they are; the reconnect tries again
            }
            var left = TimeSpan.FromSeconds((double)(end - _time.GetTimestamp()) / _time.TimestampFrequency);
            try
            {
                if (left <= TimeSpan.Zero)
                    return false;
                await Task.Delay(left < PollInterval ? left : PollInterval, _time, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }
    }

    private void Notified(string payload)
    {
        if (CheckpointNotice.TryParse(payload, out var name, out var version, out var checkpoint))
            EntryOf(name, version).Advance(checkpoint);
    }

    [LoggerMessage(EventId = 12101, Level = LogLevel.Warning, Message = "Checkpoint LISTEN on {Channel} failed ({Failures}); reconnecting in {Wait}")]
    private static partial void ConnectionLost(ILogger logger, Exception exception, string channel, int failures, TimeSpan wait);

    [LoggerMessage(EventId = 12102, Level = LogLevel.Information, Message = "Checkpoint LISTEN on {Channel} is down: the checkpoints are polled every {Interval} until it is back")]
    private static partial void FallbackPollUsed(ILogger logger, string channel, TimeSpan interval);

    [LoggerMessage(EventId = 12103, Level = LogLevel.Information, Message = "Checkpoint LISTEN on {Channel} is up again")]
    private static partial void Reconnected(ILogger logger, string channel);

    // one projection version's checkpoint: moves only forward; waiters await Moved
    private sealed class Entry
    {
        private readonly Lock _gate = new();
        private ProjectionCheckpoint _value;
        private bool _seen;
        private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryGet(out ProjectionCheckpoint value, out Task moved)
        {
            lock (_gate)
            {
                value = _value;
                moved = _moved.Task;
                return _seen;
            }
        }

        public void Advance(ProjectionCheckpoint next)
        {
            TaskCompletionSource moved;
            lock (_gate)
            {
                var forward = !_seen
                    || next.Position > _value.Position
                    || (next.Position == _value.Position && next.State == ProjectionState.Current && _value.State == ProjectionState.Building);
                _seen = true;
                if (!forward)
                    return;
                _value = next;
                moved = _moved;
                _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            moved.TrySetResult();
        }
    }
}

/// <summary>The payload of the checkpoint <c>NOTIFY</c>: <c>&lt;name&gt;:&lt;version&gt;:&lt;tx&gt;:&lt;pos&gt;:&lt;state&gt;</c>.</summary>
internal static class CheckpointNotice
{
    internal static string Format(string name, int version, GlobalPosition to, ProjectionState state)
        => string.Create(CultureInfo.InvariantCulture, $"{name}:{version}:{to.Transaction}:{to.Position}:{(state == ProjectionState.Current ? "current" : "building")}");

    internal static bool TryParse(string payload, out string name, out int version, out ProjectionCheckpoint checkpoint)
    {
        (name, version, checkpoint) = ("", 0, default);
        var parts = payload.Split(':');
        if (parts.Length != 5
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tx)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pos)
            || parts[4] is not ("current" or "building"))
            return false;
        name = parts[0];
        checkpoint = new ProjectionCheckpoint(new GlobalPosition(tx, pos), parts[4] == "current" ? ProjectionState.Current : ProjectionState.Building);
        return true;
    }
}
