namespace Tarol.Persistence.Npgsql;

/// <summary>What <c>EnsureSchema</c> does with a schema (docs/architecture/operations.md §4).</summary>
public enum SchemaMode
{
    /// <summary>Create an empty schema, run the missing upgrade steps under an advisory lock, then check compatibility (the default).</summary>
    Upgrade,

    /// <summary>
    /// Change nothing: check that the schema exists, is at least the version this code knows and is compatible with it, and fail
    /// fast otherwise. For applications whose migration tool runs <c>UpgradeScript</c>.
    /// </summary>
    VerifyOnly,
}

/// <summary>The schema's version after <c>EnsureSchema</c>.</summary>
/// <param name="Version">The stored version (may be newer than the code's, when compatible).</param>
/// <param name="CompatibleFrom">The oldest code version that runs on it.</param>
/// <param name="StepsRun">The steps this call ran (0 when another process upgraded, or in <see cref="SchemaMode.VerifyOnly"/>).</param>
public readonly record struct SchemaState(int Version, int CompatibleFrom, int StepsRun);
