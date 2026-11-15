using Npgsql;
using Tarol.Messaging.Contract;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Messaging.Npgsql.IntegrationTests.Postgres))]

namespace Tarol.Messaging.Npgsql.IntegrationTests;

/// <summary>One PostgreSQL 17 container per test run; every test gets a bus schema of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(ConnectionString + ";Maximum Pool Size=200").Build();
    }

    public static string NewSchema() => "bus_" + Guid.NewGuid().ToString("N")[..12];

    public async Task Execute(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> Scalar(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>The transport contract on the PostgreSQL bus, polling.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlBusContractTests(Postgres postgres) : TransportContract
{
    protected override ConsumerSet Consumers => GeneratedConsumers.Set;

    protected override bool DropsDuplicates => true;

    protected override Task<IMessageTransport> CreateTransport(string isolation)
        => Task.FromResult<IMessageTransport>(new NpgsqlBus(postgres.DataSource, TimeProvider.System, new NpgsqlBusOptions { Schema = "bus_" + isolation, PollInterval = TimeSpan.FromMilliseconds(50) }));
}

/// <summary>The transport contract on the PostgreSQL bus with NOTIFY.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlBusNotifyContractTests(Postgres postgres) : TransportContract
{
    protected override ConsumerSet Consumers => GeneratedConsumers.Set;

    protected override bool DropsDuplicates => true;

    protected override Task<IMessageTransport> CreateTransport(string isolation)
        => Task.FromResult<IMessageTransport>(new NpgsqlBus(postgres.DataSource, TimeProvider.System,
            new NpgsqlBusOptions { Schema = "bus_" + isolation, Notify = true, PollInterval = TimeSpan.FromSeconds(5) }));   // the NOTIFY, not the poll, delivers
}
