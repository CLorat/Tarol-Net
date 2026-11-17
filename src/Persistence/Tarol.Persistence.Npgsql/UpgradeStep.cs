namespace Tarol.Persistence.Npgsql;

/// <summary>
/// One step of a versioned schema (docs/architecture/operations.md §4): SQL that takes the schema towards
/// <see cref="Version"/>. A version may have several steps; the version is stored after its last step.
/// </summary>
/// <param name="Version">The schema version this step belongs to (2 or more; version 1 is the baseline script).</param>
/// <param name="CompatibleFrom">
/// The oldest code version that still runs on the schema after this step: an additive (expand) step keeps it, a contract step
/// (a drop or rename, only in a major version) raises it.
/// </param>
/// <param name="Sql">The step, idempotent (<c>IF NOT EXISTS</c>): a step that ran but whose version bump was lost runs again harmlessly.</param>
/// <param name="Concurrently">
/// A <c>CREATE INDEX CONCURRENTLY</c> step: it runs outside a transaction, and an invalid index of the name <see cref="Index"/>
/// left by an interrupted build is dropped first.
/// </param>
/// <param name="Index">For a <see cref="Concurrently"/> step: the name of the index it builds.</param>
public sealed record UpgradeStep(int Version, int CompatibleFrom, string Sql, bool Concurrently = false, string? Index = null);
