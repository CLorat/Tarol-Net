using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Search.OpenSearch.IntegrationTests.Engine))]

namespace Tarol.Search.OpenSearch.IntegrationTests;

/// <summary>One OpenSearch and one PostgreSQL container per run; every test gets an index prefix and a schema of its own.</summary>
public sealed class Engine : IAsyncLifetime
{
    public const string Image = "opensearchproject/opensearch:2.19.1";
    private readonly IContainer _opensearch = new ContainerBuilder(Image)
        .WithPortBinding(9200, true)
        .WithEnvironment(new Dictionary<string, string>
        {
            ["discovery.type"] = "single-node",
            ["DISABLE_SECURITY_PLUGIN"] = "true",
            ["DISABLE_INSTALL_DEMO_CONFIG"] = "true",
            ["cluster.routing.allocation.disk.threshold_enabled"] = "false",
            ["OPENSEARCH_JAVA_OPTS"] = "-Xms512m -Xmx512m",
        })
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/")))
        .Build();
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private int _next;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public Uri Address => new($"http://{_opensearch.Hostname}:{_opensearch.GetMappedPublicPort(9200)}");

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_opensearch.StartAsync(), _postgres.StartAsync());
        DataSource = new NpgsqlSlimDataSourceBuilder(_postgres.GetConnectionString()).EnableArrays().Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _opensearch.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public int Next() => Interlocked.Increment(ref _next);

    public HttpClient Client() => new() { BaseAddress = Address };
}
