using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.DataProtection.IntegrationTests.Postgres))]

namespace Tarol.DataProtection.IntegrationTests;

/// <summary>One PostgreSQL 17 container per run; each test uses a schema of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    public string ConnectionString => _container.GetConnectionString();

    public string NewSchema() => $"dp{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
