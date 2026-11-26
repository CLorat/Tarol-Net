using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // the tests build inputs as JSON would (trusting constructors); the pipeline validates them

public sealed class ReactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>An auction with alice's winning bid of 150, settled by alice: the store holds the Bid.Won event.</summary>
    private static async Task<(ChangeTest Test, RecordedEvent Won, OperationContext Alice)> Settled()
    {
        var test = ChangeTest.At(Start, TestJson.Default.Options);
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(1), Start).GetValueOrThrow();
        _ = auction.PlaceBid("bidder", new Amount(150), Start).GetValueOrThrow();
        test.Given(auction).Later(TimeSpan.FromDays(2));
        var alice = test.Context(Actor.User("alice"));
        (await test.Pipeline.Run(new SettleAuction(auction.Id), alice, cancellationToken: Ct)).GetValueOrThrow();
        return (test, test.Store.Recorded.Single(e => e.Event.Event is Bid.Won), alice);
    }

    [Fact]
    public void The_generated_set_lists_names_tables_and_schedules()
    {
        var set = GeneratedReactions.Set;

        set.Names.ShouldBe(["ArchiveOldAuction", "Auctions.RecordWinningAmount", "ForgetAuction", "MarkPayoutDue", "SettleAfterClose", "SplitPayout"]);
        set.Table(new Bid.Won(150)).ToArray().ShouldBe(["Auctions.RecordWinningAmount", "MarkPayoutDue"]);
        set.Table(new Auction.Closed(Start)).ToArray().ShouldBe(["SettleAfterClose"]);
        set.Table(new Auction.Settled(Start)).ToArray().ShouldBe(["ArchiveOldAuction", "ForgetAuction", "SplitPayout"]);
        set.IsScheduled("SettleAfterClose").ShouldBeTrue();
        set.IsScheduled("MarkPayoutDue").ShouldBeFalse();
        set.DueAt(new Auction.Closed(Start.AddHours(3)), "SettleAfterClose").ShouldBe(Start.AddHours(3));
        set.DueAt(new Bid.Won(150), "MarkPayoutDue").ShouldBeNull();
    }

    [Fact]
    public async Task A_reaction_runs_as_the_system_for_the_user_caused_by_the_event_with_its_key()
    {
        var (test, won, alice) = await Settled();

        var outcome = (await GeneratedReactions.Set.Run(test.Pipeline, "MarkPayoutDue", won, Ct)).GetValueOrThrow();

        var due = test.Store.Recorded.Single(e => e.Event.Event is Payout.BecameDue);
        due.StreamId.ShouldBe(outcome.StreamId);
        ((Payout.BecameDue)due.Event.Event).Amount.ShouldBe(150);
        due.Context.Actor.Kind.ShouldBe(ActorKind.System);
        due.Context.Actor.Id.ShouldBe("Reactions.MarkPayoutDue");
        due.Context.Actor.OnBehalfOf.ShouldBe(Option.Some("alice"));
        due.Context.Tenant.ShouldBe(alice.Tenant);
        due.Context.CorrelationId.ShouldBe(alice.CorrelationId);
        due.Context.CausationId.ShouldBe(Option.Some((Id<ICausation>)won.EventId));
        var key = ReactionSet.Options("MarkPayoutDue", won).Key.GetValueOrThrow();
        (key.Scope, key.Key).ShouldBe(("reaction:MarkPayoutDue", won.EventId.ToString("N")));
        (await test.Store.Seen(key, Ct)).GetValueOrThrow().ShouldBe(new StoredOutcome(outcome.StreamId, 1, 1));
    }

    [Fact]
    public async Task A_redelivery_returns_the_first_outcome_without_running()
    {
        var (test, won, _) = await Settled();
        var first = (await GeneratedReactions.Set.Run(test.Pipeline, "MarkPayoutDue", won, Ct)).GetValueOrThrow();

        var again = (await GeneratedReactions.Set.Run(test.Pipeline, "MarkPayoutDue", won, Ct)).GetValueOrThrow();

        again.Replayed.ShouldBeTrue();
        again.StreamId.ShouldBe(first.StreamId);
        test.Store.Recorded.Count(e => e.Event.Event is Payout.BecameDue).ShouldBe(1);
    }

    [Fact]
    public async Task A_reaction_to_the_same_aggregate_and_one_with_a_pinned_name_run_through_the_pipeline()
    {
        var (test, won, _) = await Settled();

        var outcome = (await GeneratedReactions.Set.Run(test.Pipeline, "Auctions.RecordWinningAmount", won, Ct)).GetValueOrThrow();

        outcome.StreamId.ShouldBe(won.StreamId);
        outcome.Events.ShouldBe(0);                                                         // Touch raises nothing
    }

    [Fact]
    public async Task The_factorys_input_is_validated_before_any_read()
    {
        var (test, won, _) = await Settled();
        var zero = won with { Event = won.Event with { Event = new Bid.Won(0) } };
        var loads = test.Store.Loads;

        var result = await GeneratedReactions.Set.Run(test.Pipeline, "Auctions.RecordWinningAmount", zero, Ct);

        result.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Invalid>().Errors.Single().Field.ShouldBe("Amount");
        test.Store.Loads.ShouldBe(loads);
    }

    [Fact]
    public async Task A_scheduled_reaction_is_an_ordinary_change_when_it_runs()
    {
        var (test, _, _) = await Settled();
        var closed = test.Store.Recorded.Single(e => e.Event.Event is Auction.Closed);

        var result = await GeneratedReactions.Set.Run(test.Pipeline, "SettleAfterClose", closed, Ct);

        result.TryGetValue(out Violation? violation).ShouldBeTrue();                       // already settled: its guard answers
        violation.ShouldBeOfType<Error>().Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task An_unknown_name_or_an_event_the_reaction_does_not_handle_throws()
    {
        var (test, won, _) = await Settled();
        var closed = test.Store.Recorded.Single(e => e.Event.Event is Auction.Closed);

        (await Should.ThrowAsync<InvalidOperationException>(async () => _ = await GeneratedReactions.Set.Run(test.Pipeline, "RenamedAway", won, Ct)))
            .Message.ShouldContain("No reaction named 'RenamedAway'");
        (await Should.ThrowAsync<InvalidOperationException>(async () => _ = await GeneratedReactions.Set.Run(test.Pipeline, "MarkPayoutDue", closed, Ct)))
            .Message.ShouldContain("does not handle Closed");
    }

    [Fact]
    public void Combine_joins_assemblies_and_refuses_a_name_twice()
    {
        static ValueTask<Result<ChangeOutcome>> None(ChangePipeline p, string r, RecordedEvent e, CancellationToken ct)
            => ValueTask.FromResult<Result<ChangeOutcome>>(new ChangeOutcome(Guid.Empty, 0, 0));
        string[] notify = ["NotifyBuyer"];
        var other = new ReactionSet(notify, e => e is Bid.Won ? notify : ReadOnlySpan<string>.Empty, (_, _) => null, ["NotifyBuyer"], None);

        var combined = ReactionSet.Combine(GeneratedReactions.Set, other);

        combined.Names.Count.ShouldBe(7);
        combined.Table(new Bid.Won(1)).ToArray().ShouldBe(["Auctions.RecordWinningAmount", "MarkPayoutDue", "NotifyBuyer"]);
        combined.IsScheduled("SettleAfterClose").ShouldBeTrue();
        combined.IsScheduled("NotifyBuyer").ShouldBeTrue();
        combined.DueAt(new Auction.Closed(Start), "SettleAfterClose").ShouldBe(Start);
        Should.Throw<InvalidOperationException>(() => ReactionSet.Combine(GeneratedReactions.Set, GeneratedReactions.Set))
            .Message.ShouldContain("declared in two assemblies");
        Should.Throw<ArgumentException>(() => new ReactionSet(["A", "A"], _ => ReadOnlySpan<string>.Empty, (_, _) => null, [], None));
    }

    [Fact]
    public async Task The_context_of_a_reaction_to_a_systems_event_keeps_the_original_user()
    {
        var (_, won, _) = await Settled();
        var systemEvent = won with { Context = ReactionSet.Context("First", won) };

        var context = ReactionSet.Context("Second", systemEvent);

        context.Actor.Id.ShouldBe("Reactions.Second");
        context.Actor.OnBehalfOf.ShouldBe(Option.Some("alice"));
        context.CausationId.ShouldBe(Option.Some((Id<ICausation>)won.EventId));
    }

    [Fact]
    public async Task A_fan_out_runs_one_change_per_element_each_under_its_own_key_and_a_retry_replays_them()
    {
        var (test, _, _) = await Settled();
        var settled = test.Store.Recorded.Single(e => e.Event.Event is Auction.Settled);

        var outcome = (await GeneratedReactions.Set.Run(test.Pipeline, "SplitPayout", settled, Ct)).GetValueOrThrow();

        outcome.Events.ShouldBe(2);
        outcome.Replayed.ShouldBeFalse();
        test.Store.Recorded.Where(e => e.Event.Event is Payout.BecameDue).Select(e => ((Payout.BecameDue)e.Event.Event).Amount).ShouldBe([60m, 40m]);
        foreach (var element in new[] { "#0", "#1" })
            (await test.Store.Seen(ReactionSet.Options("SplitPayout", settled, element).Key.GetValueOrThrow(), Ct)).IsSome.ShouldBeTrue(element);
        ReactionSet.Options("SplitPayout", settled, "#1").Key.GetValueOrThrow().Key.ShouldBe(settled.EventId.ToString("N") + ":#1");
        ReactionSet.Element(Guid.Parse("0196f000-0000-7000-8000-0000000000aa"), 3).ShouldBe("0196f0000000700080000000000000aa");   // a target keys by itself, not its position

        var again = (await GeneratedReactions.Set.Run(test.Pipeline, "SplitPayout", settled, Ct)).GetValueOrThrow();

        again.Replayed.ShouldBeTrue();
        test.Store.Recorded.Count(e => e.Event.Event is Payout.BecameDue).ShouldBe(2);
    }

    [Fact]
    public async Task A_factory_answering_null_or_a_Load_answering_Skip_is_a_done_row_that_appends_nothing()
    {
        var (test, _, _) = await Settled();
        var settled = test.Store.Recorded.Single(e => e.Event.Event is Auction.Settled);
        var before = test.Store.Recorded.Count;

        var nothing = (await GeneratedReactions.Set.Run(test.Pipeline, "ArchiveOldAuction", settled, Ct)).GetValueOrThrow();
        var skipped = (await GeneratedReactions.Set.Run(test.Pipeline, "ForgetAuction", settled, Ct)).GetValueOrThrow();

        nothing.ShouldBe(ChangeOutcome.Nothing);
        skipped.Skipped.ShouldBeTrue();
        (skipped.Events, skipped.StreamId).ShouldBe((0, Guid.Empty));
        test.Store.Recorded.Count.ShouldBe(before);
        (await ReactionSet.Each<SplitPayout>(null, (_, _) => throw new InvalidOperationException())).GetValueOrThrow().Skipped.ShouldBeTrue();
        (await ReactionSet.Each<SplitPayout>([], (_, _) => throw new InvalidOperationException())).GetValueOrThrow().Skipped.ShouldBeTrue();
    }
}
