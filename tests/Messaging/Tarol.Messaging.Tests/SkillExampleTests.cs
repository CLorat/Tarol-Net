using Microsoft.Extensions.DependencyInjection;
using Shop;
using Tarol.Changes;
using Tarol.Kernel;
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
}
