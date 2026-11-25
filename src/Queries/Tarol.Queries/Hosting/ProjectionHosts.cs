using System.Globalization;

namespace Tarol.Queries;

/// <summary>
/// The projection hosts of this process (TN-26, Tatalu FB-INT2-3): after a start every projection reads the feed from its
/// checkpoint and lists answer 503 <c>Projection.Building</c> until it is current. <see cref="WaitUntilCurrent"/> waits for all
/// of them (a test, a start-up step, a one-off job); <see cref="ProjectionsBuilder.ReadyWhenCurrent"/> makes readiness wait too.
/// </summary>
public sealed class ProjectionHosts
{
    /// <summary>Creates the view over the hosts.</summary>
    /// <param name="hosts">The projection hosts of this process.</param>
    public ProjectionHosts(IEnumerable<ProjectionHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        All = [.. hosts];
    }

    /// <summary>Every projection host of this process.</summary>
    public IReadOnlyList<ProjectionHost> All { get; }

    /// <summary>The projections still building, as <c>name vN</c> (empty when all are current).</summary>
    public IReadOnlyList<string> Building
        => [.. All.Where(static h => h.State != ProjectionState.Current).Select(static h => string.Create(CultureInfo.InvariantCulture, $"{h.Name} v{h.Version}"))];

    /// <summary>
    /// The projections whose loop keeps failing, as <c>name vN: Type: message (N attempts)</c> (empty when none fails):
    /// a read table that cannot be created, a database that refuses the batch (<see cref="ProjectionHost.LastFailure"/>).
    /// </summary>
    public IReadOnlyList<string> Failing
        => [.. All.Select(static h => (Host: h, Failure: h.LastFailure)).Where(static x => x.Failure is not null)
            .Select(static x => string.Create(CultureInfo.InvariantCulture, $"{x.Host.Name} v{x.Host.Version}: {x.Failure!.Message} ({x.Failure.Failures} attempts)"))];

    /// <summary>Waits until every host of this process is current, up to <paramref name="timeout"/> in all.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when all are current; <see langword="false"/> on timeout (<see cref="Building"/> names the rest).</returns>
    public async Task<bool> WaitUntilCurrent(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var all = await Task.WhenAll(All.Select(h => h.WaitUntilCurrent(timeout, cancellationToken))).ConfigureAwait(false);
        return all.All(static current => current);
    }
}
