// PostgreSQL as a container resource, the worker with a reference to its database, the dashboard.
// ARTFLOW_WORKER_NATIVE=<path> runs the Native AOT build of the worker instead of the project.
var builder = DistributedApplication.CreateBuilder(args);
var db = builder.AddPostgres("pg").AddDatabase("tarol");
var native = Environment.GetEnvironmentVariable("ARTFLOW_WORKER_NATIVE");
if (native is null)
    builder.AddProject<Projects.Worker>("worker").WithArgs("20").WithReference(db).WaitFor(db);
else
    builder.AddExecutable("worker", native, Path.GetDirectoryName(native)!, "20").WithOtlpExporter().WithReference(db).WaitFor(db);
builder.Build().Run();
