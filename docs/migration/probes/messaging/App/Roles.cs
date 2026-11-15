using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Nats;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace ArtFlow;

/// <summary>The AppHost's resources: ArtFlow's api and worker, and billing. NATS is the bus, each system has its own database.</summary>
internal static class Roles
{
    private static readonly ActivitySource Http = new("Probe.Http");
    private static readonly Id<ITenant> Tenant = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");

    public static async Task<int> Run(string role)
    {
        var time = TimeProvider.System;
        using var tracing = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is null ? null
            : Sdk.CreateTracerProviderBuilder().ConfigureResource(r => r.AddService(role))
                .AddSource(ReactionWorker.ActivitySourceName, ConsumerHost.ActivitySourceName, "Probe.Http",
                    "Microsoft.AspNetCore")   // else the request's own activity is unsampled and the parent-based sampler drops every span below it
                .AddOtlpExporter().Build();
        string Conn(string name) => Environment.GetEnvironmentVariable("ConnectionStrings__" + name) ?? throw new InvalidOperationException("no connection string " + name);
        await using var db = new NpgsqlSlimDataSourceBuilder(Conn(role == "billing" ? "billingdb" : "artflowdb")).EnableArrays().Build();
        await using var nats = new NatsTransport(Conn("nats"));
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

        if (role == "billing")
        {
            await using var billing = await Sys.Create("billing", "aspire", true, time, nats, Billing.GeneratedPublications.Create(), Billing.GeneratedConsumers.Set,
                Billing.BillingJson.Default.Options, c => c.Add<Billing.Invoice>().Add<Billing.Ledger>(), [Probe.Messaging.App.GeneratedReactions.Set],
                o => o.Sources["artflow"] = new SourceTrust { AnyTenant = true }, db: db, fresh: false);
            await nats.Prepare(billing.Publications.Topics, billing.Consumers.Subscriptions(), stop.Token);
            billing.Start(publishers: 2, concurrency: 4);
            Console.WriteLine("billing: consuming artflow.auction-settled, paying invoices, publishing billing.invoice-paid");
            var psp = Actor.Service("psp");
            var after = GlobalPosition.Start;
            while (!stop.IsCancellationRequested)                                     // the payment provider: pays every issued invoice
            {
                await foreach (var e in billing.Feed.ReadAll(after, 100, stop.Token))
                {
                    after = e.Position;
                    if (e.Event.Event.Event is not Billing.Invoice.Issued)
                        continue;
                    using var activity = Http.StartActivity("POST /webhooks/psp (invoice paid)", ActivityKind.Server);
                    _ = await billing.Pipeline.Run(new Billing.PayInvoice((Id<Billing.Invoice>)e.Event.StreamId), OperationContext.Start(psp, Tenant, time));
                }
                try { await Task.Delay(200, stop.Token); } catch (OperationCanceledException) { }
            }
            await billing.Stop();
            return 0;
        }

        await using var artflow = await Sys.Create("artflow", "aspire", true, time, nats, GeneratedPublications.Create(), GeneratedConsumers.Set,
            ArtFlowJson.Default.Options, c => c.Add<Auction>(), [], o => o.Sources["billing"] = new SourceTrust { AnyTenant = true }, db: db, fresh: false);
        if (role == "worker")
        {
            await nats.Prepare(artflow.Publications.Topics, artflow.Consumers.Subscriptions(), stop.Token);
            artflow.Start(publishers: 2, concurrency: 4);
            Console.WriteLine("worker: publishing artflow.auction-settled, consuming billing.invoice-paid");
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
            await artflow.Stop();
            return 0;
        }

        var builder = WebApplication.CreateSlimBuilder();
        if (Environment.GetEnvironmentVariable("PROBE_URL") is { } url)                 // else ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS from the AppHost
            builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.MapGet("/health", (RequestDelegate)(http => http.Response.WriteAsync("ok")));
        app.MapPost("/auctions", (RequestDelegate)(async http =>
        {
            using var activity = Http.StartActivity("POST /auctions (open and settle)", ActivityKind.Server);
            var alice = OperationContext.Start(Actor.User("alice"), Tenant, time);
            var id = (Id<Auction>)(await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow().StreamId;
            (await artflow.Pipeline.Run(new SettleAuction(id, "bob", 150), alice)).GetValueOrThrow();
            await http.Response.WriteAsync(id.Value.ToString("N"));
        }));
        await app.RunAsync(stop.Token);
        return 0;
    }
}
