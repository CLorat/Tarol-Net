namespace Tarol.Queries;

/// <summary>
/// Lets one runner of a projection version work at a time (step 15, O-8): the other hosts wait and take over when the runner
/// stops. Without a gate every host runs and the checkpoint guard refuses all but one batch (4 runners wasted 591 of 791
/// batches). <c>Tarol.Queries.Npgsql</c> registers a session advisory lock (<c>NpgsqlReadModelOptions.SingleRunner</c>).
/// The checkpoint guard stays: a runner that lost its gate without noticing is still refused.
/// </summary>
public interface IProjectionGate
{
    /// <summary>How long a host that did not get the gate waits before it asks again (default 1 s).</summary>
    TimeSpan RetryEvery { get; }

    /// <summary>Tries to become the runner of <paramref name="name"/> v<paramref name="version"/>; never waits for another runner.</summary>
    /// <param name="name">The projection.</param>
    /// <param name="version">Its version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The held gate (dispose it to let another runner in), or <see langword="null"/> when another runner holds it.</returns>
    ValueTask<IProjectionGateLease?> TryEnter(string name, int version, CancellationToken cancellationToken);
}

/// <summary>A held <see cref="IProjectionGate"/>.</summary>
public interface IProjectionGateLease : IAsyncDisposable
{
    /// <summary>Whether it is still held (a database gate checks its connection now and then).</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="false"/> when it was lost: the host stops and asks again.</returns>
    ValueTask<bool> Held(CancellationToken cancellationToken);
}
