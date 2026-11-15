using ArtFlow.Samples;
using ArtFlow.Billing;
using Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Messaging.Npgsql;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas;

// Billing, ArtFlow's second system. Under the AppHost: its own database (connection string "billingdb"), the broker
// ("nats" or "rabbitmq") and the OTLP endpoint come from Aspire.
var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("billingdb")
                       ?? throw new InvalidOperationException("No connection string 'billingdb' (run it through the AppHost).");
builder.Services.AddTarol(BillingJson.Default, codec => codec.Add<Invoice>())
    .WithReactions(GeneratedReactions.Set)                         // PayInvoice
    .UseNpgsqlStore(connectionString)
    .AddReactionWorker()
    .AddMessaging("billing", m => m
        .Consume(GeneratedConsumers.Set)                           // artflow.auction-settled → IssueInvoice
        .Publish(GeneratedPublications.Set)                        // billing.invoice-paid
        .Trust("artflow", t => t.AnyTenant())
        .UseBroker(builder.Configuration)
        .UseNpgsqlDeadLetters());
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("billing"))
        .WithTracing(t => t.AddSource(Broker.Sources).AddOtlpExporter());

await builder.Build().RunAsync();
