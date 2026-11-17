namespace Tarol.Operations;

/// <summary>How <c>AddOperations</c> polls, judges and retains (docs/architecture/operations.md §2–§5).</summary>
public sealed class OperationsOptions
{
    /// <summary>How often the snapshot is read (default 15 s). Gauges and checks read the last snapshot, never the database.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How many polls in a row may fail before <c>ready</c> is Unhealthy (default 3).</summary>
    public int StaleAfter { get; set; } = 3;

    /// <summary>The thresholds of the <c>tarol</c> checks.</summary>
    public OperationsThresholds Thresholds { get; } = new();

    /// <summary>
    /// Delete dead letters older than this (O-5; default <see langword="null"/>: dead letters are kept until a human acts). Runs
    /// as the system job <see cref="OperationsTarolBuilderExtensions.PurgeDeadJob"/> every hour, which needs a job host
    /// (<c>AddJobs</c> of <c>Tarol.Jobs</c>); every purge writes an audit row.
    /// </summary>
    public TimeSpan? DeadRetention { get; set; }

    /// <summary>The most dead letters one purge statement deletes (default 1000).</summary>
    public int PurgeBatch { get; set; } = 1000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PollEvery, TimeSpan.FromSeconds(1), nameof(PollEvery));
        ArgumentOutOfRangeException.ThrowIfLessThan(StaleAfter, 1, nameof(StaleAfter));
        ArgumentOutOfRangeException.ThrowIfLessThan(PurgeBatch, 1, nameof(PurgeBatch));
        if (DeadRetention is { } retention)
            ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.FromHours(1), nameof(DeadRetention));
        Thresholds.Validate();
    }
}

/// <summary>When a backlog is Degraded or Unhealthy in the <c>tarol</c> group (O-2).</summary>
public sealed class OperationsThresholds
{
    /// <summary>The oldest due reaction row is older than this: Degraded (default 30 s).</summary>
    public TimeSpan ReactionsDegraded { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>… Unhealthy (default 5 min).</summary>
    public TimeSpan ReactionsUnhealthy { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most overdue job cursor is later than this: Degraded (default 1 min).</summary>
    public TimeSpan JobsDegraded { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>… Unhealthy (default 10 min).</summary>
    public TimeSpan JobsUnhealthy { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A projection's lag is above this: Degraded (default 30 s).</summary>
    public TimeSpan LagDegraded { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>… Unhealthy (default 5 min).</summary>
    public TimeSpan LagUnhealthy { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>More dead letters (of all kinds) than this: Degraded (default 0: any dead letter).</summary>
    public long DeadDegraded { get; set; }

    internal void Validate()
    {
        static void Pair(TimeSpan degraded, TimeSpan unhealthy, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degraded, TimeSpan.Zero, name + "Degraded");
            ArgumentOutOfRangeException.ThrowIfLessThan(unhealthy, degraded, name + "Unhealthy");
        }

        Pair(ReactionsDegraded, ReactionsUnhealthy, "Reactions");
        Pair(JobsDegraded, JobsUnhealthy, "Jobs");
        Pair(LagDegraded, LagUnhealthy, "Lag");
        ArgumentOutOfRangeException.ThrowIfNegative(DeadDegraded, nameof(DeadDegraded));
    }
}
