using System.Text.Json;

namespace Tarol.Persistence.Npgsql;

/// <summary>How an <see cref="NpgsqlAggregateStore"/> stores; the defaults fit most applications.</summary>
public sealed record NpgsqlStoreOptions
{
    private readonly string _schema = NpgsqlSchema.DefaultSchema;

    /// <summary>The PostgreSQL schema of the tables (default <c>tarol</c>).</summary>
    /// <exception cref="ArgumentException">Not a plain identifier.</exception>
    public string Schema
    {
        get => _schema;
        init
        {
            NpgsqlSchema.Validate(value);
            _schema = value;
        }
    }

    /// <summary>JSON options of the stored state; <see langword="null"/> = the codec's options.</summary>
    public JsonSerializerOptions? StateOptions { get; init; }

    /// <summary>
    /// The reactions (the generated <c>GeneratedReactions.Set</c>, or <see cref="OutboxReactions.From"/>): one outbox row per
    /// (event, reaction) in the commit, due at the commit or at a scheduled reaction's time; <see langword="null"/> = no
    /// outbox rows. Claim them with <see cref="NpgsqlAggregateStore.Outbox"/>.
    /// </summary>
    public IOutboxReactions? Reactions { get; init; }

    /// <summary>
    /// Send a <c>NOTIFY</c> on <see cref="NpgsqlOutbox.Channel"/> with every commit that writes outbox rows, so waiting
    /// workers wake at once (≈ 7 ms instead of the poll interval). Off by default: PostgreSQL serializes notifications at
    /// commit, which cost 16 concurrent writers about 30 % of their commits/s in the step 9 measurements (S-10).
    /// </summary>
    public bool Notify { get; init; }

    /// <summary>
    /// Send a <c>NOTIFY</c> on <see cref="NpgsqlAggregateStore.FeedChannel"/> (<c>&lt;schema&gt;_feed</c>) with every commit, so
    /// projection hosts (<c>Tarol.Queries.Npgsql</c>) wake at once: lag p50 3.6 ms instead of 56 ms with the 100 ms poll
    /// (queries.md §4.3). Off by default, like <see cref="Notify"/> (S-10). The hosts keep polling (1 s) as the fallback:
    /// the feed holds a commit back while an older transaction runs, after its notification.
    /// </summary>
    public bool NotifyFeed { get; init; }

    /// <summary>
    /// What <see cref="NpgsqlAggregateStore.EnsureSchema(CancellationToken)"/> does: <see cref="SchemaMode.Upgrade"/> (default) runs
    /// the missing upgrade steps under an advisory lock; <see cref="SchemaMode.VerifyOnly"/> only checks the version, for
    /// applications whose migration tool runs <see cref="NpgsqlSchema.UpgradeScript"/>.
    /// </summary>
    public SchemaMode SchemaMode { get; init; } = SchemaMode.Upgrade;

    /// <summary>
    /// The <c>lock_timeout</c> of each upgrade step (default 2 s); a step that waits longer for a table lock gives up, so the
    /// commits do not queue behind it, and is retried up to 5 times.
    /// </summary>
    public TimeSpan SchemaLockTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The unique lookups; share one instance with the in-memory store of your tests.</summary>
    public StoreLookups Lookups { get; init; } = new();
}
