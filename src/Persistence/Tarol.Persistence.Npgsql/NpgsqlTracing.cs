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
}
