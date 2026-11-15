using Npgsql;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.StronglyTypedId;

// One round trip on the one transport this binary has: declare, publish, receive.
var time = TimeProvider.System;
#if NATS
await using IMessageTransport transport = new Tarol.Messaging.Nats.NatsTransport(Environment.GetEnvironmentVariable("PROBE_NATS") ?? "nats://localhost:54222");
#elif RABBIT
await using IMessageTransport transport = new Tarol.Messaging.RabbitMq.RabbitTransport(Environment.GetEnvironmentVariable("PROBE_RABBIT") ?? "amqp://guest:guest@localhost:55672/");
#else
await using var db = new NpgsqlSlimDataSourceBuilder(Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Database=postgres").Build();
await using IMessageTransport transport = new PgTransport(db, time, "bus_size");
#endif
var subscription = new Subscription("billing.Size" + Environment.ProcessId, ["artflow.size-checked"]);
await transport.Prepare(["artflow.size-checked"], [subscription], default);
var got = new TaskCompletionSource();
using var stop = new CancellationTokenSource();
var consume = transport.Consume(subscription, (_, _) => { got.TrySetResult(); return ValueTask.FromResult(new Handled(Ack.Ack)); }, stop.Token);
await transport.Publish(new Message { Id = Guid.CreateVersion7(), Source = "artflow", Type = "artflow.size-checked", Version = 1, Time = time.GetUtcNow(), Correlation = Id<ICorrelation>.New(time), Data = "{}"u8.ToArray() }, default);
await got.Task.WaitAsync(TimeSpan.FromSeconds(30));
await stop.CancelAsync();
try { await consume; } catch (OperationCanceledException) { }
Console.WriteLine($"{transport.Name}: round trip ok");
