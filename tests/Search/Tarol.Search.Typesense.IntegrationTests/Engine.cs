using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Search.Typesense.IntegrationTests.Engine))]

namespace Tarol.Search.Typesense.IntegrationTests;

/// <summary>One Typesense and one PostgreSQL container per run; the API key is generated per run (a throwaway, not a secret).</summary>
public sealed class Engine : IAsyncLifetime
{
    public const string Image = "typesense/typesense:28.0";
    private readonly IContainer _typesense;
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private int _next;

    public Engine()
    {
        Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _typesense = new ContainerBuilder(Image)
            .WithPortBinding(8108, true)
            .WithCommand("--data-dir", "/tmp", "--api-key", Key)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8108).ForPath("/health")))
            .Build();
    }

    public string Key { get; }

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_typesense.StartAsync(), _postgres.StartAsync());
        DataSource = new NpgsqlSlimDataSourceBuilder(_postgres.GetConnectionString()).EnableArrays().Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _typesense.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public int Next() => Interlocked.Increment(ref _next);

    public HttpClient Client() => TypesenseTarolBuilderExtensions.Client(new Uri($"http://{_typesense.Hostname}:{_typesense.GetMappedPublicPort(8108)}"), Key);
}
