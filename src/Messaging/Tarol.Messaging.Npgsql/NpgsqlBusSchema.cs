using System.Text.RegularExpressions;
using Tarol.Persistence.Npgsql;

namespace Tarol.Messaging.Npgsql;

/// <summary>
/// The SQL of the in-database bus (docs/architecture/messaging.md §6): the versioned scripts of the bus schema every system
/// reaches and of the consumer's <c>messages_dead</c> table, and the row-level-security policy that binds a publisher's role
/// to its contract prefix. <see cref="NpgsqlBus.EnsureSchema(CancellationToken)"/> and
/// <see cref="NpgsqlMessageDeadLetters.EnsureSchema(CancellationToken)"/> apply them under an advisory lock
/// (<see cref="NpgsqlSchemaUpgrader"/>, operations.md §4); a migration tool runs <see cref="UpgradeScript"/> and
/// <see cref="DeadLettersUpgradeScript"/>.
/// </summary>
public static partial class NpgsqlBusSchema
{
    /// <summary>
    /// The version of the bus schema this package knows; stored in <c>&lt;schema&gt;.schema_version</c> with
    /// <c>compatible_from</c> (step 15 added the column, not a step: code of steps 11–14 still runs on it).
    /// </summary>
    public const int Version = 1;

    /// <summary>The version of the <c>messages_dead</c> schema this package knows; stored in <c>&lt;schema&gt;.messages_dead_version</c>.</summary>
    public const int DeadLettersVersion = 2;

    /// <summary>The default bus schema.</summary>
    public const string DefaultSchema = "bus";

    /// <summary>The bus script for <paramref name="schema"/>: creates what is missing, changes nothing that exists.</summary>
    /// <param name="schema">A plain PostgreSQL identifier (lowercase letters, digits, <c>_</c>).</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string Script(string schema = DefaultSchema)
    {
        Validate(schema);
        var s = schema;
        return $"""
            CREATE SCHEMA IF NOT EXISTS {s};

            -- one row per published (event, contract, version): a republish after a crash is dropped (ON CONFLICT)
            CREATE TABLE IF NOT EXISTS {s}.messages (
                id           uuid        NOT NULL,            -- ce-id: the event id
                type         text        NOT NULL,            -- ce-type: the contract, its prefix is the source
                version      integer     NOT NULL,            -- ce-contractversion
                source       text        NOT NULL,            -- ce-source as claimed
                publisher    text        NOT NULL DEFAULT session_user,   -- the role, stamped by the database (M-13)
                headers      jsonb       NOT NULL,            -- the CloudEvents headers (binary mode)
                data         bytea       NOT NULL,            -- the contract JSON
                published_at timestamptz NOT NULL,
                PRIMARY KEY (id, type, version));
            CREATE INDEX IF NOT EXISTS messages_published_at ON {s}.messages (published_at);

            -- which subscription receives which contract (one row per (contract, subscription))
            CREATE TABLE IF NOT EXISTS {s}.subscriptions (
                topic text NOT NULL,
                name  text NOT NULL,
                PRIMARY KEY (topic, name));

            -- one row per (subscription, message) until it is acknowledged; due_at is the next delivery (lease or retry)
            CREATE TABLE IF NOT EXISTS {s}.deliveries (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,   -- the publish order of the subscription
                subscription text        NOT NULL,
                message_id   uuid        NOT NULL,
                type         text        NOT NULL,
                version      integer     NOT NULL,
                due_at       timestamptz NOT NULL,
                attempts     integer     NOT NULL DEFAULT 0,
                claim_id     uuid        NULL,             -- the current claim; acknowledge and retry must present it
                UNIQUE (subscription, message_id, type, version));
            CREATE INDEX IF NOT EXISTS deliveries_due ON {s}.deliveries (subscription, due_at, id);
            CREATE INDEX IF NOT EXISTS deliveries_message ON {s}.deliveries (message_id, type, version);

            CREATE TABLE IF NOT EXISTS {s}.schema_version (version integer NOT NULL);
            INSERT INTO {s}.schema_version SELECT {Version} WHERE NOT EXISTS (SELECT 1 FROM {s}.schema_version);
            """;
    }

    /// <summary>The steps of the bus schema after version 1: none yet.</summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <returns>The steps.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static IReadOnlyList<UpgradeStep> Steps(string schema = DefaultSchema)
    {
        Validate(schema);
        return [];
    }

    /// <summary>The bus schema as a definition for <see cref="NpgsqlSchemaUpgrader"/>.</summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <returns>The definition (baseline <see cref="Script"/>, <see cref="Steps"/>, table <c>schema_version</c>).</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static NpgsqlSchemaDefinition Definition(string schema = DefaultSchema)
        => new("Tarol.Messaging.Npgsql", schema, "schema_version", Script(schema), Steps(schema));

