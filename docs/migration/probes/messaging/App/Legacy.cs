// Candidate T-D: the old Messaging layering as the base. IMessageBackplane, InMemoryMessageBackplane and the loop of
// MessageBackplaneRunner are copied from CLorat/Tarol@6a62f81 (src/Messaging/Messaging.Backplane/IMessageBackplane.cs:3-8,
// Messaging.Backplane.InMemory/InMemoryMessageBackplane.cs:5-23, Messaging.Backplane/MessageBackplaneRunner.cs:10-55);
// only Clock.UtcNow became the injected TimeProvider and the gateway call a delegate.
using System.Diagnostics;
using System.Threading.Channels;
using static ArtFlow.Lab;

namespace ArtFlow.Legacy;

public interface IMessageBackplane
{
    Task Publish(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    Task Start(Func<string, ReadOnlyMemory<byte>, Task> onMessageReceived, CancellationToken ct);
}

public class InMemoryMessageBackplane : IMessageBackplane
{
    private record InMemoryMessage(string Topic, ReadOnlyMemory<byte> Payload);

    private readonly Channel<InMemoryMessage> _channel = Channel.CreateUnbounded<InMemoryMessage>();

    public async Task Publish(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(new InMemoryMessage(topic, payload), cancellationToken);
    }

    public async Task Start(Func<string, ReadOnlyMemory<byte>, Task> onMessageReceived, CancellationToken cancellationToken)
    {
        await foreach (var msg in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            await onMessageReceived(msg.Topic, msg.Payload);
        }
    }
}

/// <summary>The loop of the old MessageBackplaneRunner.ExecuteAsync (the gateway's BroadcastAsync is <c>broadcast</c>).</summary>
public sealed class Runner(IMessageBackplane backplane, Func<string, ReadOnlyMemory<byte>, Task> broadcast, TimeProvider time)
{
    public int Starts;

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int retryCount = 0;
        const int maxDelayMs = 30000;
        DateTimeOffset? lastStartTime = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (lastStartTime.HasValue && (time.GetUtcNow() - lastStartTime.Value).TotalSeconds > 30)
                    retryCount = 0;
                lastStartTime = time.GetUtcNow();
                Interlocked.Increment(ref Starts);
                await backplane.Start(async (topic, payload) => await broadcast(topic, payload), stoppingToken);
                retryCount = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                retryCount = Math.Min(retryCount + 1, 40);
                double powerValue = Math.Min(retryCount, 20);
                int delay = (int)Math.Min(Math.Pow(2, powerValue) * 1000, maxDelayMs);
                int jitter = Random.Shared.Next(-500, 501);
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, delay + jitter)), time, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}

/// <summary>A backplane whose stream ends (a broker that closes the subscription): Start returns at once.</summary>
public sealed class EndingBackplane : IMessageBackplane
{
    public Task Publish(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Start(Func<string, ReadOnlyMemory<byte>, Task> onMessageReceived, CancellationToken ct) => Task.CompletedTask;
}

internal static class LegacyProbe
{
    public static async Task<int> Run()
    {
        Console.WriteLine($"# legacy: the old backplane shape (T-D), {Runtime}");
        var time = TimeProvider.System;

        // 1. one failing handler: the message is gone and every later one waits for the back-off (F13)
        var backplane = new InMemoryMessageBackplane();
        var got = new List<(string Payload, TimeSpan At)>();
        var clock = Stopwatch.StartNew();
        var runner = new Runner(backplane, (topic, payload) =>
        {
            var text = System.Text.Encoding.UTF8.GetString(payload.Span);
            if (text == "poison")
                throw new InvalidOperationException("one bad message");
            lock (got) got.Add((text, clock.Elapsed));
            return Task.CompletedTask;
        }, time);
        using (var stop = new CancellationTokenSource())
        {
            var loop = runner.ExecuteAsync(stop.Token);
            await backplane.Publish("t", "poison"u8.ToArray(), default);
            await backplane.Publish("t", "next"u8.ToArray(), default);
            await Until(() => { lock (got) return got.Count >= 1; }, TimeSpan.FromSeconds(10));
            await stop.CancelAsync();
            await loop;
        }
        Check("a handler that throws once: the message is lost (no ack, no redelivery) and the next waits for the back-off",
            got.Count == 1 && got[0].Payload == "next" && got[0].At > TimeSpan.FromSeconds(1),
            $"received [{string.Join(", ", got.Select(g => g.Payload))}], 'next' after {got.FirstOrDefault().At.TotalSeconds:F1} s, Start called {runner.Starts}x");

        // 2. two runners on one backplane: competing consumers, not a fan-out
        var shared = new InMemoryMessageBackplane();
        int a = 0, b = 0;
        using (var stop = new CancellationTokenSource())
        {
            var ra = new Runner(shared, (_, _) => { Interlocked.Increment(ref a); return Task.Delay(1); }, time).ExecuteAsync(stop.Token);
            var rb = new Runner(shared, (_, _) => { Interlocked.Increment(ref b); return Task.Delay(1); }, time).ExecuteAsync(stop.Token);
            for (var i = 0; i < 100; i++)
                await shared.Publish("t", "x"u8.ToArray(), default);
            await Until(() => a + b >= 100, TimeSpan.FromSeconds(10));
            await stop.CancelAsync();
            await Task.WhenAll(ra, rb);
        }
        Check("two runners (two nodes) on one in-memory backplane: each message reaches one of them", a + b == 100 && a > 0 && b > 0, $"runner A {a}, runner B {b}");

        // 3. a backplane whose stream ends: the loop restarts at once (F12)
        var spin = new Runner(new EndingBackplane(), (_, _) => Task.CompletedTask, time);
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            await spin.ExecuteAsync(stop.Token);
        Check("a stream that ends: the runner restarts without a delay (busy spin)", spin.Starts > 10_000, $"{spin.Starts:N0} restarts in 1 s");

        Info("what T-A adds to this shape: an envelope (Message), a durable subscription per consumer, an answer per message (Ack/Retry), dead letters; Gateway and SignalR stay for the UI push (11D)");
        Console.WriteLine(Failures == 0 ? "ALL CHECKS PASSED" : $"{Failures} CHECK(S) FAILED");
        return Failures == 0 ? 0 : 1;
    }
}
