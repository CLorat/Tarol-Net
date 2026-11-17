using System.Globalization;
using System.Text;

namespace Tarol.Persistence.Npgsql;

/// <summary>
/// A versioned schema (docs/architecture/operations.md §4): the version 1 baseline script, the steps after it, and the table
/// that stores the version. <see cref="NpgsqlSchemaUpgrader"/> applies it; <see cref="UpgradeScript"/> hands the same SQL to a
/// migration tool. Tarol's own definitions are <see cref="NpgsqlSchema.Definition"/> and those of <c>Tarol.Messaging.Npgsql</c>.
/// </summary>
public sealed record NpgsqlSchemaDefinition
{
    /// <summary>Creates a definition.</summary>
    /// <param name="owner">Who owns the schema, for messages (e.g. <c>Tarol.Persistence.Npgsql</c>).</param>
    /// <param name="schema">The PostgreSQL schema: a plain identifier.</param>
    /// <param name="versionTable">The table in <paramref name="schema"/> that stores the version: a plain identifier.</param>
    /// <param name="baseline">The version 1 script: creates what is missing, changes nothing that exists.</param>
    /// <param name="steps">The steps after version 1, in order; their highest version is the version this code knows.</param>
    /// <exception cref="ArgumentException">An identifier is not plain, or the steps are out of order or below version 2.</exception>
    public NpgsqlSchemaDefinition(string owner, string schema, string versionTable, string baseline, IReadOnlyList<UpgradeStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(steps);
        NpgsqlSchema.Validate(schema);
        NpgsqlSchema.Validate(versionTable);
        var previous = 2;
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step, nameof(steps));
            if (step.Version < previous)
                throw new ArgumentException($"Step versions must start at 2 and not decrease; found {step.Version} after {previous}.", nameof(steps));
            if (step.CompatibleFrom < 1 || step.CompatibleFrom > step.Version)
                throw new ArgumentException($"A step of version {step.Version} has CompatibleFrom {step.CompatibleFrom}; it must be 1 … {step.Version}.", nameof(steps));
            if (step.Concurrently != step.Index is not null)
                throw new ArgumentException($"A step of version {step.Version} must name its index exactly when it runs concurrently.", nameof(steps));
            if (step.Index is { } index)
                NpgsqlSchema.Validate(index);
            previous = step.Version;
        }
        Owner = owner;
        Schema = schema;
        VersionTable = versionTable;
        Baseline = baseline;
        Steps = steps;
        Version = steps.Count == 0 ? 1 : steps[^1].Version;
    }

    /// <summary>Who owns the schema, for messages.</summary>
    public string Owner { get; }

    /// <summary>The PostgreSQL schema.</summary>
    public string Schema { get; }

    /// <summary>The table that stores the version (one row: <c>version</c>, <c>compatible_from</c>).</summary>
    public string VersionTable { get; }

    /// <summary>The version 1 script.</summary>
    public string Baseline { get; }

    /// <summary>The steps after version 1.</summary>
    public IReadOnlyList<UpgradeStep> Steps { get; }

    /// <summary>The version this code knows: the highest step's, or 1 without steps.</summary>
    public int Version { get; }

    /// <summary>The advisory-lock key of the schema: one upgrader per PostgreSQL schema at a time, whatever it upgrades.</summary>
    internal string LockKey => "tarol.schema:" + Schema;

    /// <summary>
    /// Makes the version table of version 1 versioned for step 15: it gains <c>compatible_from</c> (and is created with version 1
    /// where the baseline had none). Idempotent; runs together with the baseline.
    /// </summary>
    internal string Prepare => $"""
        CREATE TABLE IF NOT EXISTS {Schema}.{VersionTable} (version integer NOT NULL);
        INSERT INTO {Schema}.{VersionTable} SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM {Schema}.{VersionTable});
        ALTER TABLE {Schema}.{VersionTable} ADD COLUMN IF NOT EXISTS compatible_from integer NOT NULL DEFAULT 1;
        """;

    /// <summary>Whether the step at <paramref name="index"/> is the last of its version (its transaction stores the version).</summary>
    internal bool Closes(int index) => index == Steps.Count - 1 || Steps[index + 1].Version != Steps[index].Version;

    /// <summary>The <c>compatible_from</c> of a version: the highest of its steps.</summary>
    internal int CompatibleFromOf(int version) => Steps.Where(s => s.Version == version).Max(static s => s.CompatibleFrom);

    internal string Bump(int version) => string.Create(CultureInfo.InvariantCulture,
        $"UPDATE {Schema}.{VersionTable} SET version = {version}, compatible_from = greatest(compatible_from, {CompatibleFromOf(version)})");

    internal string DropInvalid(string index) => $"""
        DO $$ BEGIN
          IF EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = '{Schema}' AND c.relname = '{index}' AND NOT i.indisvalid) THEN
            EXECUTE 'DROP INDEX {Schema}.{index}';
          END IF;
        END $$;
        """;

    /// <summary>
    /// The SQL that takes the schema from version <paramref name="from"/> to <see cref="Version"/>, for a migration tool: with
    /// <c>0</c>, the whole schema for an empty database. The <c>CREATE INDEX CONCURRENTLY</c> statements must run outside a
    /// transaction (split the script there, or run it statement by statement); each is preceded by a block that drops an invalid
    /// index of its name, and each version ends with its version bump. Use <see cref="SchemaMode.VerifyOnly"/> in the application.
    /// </summary>
    /// <param name="from">The stored version (0 = nothing yet).</param>
    /// <returns>The SQL; empty when <paramref name="from"/> is already <see cref="Version"/> or newer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is negative.</exception>
    public string UpgradeScript(int from)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        if (from >= Version)
            return "";
        var sql = new StringBuilder();
        if (from == 0)
            sql.Append("-- version 1 (baseline)\n").Append(Baseline).Append('\n');
        if (from <= 1)
            sql.Append("-- the version table gains compatible_from\n").Append(Prepare).Append('\n');
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (step.Version <= from)
                continue;
            sql.Append(CultureInfo.InvariantCulture, $"-- version {step.Version}{(step.Concurrently ? " (outside a transaction)" : "")}\n");
            if (step.Index is { } index)
                sql.Append(DropInvalid(index)).Append('\n');
            sql.Append(step.Sql.TrimEnd().TrimEnd(';')).Append(";\n");
            if (Closes(i))
                sql.Append(Bump(step.Version)).Append(";\n");
        }
        return sql.ToString();
    }
}
