using Microsoft.Extensions.DependencyInjection;
using Shop;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Sagas;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

/// <summary>The example of skills/tarol-messaging/SKILL.md, verbatim (the records are in Domain.cs).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_runs()
    {
        var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var tenantA = (Id<ITenant>)Guid.Parse("0193a000-0000-7000-8000-00000000000a");
        var auction = Auction.Open(start);
        _ = auction.Settle();

        // registration
        var services = new ServiceCollection();
        services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>().Add<Invoice>().Add<Ledger>())
            .UseInMemoryStore()
            .AddMessaging("shop", m => m
                .Consume(GeneratedConsumers.Set)
                .Trust("billing", t => t.Tenants(tenantA))
                .UseInMemoryTransport()
                .UseInMemoryDeadLetters());

        // test: billing's invoice-paid becomes RecordPayment (see the record in rule 1's shape)
        var test = MessageTest.At(start, GeneratedConsumers.Set, ShopJson.Default.Options, "shop")
            .Trust("billing", t => t.Tenants(tenantA))
            .InTenant(tenantA)
            .Given(auction);
        await test.Send(test.Message("billing.invoice-paid", 1, new InvoicePaidV1(Guid.NewGuid(), auction.Id.Value, 150m), ShopJson.Default.InvoicePaidV1));
        var attempts = await test.Drain();

        attempts.Single().Verdict.ShouldBe(Verdict.Done);
        test.Load(auction.Id).Paid.ShouldBe(150m);
        services.BuildServiceProvider().GetRequiredService<MessageConsumers>().Subscriptions.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_skill_publication_example_runs()
    {
        var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var open = Auction.Open(start);
        var services = new ServiceCollection();
        services.AddSingleton<Mailbox>();

        services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>())
            .UseInMemoryStore()
            .AddReactionWorker()
            .AddMessaging("artflow", m => m
                .Publish(GeneratedPublications.Set)
                .Effect<Auction.PaymentRecorded, SendReceipt>("send-receipt")
                .UseInMemoryTransport());

        var test = MessagingTest.At(start)
            .System("artflow", ShopJson.Default.Options, o => o.Publications = GeneratedPublications.Set)
            .System("billing", ShopJson.Default.Options, o => { o.Consumers = GeneratedConsumers.Set; o.Trust("artflow", t => t.AnyTenant()); });
        test["artflow"].Given(open);
        (await test["artflow"].When(new SettleAuction(open.Id, 150m))).IsSuccess.ShouldBeTrue();
        await test.Drain();

        test.Transport.Published.Single().Type.ShouldBe("artflow.auction-settled");
        test["billing"].Store.Recorded.Count(r => r.Event.Event is Invoice.Issued).ShouldBe(1);
        services.BuildServiceProvider().GetServices<IOutboxRowHandler>().Count().ShouldBe(2);
    }
}
