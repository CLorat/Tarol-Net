using System.Collections.Concurrent;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.RabbitMq;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var prefix = "aot" + Environment.ProcessId;
var url = Environment.GetEnvironmentVariable("TAROL_RABBITMQ");
await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { Uri = new Uri(url ?? "amqp://guest:guest@localhost:5672/"), Prefix = prefix, StampUserId = false });
Check("exchange and queue names", transport.ExchangeOf("artflow.auction-settled") == prefix + ".artflow" && transport.QueueOf("smoke.Consumer") == prefix + ".smoke.Consumer");
Check("options are validated", Throws(() => _ = new RabbitMqTransport(new RabbitMqTransportOptions { Uri = new Uri("amqp://x/"), Prefix = "a.b" })));

if (string.IsNullOrWhiteSpace(url))
{
    Console.WriteLine("skip broker checks: TAROL_RABBITMQ is not set");
}
else
{
    var subscription = new Subscription("smoke.Consumer", ["artflow.auction-settled"]) { Lease = TimeSpan.FromSeconds(5) };
    await transport.Prepare(["artflow.auction-settled"], [subscription], default);
    var message = new Message
    {
        Id = Guid.CreateVersion7(),
        Source = "artflow",
        Type = "artflow.auction-settled",
        Version = 1,
        Time = TimeProvider.System.GetUtcNow(),
        Correlation = Id<ICorrelation>.New(),
        Actor = "User:alice",
        Data = "{\"price\":150}"u8.ToArray(),
    };
    await transport.Publish(message, default);
    await transport.Publish(message with { Type = "artflow.nobody" }, default);
    Check("an unroutable publish counts as delivered", transport.Unroutable == 1);
    var seen = new ConcurrentQueue<Incoming>();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await transport.Consume(subscription, async (incoming, _) =>
    {
        seen.Enqueue(incoming);
        if (incoming.Attempt == 1)
            return new Handled(Ack.Retry, TimeSpan.FromMilliseconds(200));
        await stop.CancelAsync();
        return new Handled(Ack.Ack);
    }, stop.Token);
    Check("consumed, retried after a delay, acknowledged", seen.Select(i => i.Attempt).SequenceEqual([1, 2]));
    Check("the envelope survived", seen.First().Message is { ActorSubject: "alice", Type: "artflow.auction-settled" } m && m.Id == message.Id && seen.First().Topic == message.Type);
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (ArgumentException)
    {
        return true;
    }
}
