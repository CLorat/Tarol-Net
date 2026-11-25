using Npgsql;

namespace Tarol.Queries.Npgsql;

/// <summary>
/// Wakes projection hosts on the store's feed <c>NOTIFY</c> (<see cref="Persistence.Npgsql.NpgsqlStoreOptions.NotifyFeed"/>):
/// one <c>LISTEN</c> connection per process, opened on the first wait, reconnected after a failure. The hosts keep their
/// poll (1 s) as the fallback, because the feed shows a commit only once every older transaction has ended.
/// </summary>
public sealed class NpgsqlFeedSignal : IFeedSignal, IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _listener;
    private int _disposed;

    /// <summary>Creates the signal.</summary>
    /// <param name="dataSource">The data source of the event store.</param>
    /// <param name="channel">The store's <see cref="Persistence.Npgsql.NpgsqlAggregateStore.FeedChannel"/>.</param>
    /// <param name="time">The clock of the waits.</param>
    public NpgsqlFeedSignal(NpgsqlDataSource dataSource, string channel, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        NpgsqlReadModelSchema.Validate(channel);
        _db = dataSource;
        _time = time ?? TimeProvider.System;
        Channel = channel;
    }

    /// <summary>The channel.</summary>
    public string Channel { get; }

    /// <inheritdoc />
    public async ValueTask Wait(TimeSpan max, CancellationToken cancellationToken)
    {
        Task notified;
        lock (_gate)
        {
            _listener ??= Detached.Run(Listen);
            notified = _notified.Task;
        }
        await Task.WhenAny(notified, Task.Delay(max, _time, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Stops the <c>LISTEN</c> connection.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
            await _listener.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var connection = await _db.OpenConnectionAsync(_stop.Token).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    connection.Notification += (_, _) => Signal();
                    await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                        await listen.ExecuteNonQueryAsync(_stop.Token).ConfigureAwait(false);
                    Signal();                                                       // commits may have landed before LISTEN
                    while (!_stop.IsCancellationRequested)
                        await connection.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (NpgsqlException)
            {
                // the connection broke: the hosts keep polling; reconnect after a pause
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Signal()
    {
        TaskCompletionSource notified;
        lock (_gate)
        {
            notified = _notified;
            _notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        notified.TrySetResult();
    }
}
