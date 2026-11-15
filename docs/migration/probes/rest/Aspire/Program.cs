// PostgreSQL as a container resource; the API and the reaction worker are the same app in two modes, two resources.
// PROBE_APP_NATIVE=<path> runs the Native AOT build of the app for both instead of the project.
// PROBE_ZITADEL_DIR=<dir> adds Zitadel (a container on the same PostgreSQL) and a setup resource; api and worker then
// validate Zitadel's tokens (Authority) and start after the setup completed.
var builder = DistributedApplication.CreateBuilder(args);
var pg = builder.AddPostgres("pg");
var db = pg.AddDatabase("tarol");
var native = Environment.GetEnvironmentVariable("PROBE_APP_NATIVE");
var zitadelDir = Environment.GetEnvironmentVariable("PROBE_ZITADEL_DIR");
IResourceBuilder<ExecutableResource>? setup = null;
if (zitadelDir is not null)
{
    var zitadel = builder.AddContainer("zitadel", "ghcr.io/zitadel/zitadel", "v4.20.3")
        .WithArgs("start-from-init", "--masterkey", "MasterkeyNeedsToHave32Characters", "--tlsMode", "disabled")
        .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http", isProxied: false)   // the issuer must be http://localhost:8080
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
    setup = builder.AddExecutable("zitadel-setup", "python3", zitadelDir,
            Path.Combine(Environment.GetEnvironmentVariable("PROBE_ZITADEL_SETUP")!), "http://localhost:8080",
            Path.Combine(zitadelDir, "admin.pat"), Path.Combine(zitadelDir, "project.txt"))
        .WaitFor(zitadel);
}
foreach (var (name, mode, url) in new[] { ("api", "serve", "http://127.0.0.1:5099"), ("worker", "worker", "http://127.0.0.1:5098") })
    if (native is null)
        Wire(builder.AddProject<Projects.App>(name).WithArgs(mode), url, db, setup, zitadelDir);
    else
        Wire(builder.AddExecutable(name, native, Path.GetDirectoryName(native)!, mode).WithOtlpExporter(), url, db, setup, zitadelDir);
builder.Build().Run();

static void Wire<T>(IResourceBuilder<T> app, string url, IResourceBuilder<PostgresDatabaseResource> db,
    IResourceBuilder<ExecutableResource>? setup, string? zitadelDir)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    app.WithEnvironment("PROBE_URL", url).WithReference(db).WaitFor(db);
    if (setup is null)
        return;
    app.WaitForCompletion(setup)
       .WithEnvironment("PROBE_OIDC", "http://localhost:8080")
       .WithEnvironment("PROBE_OIDC_AUDIENCE_FILE", Path.Combine(zitadelDir!, "project.txt"));
}
