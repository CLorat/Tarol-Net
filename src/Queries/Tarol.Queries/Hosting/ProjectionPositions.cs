using Tarol.Persistence;

namespace Tarol.Queries;

/// <summary>
/// Where a projection stands, for read-your-writes (QR-5): the API process asks it before a list answers
/// (<c>Tarol-Read-After</c>) and for the answer's <c>Tarol-Position</c>.
/// </summary>
public interface IProjectionPositions
{
    /// <summary>The projection's checkpoint (its position and whether it is still building).</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> Checkpoint<TRow>(CancellationToken cancellationToken)
        where TRow : class, IProjection<TRow>;

    /// <summary>Waits until the projection has handled <paramref name="position"/>, up to <paramref name="timeout"/>.</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="position">A commit's position (<c>ChangeOutcome.Position</c>).</param>
    /// <param name="timeout">The longest wait (default of the endpoints: 2 s).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when it passed; <see langword="false"/> on timeout (answer with <c>Tarol-Stale: true</c>).</returns>
    ValueTask<bool> WaitFor<TRow>(GlobalPosition position, TimeSpan timeout, CancellationToken cancellationToken)
        where TRow : class, IProjection<TRow>;
}

/// <summary>
/// <see cref="IProjectionPositions"/> over the read stores: the projection's host when it runs in this process (a signal
/// after each batch), otherwise the stored checkpoint, read every <see cref="PollInterval"/> (the API process).
/// </summary>
public sealed class ProjectionPositions : IProjectionPositions
{
    private readonly IReadModels _readModels;
    private readonly IReadOnlyList<ProjectionHost> _hosts;
    private readonly TimeProvider _time;

    /// <summary>Creates the positions.</summary>
    /// <param name="readModels">The read stores.</param>
    /// <param name="hosts">The projection hosts of this process (none in the API process).</param>
    /// <param name="time">The clock of the waits.</param>
    public ProjectionPositions(IReadModels readModels, IEnumerable<ProjectionHost>? hosts = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(readModels);
        _readModels = readModels;
        _hosts = [.. hosts ?? []];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How often the stored checkpoint is read while waiting without a host in this process (default 5 ms).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint<TRow>(CancellationToken cancellationToken)
        where TRow : class, IProjection<TRow>
        => _readModels.Store<TRow>().Checkpoint(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> WaitFor<TRow>(GlobalPosition position, TimeSpan timeout, CancellationToken cancellationToken)
        where TRow : class, IProjection<TRow>
    {
        foreach (var host in _hosts)
            if (host.Name == TRow.Name && host.Version == TRow.Version)
                return await host.WaitFor(position, timeout, cancellationToken).ConfigureAwait(false);
        var store = _readModels.Store<TRow>();
        var end = _time.GetTimestamp() + (long)(timeout.TotalSeconds * _time.TimestampFrequency);
        while (true)
        {
            if ((await store.Checkpoint(cancellationToken).ConfigureAwait(false)).Position >= position)
                return true;
            var left = end - _time.GetTimestamp();
            if (left <= 0)
                return false;
            var wait = TimeSpan.FromSeconds(Math.Min((double)left / _time.TimestampFrequency, PollInterval.TotalSeconds));
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
