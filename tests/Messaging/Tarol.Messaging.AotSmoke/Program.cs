using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Smoke;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.AotSmoke;
using Tarol.Persistence;
using Tarol.Sagas;
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

var account = Guid.Parse("0193a000-0000-7000-8000-000000000001");

// the envelope: CloudEvents binary mode, read back
var sample = new Message
{
    Id = Guid.CreateVersion7(),
    Source = "billing",
    Type = "billing.paid",
    Version = 1,
    Time = TimeProvider.System.GetUtcNow(),
    Correlation = Id<ICorrelation>.New(),
    Actor = "User:alice",
    Data = JsonSerializer.SerializeToUtf8Bytes(new PaidV1(account, 5m), SmokeJson.Default.PaidV1),
};
var headers = sample.Headers().ToDictionary();
Check("envelope round trip", Message.From(h => headers.TryGetValue(h, out var v) ? v : null, sample.Data) is { Type: "billing.paid", ActorSubject: "alice" });

// MessageTest: commit, replay, invalid mapping, unknown source
var test = MessageTest.At(DateTimeOffset.UnixEpoch, GeneratedConsumers.Set, SmokeJson.Default.Options, "smoke").Trust("billing");
var paid = test.Message("billing.paid", 1, new PaidV1(account, 5m), SmokeJson.Default.PaidV1);
await test.Send(paid);
await test.Send(paid);
await test.Send(test.Message("billing.paid", 1, new PaidV1(account, 0m), SmokeJson.Default.PaidV1));
await test.Send(test.Message("payments.paid", 1, new PaidV1(account, 5m), SmokeJson.Default.PaidV1));
var attempts = await test.Drain();
Check("consumed, replayed, invalid buried", attempts is [{ Verdict: Verdict.Done, Outcome.Replayed: false }, { Verdict: Verdict.Done, Outcome.Replayed: true }, { Verdict: Verdict.Bury }]);
Check("balance", test.Load((Id<Account>)account).Balance == 5m);
Check("consumer context", test.Store.Recorded.Single().Context.Actor.ToString() == "System:Consumers.CreditAccount (for test-sender)");

// the hosted consumers on the in-memory transport
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddTarol(SmokeJson.Default, codec => codec.Add<Account>()).UseInMemoryStore()
    .AddMessaging("smoke", m => m.Consume(GeneratedConsumers.Set).Trust("billing").UseInMemoryTransport().UseInMemoryDeadLetters());
using var host = builder.Build();
await host.StartAsync();
await Task.Delay(100);
await host.Services.GetRequiredService<IMessageTransport>().Publish(sample, default);
var store = (InMemoryAggregateStore)host.Services.GetRequiredService<IAggregateStore>();
var waited = System.Diagnostics.Stopwatch.StartNew();
while (store.Recorded.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(10))
    await Task.Delay(20);
await host.StopAsync();
Check("hosted consumer committed", store.Recorded.Count == 1 && store.Recorded[0].Event.Event is Account.Credited { Amount: 5m });

// publications and effects (11B): MessagingTest drains outbox → transport → consumers
var two = MessagingTest.At(DateTimeOffset.UnixEpoch)
    .System("smoke", SmokeJson.Default.Options, o =>
    {
        o.Consumers = GeneratedConsumers.Set;
        o.Publications = GeneratedPublications.Set;
        o.Effects = new EffectSet().Add<Account.Credited>("notify", static _ => new Notify(Notifiers.Test));
        o.Trust("billing");
    });
await two.Transport.Publish(sample, default);
var steps = await two.Drain();
var credited = two["smoke"].Store.Recorded.Single();
Check("published from the outbox", two.Transport.Published is [_, { Type: "smoke.account-credited", Source: "smoke" } m] && m.Id == credited.EventId);
Check("effect keyed by the event id", Notifiers.Test.Keys is [var key] && key == credited.EventId.ToString("N") && steps.Count(s => s.Row is not null) == 2);

// the hosted reaction worker publishes and runs the effect from DI
var workerBuilder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
workerBuilder.Services.AddSingleton<Notifier>();
workerBuilder.Services.AddTarol(SmokeJson.Default, codec => codec.Add<Account>()).UseInMemoryStore()
    .AddReactionWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(10))
    .AddMessaging("smoke", m => m.Consume(GeneratedConsumers.Set).Publish(GeneratedPublications.Set).Effect<Account.Credited, Notify>("notify")
        .Trust("billing").UseInMemoryTransport().UseInMemoryDeadLetters());
using var workerHost = workerBuilder.Build();
await workerHost.StartAsync();
await Task.Delay(100);
var workerTransport = (InMemoryTransport)workerHost.Services.GetRequiredService<IMessageTransport>();
await workerTransport.Publish(sample, default);
var notifier = workerHost.Services.GetRequiredService<Notifier>();
waited.Restart();
while ((workerTransport.Published.Count < 2 || notifier.Keys.Count == 0) && waited.Elapsed < TimeSpan.FromSeconds(10))
    await Task.Delay(20);
await workerHost.StopAsync();
Check("hosted publication and effect", workerTransport.Published.Count == 2 && notifier.Keys.Count == 1);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

internal static class Notifiers
{
    public static readonly Notifier Test = new();
}
