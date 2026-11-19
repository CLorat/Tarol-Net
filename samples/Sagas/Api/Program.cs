using ArtFlow;
using ArtFlow.Api;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tarol.Caching;
using Tarol.Changes;
using Tarol.DataProtection;
using Tarol.Changes.AspNetCore;
using Tarol.Operations;
using Tarol.Persistence.Npgsql;
using Tarol.Queries.Npgsql;
using Tarol.Security;
using Tarol.Security.Zitadel;
using Tarol.Search;
using Tarol.Search.Npgsql;
using Tarol.Messaging;
using Tarol.Messaging.SignalR;
using ArtFlow.Samples;

// ArtFlow's API. Under the AppHost: the connection string "tarol", Zitadel's issuer and project, the OTLP endpoint.
var builder = WebApplication.CreateSlimBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("tarol")
                       ?? throw new InvalidOperationException("No connection string 'tarol' (run it through the AppHost).");
var project = builder.Configuration["Zitadel:Project"]
              ?? (builder.Configuration["Zitadel:ProjectFile"] is { } file ? File.ReadAllText(file).Trim() : null)
              ?? throw new InvalidOperationException("No Zitadel project (run it through the AppHost).");

builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Auction>().Add<Payout>().Add<Settlement>().Add<BidderProfile>())
    .WithReactions(GeneratedReactions.Set)                     // the API's commits write the worker's outbox rows …
    .WithOutboxRows(GeneratedPublications.Set)                 // … and its publication rows (the worker publishes them)
    .UseNpgsqlStore(connectionString)
    .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true })   // the worker's auction list; its checkpoint pushed (LISTEN)
    .AddCaching()                                              // list pages from memory while the checkpoint stands
    .AddSearch(s => s.Add<Auction>().Add<BidderProfile>())     // step 18: GET /search (the worker feeds the index; this host stays gated)
    .UseNpgsqlSearch()
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<SettleAuction>().May<Read<Auction>>();
        roles.Role("bidder").May<PlaceBid>().May<Read<Auction>>();
        roles.Role("tarol-admin").May<Read<BidderProfile>>();     // only operators find bidder profiles
    })
    .AddEndpoints()
    .AddOperations()                                           // step 15: the snapshot, its gauges, the health groups, the admin entry
    .UseNpgsqlKeyStore()                                       // step 16: the keys of [PersonalData] (BidderProfile)
    .AddDataProtection()                                       // …/subjects/{id}/export and …/erase under the admin policy
    // BC-9: the browser's push: the worker publishes artflow.bid-placed, this API forwards it to the hub's group
    // <tenant>/artflow.bid-placed; a signed-in user of the tenant may subscribe (the token in ?access_token=, BC-2)
    .AddMessaging("artflow", m => m
        .UseBroker(builder.Configuration)
        .PushToUi(push =>
        {
            push.Name = "UiPush-" + Environment.MachineName.Replace('.', '-');   // one subscription per web node
            push.Forward("artflow.bid-placed");
            push.Rules.Subscribe("artflow.bid-placed");
        }))
    .AddZitadel(zitadel =>
    {
        zitadel.Authority = builder.Configuration["Zitadel:Authority"] ?? "http://localhost:8080";
        zitadel.ProjectId = project;
        zitadel.RequireHttpsMetadata = false;                    // the local container has no TLS
        if (builder.Configuration["Zitadel:ClockSkew"] is { } skew)   // ARTFLOW_TOKEN_LIFETIME: an expired token is refused at once
            zitadel.ClockSkew = TimeSpan.Parse(skew, System.Globalization.CultureInfo.InvariantCulture);
    });
builder.Services.AddSingleton<IAuthorizationHandler, SettleOwnAuction>();
builder.Services.Configure<WebhookOptions>(o => o.Senders["payments"] = new WebhookSender { Secrets = { builder.Configuration["Webhooks:Payments"] ?? "whsec_sample" } });
builder.Services.AddAuthorizationBuilder().AddPolicy("tarol-admin", p => p.RequireRole("tarol-admin"));   // operators (a Zitadel project role)
builder.Services.AddOpenApi(o => o.AddTarolEndpoints());
// BC-1: the browser client's dev server (Vite) on another origin may send Tarol's request headers and read ETag, Location,
// Tarol-Position, …; ArtFlow:BrowserOrigins (comma-separated) overrides the default
var origins = (builder.Configuration["ArtFlow:BrowserOrigins"] ?? "http://localhost:5173").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(cors => cors.AddPolicy("browser", policy => policy.WithOrigins(origins).AllowAnyMethod().WithTarolHeaders()));
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("api"))
        // step 15: Tarol's sources and meters by wildcard; Npgsql traces only inside a request (the store's data source filters roots)
        .WithTracing(t => t.AddSource("Microsoft.AspNetCore", "Tarol.*", "Npgsql").AddOtlpExporter())
        .WithMetrics(m => m.AddMeter("Tarol.*", "Npgsql", "Microsoft.AspNetCore.Hosting").AddOtlpExporter());

var app = builder.Build();
app.UseCors("browser");                                        // before authentication: a preflight carries no token
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapTarolHealth();                                          // /health/live, /health/ready (the AppHost waits on it), /health/tarol
app.MapTarolAdmin("/tarol/admin", policy: "tarol-admin");
app.MapChanges();
app.MapTarolSearch("/search");
app.MapMessageHub();                                           // BC-9: /tarol/messages                                 // step 18: one search box over auctions, bids and (for operators) bidders
app.Run();
