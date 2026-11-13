using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

var fake = new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
static string Seen() => Ambient.Provider is FakeTimeProvider ? "fake" : "system";
void Row(string c, string seen, string expect) => Console.WriteLine($"{(seen == expect ? "ok  " : "DIFF")} {c,-62} {seen}");

Console.WriteLine($"native={!RuntimeFeature.IsDynamicCodeSupported}");
var timerCreatedBefore = new TaskCompletionSource<string>();
using var early = new Timer(_ => timerCreatedBefore.TrySetResult(Seen()), null, 200, Timeout.Infinite);
var loopStartedBefore = Task.Run(async () => { await Task.Delay(200); return Seen(); });

using (Ambient.Set(fake))
{
    Row("same method after await", await After(), "fake");
    Row("Task.Run", await Task.Run(Seen), "fake");
    Row("ConfigureAwait(false) continuation", await AfterConfigureAwaitFalse(), "fake");
    Row("Task.Factory.StartNew(LongRunning)", await Task.Factory.StartNew(Seen, TaskCreationOptions.LongRunning), "fake");
    Row("Parallel.For body", ParallelSeen(), "fake");
    var tcs = new TaskCompletionSource<string>();
    using (new Timer(_ => tcs.TrySetResult(Seen()), null, 10, Timeout.Infinite)) Row("Timer created inside the scope", await tcs.Task, "fake");
    Row("Timer created BEFORE the scope", await timerCreatedBefore.Task, "system");
    Row("loop started BEFORE the scope (Task.Run)", await loopStartedBefore, "system");
    var unsafeTcs = new TaskCompletionSource<string>();
    ThreadPool.UnsafeQueueUserWorkItem(_ => unsafeTcs.TrySetResult(Seen()), null);
    Row("ThreadPool.UnsafeQueueUserWorkItem", await unsafeTcs.Task, "system");
    Task<string> suppressed;
    using (ExecutionContext.SuppressFlow()) suppressed = Task.Run(Seen);
    Row("Task.Run under ExecutionContext.SuppressFlow", await suppressed, "system");
    var unsafeTimer = new TaskCompletionSource<string>();
    using (fake.CreateTimer(_ => unsafeTimer.TrySetResult(Seen()), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan))
    {
        fake.Advance(TimeSpan.FromSeconds(1));
        Row("FakeTimeProvider timer callback (Advance)", await unsafeTimer.Task, "fake");
    }

    // A hosted BackgroundService started inside the scope: its loop captured the context at StartAsync.
    var host = Host.CreateApplicationBuilder();
    host.Services.AddHostedService<Worker>();
    using var app = host.Build();
    await app.StartAsync();
    Row("BackgroundService started inside the scope", await Worker.Seen.Task, "fake");
    await app.StopAsync();
}

// After the scope.
Row("after the scope is disposed", Seen(), "system");
await SetInAsyncHelper();
Row("set inside an awaited async helper, read by the caller", Seen(), "system");
SetInSyncHelper();
Row("set inside a sync helper, read by the caller", Seen(), "fake");

async Task<string> After() { await Task.Delay(1); return Seen(); }
async Task<string> AfterConfigureAwaitFalse() { await Task.Delay(1).ConfigureAwait(false); return Seen(); }
string ParallelSeen() { var all = new System.Collections.Concurrent.ConcurrentBag<string>(); Parallel.For(0, 64, _ => all.Add(Seen())); return all.Distinct().Single(); }
async Task SetInAsyncHelper() { await Task.Yield(); Ambient.Set(fake); }
void SetInSyncHelper() => Ambient.Set(fake);

static class Ambient
{
    private static readonly AsyncLocal<TimeProvider?> Current = new();
    public static TimeProvider Provider => Current.Value ?? TimeProvider.System;
    public static IDisposable Set(TimeProvider p) { var prev = Current.Value; Current.Value = p; return new Reset(prev); }
    private sealed class Reset(TimeProvider? prev) : IDisposable { public void Dispose() => Current.Value = prev; }
}

sealed class Worker : BackgroundService
{
    public static readonly TaskCompletionSource<string> Seen = new();
    protected override async Task ExecuteAsync(CancellationToken ct) { await Task.Yield(); Seen.TrySetResult(Ambient.Provider is FakeTimeProvider ? "fake" : "system"); }
}
