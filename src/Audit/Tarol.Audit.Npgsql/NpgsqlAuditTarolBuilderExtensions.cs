using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries.Npgsql;

namespace Tarol.Audit.Npgsql;

/// <summary>Registers the PostgreSQL store of the change log (TN-64).</summary>
public static class NpgsqlAuditTarolBuilderExtensions
{
    /// <summary>
    /// Keeps the change log in PostgreSQL (<see cref="NpgsqlAuditStore"/>), by default in the event store's database and schema:
    /// <c>.AddAudit(a => a.Add&lt;Child&gt;()).UseNpgsqlAudit()</c>. The host creates its tables at start.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="schema">The schema, or <see langword="null"/> for the event store's.</param>
    /// <param name="dataSource">The database, or <see langword="null"/> for the registered <see cref="NpgsqlDataSource"/>.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseNpgsqlAudit(this TarolBuilder builder, string? schema = null, Func<IServiceProvider, NpgsqlDataSource>? dataSource = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        dataSource ??= static provider => provider.GetRequiredService<NpgsqlDataSource>();
        builder.Services.TryAddSingleton(provider => new NpgsqlAuditStore(dataSource(provider),
            schema ?? provider.GetService<NpgsqlAggregateStore>()?.Schema ?? NpgsqlReadModelSchema.DefaultSchema));
        builder.Services.TryAddSingleton<IAuditStore>(static provider => provider.GetRequiredService<NpgsqlAuditStore>());
        return builder;
    }
}
