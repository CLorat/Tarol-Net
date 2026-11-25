namespace Tarol.Persistence.Npgsql;

/// <summary>
/// Starts work that outlives its caller without the caller's <see cref="ExecutionContext"/>: a listener or poll loop
/// started lazily by the first request would otherwise carry that request's <c>Activity.Current</c> (and every
/// <c>AsyncLocal</c>) forever, and each of its database calls would be a child span of that request's trace (Tatalu
/// FB-ASPIRE-1).
/// </summary>
internal static class Detached
{
    public static Task Run(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(work, CancellationToken.None);
    }
}
