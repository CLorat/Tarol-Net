using System.Text;
using System.Text.Json;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Messaging;
using Tarol.Messaging.Nats;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.Sagas;
using Tarol.StronglyTypedId;
using static ArtFlow.Lab;

namespace ArtFlow;

internal static class SelfTest
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public static async Task<int> Run(string kind)
    {
        var time = TimeProvider.System;
        var suffix = $"{Environment.ProcessId}_{kind.Replace("+", "_", StringComparison.Ordinal)}";
        var postgres = kind != "memory";
        Console.WriteLine($"# selftest: transport {kind}, store {(postgres ? "Npgsql" : "in-memory")}, {Runtime}");
        var t1 = Id<ITenant>.New();
        var t2 = Id<ITenant>.New();
        var alice = OperationContext.Start(Actor.User("alice"), t1, time);
        var bus = "bus_" + suffix;
        await using var transport = Transport(kind, time, bus);
        var mailer = new Mailer();
        var receiptTable = (ReactionTable)(e => e is Billing.Invoice.Paid ? OnPaid : ReadOnlySpan<string>.Empty);

        await using var artflow = await Sys.Create("artflow", suffix, postgres, time, transport, GeneratedPublications.Create(), GeneratedConsumers.Set,
            ArtFlowJson.Default.Options, c => c.Add<Auction>(), [], o => o.Sources["billing"] = Trust(t1.Value));
        await using var billing = await Sys.Create("billing", suffix, postgres, time, transport, Billing.GeneratedPublications.Create(), Billing.GeneratedConsumers.Set,
            Billing.BillingJson.Default.Options, c => c.Add<Billing.Invoice>().Add<Billing.Ledger>(),
            [Probe.Messaging.App.GeneratedReactions.Set, Effects.Of("effect:SendReceipt", receiptTable, mailer.Send)], o => o.Sources["artflow"] = new SourceTrust { AnyTenant = true });

        var subscriptions = artflow.Consumers.Subscriptions().Concat(billing.Consumers.Subscriptions())
            .Append(new Subscription("billing.Notes1", ["artflow.auction-noted"])).Append(new Subscription("billing.Notes4", ["artflow.auction-noted"]) { Concurrency = 4 }).ToList();
        await Reset(kind, subscriptions.Select(s => s.Name));
        await transport.Prepare([.. artflow.Publications.Topics, .. billing.Publications.Topics, "artflow.auction-renamed"], subscriptions, default);
        artflow.Start(publishers: 2);
        billing.Start(publishers: 2);

        async Task<(Id<Auction> Id, Message Settled)> Settle(string buyer, decimal price)
        {
            var opened = (await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow();
            var id = (Id<Auction>)opened.StreamId;
            (await artflow.Pipeline.Run(new SettleAuction(id, buyer, price), alice)).GetValueOrThrow();
            var message = await Until(() => artflow.Published.Select(p => p.Message).FirstOrDefault(m => m.Subject == id.Value.ToString("N") && m.Type == "artflow.auction-settled"),
                Wait, "the publication of " + id);
            return (id, message);
        }
        Message Paid(Guid auction, decimal amount, Option<Id<ITenant>> tenant, string source = "billing", string? actor = "Service:psp", string? data = null) => new()
        {
            Id = Guid.CreateVersion7(), Source = source, Type = "billing.invoice-paid", Version = 1, Time = time.GetUtcNow(), Tenant = tenant,
            Correlation = Id<ICorrelation>.New(time), Actor = actor,
            Data = data is null ? JsonSerializer.SerializeToUtf8Bytes(new Contracts.InvoicePaidV1(Guid.NewGuid(), auction, amount), ArtFlowJson.Default.InvoicePaidV1) : Encoding.UTF8.GetBytes(data),
        };
        async Task<ConsumerAttempt> Final(Sys sys, string consumer, Guid message)
            => await Until(() => sys.For(consumer, message).FirstOrDefault(a => a.Verdict != Verdict.Retry), Wait, $"{consumer} to finish {message}");

        try
        {
            Console.WriteLine("## consumers through the pipeline");
            // 1. a valid message
            var (a1, m1) = await Settle("bob", 150);
            var issue1 = await Final(billing, "IssueInvoice", m1.Id);
            var sale1 = await Final(billing, "RecordSale", m1.Id);
            var invoiceId = (Id<Billing.Invoice>)issue1.Outcome!.Value.StreamId;
            var issued = (await billing.Events()).Single(e => e.StreamId == invoiceId.Value && e.Event.Event is Billing.Invoice.Issued);
            Check("valid message: IssueInvoice and RecordSale committed (one subscription each)", issue1.Verdict == Verdict.Done && sale1.Verdict == Verdict.Done
                && issue1.Outcome!.Value.Events == 1, $"invoice {invoiceId.Value:N}, attempt {issue1.Incoming.Attempt}");
            var behalf = issued.Context.Actor.OnBehalfOf.TryGetValue(out string? onBehalf) ? onBehalf : null;
            Check("context (rule Service): Service(artflow), the envelope's tenant and correlation, causation = message id",
                issued.Context.Actor is { Kind: ActorKind.Service, Id: "artflow" } && behalf is null
                && issued.Context.Tenant.Equals((Option<Id<ITenant>>)t1) && issued.Context.CorrelationId == alice.CorrelationId
                && issued.Context.CausationId.Equals((Option<Id<ICausation>>)(Id<ICausation>)m1.Id), issued.Context.Actor + ", causation = event id of Settled");
            var reminded = await UntilEvent(async () => (await billing.Events()).FirstOrDefault(e => e.StreamId == invoiceId.Value && e.Event.Event is Billing.Invoice.Reminded), Wait);
            Info($"rule Service: the next hop in billing (reaction RemindBuyer) records {reminded?.Context.Actor}");

            // 2. a redelivery: the ack after the commit is lost, the transport delivers again (and the other actor rule)
            billing.Options.ActorRule = ActorRule.SystemOnBehalf;
            string? lose = null;
            var lost = 0;
            billing.Consumers.LoseAck = (c, m) => c == "IssueInvoice" && m.Subject == lose && Interlocked.Increment(ref lost) == 1;
            var a2 = (Id<Auction>)(await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow().StreamId;
            lose = a2.Value.ToString("N");
            (await artflow.Pipeline.Run(new SettleAuction(a2, "carol", 120), alice)).GetValueOrThrow();
            var m2 = await Until(() => artflow.Published.Select(p => p.Message).FirstOrDefault(m => m.Subject == lose), Wait, "the publication");
            _ = await Until(() => billing.For("IssueInvoice", m2.Id).Count(x => x.Verdict == Verdict.Done) >= 2 ? "" : null, Wait, "the redelivery");
            var runs2 = billing.For("IssueInvoice", m2.Id).Where(x => x.Verdict == Verdict.Done).ToList();
            var invoices2 = (await billing.Events()).Count(e => e.Event.Event is Billing.Invoice.Issued i && i.Auction == a2.Value);
            Check("redelivery after a lost ack: replayed by the key, nothing written twice", runs2.Count >= 2 && runs2[0].Outcome!.Value.Replayed == false && runs2[^1].Outcome!.Value.Replayed && invoices2 == 1,
                $"attempts {string.Join(", ", runs2.Select(r => r.Incoming.Attempt + (r.Outcome!.Value.Replayed ? " replayed" : " committed")))}, invoices {invoices2}");
            billing.Consumers.LoseAck = null;
            billing.Options.ActorRule = ActorRule.Service;
            var invoice2 = (Id<Billing.Invoice>)runs2[0].Outcome!.Value.StreamId;
            var issued2 = (await billing.Events()).Single(e => e.StreamId == invoice2.Value && e.Event.Event is Billing.Invoice.Issued);
            var reminded2 = await UntilEvent(async () => (await billing.Events()).FirstOrDefault(e => e.StreamId == invoice2.Value && e.Event.Event is Billing.Invoice.Reminded), Wait);
            Check("rule SystemOnBehalf: System Consumers.IssueInvoice on behalf of alice, and the next hop keeps it",
                issued2.Context.Actor.ToString().Contains("Consumers.IssueInvoice", StringComparison.Ordinal) && reminded2?.Context.Actor.ToString().Contains("alice", StringComparison.Ordinal) == true,
                $"{issued2.Context.Actor} → {reminded2?.Context.Actor}");

            // 3. publisher crash between publish and complete: published twice
            var crashFor = "";
            artflow.Publications.FailAfterPublish = m => m.Subject == crashFor && artflow.Published.Count(p => p.Message.Subject == crashFor) == 1;
            var opened3 = (await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow();
            crashFor = opened3.StreamId.ToString("N");
            (await artflow.Pipeline.Run(new SettleAuction((Id<Auction>)opened3.StreamId, "dave", 130), alice)).GetValueOrThrow();
            _ = await Until(() => artflow.Published.Count(p => p.Message.Subject == crashFor) >= 2 ? "" : null, Wait, "the second publish");
            var m3 = artflow.Published.First(p => p.Message.Subject == crashFor).Message;
            await Final(billing, "IssueInvoice", m3.Id);
            await Task.Delay(1500);                                                        // a duplicate delivery, if the transport passes one on
            var runs3 = billing.For("IssueInvoice", m3.Id).ToList();
            var invoices3 = (await billing.Events()).Count(e => e.Event.Event is Billing.Invoice.Issued i && i.Auction == opened3.StreamId);
            var dedup = transport switch { NatsTransport n => $"JetStream reported {n.Duplicated} duplicate(s) (Nats-Msg-Id)", PgTransport => "bus: ON CONFLICT (id, type, version)", _ => "no broker dedup" };
            Check("publisher crash after publish: published twice, one invoice", artflow.Published.Count(p => p.Message.Subject == crashFor) == 2 && invoices3 == 1,
                $"deliveries to IssueInvoice {runs3.Count} ({string.Join(", ", runs3.Select(r => r.Outcome is { Replayed: true } ? "replayed" : r.Verdict.ToString()))}); {dedup}");
            artflow.Publications.FailAfterPublish = null;

            // 4. an unknown contract: subscriptions filter by contract; the switch drops one that arrives anyway
            var renamed = m1 with { Id = Guid.CreateVersion7(), Type = "artflow.auction-renamed", Data = "{}"u8.ToArray() };
            try
            {
                await transport.Publish(renamed, default);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Info("publishing a contract nobody subscribed: " + e.GetType().Name + ": " + e.Message.Split('\n')[0]);
            }
            await Task.Delay(1000);
            var deliveredBefore = billing.Attempts.Count(x => x.Incoming.Message?.Id == renamed.Id);
            var direct = await billing.Consumers.Handle("IssueInvoice", new Incoming(renamed, 1, null, renamed.Data, renamed.Type), default);
            Check("unknown contract: not delivered (subscriptions filter by contract); acknowledged and dropped when it arrives anyway", deliveredBefore == 0
                && direct.Ack == Ack.Ack && billing.Attempts.Any(x => x.Incoming.Message?.Id == renamed.Id && x.Reason!.Contains("UnknownContract", StringComparison.Ordinal)));

            // 5. an unknown version of a known contract: buried (R-10)
            var v3 = m1 with { Id = Guid.CreateVersion7(), Version = 3 };
            await transport.Publish(v3, default);
            var v3a = await Final(billing, "IssueInvoice", v3.Id);
            var v3b = await Final(billing, "RecordSale", v3.Id);
            Check("unknown version: buried by both consumers", v3a.Verdict == Verdict.Bury && v3b.Verdict == Verdict.Bury, v3a.Reason);

            // 6. an invalid mapping: the pipeline's validation refuses the change → buried; the other consumer is unaffected
            var (_, m6) = await Settle("erin", 0);
            var bad = await Final(billing, "IssueInvoice", m6.Id);
            var sale6 = await Final(billing, "RecordSale", m6.Id);
            Check("invalid mapping (price 0): IssueInvoice buried at attempt 1, RecordSale committed", bad.Verdict == Verdict.Bury && bad.Incoming.Attempt == 1 && sale6.Verdict == Verdict.Done, bad.Reason);

            // 7. a Conflict: the payment arrives before the settlement it pays → retried until the auction is settled
            var open7 = (Id<Auction>)(await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow().StreamId;
            var p7 = Paid(open7.Value, 99, t1);
            await transport.Publish(p7, default);
            _ = await Until(() => artflow.For("RecordPayment", p7.Id).FirstOrDefault(x => x.Verdict == Verdict.Retry), Wait, "a retry");
            (await artflow.Pipeline.Run(new SettleAuction(open7, "frank", 99), alice)).GetValueOrThrow();
            var done7 = await Final(artflow, "RecordPayment", p7.Id);
            var auction7 = await Load(artflow, open7, alice);
            Check("Conflict (Auction.NotSettled): retried, then committed once the auction is settled", done7.Verdict == Verdict.Done && auction7.Paid == 99,
                $"attempts {string.Join(" → ", artflow.For("RecordPayment", p7.Id).Select(x => x.Incoming.Attempt + " " + (x.Reason ?? x.Verdict.ToString())))}");

            // 8. a NotFound: retried, then dead-lettered
            var p8 = Paid(Guid.CreateVersion7(), 10, t1);
            await transport.Publish(p8, default);
            var dead8 = await Final(artflow, "RecordPayment", p8.Id);
            Check("NotFound: retried 4 times (Quick policy), then dead-lettered", dead8.Verdict == Verdict.Bury && dead8.Incoming.Attempt == 4
                && (await artflow.Dead.List(100, default)).Any(d => d.MessageId == p8.Id), dead8.Reason);

            // 9. a poison message: the data does not parse
            var p9 = Paid(a1.Value, 1, t1, data: "{not json");
            await transport.Publish(p9, default);
            var dead9 = await Final(artflow, "RecordPayment", p9.Id);
            Check("poison (data is not JSON): buried at attempt 1, kept with its bytes", dead9.Verdict == Verdict.Bury && dead9.Incoming.Attempt == 1
                && (await artflow.Dead.List(100, default)).Single(d => d.MessageId == p9.Id).Data.Length == 9, dead9.Reason);

            // 10. the loop back: billing pays the invoice → billing.invoice-paid → ArtFlow records the payment; an effect sends the receipt
            var psp = OperationContext.Start(Actor.Service("psp"), t1, time);
            (await billing.Pipeline.Run(new Billing.PayInvoice(invoiceId), psp)).GetValueOrThrow();
            var paidMessage = await Until(() => billing.Published.Select(p => p.Message).FirstOrDefault(m => m.Type == "billing.invoice-paid"), Wait, "invoice-paid");
            var done10 = await Final(artflow, "RecordPayment", paidMessage.Id);
            var recorded10 = (await artflow.Events()).Single(e => e.StreamId == a1.Value && e.Event.Event is Auction.PaymentRecorded);
            Check("the loop: invoice paid in billing → payment recorded in ArtFlow as Service(billing), with billing's correlation",
                done10.Verdict == Verdict.Done && recorded10.Context.Actor.Id == "billing" && recorded10.Context.CorrelationId == psp.CorrelationId, recorded10.Context.Actor.ToString());
            _ = await Until(() => mailer.Delivered >= 1 ? "" : null, Wait, "the receipt");
            await Task.Delay(500);
            Check("effect SendReceipt: provider down once, crash after sending once → 3 calls, 1 key, 1 mail delivered", mailer.Calls == 3 && mailer.Keys.Count == 1 && mailer.Delivered == 1,
                $"calls {mailer.Calls}, keys {mailer.Keys.Count}, delivered {mailer.Delivered}");

            Console.WriteLine("## context across systems");
            var (a11, _) = await Settle("gina", 50);
            var p11 = Paid(a11.Value, 50, t2);
            await transport.Publish(p11, default);
            var dead11 = await Final(artflow, "RecordPayment", p11.Id);
            Check("forged tenant: billing may act only for its tenant → buried", dead11.Verdict == Verdict.Bury && dead11.Reason!.StartsWith("Messaging.TenantNotAllowed", StringComparison.Ordinal), dead11.Reason);
            var p12 = Paid(a11.Value, 50, t1, source: "artflow");
            await transport.Publish(p12, default);
            var dead12 = await Final(artflow, "RecordPayment", p12.Id);
            Check("forged source (ce-source artflow on billing's contract) → buried", dead12.Verdict == Verdict.Bury && dead12.Reason!.StartsWith("Messaging.SourceMismatch", StringComparison.Ordinal), dead12.Reason);
            var p13 = Paid(a11.Value, 50, t1, actor: "User:admin");
            await transport.Publish(p13, default);
            await Final(artflow, "RecordPayment", p13.Id);
            var rec13 = (await artflow.Events()).Single(e => e.StreamId == a11.Value && e.Event.Event is Auction.PaymentRecorded);
            Check("forged actor User:admin: recorded as Service(billing), never as the user", rec13.Context.Actor.Kind == ActorKind.Service && rec13.Context.Actor.Id == "billing",
                rec13.Context.Actor.ToString());
            artflow.Options.TrustEnvelopeActor = true;
            var (a13b, _) = await Settle("hugo", 40);
            var p13b = Paid(a13b.Value, 40, t1, actor: "User:admin");
            await transport.Publish(p13b, default);
            await Final(artflow, "RecordPayment", p13b.Id);
            var rec13b = (await artflow.Events()).Single(e => e.StreamId == a13b.Value && e.Event.Event is Auction.PaymentRecorded);
            Info($"with the envelope's actor trusted (what not to do) the payment is recorded as {rec13b.Context.Actor}");
            artflow.Options.TrustEnvelopeActor = false;

            Console.WriteLine("## keys, versions, re-drive");
            billing.Options.KeyScope = KeyScope.Contract;
            var (_, m15) = await Settle("ida", 70);
            var i15 = await Final(billing, "IssueInvoice", m15.Id);
            var s15 = await Final(billing, "RecordSale", m15.Id);
            await Task.Delay(300);
            var i15all = billing.For("IssueInvoice", m15.Id).Where(x => x.Verdict == Verdict.Done).ToList();
            var s15all = billing.For("RecordSale", m15.Id).Where(x => x.Verdict == Verdict.Done).ToList();
            var skipped = i15all.Concat(s15all).Count(x => x.Outcome!.Value.Replayed);
            Check("key scope consumer:<contract>: the second consumer of the contract replays the first's outcome and never runs", skipped >= 1,
                $"IssueInvoice {(i15.Outcome is { Replayed: true } ? "replayed" : "committed")}, RecordSale {(s15.Outcome is { Replayed: true } ? "replayed" : "committed")}: with consumer:<name> both commit");
            billing.Options.KeyScope = KeyScope.Consumer;

            var dual = await Sys.Create("artflow2", suffix, postgres, time, transport, GeneratedPublications.Create(dual: true), GeneratedConsumers.Set,
                ArtFlowJson.Default.Options, c => c.Add<Auction>(), [], o => o.Sources["billing"] = Trust(t1.Value));
            await using (dual)
            {
                dual.Start(publishers: 1, consume: false);
                var opened14 = (await dual.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow();
                (await dual.Pipeline.Run(new SettleAuction((Id<Auction>)opened14.StreamId, "jan", 60), alice)).GetValueOrThrow();
                var both = await Until(() => dual.Published.Count >= 2 ? dual.Published.Select(p => p.Message).ToList() : null, Wait, "v1 and v2");
                var id14 = both[0].Id;
                await Final(billing, "IssueInvoice", id14);
                _ = await Until(() => billing.For("IssueInvoice", id14).Count(x => x.Verdict != Verdict.Retry) >= 2 && billing.For("RecordSale", id14).Count(x => x.Verdict != Verdict.Retry) >= 2 ? "" : null, Wait, "both versions");
                var invoices14 = (await billing.Events()).Count(e => e.Event.Event is Billing.Invoice.Issued i && i.Auction == opened14.StreamId);
                var rs = billing.For("RecordSale", id14).Select(x => $"v{x.Incoming.Message!.Version} {x.Reason ?? x.Verdict.ToString()}");
                Check("dual publish v1 + v2 (same message id): IssueInvoice (reads both) commits once", invoices14 == 1 && both.All(m => m.Id == id14),
                    "IssueInvoice: " + string.Join(", ", billing.For("IssueInvoice", id14).Select(x => $"v{x.Incoming.Message!.Version} {(x.Outcome is { Replayed: true } ? "replayed" : x.Verdict.ToString())}")));
                Info("RecordSale reads v1 only: " + string.Join(", ", rs));
            }

            var open16 = (Id<Auction>)(await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow().StreamId;
            var p16 = Paid(open16.Value, 33, t1);
            await transport.Publish(p16, default);
            var dead16 = await Final(artflow, "RecordPayment", p16.Id);
            (await artflow.Pipeline.Run(new SettleAuction(open16, "kim", 33), alice)).GetValueOrThrow();
            var letter = (await artflow.Dead.List(100, default)).Single(d => d.MessageId == p16.Id);
            var redriven = await artflow.Consumers.Redrive(letter.Id, default);
            var auction16 = await Load(artflow, open16, alice);
            Check("re-drive: a Conflict dead-lettered after 4 attempts, the auction settled, re-driven → committed", dead16.Verdict == Verdict.Bury && redriven == Verdict.Done && auction16.Paid == 33);

            Console.WriteLine("## order per stream");
            var order1 = new List<(string Stream, int Seq)>();
            var order4 = new List<(string Stream, int Seq)>();
            using var notesStop = new CancellationTokenSource();
            var notes = Task.WhenAll(
                transport.Consume(subscriptions.Single(s => s.Name == "billing.Notes1"), Record(order1), notesStop.Token),
                transport.Consume(subscriptions.Single(s => s.Name == "billing.Notes4"), Record(order4, 5), notesStop.Token));
            var streams = new List<Id<Auction>>();
            for (var i = 0; i < 5; i++)
                streams.Add((Id<Auction>)(await artflow.Pipeline.Run(new OpenAuction("alice"), alice)).GetValueOrThrow().StreamId);
            for (var seq = 1; seq <= 10; seq++)
                foreach (var s in streams)
                    (await artflow.Pipeline.Run(new NoteAuction(s, seq), alice)).GetValueOrThrow();
            _ = await Until(() => { lock (order1) lock (order4) return order1.Count >= 50 && order4.Count >= 50 ? "" : null; }, Wait, "50 notes on both subscriptions");
            await notesStop.CancelAsync();
            try { await notes; } catch (OperationCanceledException) { }
            int Inversions(List<(string Stream, int Seq)> got) => got.GroupBy(x => x.Stream).Sum(g => g.Zip(g.Skip(1)).Count(p => p.Second.Seq < p.First.Seq));
            var published = artflow.Published.Where(p => p.Message.Type == "artflow.auction-noted").Select(p => (p.Message.Subject!, JsonSerializer.Deserialize(p.Message.Data.Span, ArtFlowJson.Default.AuctionNotedV1)!.Sequence)).ToList();
            Check("publication order per stream = version order (head per (stream, publication))", Inversions(published) == 0, $"{published.Count} published, 2 publisher loops");
            Check("consumed in order per stream with concurrency 1", Inversions(order1) == 0, $"{order1.Count} received");
            Info($"concurrency 4 (handler sleeps 5 ms): {Inversions(order4)} inversions per stream in {order4.Count} messages");

            Console.WriteLine($"INFO published: artflow {artflow.Published.Count}, billing {billing.Published.Count}; dead letters: artflow {(await artflow.Dead.List(100, default)).Count}, billing {(await billing.Dead.List(100, default)).Count}");
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException)
        {
            Check("scenario", false, e.GetType().Name + ": " + e.Message);
        }
        await artflow.Stop();
        await billing.Stop();
        Console.WriteLine(Failures == 0 ? "ALL CHECKS PASSED" : $"{Failures} CHECK(S) FAILED");
        return Failures == 0 ? 0 : 1;
    }

    private static readonly string[] OnPaid = ["effect:SendReceipt"];

    private static async Task<Auction> Load(Sys sys, Id<Auction> id, OperationContext context)
        => (await sys.Store.Load(id, context, default)).TryGetValue(out Auction? found) ? found : throw new InvalidOperationException("no auction " + id);

    private static SourceTrust Trust(Guid tenant)
    {
        var trust = new SourceTrust();
        trust.Tenants.Add(tenant);
        return trust;
    }

    private static MessageHandler Record(List<(string, int)> into, int sleep = 0) => async (incoming, ct) =>
    {
        if (sleep > 0)
            await Task.Delay(Random.Shared.Next(sleep * 2), ct);
        var notedV1 = JsonSerializer.Deserialize(incoming.Message!.Data.Span, ArtFlowJson.Default.AuctionNotedV1)!;
        lock (into)
            into.Add((incoming.Message.Subject!, notedV1.Sequence));
        return new Handled(Ack.Ack);
    };

    private static async Task<RecordedEvent?> UntilEvent(Func<Task<RecordedEvent?>> probe, TimeSpan timeout)
    {
        var end = TimeProvider.System.GetUtcNow() + timeout;
        while (TimeProvider.System.GetUtcNow() < end)
        {
            if (await probe() is { } found)
                return found;
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>A mail provider that deduplicates by idempotency key: down at the first call, the second "crashes" after sending.</summary>
    private sealed class Mailer
    {
        public int Calls;
        public int Delivered;
        public HashSet<string> Keys { get; } = [];

        public Task Send(RecordedEvent recorded, string key, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            if (call == 1)
                throw new HttpRequestException("probe: the mail provider is down");
            lock (Keys)
                if (Keys.Add(key))
                    Delivered++;
            if (call == 2)
                throw new InvalidOperationException("probe: crashed after the provider accepted the mail");
            return Task.CompletedTask;
        }
    }
}
