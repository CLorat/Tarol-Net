using ArtFlow;
using ArtFlow.Api;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tarol.Changes;
using Tarol.Changes.AspNetCore;
using Tarol.Persistence.Npgsql;
using Tarol.Security;
using Tarol.Security.Zitadel;

// ArtFlow's API. Under the AppHost: the connection string "tarol", Zitadel's issuer and project, the OTLP endpoint.
var builder = WebApplication.CreateSlimBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("tarol")
                       ?? throw new InvalidOperationException("No connection string 'tarol' (run it through the AppHost).");
var project = builder.Configuration["Zitadel:Project"]
              ?? (builder.Configuration["Zitadel:ProjectFile"] is { } file ? File.ReadAllText(file).Trim() : null)
              ?? throw new InvalidOperationException("No Zitadel project (run it through the AppHost).");

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Settlement>())
    .WithReactions(GeneratedReactions.Set)                     // the API's commits write the worker's outbox rows …
    .WithOutboxRows(GeneratedPublications.Set)                 // … and its publication rows (the worker publishes them)
    .UseNpgsqlStore(connectionString)
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<SettleAuction>().May<Read<Auction>>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
    })
    .AddEndpoints()
    .AddZitadel(zitadel =>
    {
        zitadel.Authority = builder.Configuration["Zitadel:Authority"] ?? "http://localhost:8080";
        zitadel.ProjectId = project;
        zitadel.RequireHttpsMetadata = false;                    // the local container has no TLS
    });
builder.Services.AddSingleton<IAuthorizationHandler, SettleOwnAuction>();
builder.Services.Configure<WebhookOptions>(o => o.Senders["payments"] = new WebhookSender { Secrets = { builder.Configuration["Webhooks:Payments"] ?? "whsec_sample" } });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi(o => o.AddTarolEndpoints());
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("api"))
        .WithTracing(t => t.AddSource("Microsoft.AspNetCore").AddOtlpExporter());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapGet("/health", () => "ok");
app.MapChanges();
app.Run();
