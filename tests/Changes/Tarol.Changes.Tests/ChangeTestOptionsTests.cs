using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // the tests build inputs as JSON would (trusting constructors); the pipeline validates them

/// <summary>A log entry with a personal text of a child (Tatalu's DailyLog, FB-F05.3-1).</summary>
public partial class DailyLog : AggregateRoot<DailyLog>
{
    public Guid Child { get; private set; }
    [PersonalData(Subject = nameof(Child))] public string? Text { get; private set; }

    public static DailyLog Open(Id<DailyLog> id, Guid child)
    {
        var log = new DailyLog(id);
        log.Raise(new Opened(child));
        return log;
    }

    public Success Write(Guid child, string text) => Raise(new Written(child, text));

    public sealed record Opened(Guid Child) : Event;
    public sealed record Written(Guid Child, [property: PersonalData(Subject = nameof(Written.Child))] string? Text) : Event;

    private void On(Opened e) => Child = e.Child;
    private void On(Written e) => (Child, Text) = (e.Child, e.Text);
}

/// <summary>Writes the text for a child given in the input (a mistake: the subject can be empty).</summary>
public sealed partial record WriteLog(Id<DailyLog> Log, Guid Child, string Text) : IChange<DailyLog>
{
    public Result Execute(DailyLog log, ChangeContext context) => log.Write(Child, Text);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DailyLog.Opened))]
[JsonSerializable(typeof(DailyLog.Written))]
[JsonSerializable(typeof(Payout.BecameDue))]
[JsonSerializable(typeof(Payout.WasApproved))]
public sealed partial class SealingJson : JsonSerializerContext;

/// <summary>
/// Round 3: <see cref="ChangeTestOptions"/> (Tatalu FB-F05.3-1 sealing, FB-F06.2-4 reactions, FB-F06.3-4 principal, TN-15 lookups).
/// </summary>
public sealed class ChangeTestOptionsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Mia = Guid.Parse("0196f000-0000-7000-8000-0000000000aa");

    private static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(SealingJson.Default.Options)).Add<DailyLog>().Add<Payout>();

    [Fact]
    public async Task Without_a_codec_a_change_that_cannot_be_sealed_passes_unnoticed()
    {
        var log = DailyLog.Open(Id<DailyLog>.New(Start), Mia);
        var test = ChangeTest.At(Start).Given(log);

        var then = await test.When(new WriteLog(log.Id, Guid.Empty, "allergic to nuts"));

        then.Result.IsSuccess.ShouldBeTrue();                              // what Tatalu saw: the unit test stayed green
    }

    [Fact]
    public async Task With_the_apps_codec_the_change_fails_like_in_the_store_and_a_sealed_value_is_not_in_plain_text()
    {
        var log = DailyLog.Open(Id<DailyLog>.New(Start), Mia);
        var test = ChangeTest.At(Start, new ChangeTestOptions { Codec = Codec() }).Given(log);

        var error = await Should.ThrowAsync<JsonException>(() => test.When(new WriteLog(log.Id, Guid.Empty, "allergic to nuts")));
        error.Message.ShouldContain("subject member");

        (await test.When(new WriteLog(log.Id, Mia, "allergic to nuts"))).Result.IsSuccess.ShouldBeTrue();
        test.Store.AssertNoPlaintext("allergic to nuts");
        test.Load(log.Id).Text.ShouldBe("allergic to nuts");               // opened with the test's key store
    }

    [Fact]
    public async Task With_RunReactions_the_reactions_of_a_change_run_before_When_returns_but_not_those_of_the_history()
    {
        var test = ChangeTest.At(Start, new ChangeTestOptions { StateOptions = TestJson.Default.Options, Reactions = GeneratedReactions.Set, RunReactions = true });
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(1), Start).GetValueOrThrow();
        _ = auction.PlaceBid("bidder", new Amount(150), Start).GetValueOrThrow();
        test.Given(auction).Later(TimeSpan.FromDays(2));

        var then = await test.When(new SettleAuction(auction.Id), Actor.User("alice"));

        then.Result.IsSuccess.ShouldBeTrue(then.Result.ToString());
        then.Reactions.Select(r => r.Reaction).Order(StringComparer.Ordinal).ShouldBe(["Auctions.RecordWinningAmount", "MarkPayoutDue", "SettleAfterClose"]);   // the close is due already
        then.Reactions.Single(r => r.Reaction == "SettleAfterClose").Result.IsSuccess.ShouldBeFalse();   // a refusal is reported, not retried
        then.Reactions.Single(r => r.Reaction == "MarkPayoutDue").Recorded.Single().Event.Event.ShouldBeOfType<Payout.BecameDue>().Amount.ShouldBe(150);
        then.Events.ShouldNotContain(e => e is Payout.BecameDue);           // the change's own events stay apart
        Should.Throw<ArgumentException>(() => ChangeTest.At(Start, new ChangeTestOptions { RunReactions = true }));
    }

    [Fact]
    public async Task A_principal_without_an_authorization_is_a_set_up_error_with_a_clear_message()
    {
        var log = DailyLog.Open(Id<DailyLog>.New(Start), Mia);
        var test = ChangeTest.At(Start).Given(log);
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("sub", "kim")], "test"));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => test.When(new WriteLog(log.Id, Mia, "x"), options: new ChangeOptions(Principal: principal)));

        error.Message.ShouldContain("ChangeTestOptions.Authorization");
        error.Message.ShouldContain("Auth.NotConfigured");
    }

    [Fact]
    public async Task History_holds_the_events_of_one_aggregate_type_although_another_shares_its_id()
    {
        // Tatalu FB-F06.3-2: per-tenant singletons share the tenant's id as stream id
        var shared = Guid.Parse("0196f000-0000-7000-8000-0000000000bb");
        var log = DailyLog.Open((Id<DailyLog>)shared, Mia);
        var payout = Payout.Due((Id<Payout>)shared, 10).GetValueOrThrow();
        var test = ChangeTest.At(Start, new ChangeTestOptions { Codec = Codec() }).Given(log);
        test.Given(payout);
        (await test.When(new WriteLog(log.Id, Mia, "slept well"))).Result.IsSuccess.ShouldBeTrue();

        test.Store.Recorded.Count(r => r.StreamId == shared).ShouldBe(3);
        test.History(log.Id).Select(r => r.Event.Event.GetType().Name).ShouldBe(["Opened", "Written"]);
        test.StoredStateMatchesReplay(log.Id, SealingJson.Default.Options).Text.ShouldBe("slept well");
    }
}
