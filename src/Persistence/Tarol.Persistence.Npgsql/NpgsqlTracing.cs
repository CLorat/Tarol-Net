using System.Diagnostics;
using Npgsql;

namespace Tarol.Persistence.Npgsql;

/// <summary>Npgsql tracing for Tarol's data sources (docs/architecture/operations.md §7, O-6).</summary>
public static class NpgsqlTracing
{
    /// <summary>
    /// Traces a command or batch only inside an activity (an HTTP request, a reaction, a job, a change): an idle worker's polls
    /// make no root spans (51 per second, 4.4 million a day, without it), while the commands of the work stay its children.
    /// Tarol sets it on the data sources it builds (<c>UseNpgsqlStore(connectionString)</c>, <c>UseNpgsqlBus(connectionString)</c>)
    /// unless <c>TraceRootCommands</c> is set; call it on a data source you build yourself.
    /// </summary>
    /// <param name="builder">The data source builder.</param>
    /// <returns>The builder.</returns>
    public static NpgsqlSlimDataSourceBuilder TraceOnlyInActivities(this NpgsqlSlimDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureTracing(static o => o.ConfigureCommandFilter(static _ => Activity.Current is not null)
            .ConfigureBatchFilter(static _ => Activity.Current is not null));
        return builder;
    }

    /// <summary>
    /// Lets Npgsql prepare the statements a data source runs often (<c>Max Auto Prepare</c> = <paramref name="statements"/>)
    /// unless the connection string sets it: Tarol's commits, claims and loads are the same few statements every time, and
    /// without preparing each is parsed and planned again. Tarol sets it on the data sources it builds; set
    /// <c>Max Auto Prepare=0</c> in the connection string behind a pooler that cannot keep prepared statements (PgBouncer in
    /// transaction mode without <c>max_prepared_statements</c>).
    /// </summary>
    /// <param name="builder">The data source builder.</param>
    /// <param name="statements">How many statements to keep prepared per connection (default 64).</param>
    /// <returns>The builder.</returns>
    public static NpgsqlSlimDataSourceBuilder AutoPrepareUnlessSet(this NpgsqlSlimDataSourceBuilder builder, int statements = 64)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegative(statements);
        var set = builder.ConnectionString.Replace(" ", "", StringComparison.Ordinal).Contains("MaxAutoPrepare=", StringComparison.OrdinalIgnoreCase);
        if (!set)
            builder.ConnectionStringBuilder.MaxAutoPrepare = statements;
        return builder;
    }
}
