using Shop;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging.Tests;

public sealed class SetAndOptionsTests
{
    private static readonly ConsumerRunner Nothing = static (_, _, _, _, _) => ValueTask.FromResult(ConsumerRun.Not(Mapped.UnknownContract));

    [Fact]
    public void The_generated_set_lists_one_consumer_per_change_with_its_contracts()
        => GeneratedConsumers.Set.Consumers.Select(c => (c.Name, string.Join(",", c.Topics))).ShouldBe([
            ("IssueInvoice", "artflow.auction-settled"),
            ("RecordPayment", "billing.invoice-paid"),
            ("Sales", "artflow.auction-settled")]);

    [Fact]
    public void Sets_of_several_assemblies_combine_and_names_stay_unique()
    {
        var other = new ConsumerSet([new ConsumerInfo("Refund", ["billing.refunded"])], Nothing);

        ConsumerSet.Combine(GeneratedConsumers.Set, other).Consumers.Select(c => c.Name).ShouldBe(["IssueInvoice", "RecordPayment", "Sales", "Refund"]);
        Should.Throw<InvalidOperationException>(() => ConsumerSet.Combine(other, new ConsumerSet([new ConsumerInfo("Refund", ["x.y"])], Nothing)))
            .Message.ShouldContain("'Refund' is declared in two assemblies");
    }

    [Fact]
    public void A_set_refuses_duplicate_names_and_consumers_without_contracts()
    {
        Should.Throw<ArgumentException>(() => new ConsumerSet([new ConsumerInfo("A", ["x.y"]), new ConsumerInfo("A", ["x.z"])], Nothing));
        Should.Throw<ArgumentException>(() => new ConsumerSet([new ConsumerInfo("A", [])], Nothing));
    }

    [Fact]
    public async Task Running_an_unknown_consumer_throws()
        => (await Should.ThrowAsync<InvalidOperationException>(async () => await GeneratedConsumers.Set.Run("Gone", null!, null!, null!, TestContext.Current.CancellationToken)))
            .Message.ShouldContain("No consumer named 'Gone'");

    [Theory]
    [InlineData("", 1, 60)]
    [InlineData("bill.ing", 1, 60)]
    [InlineData("billing", 0, 60)]
    [InlineData("billing", 1, 0)]
    public void Options_are_validated(string application, int concurrency, int leaseSeconds)
        => Should.Throw<InvalidOperationException>(() => new MessagingOptions { Application = application, Concurrency = concurrency, Lease = TimeSpan.FromSeconds(leaseSeconds) }.Validate());

    [Fact]
    public void A_source_may_name_only_the_tenants_it_is_allowed()
    {
        var a = (Id<ITenant>)Guid.NewGuid();
        var options = new MessagingOptions { Application = "shop" }.Trust("billing", t => t.Tenants(a)).Trust("artflow", t => t.AnyTenant()).Trust("quiet");

        options.Sources["billing"].Allows(a).ShouldBeTrue();
        options.Sources["billing"].Allows((Id<ITenant>)Guid.NewGuid()).ShouldBeFalse();
        options.Sources["billing"].Allows(Option.None).ShouldBeTrue();   // a message without a tenant (single-tenant systems)
        options.Sources["artflow"].Allows((Id<ITenant>)Guid.NewGuid()).ShouldBeTrue();
        options.Sources["quiet"].Allows(a).ShouldBeFalse();                            // default: no tenant
        options.Sources["quiet"].AllowedTenants.ShouldBeEmpty();
    }

    [Fact]
    public void A_payload_type_missing_from_the_JSON_context_says_what_to_add()
        => Should.Throw<InvalidOperationException>(() => new MessageScope(OperationContext.Start(Actor.System("x"), Option.None, TimeProvider.System), default,
                new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = ShopJson.Default }).Payload<Unlisted>("{}"u8.ToArray()))
            .Message.ShouldContain("[JsonSerializable(typeof(Unlisted))]");

    public sealed record Unlisted(int X);
}
