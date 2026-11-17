// PostgreSQL, Zitadel and a broker as container resources, Zitadel provisioned by zitadel-setup, then ArtFlow's API and
// worker and billing (step 11, M-15). ARTFLOW_BROKER=rabbitmq switches NATS JetStream for RabbitMQ.
// ARTFLOW_WORKER_NATIVE / ARTFLOW_API_NATIVE / ARTFLOW_BILLING_NATIVE = <path> run the Native AOT builds instead of the projects.
var builder = DistributedApplication.CreateBuilder(args);
var pg = builder.AddPostgres("pg");
var db = pg.AddDatabase("tarol");
var billingDb = pg.AddDatabase("billingdb");                                              // resource names are global: not "billing"
IResourceBuilder<IResourceWithConnectionString> broker = Environment.GetEnvironmentVariable("ARTFLOW_BROKER") == "rabbitmq"
    ? builder.AddRabbitMQ("rabbitmq")
    : builder.AddNats("nats").WithJetStream();

var zitadelDir = Path.Combine(Path.GetTempPath(), "artflow-zitadel");
if (Directory.Exists(zitadelDir))
    Directory.Delete(zitadelDir, recursive: true);
Directory.CreateDirectory(zitadelDir);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(zitadelDir, (UnixFileMode)0x1FF);                                // the container's user writes the PAT
var zitadel = builder.AddContainer("zitadel", "ghcr.io/zitadel/zitadel", "v4.20.3")
    .WithArgs("start-from-init", "--masterkey", "MasterkeyNeedsToHave32Characters", "--tlsMode", "disabled")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http", isProxied: false)       // the issuer is http://localhost:8080
    .WithHttpHealthCheck("/debug/ready")
    .WithBindMount(zitadelDir, "/out")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_HOST", pg.Resource.Name)
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_PORT", "5432")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_DATABASE", "zitadel")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_USERNAME", "zitadel")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_PASSWORD", "zitadel")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_SSL_MODE", "disable")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_USERNAME", "postgres")
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_PASSWORD", pg.Resource.PasswordParameter)
    .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_SSL_MODE", "disable")
    .WithEnvironment("ZITADEL_EXTERNALSECURE", "false")
    .WithEnvironment("ZITADEL_EXTERNALDOMAIN", "localhost")
    .WithEnvironment("ZITADEL_EXTERNALPORT", "8080")
    .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_USERNAME", "admin-sa")
    .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_NAME", "admin-sa")
    .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_PAT_EXPIRATIONDATE", "2030-01-01T00:00:00Z")
    .WithEnvironment("ZITADEL_FIRSTINSTANCE_PATPATH", "/out/admin.pat")
    .WaitFor(pg);
var setup = builder.AddProject<Projects.ZitadelSetup>("zitadel-setup").WithArgs("http://localhost:8080", zitadelDir).WaitFor(zitadel);

var apiNative = Environment.GetEnvironmentVariable("ARTFLOW_API_NATIVE");
var api = apiNative is null
    ? (IResourceBuilder<IResourceWithEnvironment>)builder.AddProject<Projects.Api>("api", launchProfileName: null).WithHttpEndpoint(port: 5080, name: "http", isProxied: false)
    : builder.AddExecutable("api", apiNative, Path.GetDirectoryName(apiNative)!).WithHttpEndpoint(port: 5080, name: "http", env: "ASPNETCORE_HTTP_PORTS", isProxied: false).WithOtlpExporter();
((IResourceBuilder<IResourceWithEndpoints>)api).WithHttpHealthCheck("/health/ready");      // step 15: ready = snapshot fresh, schema ensured, LISTEN up
api.WithReference(db)
   .WithEnvironment("Zitadel__Authority", "http://localhost:8080")
   .WithEnvironment("Zitadel__ProjectFile", Path.Combine(zitadelDir, "project.txt"));
((IResourceBuilder<IResourceWithWaitSupport>)api).WaitFor(db).WaitForCompletion(setup);

var workerNative = Environment.GetEnvironmentVariable("ARTFLOW_WORKER_NATIVE");
var worker = workerNative is null
    ? (IResourceBuilder<IResourceWithEnvironment>)builder.AddProject<Projects.Worker>("worker", launchProfileName: null).WithArgs("0")
        .WithHttpEndpoint(port: 5081, name: "http", isProxied: false)
    : builder.AddExecutable("worker", workerNative, Path.GetDirectoryName(workerNative)!, "0")
        .WithHttpEndpoint(port: 5081, name: "http", env: "ASPNETCORE_HTTP_PORTS", isProxied: false).WithOtlpExporter();
((IResourceBuilder<IResourceWithEndpoints>)worker).WithHttpHealthCheck("/health/ready");
worker.WithReference(db).WithReference(broker)
    .WithEnvironment("Tarol__Jobs__SettlementReport__Cron", "* * * * *");   // the nightly report every minute, so a run shows it (J-2)
((IResourceBuilder<IResourceWithWaitSupport>)worker).WaitFor(db).WaitFor(broker);

var billingNative = Environment.GetEnvironmentVariable("ARTFLOW_BILLING_NATIVE");
var billing = billingNative is null
    ? (IResourceBuilder<IResourceWithEnvironment>)builder.AddProject<Projects.Billing>("billing")
    : builder.AddExecutable("billing", billingNative, Path.GetDirectoryName(billingNative)!).WithOtlpExporter();
billing.WithReference(billingDb).WithReference(broker);
((IResourceBuilder<IResourceWithWaitSupport>)billing).WaitFor(billingDb).WaitFor(broker);
builder.Build().Run();
