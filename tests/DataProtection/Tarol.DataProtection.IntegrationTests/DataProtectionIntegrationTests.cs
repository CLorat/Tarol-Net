using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Caching;
using Tarol.DataProtection.Tests;
using Tarol.Kernel;
using Tarol.Operations;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.DataProtection.IntegrationTests;

/// <summary>Step 16C on PostgreSQL: erasure across two processes, the admin entry, retention.</summary>
[Trait("Category", "Integration")]
public sealed class DataProtectionIntegrationTests(Postgres postgres)
{
    private static readonly string MasterKey = App.MasterKey;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly OperationContext _context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);

    private Guid Tenant => _context.Tenant.GetValueOrThrow().Value;

    /// <summary>One process: the Npgsql store with keys, two projections on Npgsql read models behind the page cache, data protection, the admin entry.</summary>
    private async Task<WebApplication> Process(string schema)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tarol:DataProtection:MasterKey"] = MasterKey,
            ["Tarol:Retention:order:Days"] = "10",
        });
        builder.Services.AddSingleton<TimeProvider>(_time);
        var json = new JsonSerializerOptions(TestJson.Default.Options);
        var codec = new EventCodec(json).Add<Customer>().Add<Order>();
        _ = new TarolBuilder(builder.Services, json, codec)
            .UseNpgsqlStore(postgres.ConnectionString, new NpgsqlStoreOptions
            {
                Schema = schema,
                Lookups = new StoreLookups().DefinePersonal<Customer>("email", static c => c.Email?.ToLowerInvariant()),
            })
            .UseNpgsqlKeyStore(new NpgsqlKeyStoreOptions { Schema = schema })
            .UseNpgsqlReadModels(new NpgsqlReadModelOptions { Schema = schema })
            .AddProjections(p => p.Add<CustomerRow>().Add<OrderRow>())
            .AddCaching()
            .AddDataProtection()
            .AddOperations();
        builder.Services.AddAuthentication("header").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("header", null);
        builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", static p => p.RequireRole("tarol-admin"));
        var web = builder.Build();
        web.UseAuthentication();
        web.UseAuthorization();
        web.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");
        // start what a process starts, without the background loops the tests drive themselves
        foreach (var hosted in web.Services.GetServices<IHostedService>().Where(static h => h.GetType().Name is "EnsureSchemaService" or "KeyListenerService" or "ErasureSignals"))
            await hosted.StartAsync(Ct);
        await web.StartAsync(Ct);
        await web.Services.GetRequiredService<NpgsqlKeyStore>().StartListening(Ct);
        foreach (var host in web.Services.GetServices<ProjectionHost>())
            await host.CatchUp(Ct);  // prepares the read tables
        return web;
    }

    private async Task Commit<T>(IServiceProvider services, T aggregate, long expected = 0)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        (await services.GetRequiredService<IAggregateStore>().Append(aggregate, expected, _context, Option.None, Ct)).ToResult().ShouldBe(Result.Success());
        AggregateChanges.Committed(aggregate);
    }

    private static async Task Project(IServiceProvider services)
    {
        foreach (var host in services.GetServices<ProjectionHost>())
            await host.CatchUp(Ct);
    }

    private async Task<T?> Row<T>(IServiceProvider services, Guid id)
        where T : class, IProjection<T>
        => (await services.GetRequiredService<IReadModels>().Store<T>().Find(new RowKey(Tenant, id), Ct))?.Row;

    private async Task<object?> Scalar(string sql)
    {
        await using var db = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = db.CreateCommand(sql);
        return await command.ExecuteScalarAsync(Ct);
    }

    private static HttpRequestMessage As(HttpMethod method, string path, string? user = "ops-alice", string? role = "tarol-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null)
            request.Headers.Add("X-User", user);
        if (role is not null)
            request.Headers.Add("X-Role", role);
        return request;
    }

    [Fact]
    public async Task An_Erasure_In_One_Process_Forgets_Everywhere_And_Is_Audited()
    {
        var schema = postgres.NewSchema();
        await using var a = await Process(schema);
        await using var b = await Process(schema);
        var ada = Customer.Register(Id<Customer>.New(), "Ada", "ada@example.org", "GB");
        await Commit(a.Services, ada);
        var order = Order.Place(Id<Order>.New(), ada.Id.Value, "Ada, 1 Main St", 42m);
        await Commit(a.Services, order);
        await Project(a.Services);
        // process b serves a cached page with Ada's name
        (await b.Services.GetRequiredService<IReadModels>().Store<CustomerRow>().Search(new ListQuery(Tenant), Ct)).TryGetValue(out Page<CustomerRow>? before).ShouldBeTrue();
        before.Items.ShouldContain(r => r.Name == "Ada");
        var cacheB = (MemoryPageCache)b.Services.GetRequiredService<IPageCache>();
        cacheB.Count.ShouldBe(1);
        var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Services.GetRequiredService<IKeyStore>().Erased += (_, _) => evicted.TrySetResult();

        var erasure = await a.Services.GetRequiredService<IDataSubjects>().EraseSubject(Tenant, ada.Id.Value, "operator", Ct);
        await evicted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);   // bounded by the notification

        erasure.Streams.Count.ShouldBe(2);
        cacheB.Count.ShouldBe(0);
        (await b.Services.GetRequiredService<IReadModels>().Store<CustomerRow>().Search(new ListQuery(Tenant), Ct)).TryGetValue(out Page<CustomerRow>? after).ShouldBeTrue();
        after.Items.ShouldHaveSingleItem().ShouldBe(new CustomerRow(ada.Id.Value, null, null, "GB"));
        (await Row<OrderRow>(b.Services, order.Id.Value))!.ShipTo.ShouldBeNull();
        (await Row<OrderRow>(b.Services, order.Id.Value))!.Total.ShouldBe(42m);
        ((await b.Services.GetRequiredService<IAggregateStore>().Load(ada.Id, _context, Ct)).GetValueOrThrow().Email).ShouldBeNull();
        (await b.Services.GetRequiredService<IAggregateReader>().Find<Customer>("email", "ada@example.org", _context, Ct)).IsNone.ShouldBeTrue();
        (await Scalar($"SELECT count(*) FROM {schema}.lookups")).ShouldBe(0L);
        (await Scalar($"SELECT actor || ' ' || action || ' ' || target FROM {schema}.operations_audit")).ShouldBe($"operator erase subject:{ada.Id.Value}");
        ((string)(await Scalar($"SELECT doc::text FROM {schema}.rm_customers_v1"))!).ShouldNotContain("ada@");
    }

    [Fact]
    public async Task The_Admin_Entry_Exports_And_Erases_A_Subject_Behind_Its_Policy()
    {
        var schema = postgres.NewSchema();
        await using var web = await Process(schema);
        var ada = Customer.Register(Id<Customer>.New(), "Ada", "ada@example.org", "GB");
        await Commit(web.Services, ada);
        var client = web.GetTestClient();
        var export = $"/tarol/admin/subjects/{ada.Id.Value}/export?tenant={Tenant}";

        (await client.SendAsync(As(HttpMethod.Get, export, user: null, role: null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(As(HttpMethod.Get, export, role: "member"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(As(HttpMethod.Get, $"/tarol/admin/subjects/{ada.Id.Value}/export?tenant=nope"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var exported = await client.SendAsync(As(HttpMethod.Get, export), Ct);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var document = JsonElement.Parse(await exported.Content.ReadAsByteArrayAsync(Ct));
        document.GetProperty("events")[0].GetProperty("data").GetProperty("email").GetString().ShouldBe("ada@example.org");

        (await client.SendAsync(As(HttpMethod.Post, $"/tarol/admin/subjects/{ada.Id.Value}/erase?tenant={Tenant}", role: "member"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var erased = await client.SendAsync(As(HttpMethod.Post, $"/tarol/admin/subjects/{ada.Id.Value}/erase?tenant={Tenant}"), Ct);
        erased.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await erased.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("streams").GetInt32().ShouldBe(1);

        var afterwards = JsonElement.Parse(await (await client.SendAsync(As(HttpMethod.Get, export), Ct)).Content.ReadAsByteArrayAsync(Ct));
        afterwards.GetProperty("events")[0].GetProperty("data").GetProperty("email").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Scalar($"SELECT string_agg(action || ':' || actor, ',' ORDER BY id) FROM {schema}.operations_audit")).ShouldBe("export:ops-alice,erase:ops-alice,export:ops-alice");
    }

    [Fact]
    public async Task Retention_Deletes_Expired_Streams_With_Their_Rows_On_A_Fake_Clock()
    {
        var schema = postgres.NewSchema();
        await using var web = await Process(schema);
        var ada = Customer.Register(Id<Customer>.New(), "Ada", "ada@example.org", "GB");
        await Commit(web.Services, ada);
        var old = Order.Place(Id<Order>.New(), ada.Id.Value, "x", 1m);
        await Commit(web.Services, old);
        _time.Advance(TimeSpan.FromDays(11));
        var fresh = Order.Place(Id<Order>.New(), ada.Id.Value, "y", 2m);
        await Commit(web.Services, fresh);
        await Project(web.Services);
        var retention = web.Services.GetServices<MaintenanceTask>().Single(static t => t.Name == "tarol.retention");

        (await retention.Run(web.Services, _time.GetUtcNow(), Ct)).ShouldBe(1);   // order: 10 days (configuration); customer: 30 ([Retention])

        (await web.Services.GetRequiredService<IAggregateStore>().Load(old.Id, _context, Ct)).IsNone.ShouldBeTrue();
        (await Row<OrderRow>(web.Services, old.Id.Value)).ShouldBeNull();
        (await Row<OrderRow>(web.Services, fresh.Id.Value)).ShouldNotBeNull();
        (await Row<CustomerRow>(web.Services, ada.Id.Value)).ShouldNotBeNull();
        (await Scalar($"SELECT action || ' ' || target FROM {schema}.operations_audit")).ShouldBe("retention stream-type:order");
    }
}

public sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-User"] is not [{ } user])
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Name, user) };
        if (Request.Headers["X-Role"] is [{ } role])
            claims.Add(new Claim(ClaimTypes.Role, role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "header")), "header")));
    }
}
