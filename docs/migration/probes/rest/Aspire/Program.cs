// PostgreSQL as a container resource; the API and the reaction worker are the same app in two modes, two resources.
// PROBE_APP_NATIVE=<path> runs the Native AOT build of the app for both instead of the project.
var builder = DistributedApplication.CreateBuilder(args);
var db = builder.AddPostgres("pg").AddDatabase("tarol");
var native = Environment.GetEnvironmentVariable("PROBE_APP_NATIVE");
foreach (var (name, mode, url) in new[] { ("api", "serve", "http://127.0.0.1:5099"), ("worker", "worker", "http://127.0.0.1:5098") })
    if (native is null)
        builder.AddProject<Projects.App>(name).WithArgs(mode).WithEnvironment("PROBE_URL", url).WithReference(db).WaitFor(db);
    else
        builder.AddExecutable(name, native, Path.GetDirectoryName(native)!, mode).WithEnvironment("PROBE_URL", url).WithOtlpExporter().WithReference(db).WaitFor(db);
builder.Build().Run();
