namespace Tarol.Messaging.Npgsql;

/// <summary>How the in-database bus runs; the defaults fit most applications.</summary>
public sealed record NpgsqlBusOptions
{
    /// <summary>The bus schema every system reaches (default <c>bus</c>).</summary>
    public string Schema { get; init; } = NpgsqlBusSchema.DefaultSchema;

    /// <summary>
    /// Whether <see cref="NpgsqlBus.Prepare"/> runs the schema script (default <see langword="true"/>). Turn it off when the
    /// systems connect with restricted roles and the schema's owner runs <see cref="NpgsqlBusSchema.Script"/> once.
    /// </summary>
    public bool EnsureSchema { get; init; } = true;

    /// <summary>
    /// Send a <c>NOTIFY</c> with every publish (in the same statement batch) and <c>LISTEN</c> while idle, so a delivery
    /// arrives in milliseconds instead of at the next poll (S-10: opt-in, it serializes the publishers' commits).
    /// </summary>
    public bool Notify { get; init; }

    /// <summary>How long an idle consumer waits before it claims again (default 100 ms; a NOTIFY wakes it sooner).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Deliveries claimed per round trip and consumer loop (default 16); the lease covers the whole batch.</summary>
    public int Batch { get; init; } = 16;

    /// <summary>
    /// The database roles of the publishing systems: role → source (M-13). The bus stamps the publishing role
    /// (<c>session_user</c>) on every message; with this map a consumer sees the role's source as
    /// <see cref="Incoming.AuthenticatedAs"/> and buries a message whose contract prefix differs (an unknown role becomes
    /// <c>role:&lt;name&gt;</c>, never a source). <see langword="null"/> (default): every system connects as the same role, the
    /// stamp proves nothing and <see cref="Incoming.AuthenticatedAs"/> stays <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Roles { get; init; }

    /// <summary>
    /// How long published messages that every subscription received are kept (step 14, J-9): with a job host
    /// (<c>AddJobs</c> of <c>Tarol.Jobs</c>) the system job <c>tarol.purge-messages</c> deletes older ones every hour, in
    /// batches of <see cref="PurgeBatch"/>, once per cluster. Without a job host nothing purges (call
    /// <see cref="NpgsqlBus.PurgeMessages"/> yourself). Default 7 days.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The most messages one purge statement deletes (default 1000).</summary>
    public int PurgeBatch { get; init; } = 1000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Retention, TimeSpan.Zero, nameof(Retention));
        ArgumentOutOfRangeException.ThrowIfLessThan(PurgeBatch, 1, nameof(PurgeBatch));
        NpgsqlBusSchema.Validate(Schema);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(Batch, 1, nameof(Batch));
    }
}
