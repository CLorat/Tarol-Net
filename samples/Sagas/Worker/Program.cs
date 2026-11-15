using ArtFlow;
using ArtFlow.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;

// ArtFlow's reaction worker. Under the AppHost the connection string "tarol" and the OTLP endpoint come from Aspire.
// Arguments: the number of settled auctions to produce (default 20; 0 = only work), one per second, for the dashboard.
var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("tarol")
                       ?? throw new InvalidOperationException("No connection string 'tarol' (run it through the AppHost).");
var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
var codec = new EventCodec(new System.Text.Json.JsonSerializerOptions(AppJson.Default.Options)).Add<Auction>().Add<Payout>().Add<Settlement>();
var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Reactions = GeneratedReactions.Set });
await store.EnsureSchema(default);
var pipeline = new ChangePipeline(store, TimeProvider.System);

builder.Services.AddSingleton<IOutbox>(store.Outbox);
builder.Services.AddSingleton(pipeline);
builder.Services.AddTarolReactions(GeneratedReactions.Set, o => o.PurgeKeys = store.PurgeKeys);
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("worker"))
        // not "Npgsql": every idle claim (every 100 ms per worker) would be a span and bury the reactions in the dashboard
        .WithTracing(t => t.AddSource(ReactionWorker.ActivitySourceName, Producer.SourceName).AddOtlpExporter());

using var host = builder.Build();
await host.StartAsync();
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
