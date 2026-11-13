using Microsoft.Extensions.Time.Testing;

// (old) Tarol.Primitives.DomainClock at 6a62f81, DomainEvent.cs:114: a global mutable static.
public static class DomainClock
{
    public static TimeProvider Provider { get; set; } = TimeProvider.System;
}

// (old) Tarol.Clock at 6a62f81: AsyncLocal with a restore scope.
public static class AmbientClock
{
    private static readonly AsyncLocal<TimeProvider?> Current = new();
    public static TimeProvider Provider => Current.Value ?? TimeProvider.System;
    public static IDisposable SetProvider(TimeProvider provider)
    {
        var previous = Current.Value;
        Current.Value = provider;
        return new Reset(previous);
    }
    private sealed class Reset(TimeProvider? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public sealed record Event
{
    public DateTimeOffset StaticStamp { get; } = DomainClock.Provider.GetUtcNow();
    public DateTimeOffset AmbientStamp { get; } = AmbientClock.Provider.GetUtcNow();
}

public static class Fixed
{
    public static readonly DateTimeOffset Y2000 = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static FakeTimeProvider Clock() => new(Y2000);
}

// Rendezvous so the two classes provably overlap (each waits up to 5 s for the other).
public static class Overlap
{
    private static int _arrived;
    private static readonly TaskCompletionSource Both = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task<bool> Meet()
    {
        if (Interlocked.Increment(ref _arrived) == 2) Both.TrySetResult();
        return await Task.WhenAny(Both.Task, Task.Delay(5000)) == Both.Task;
    }
}
