using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tarol.Persistence.Npgsql;
using Testcontainers.PostgreSql;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

[assembly: AssemblyFixture(typeof(Tarol.Changes.AspNetCore.IntegrationTests.Postgres))]

namespace Tarol.Changes.AspNetCore.IntegrationTests;

/// <summary>One PostgreSQL 17 container per run; each test gets a schema of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    public string ConnectionString => _container.GetConnectionString();

    public string NewSchema() => $"h{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>The HTTP entries on the PostgreSQL store: keys with fingerprints, If-Match, views, the trace in the event.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlEndpointTests(Postgres postgres) : IAsyncLifetime
{
    private Tests.TestApp _app = null!;

    public async ValueTask InitializeAsync()
        => _app = await Start(store: tarol => tarol.UseNpgsqlStore(postgres.ConnectionString, new NpgsqlStoreOptions { Schema = postgres.NewSchema() }));

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task Keys_replay_refuse_another_body_and_are_scoped_to_the_caller()
    {
        var tulips = Auction("Tulips");

        var first = await _app.Post("/auctions", tulips, Alice, h => h.Add("Idempotency-Key", "k-1"));
        var again = await _app.Post("/auctions", tulips, Alice, h => h.Add("Idempotency-Key", "k-1"));
        var other = await _app.Post("/auctions", Auction("Roses"), Alice, h => h.Add("Idempotency-Key", "k-1"));
        var bob = await _app.Post("/auctions", tulips, Bob, h => h.Add("Idempotency-Key", "k-1"));

        first.Status.ShouldBe(HttpStatusCode.Created);
        again.Status.ShouldBe(HttpStatusCode.OK);
        again.Body.ShouldBe(first.Body);
        other.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        bob.Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task If_match_and_the_view_follow_the_stored_version()
    {
        var id = await _app.Created(Alice);

        var stale = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"5\"")));
        var current = await _app.Post($"/auctions/{id}/bids", """{"amount":150}""", Bob, h => h.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));
        var view = await _app.Get($"/auctions/{id}", Rita);
        var same = await _app.Get($"/auctions/{id}", Rita, h => h.IfNoneMatch.Add(new EntityTagHeaderValue("\"2\"")));
        var otherTenant = await _app.Get($"/auctions/{id}", Carol);

        stale.Status.ShouldBe(HttpStatusCode.Conflict);
        current.Status.ShouldBe(HttpStatusCode.NoContent);
        view.ETag.ShouldBe("\"2\"");
        same.Status.ShouldBe(HttpStatusCode.NotModified);
        otherTenant.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_callers_traceparent_continues_into_the_event_when_tracing_listens()
    {
        var trace = ActivityTraceId.CreateRandom();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.TraceId == trace ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
        };
        ActivitySource.AddActivityListener(listener);

        var created = await _app.Post("/auctions", Auction(), Alice, h => h.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01"));

        created.Status.ShouldBe(HttpStatusCode.Created);
        var store = _app.App.Services.GetRequiredService<NpgsqlAggregateStore>();
        var db = _app.App.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = db.CreateCommand($"SELECT traceparent FROM {store.Schema}.events ORDER BY position DESC LIMIT 1");
        ((string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull().ShouldContain(trace.ToString());
    }
}
