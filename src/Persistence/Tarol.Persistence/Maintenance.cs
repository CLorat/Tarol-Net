namespace Tarol.Persistence;

/// <summary>
/// Recurring maintenance of a Tarol package (step 14, J-9), e.g. the bus retention of <c>Tarol.Messaging.Npgsql</c>.
/// Register it as a singleton; with a job host (<c>AddJobs</c> of <c>Tarol.Jobs</c>) it runs as the system job
/// <see cref="Name"/> once per <see cref="Every"/> across the cluster. Without a job host nothing runs it.
/// </summary>
/// <param name="Name">The system job's name, e.g. <c>"tarol.purge-messages"</c> (configurable as <c>Tarol:Jobs:&lt;name&gt;</c>).</param>
/// <param name="Every">The interval (whole seconds).</param>
/// <param name="Run">The work: services, the occurrence (UTC) and the token in, the number of things it removed or changed out (logged).</param>
public sealed record MaintenanceTask(string Name, TimeSpan Every, Func<IServiceProvider, DateTimeOffset, CancellationToken, Task<int>> Run);

/// <summary>
/// Registered by a job host (<c>Tarol.Jobs</c>): Tarol's maintenance runs as its system jobs, once per cluster. Packages that
/// would otherwise run maintenance in every process (the key purge of <c>Tarol.Sagas</c>' <c>ReactionHost</c>) skip it.
/// </summary>
public interface IMaintenanceHost;
