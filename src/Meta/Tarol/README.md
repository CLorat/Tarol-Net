# Tarol

One reference for everything Tarol offers in-process: event-sourced aggregates, changes and their HTTP entries, queries and the
page cache, reactions and sagas, messaging, jobs, security, operations and data protection, with every generator.

```xml
<PackageReference Include="Tarol" Version="0.1.0" />
<!-- then the implementations you run on -->
<PackageReference Include="Tarol.Persistence.Npgsql" Version="0.1.0" />   <!-- the PostgreSQL event store -->
<PackageReference Include="Tarol.Queries.Npgsql" Version="0.1.0" />       <!-- read tables in PostgreSQL -->
<PackageReference Include="Tarol.Messaging.Nats" Version="0.1.0" />       <!-- or .RabbitMq, or .Npgsql (the in-database bus) -->
```

| Included | Not included (reference it when you use it) |
|---|---|
| Tarol.Options, Tarol.Results, Tarol.StronglyTypedId, Tarol.Kernel, Tarol.Validations, Tarol.Primitives, Tarol.Persistence, Tarol.Changes, Tarol.Changes.AspNetCore, Tarol.Queries, Tarol.Caching, Tarol.Search, Tarol.Sagas, Tarol.Messaging, Tarol.Jobs, Tarol.Security, Tarol.Operations, Tarol.DataProtection | Tarol.Persistence.Npgsql, Tarol.Queries.Npgsql, Tarol.Search.Npgsql, Tarol.Messaging.Npgsql, Tarol.Messaging.Nats, Tarol.Messaging.RabbitMq, Tarol.Messaging.SignalR, Tarol.Security.Zitadel |

* The package has no code. Every included package's generators and analyzers run in your project.
* `Tarol.Changes.AspNetCore`, `Tarol.Security` and `Tarol.Operations` bring the ASP.NET Core shared framework. A worker service
  that wants none of it references the packages it uses instead of `Tarol`.
* Each package has its own README and agent skill. The skill `tarol-overview` says which package to use for what.
