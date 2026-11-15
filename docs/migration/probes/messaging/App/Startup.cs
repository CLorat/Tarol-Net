using System.Diagnostics;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.StronglyTypedId;
using static ArtFlow.Lab;

namespace ArtFlow;

/// <summary>Process start → transport connected, subscription declared, one message published and received.</summary>
internal static class Startup
{
    public static async Task<int> Run(string kind)
    {
        var time = TimeProvider.System;
        await using var transport = Transport(kind, time, "bus_startup");
        var subscription = new Subscription("billing.Startup" + Environment.ProcessId, ["artflow.auction-started"]);
        await transport.Prepare(["artflow.auction-started"], [subscription], default);
        var got = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();
        var consume = transport.Consume(subscription, (_, _) => { got.TrySetResult(); return ValueTask.FromResult(new Handled(Ack.Ack)); }, stop.Token);
        await transport.Publish(new Message
        {
            Id = Guid.CreateVersion7(), Source = "artflow", Type = "artflow.auction-started", Version = 1, Time = time.GetUtcNow(),
            Correlation = Id<ICorrelation>.New(time), Data = "{}"u8.ToArray(),
        }, default);
        await got.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        await stop.CancelAsync();
        try { await consume; } catch (OperationCanceledException) { }
        Console.WriteLine($"{kind}: first message after {ms:F0} ms");
        return 0;
    }
}
