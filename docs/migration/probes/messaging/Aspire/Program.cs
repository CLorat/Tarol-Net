// PostgreSQL (one database per system) and NATS (JetStream) as container resources; ArtFlow's api and worker and billing
// are the probe app in three roles. PROBE_APP_NATIVE=<path> runs the Native AOT build for all three.
var builder = DistributedApplication.CreateBuilder(args);
var pg = builder.AddPostgres("pg");
var artflowDb = pg.AddDatabase("artflowdb", "artflow");
var billingDb = pg.AddDatabase("billingdb", "billing");
var nats = builder.AddNats("nats").WithJetStream();
var native = Environment.GetEnvironmentVariable("PROBE_APP_NATIVE");

IResourceBuilder<IResourceWithEnvironment> App(string name)
    => native is null
        ? builder.AddProject<Projects.App>(name, launchProfileName: null).WithArgs(name)
        : builder.AddExecutable(name, native, Path.GetDirectoryName(native)!, name).WithOtlpExporter();

var api = App("api").WithReference(artflowDb).WithReference(nats);   // the api writes the outbox rows; the worker publishes them
var endpoints = (IResourceBuilder<IResourceWithEndpoints>)api;
if (native is null)
    endpoints.WithHttpEndpoint(port: 5199, name: "http", isProxied: false);
else
    endpoints.WithHttpEndpoint(port: 5199, name: "http", env: "ASPNETCORE_HTTP_PORTS", isProxied: false);
endpoints.WithHttpHealthCheck("/health");
((IResourceBuilder<IResourceWithWaitSupport>)api).WaitFor(artflowDb);
var worker = App("worker").WithReference(artflowDb).WithReference(nats);
((IResourceBuilder<IResourceWithWaitSupport>)worker).WaitFor(nats).WaitFor((IResourceBuilder<IResource>)api);
var billing = App("billing").WithReference(billingDb).WithReference(nats);
((IResourceBuilder<IResourceWithWaitSupport>)billing).WaitFor(nats).WaitFor(billingDb);
builder.Build().Run();
