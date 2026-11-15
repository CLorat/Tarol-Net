using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Smoke;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.AotSmoke;
using Tarol.Persistence;
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

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
