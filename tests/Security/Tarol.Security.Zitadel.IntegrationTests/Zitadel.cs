using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Security.Zitadel.IntegrationTests.Zitadel))]

namespace Tarol.Security.Zitadel.IntegrationTests;

/// <summary>Zitadel v4.20.3 and its PostgreSQL in Testcontainers, provisioned like docs/migration/probes/rest/Zitadel/setup.py:
/// project "artflow" with the app's roles, machine users (client secrets, JWT access tokens) with role grants.</summary>
public sealed class Zitadel : IAsyncLifetime
{
    public const string Image = "ghcr.io/zitadel/zitadel:v4.20.3";
    private readonly string _out = Directory.CreateTempSubdirectory("zitadel").FullName;
    private readonly int _port = FreePort();
    private INetwork _network = null!;
    private PostgreSqlContainer _db = null!;
    private IContainer _zitadel = null!;

    public string Authority => $"http://localhost:{_port}";
    public string ProjectId { get; private set; } = "";
    public Dictionary<string, (string ClientId, string Secret, string UserId)> Clients { get; } = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_out, (UnixFileMode)0x1FF);                              // the container's user writes the PAT
        _network = new NetworkBuilder().Build();
        _db = new PostgreSqlBuilder("postgres:17").WithNetwork(_network).WithNetworkAliases("zitadel-db").WithPassword("probe").Build();
        await _db.StartAsync();
        _zitadel = new ContainerBuilder(Image)
            .WithNetwork(_network)
            .WithPortBinding(_port, 8080)
            .WithBindMount(_out, "/out")
            .WithEnvironment(new Dictionary<string, string>
            {
                ["ZITADEL_DATABASE_POSTGRES_HOST"] = "zitadel-db",
                ["ZITADEL_DATABASE_POSTGRES_PORT"] = "5432",
                ["ZITADEL_DATABASE_POSTGRES_DATABASE"] = "zitadel",
                ["ZITADEL_DATABASE_POSTGRES_USER_USERNAME"] = "zitadel",
                ["ZITADEL_DATABASE_POSTGRES_USER_PASSWORD"] = "zitadel",
                ["ZITADEL_DATABASE_POSTGRES_USER_SSL_MODE"] = "disable",
                ["ZITADEL_DATABASE_POSTGRES_ADMIN_USERNAME"] = "postgres",
                ["ZITADEL_DATABASE_POSTGRES_ADMIN_PASSWORD"] = "probe",
                ["ZITADEL_DATABASE_POSTGRES_ADMIN_SSL_MODE"] = "disable",
                ["ZITADEL_EXTERNALSECURE"] = "false",
                ["ZITADEL_EXTERNALDOMAIN"] = "localhost",
                ["ZITADEL_EXTERNALPORT"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture),   // the issuer is http://localhost:<port>
                ["ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_USERNAME"] = "admin-sa",
                ["ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_NAME"] = "admin-sa",
                ["ZITADEL_FIRSTINSTANCE_ORG_MACHINE_PAT_EXPIRATIONDATE"] = "2030-01-01T00:00:00Z",
                ["ZITADEL_FIRSTINSTANCE_PATPATH"] = "/out/admin.pat",
            })
            .WithCommand("start-from-init", "--masterkey", "MasterkeyNeedsToHave32Characters", "--tlsMode", "disabled")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/debug/ready")))
            .Build();
        await _zitadel.StartAsync();
        var pat = Path.Combine(_out, "admin.pat");
        for (var i = 0; i < 120 && !(File.Exists(pat) && new FileInfo(pat).Length > 0); i++)
            await Task.Delay(500);
        await Provision((await File.ReadAllTextAsync(pat)).Trim());
    }

    private async Task Provision(string pat)
    {
        using var api = new HttpClient { BaseAddress = new Uri(Authority) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        async Task<JsonElement> Call(HttpMethod method, string path, string body = "{}")
        {
            for (var attempt = 0; ; attempt++)
            {
                using var response = await api.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 120)          // the first instance is still being set up
                {
                    await Task.Delay(500);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return JsonElement.Parse(await response.Content.ReadAsStringAsync());
            }
        }
        ProjectId = (await Call(HttpMethod.Post, "/management/v1/projects", """{"name":"artflow","projectRoleAssertion":true}""")).GetProperty("id").GetString()!;
        _ = await Call(HttpMethod.Post, $"/management/v1/projects/{ProjectId}/roles/_bulk",
            """{"roles":[{"key":"seller","displayName":"seller"},{"key":"bidder","displayName":"bidder"},{"key":"reader","displayName":"reader"},{"key":"service","displayName":"service"}]}""");
        foreach (var (name, roles) in new[] { ("alice", "\"seller\",\"bidder\""), ("bob", "\"seller\",\"bidder\""), ("nora", ""), ("billing", "\"service\"") })
        {
            var user = (await Call(HttpMethod.Post, "/management/v1/users/machine", $$"""{"userName":"{{name}}","name":"{{name}}","accessTokenType":"ACCESS_TOKEN_TYPE_JWT"}""")).GetProperty("userId").GetString()!;
            var secret = await Call(HttpMethod.Put, $"/management/v1/users/{user}/secret");
            if (roles.Length > 0)
                _ = await Call(HttpMethod.Post, $"/management/v1/users/{user}/grants", $$"""{"projectId":"{{ProjectId}}","roleKeys":[{{roles}}]}""");
            Clients[name] = (secret.GetProperty("clientId").GetString()!, secret.GetProperty("clientSecret").GetString()!, user);
        }
    }

    /// <summary>A client-credentials access token with the scopes Tarol.Security.Zitadel expects.</summary>
    public async Task<string> Token(string client, bool audience = true)
    {
        using var http = new HttpClient { BaseAddress = new Uri(Authority) };
        var (id, secret, _) = Clients[client];
        var scope = "openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner" + (audience ? $" urn:zitadel:iam:org:project:id:{ProjectId}:aud" : "");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/v2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = scope }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}")));
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("access_token").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_zitadel is not null)
            await _zitadel.DisposeAsync();
        if (_db is not null)
            await _db.DisposeAsync();
        if (_network is not null)
            await _network.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
