using Microsoft.Extensions.Time.Testing;
using Tarol.Options;

namespace Tarol.Kernel.Tests;

// Finding 14 (docs/migration/kernel.md §2): with the old global DomainClock, a test class that swapped the clock changed
// the timestamps of every other class running at the same time (100 % of them in probes/kernel/isolation). Tarol-Net has
// no shared clock: each test owns its FakeTimeProvider. These two classes run at the same time (AssemblyInfo.cs) and
// prove that neither sees the other's time.
public class ClockIsolationFakeTimeTests
{
    [Fact]
    public async Task FakeClock_Should_OnlyAffect_ThisTest()
    {
        (await ClockIsolation.Meet()).ShouldBeTrue("the two isolation classes did not run in parallel");
        var clock = new FakeTimeProvider(ClockIsolation.Y2000);
        var end = TimeProvider.System.GetUtcNow().AddMilliseconds(300);
        while (TimeProvider.System.GetUtcNow() < end)
        {
            ClockIsolation.Year(OperationContext.Start(Actor.User("a"), Option.None, clock)).ShouldBe(2000);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Task.Yield();
        }
    }
}

public class ClockIsolationSystemTimeTests
{
    [Fact]
    public async Task SystemClock_Should_NeverSee_TheOtherTestsTime()
    {
        (await ClockIsolation.Meet()).ShouldBeTrue("the two isolation classes did not run in parallel");
        int n = 0, polluted = 0;
        var end = TimeProvider.System.GetUtcNow().AddMilliseconds(250);
        while (TimeProvider.System.GetUtcNow() < end)
        {
            if (ClockIsolation.Year(OperationContext.Start(Actor.User("b"), Option.None, TimeProvider.System)) == 2000)
            {
                polluted++;
            }

            n++;
            await Task.Yield();
        }

        n.ShouldBeGreaterThan(100);
        polluted.ShouldBe(0);
    }
}

// K-3: an injected clock also reaches work that started before the test changed the time (an ambient clock does not).
public class InjectedClockReachesWorkersTests
{
    [Fact]
    public async Task Worker_StartedBefore_Should_SeeTheAdvancedTime()
    {
        var clock = new FakeTimeProvider(ClockIsolation.Y2000);
        var started = new TaskCompletionSource();
        var worker = Task.Run(async () =>
        {
            started.SetResult();
            await Task.Delay(TimeSpan.FromHours(1), clock, TestContext.Current.CancellationToken); // a timer on the test clock
            return clock.GetUtcNow();
        }, TestContext.Current.CancellationToken);
        await started.Task;
        await Task.Delay(50, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromHours(1));

        (await worker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe(ClockIsolation.Y2000.AddHours(1));
    }
}

internal static class ClockIsolation
{
    public static readonly DateTimeOffset Y2000 = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TaskCompletionSource Both = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _arrived;

    public static async Task<bool> Meet()
    {
        if (Interlocked.Increment(ref _arrived) == 2)
        {
            Both.TrySetResult();
        }

        try
        {
            await Both.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static int Year(OperationContext context)
    {
        Span<byte> bytes = stackalloc byte[16];
        context.CorrelationId.Value.TryWriteBytes(bytes, bigEndian: true, out _);
        long ms = 0;
        for (var i = 0; i < 6; i++)
        {
            ms = (ms << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(ms).Year;
    }
}
