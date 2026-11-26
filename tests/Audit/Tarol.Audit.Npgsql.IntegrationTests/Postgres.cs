using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Audit.Npgsql.IntegrationTests.Postgres))]

namespace Tarol.Audit.Npgsql.IntegrationTests;

/// <summary>One PostgreSQL 17 container per run; every test gets a schema of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>The container's connection string (with its password, unlike <see cref="NpgsqlDataSource.ConnectionString"/>).</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString() + ";Maximum Pool Size=100").EnableArrays().Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public string Schema() => $"s{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";

    public async Task<object?> Scalar(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    public async Task Execute(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
