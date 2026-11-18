using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Search;

/// <summary>
/// Where an external engine's index keeps its checkpoints (GS-4): an engine has no transaction with Tarol's, so the index writes
/// the documents, then moves the checkpoint here, guarded (at least once; a document is the current state, so a repeat is
/// harmless). <c>Tarol.Search.Npgsql</c> keeps them in <c>projections</c> (the lag gauge and <c>DropVersion</c> see them).
/// </summary>
public interface ISearchCheckpoints
{
    /// <summary>Creates the version's checkpoint if it does not exist.</summary>
    /// <param name="version">The index version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Prepare(int version, CancellationToken cancellationToken);

    /// <summary>The version's checkpoint (<see cref="GlobalPosition.Start"/> and building before the first batch).</summary>
    /// <param name="version">The index version.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> Checkpoint(int version, CancellationToken cancellationToken);

    /// <summary>Moves the checkpoint from <paramref name="from"/> to <paramref name="through"/> if it is still at <paramref name="from"/>.</summary>
    /// <param name="version">The index version.</param>
    /// <param name="from">The position the batch read after.</param>
    /// <param name="through">The batch's last position.</param>
    /// <param name="state">The state after the batch.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="false"/> when another runner moved it.</returns>
    ValueTask<bool> Move(int version, GlobalPosition from, GlobalPosition through, ProjectionState state, CancellationToken cancellationToken);

    /// <summary>The newest current version up to <paramref name="version"/> and its checkpoint (GS-5), or none.</summary>
    /// <param name="version">The highest version to consider.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The version and its checkpoint, or <see langword="null"/>.</returns>
    ValueTask<(int Version, ProjectionCheckpoint Checkpoint)?> NewestCurrent(int version, CancellationToken cancellationToken);
}

/// <summary>Checkpoints in memory (tests; a restarted process rebuilds its engine index from the start of the feed).</summary>
public sealed class InMemorySearchCheckpoints : ISearchCheckpoints
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ProjectionCheckpoint> _checkpoints = [];

    /// <inheritdoc />
    public ValueTask Prepare(int version, CancellationToken cancellationToken)
    {
        lock (_gate)
            _checkpoints.TryAdd(version, default);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint(int version, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault(version));
    }

    /// <inheritdoc />
    public ValueTask<bool> Move(int version, GlobalPosition from, GlobalPosition through, ProjectionState state, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_checkpoints.GetValueOrDefault(version).Position != from)
                return ValueTask.FromResult(false);
            _checkpoints[version] = new ProjectionCheckpoint(through, state);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<(int Version, ProjectionCheckpoint Checkpoint)?> NewestCurrent(int version, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var (v, checkpoint) in _checkpoints.Where(c => c.Key <= version).OrderByDescending(static c => c.Key))
                if (checkpoint.State == ProjectionState.Current)
                    return ValueTask.FromResult<(int, ProjectionCheckpoint)?>((v, checkpoint));
            return ValueTask.FromResult<(int, ProjectionCheckpoint)?>(null);
        }
    }
}
