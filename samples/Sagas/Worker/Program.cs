using ArtFlow;
using ArtFlow.Samples;
using ArtFlow.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tarol.Changes;
using Tarol.Jobs;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Npgsql;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Sagas;

// ArtFlow's reaction worker. Under the AppHost the connection string "tarol" and the OTLP endpoint come from Aspire.
// Arguments: the number of auctions to settle itself (default 20, one per second; 0 = only work: the API produces them).
var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("tarol")
                       ?? throw new InvalidOperationException("No connection string 'tarol' (run it through the AppHost).");
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Settlement>().Add<DayReport>())
    .WithReactions(GeneratedReactions.Set)
    .UseNpgsqlStore(connectionString, new NpgsqlStoreOptions { NotifyFeed = true })   // EnsureSchema when the host starts; NOTIFY per commit
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })   // the read tables (LISTEN on the feed); NOTIFY per batch
    .AddProjections(p => p.Add<AuctionRow>())                      // the auction list the API serves (GET /auctions)
    .AddReactionWorker()                                           // reactions and publications; the store's key purge is used
    .AddMessaging("artflow", m => m
        .Publish(GeneratedPublications.Set)                        // artflow.auction-settled
        .Consume(GeneratedConsumers.Set)                           // billing.invoice-paid → RecordPayment
        .Trust("billing", t => t.AnyTenant())                      // the company's own billing serves every tenant
        .UseBroker(builder.Configuration)
        .UseNpgsqlDeadLetters())
    .AddJobs(j => j.Add<SettlementReport>("15 2 * * *", o => { o.TimeZone = "Europe/Vienna"; o.PerTenant = true; }),   // step 14: once per occurrence and tenant
        o => o.TenantSyncEvery = TimeSpan.FromSeconds(20));       // the demo's tenants appear while it runs; hourly by default
builder.Services.AddSingleton<ITenantDirectory, AuctionTenants>();
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("worker"))
        // not "Npgsql": every idle claim (every 100 ms per worker) would be a span and bury the reactions in the dashboard
        .WithTracing(t => t.AddSource([.. Broker.Sources, Producer.SourceName, ProjectionHost.ActivitySourceName, JobHost.ActivitySourceName]).AddOtlpExporter());

using var host = builder.Build();
await host.StartAsync();
var store = host.Services.GetRequiredService<NpgsqlAggregateStore>();
var pipeline = host.Services.GetRequiredService<ChangePipeline>();
var count = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 20;
var alice = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
for (var i = 0; i < count && !host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested; i++)
{
    using (Producer.Source.StartActivity("POST /auctions/{id}/settle"))
    {
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 150, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        _ = (await store.Append(auction, 0, alice, Option.None, default)).IsSuccess;
        _ = await pipeline.Run(new SettleAuction(auction.Id), alice);
    }
    Console.WriteLine($"settled {i + 1}, outbox rows waiting: {await store.Outbox.Pending(default)}");
    await Task.Delay(1000);
}
await host.WaitForShutdownAsync();

internal static class Producer
{
    public const string SourceName = "ArtFlow.Http";
    public static readonly System.Diagnostics.ActivitySource Source = new(SourceName);
}
