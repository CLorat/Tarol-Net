// The AppHost: PostgreSQL as a container resource, the worker with a reference to its database, the dashboard.
// PROBE_WORKER_NATIVE=<path> runs the Native AOT build of the worker instead of the project (AddExecutable).
var builder = DistributedApplication.CreateBuilder(args);
var db = builder.AddPostgres("pg").AddDatabase("tarol");
var native = Environment.GetEnvironmentVariable("PROBE_WORKER_NATIVE");
if (native is null)
    builder.AddProject<Projects.App>("worker").WithArgs("serve", "20").WithReference(db).WaitFor(db);
else
    builder.AddExecutable("worker", native, Path.GetDirectoryName(native)!, "serve", "20").WithOtlpExporter().WithReference(db).WaitFor(db);
builder.Build().Run();
