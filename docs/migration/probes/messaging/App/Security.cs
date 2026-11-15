using System.Text;
using Npgsql;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Nats;
using Tarol.Messaging.RabbitMq;
using Tarol.Sagas;
using Tarol.StronglyTypedId;
using static ArtFlow.Lab;

namespace ArtFlow;

/// <summary>Who may publish under a contract prefix: enforced by the transport, checked again by the consumer.</summary>
internal static class Security
{
    public static async Task<int> Run(string kind)
    {
        Console.WriteLine($"# security: transport {kind}, {Runtime}");
        var time = TimeProvider.System;
        switch (kind)
        {
            case "nats":
            {
                var url = Environment.GetEnvironmentVariable("PROBE_NATS_AUTH") ?? "nats://localhost:54223";
                await using (var admin = new NatsTransport(url, "artflow", "artflow"))
                    await admin.Prepare(["artflow.auction-settled", "billing.invoice-paid"], [], default);
                await using var billing = new NatsTransport(url, "billing", "billing");
                await using var artflow = new NatsTransport(url, "artflow", "artflow");
                var forged = await billing.TryPublishRaw("artflow.auction-settled", Cancel(5));
                Check("NATS: user billing publishes on artflow.auction-settled → refused by the server's subject permissions", forged.StartsWith("refused", StringComparison.Ordinal), forged);
                var own = await billing.TryPublishRaw("billing.invoice-paid", Cancel(5));
                Check("NATS: user billing publishes on billing.invoice-paid → accepted", own.StartsWith("accepted", StringComparison.Ordinal), own);
                Info("NATS stamps no publisher identity on a message: the subject prefix (enforced per user) is the authenticated source");

                // the dedup trap: Nats-Msg-Id is unique per stream, not per subject
                await using var trap = new NatsTransport(url, "artflow", "artflow") { DedupByEventIdOnly = true };
                var id = Guid.CreateVersion7();
                Message M(int v) => new() { Id = id, Source = "artflow", Type = "artflow.auction-settled", Version = v, Time = time.GetUtcNow(), Correlation = Id<ICorrelation>.New(time), Data = "{}"u8.ToArray() };
                var before = await artflow.Count("ARTFLOW", default);
                await trap.Publish(M(1), default);
                await trap.Publish(M(2), default);
                var trapped = await artflow.Count("ARTFLOW", default) - before;
                before = await artflow.Count("ARTFLOW", default);
                await artflow.Publish(M(1), default);
                await artflow.Publish(M(2), default);
                var kept = await artflow.Count("ARTFLOW", default) - before;
                Check("NATS: Nats-Msg-Id = event id drops v2 of a dual publish as a duplicate; (event, contract, version) keeps both",
                    trapped == 1 && trap.Duplicated == 1 && kept == 2, $"event id only: {trapped} stored, {trap.Duplicated} duplicate; DedupId: {kept} stored");
                break;
            }
            case "rabbit":
            {
                var host = Environment.GetEnvironmentVariable("PROBE_RABBIT_HOST") ?? "localhost:55672";
                await using (var admin = new RabbitTransport($"amqp://artflow:artflow@{host}/"))
                    await admin.Prepare(["artflow.auction-settled"], [], default);
                await using (var admin = new RabbitTransport($"amqp://billing:billing@{host}/"))
                    await admin.Prepare(["billing.invoice-paid"], [], default);
                var billing = new RabbitTransport($"amqp://billing:billing@{host}/");
                var forged = await billing.TryPublishRaw("artflow", "artflow.auction-settled", null, Cancel(5));
                Check("RabbitMQ: user billing publishes to exchange artflow → refused (write permission)", forged.Contains("ACCESS_REFUSED", StringComparison.Ordinal), forged);
                var spoofed = await billing.TryPublishRaw("billing", "billing.invoice-paid", "artflow", Cancel(5));
                Check("RabbitMQ: user billing sets user_id artflow → refused by the broker", spoofed.Contains("PRECONDITION_FAILED", StringComparison.Ordinal), spoofed);
                var own = await billing.TryPublishRaw("billing", "billing.invoice-paid", "billing", Cancel(5));
                Check("RabbitMQ: user billing publishes to exchange billing with user_id billing → accepted", own == "accepted", own);
                Info("RabbitMQ validates user_id when it is set; a publisher that omits it is identified only by the exchange it may write");
                break;
            }
            case "postgres":
            {
                var schema = "bus_sec_" + Environment.ProcessId;
                var setup = new PgTransport(Db, time, schema);
                await setup.Prepare([], [new Subscription("billing.IssueInvoice", ["artflow.auction-settled"])], default);
                await using (var roles = Db.CreateCommand($"""
                    DO $$ BEGIN
                      IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'artflow') THEN CREATE ROLE artflow LOGIN PASSWORD 'artflow'; END IF;
                      IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'billing') THEN CREATE ROLE billing LOGIN PASSWORD 'billing'; END IF;
                    END $$;
                    GRANT USAGE ON SCHEMA {schema} TO artflow, billing;
                    GRANT SELECT, INSERT ON {schema}.messages, {schema}.subscriptions TO artflow, billing;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON {schema}.deliveries TO artflow, billing;
                    ALTER TABLE {schema}.messages ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY publish_own_prefix ON {schema}.messages FOR INSERT WITH CHECK (split_part(type, '.', 1) = session_user);
                    CREATE POLICY read_all ON {schema}.messages FOR SELECT USING (true);
                    """))
                    await roles.ExecuteNonQueryAsync();
                var asBilling = new NpgsqlConnectionStringBuilder(Pg) { Username = "billing", Password = "billing" }.ConnectionString;
                await using var billingDb = new NpgsqlSlimDataSourceBuilder(asBilling).Build();
                var map = new Dictionary<string, string> { ["artflow"] = "artflow", ["billing"] = "billing" };
                var billing = new PgTransport(billingDb, time, schema) { Roles = map };
                var forged = new Message { Id = Guid.CreateVersion7(), Source = "artflow", Type = "artflow.auction-settled", Version = 1, Time = time.GetUtcNow(), Correlation = Id<ICorrelation>.New(time), Data = "{}"u8.ToArray() };
                string outcome;
                try { await billing.Publish(forged, default); outcome = "accepted"; }
                catch (PostgresException e) { outcome = $"refused: {e.SqlState} {e.MessageText}"; }
                Check("PostgreSQL: role billing inserts artflow.auction-settled → refused by row-level security", outcome.StartsWith("refused: 42501", StringComparison.Ordinal), outcome);

                await using (var off = Db.CreateCommand($"DROP POLICY publish_own_prefix ON {schema}.messages; CREATE POLICY publish_any ON {schema}.messages FOR INSERT WITH CHECK (true)"))
                    await off.ExecuteNonQueryAsync();
                await billing.Publish(forged with { Id = Guid.CreateVersion7() }, default);
                Incoming? seen = null;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await billing.Consume(new Subscription("billing.IssueInvoice", ["artflow.auction-settled"]), (incoming, _) =>
                    {
                        seen = incoming;
                        stop.Cancel();
                        return ValueTask.FromResult(new Handled(Ack.Ack));
                    }, stop.Token);
                }
                catch (OperationCanceledException) { }
                Check("PostgreSQL without the policy: the bus stamps the publisher's role, so the consumer sees billing, not artflow", seen?.AuthenticatedAs == "billing",
                    $"ce-source {seen?.Message?.Source}, contract {seen?.Topic}, authenticated {seen?.AuthenticatedAs} → the consumer buries it (Messaging.SourceMismatch, selftest)");
                break;
            }
        }
        Console.WriteLine(Failures == 0 ? "ALL CHECKS PASSED" : $"{Failures} CHECK(S) FAILED");
        return Failures == 0 ? 0 : 1;
    }

    private static CancellationToken Cancel(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
