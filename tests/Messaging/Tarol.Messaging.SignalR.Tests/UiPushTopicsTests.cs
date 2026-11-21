namespace Tarol.Messaging.SignalR.Tests;

/// <summary>BC-25: what the UI push says about its topics (the source of x-tarol-topics).</summary>
public sealed class UiPushTopicsTests
{
    private sealed record OrderPaidV1(Guid Order);

    private sealed record OrderPaidV2(Guid Order, decimal Amount);

    [Fact]
    public void Forwarded_and_client_only_topics_with_the_latest_publication_and_the_rules()
    {
        var options = new UiPushOptions().Forward("shop.order-paid", "billing.invoice-paid");
        options.Rules.Subscribe("shop.*").Publish("shop.chat", role: "member").Subscribe("billing.invoice-paid", role: "accounting").Subscribe("chat");
        PublicationInfo[] publications =
        [
            new("publish:shop.order-paid@2", "shop.order-paid", 2) { ContractType = typeof(OrderPaidV2), Stream = "Order" },
            new("publish:shop.order-paid@1", "shop.order-paid", 1) { ContractType = typeof(OrderPaidV1), Stream = "Order" },
            new("publish:shop.order-shipped@1", "shop.order-shipped", 1) { ContractType = typeof(OrderPaidV1) },          // not a topic
        ];

        var topics = new UiPushTopics(options, publications).Topics;

        topics.ShouldBe([
            new UiPushTopic("billing.invoice-paid") { Forwarded = true, Subscribe = true, Publish = false },
            new UiPushTopic("shop.chat") { Subscribe = true, Publish = true },                       // only the rules: clients publish
            new UiPushTopic("shop.order-paid") { Version = 2, Stream = "Order", ContractType = typeof(OrderPaidV2), Forwarded = true, Subscribe = true, Publish = false },
        ]);                                                                                          // "chat" is not <system>.<name>
    }

    [Fact]
    public void With_its_own_authorizer_the_rules_are_unknown()
    {
        var options = new UiPushOptions().Forward("shop.order-paid");
        options.Rules.Subscribe("shop.order-paid");

        new UiPushTopics(options, [], rulesDecide: false).Topics.ShouldBe([new UiPushTopic("shop.order-paid") { Forwarded = true }]);
    }
}