    /// <summary>The SQL that takes the bus schema from version <paramref name="from"/> (0 = nothing yet) to <see cref="Version"/>.</summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <param name="from">The stored version.</param>
    /// <returns>The SQL (see <see cref="NpgsqlSchemaDefinition.UpgradeScript"/>).</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string UpgradeScript(string schema, int from) => Definition(schema).UpgradeScript(from);

    /// <summary>
    /// The steps of <c>messages_dead</c> after version 1 (today's table). Version 2: <c>operations_audit</c> in the schema and
    /// the <c>dead_at</c> index, built concurrently (the retention of step 15).
    /// </summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <returns>The steps.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static IReadOnlyList<UpgradeStep> DeadLettersSteps(string schema = "tarol")
    {
        Validate(schema);
        return
        [
            new(2, 1, NpgsqlSchema.AuditScript(schema)),
            new(2, 1, $"CREATE INDEX CONCURRENTLY IF NOT EXISTS messages_dead_at ON {schema}.messages_dead (dead_at)", Concurrently: true, Index: "messages_dead_at"),
        ];
    }

    /// <summary>
    /// The <c>messages_dead</c> schema as a definition for <see cref="NpgsqlSchemaUpgrader"/>: version table
    /// <c>messages_dead_version</c>, so it can share the store's schema and still have its own version.
    /// </summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static NpgsqlSchemaDefinition DeadLettersDefinition(string schema = "tarol")
        => new("Tarol.Messaging.Npgsql (messages_dead)", schema, "messages_dead_version", DeadLettersScript(schema), DeadLettersSteps(schema));

    /// <summary>The SQL that takes <c>messages_dead</c> from version <paramref name="from"/> (0 = nothing yet) to <see cref="DeadLettersVersion"/>.</summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <param name="from">The stored version.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string DeadLettersUpgradeScript(string schema, int from) => DeadLettersDefinition(schema).UpgradeScript(from);

    /// <summary>The consumer's dead letters (M-9) in its own schema (usually the store's, <c>tarol</c>): version 1.</summary>
    /// <param name="schema">A plain PostgreSQL identifier.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string DeadLettersScript(string schema = "tarol")
    {
        Validate(schema);
        return $"""
            CREATE SCHEMA IF NOT EXISTS {schema};
            CREATE TABLE IF NOT EXISTS {schema}.messages_dead (
                id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                subscription text        NOT NULL,
                topic        text        NOT NULL,
                message_id   uuid        NULL,             -- null for a poison message without headers
                attempts     integer     NOT NULL,
                reason       text        NOT NULL,
                headers      jsonb       NOT NULL,
                data         bytea       NOT NULL,
                dead_at      timestamptz NOT NULL);
            """;
    }

    /// <summary>
    /// Row-level security for the bus (docs/migration/messaging.md §3.2): every role may read and acknowledge, but a role
    /// inserts only messages whose contract prefix is its own name (policy <c>publish_own_prefix</c>), so <c>billing</c>
    /// cannot publish <c>artflow.*</c> (SQLSTATE 42501). Run it as the schema's owner, once; give each system a login role
    /// named like its prefix and the grants of <see cref="GrantScript"/>.
    /// </summary>
    /// <param name="schema">The bus schema.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain identifier.</exception>
    public static string RowLevelSecurityScript(string schema = DefaultSchema)
    {
        Validate(schema);
        return $"""
            ALTER TABLE {schema}.messages ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS publish_own_prefix ON {schema}.messages;
            CREATE POLICY publish_own_prefix ON {schema}.messages FOR INSERT WITH CHECK (split_part(type, '.', 1) = session_user);
            DROP POLICY IF EXISTS read_all ON {schema}.messages;
            CREATE POLICY read_all ON {schema}.messages FOR SELECT USING (true);
            """;
    }

    /// <summary>The grants a system's role needs on the bus (publish, subscribe, consume).</summary>
    /// <param name="role">The system's login role, named like its contract prefix (e.g. <c>billing</c>).</param>
    /// <param name="schema">The bus schema.</param>
    /// <returns>The SQL.</returns>
    /// <exception cref="ArgumentException">The role or the schema is not a plain identifier.</exception>
    public static string GrantScript(string role, string schema = DefaultSchema)
    {
        Validate(schema);
        Validate(role);
        return $"""
            GRANT USAGE ON SCHEMA {schema} TO {role};
            GRANT SELECT, INSERT ON {schema}.messages, {schema}.subscriptions TO {role};
            GRANT SELECT, INSERT, UPDATE, DELETE ON {schema}.deliveries TO {role};
            GRANT SELECT ON {schema}.schema_version TO {role};
            """;
    }

    internal static void Validate(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!Identifier().IsMatch(identifier))
            throw new ArgumentException($"'{identifier}' is not a plain PostgreSQL identifier (lowercase letters, digits and _, at most 63 characters).", nameof(identifier));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
