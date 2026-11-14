using System.Security.Claims;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // the tests build inputs as JSON would (trusting constructors); the pipeline validates them

public sealed class PipelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static ChangeTest Test() => ChangeTest.At(Start, TestJson.Default.Options);

    private static Auction NewAuction(string lot = "L-1", decimal reserve = 100)
        => Auction.Create("seller", lot, new Title("Sunflowers"), new Amount(reserve), Start.AddDays(7), Start).GetValueOrThrow();

    private static Violation Failure(Result<ChangeOutcome> result)
        => result.TryGetValue(out Violation? violation) ? violation : throw new InvalidOperationException($"Expected a failure, got {result}.");

    [Fact]
    public async Task A_creation_commits_the_factorys_aggregate_with_its_id()
    {
        var test = Test();

        var then = await test.When(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(100), Start.AddDays(7)), Actor.User("alice"));

        var outcome = then.Result.GetValueOrThrow();
        outcome.Events.ShouldBe(1);
        outcome.Version.ShouldBe(1);
        then.Events.ShouldBe([new Auction.Opened("alice", "L-1", "Sunflowers", 100, Start.AddDays(7))]);
        test.Load((Id<Auction>)outcome.StreamId).Seller.ShouldBe("alice");
    }

    [Fact]
    public async Task Invalid_input_returns_every_field_before_any_read()
    {
        var test = Test();

        var then = await test.When(new CreateAuction("L-1", new Title("x"), new Amount(0), Start.AddDays(7)));

        Failure(then.Result).ShouldBeOfType<Invalid>().Errors.Select(e => (e.Field, e.Code)).ShouldBe([("Title", "Length"), ("Reserve", "Range")]);
        test.Store.Loads.ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_target_id_is_invalid_and_loads_nothing()
    {
        var test = Test();

        var then = await test.When(new CloseAuction(Id<Auction>.Empty));

        Failure(then.Result).ShouldBeOfType<Invalid>().Errors.Single().Field.ShouldBe("Auction");
        test.Store.Loads.ShouldBe(0);
    }

    [Fact]
    public async Task A_guard_on_an_input_field_is_Invalid()
    {
        var then = await Test().When(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(10), Start.AddHours(1)));

        Failure(then.Result).ShouldBe(Invalid.Field("EndsAt", "TooSoon"));
        then.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_pipeline_loads_the_target_and_passes_the_time_it_read_once()
    {
        var auction = NewAuction();
        var test = Test().Given(auction).Later(TimeSpan.FromHours(2));

        var then = await test.When(new CloseAuction(auction.Id));

        then.Result.GetValueOrThrow().ShouldBe(new ChangeOutcome(auction.Id.Value, 2, 1));
        then.Events.ShouldBe([new Auction.Closed(Start.AddHours(2))]);
        test.Load(auction.Id).Status.ShouldBe(AuctionStatus.Closed);
    }

    [Fact]
    public async Task A_guard_failure_commits_nothing()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);
        (await test.When(new CloseAuction(auction.Id))).Result.IsSuccess.ShouldBeTrue();

        var twice = await test.When(new CloseAuction(auction.Id));

        Failure(twice.Result).ShouldBe(Error.Conflict("AuctionStatus.NotAllowed", "Closed -> Closed"));
        twice.Events.ShouldBeEmpty();
        test.Load(auction.Id).Version.ShouldBe(2);
    }

    [Fact]
    public async Task A_missing_target_is_NotFound()
    {
        var id = Id<Auction>.New();

        var then = await Test().When(new CloseAuction(id));

        Failure(then.Result).ShouldBe(Error.NotFound("Auction.NotFound", id.ToString()));
    }

    [Fact]
    public async Task Another_tenants_aggregate_is_NotFound()
    {
        var auction = NewAuction();
        var test = Test().InTenant(Id<ITenant>.New()).Given(auction);

        var then = await test.InTenant(Id<ITenant>.New()).When(new CloseAuction(auction.Id));

        Failure(then.Result).ShouldBeOfType<Error>().Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task The_actor_reaches_the_behaviour_and_the_recorded_events()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);

        var bid = await test.When(new PlaceBid(auction.Id, new Amount(150)), Actor.User("bob"));
        var low = await test.When(new PlaceBid(auction.Id, new Amount(120)), Actor.User("carol"));

        bid.Events.ShouldBe([new Bid.Placed("bob", 150, Start)]);
        bid.Recorded.Single().Context.Actor.ShouldBe(Actor.User("bob"));
        Failure(low.Result).ShouldBe(Error.Conflict("Bid.TooLow"));
    }

    [Fact]
    public async Task A_job_settles_after_the_end_time_as_the_system_actor()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);
        (await test.When(new PlaceBid(auction.Id, new Amount(150)), Actor.User("bob"))).Result.IsSuccess.ShouldBeTrue();
        var job = Actor.System("Jobs.AuctionSettlement");

        var early = await test.When(new SettleAuction(auction.Id), job);
        var late = await test.At(Start.AddDays(8)).When(new SettleAuction(auction.Id), job);

        Failure(early.Result).ShouldBe(Error.Conflict("Auction.NotEnded"));
        late.Events.Select(e => e.GetType()).ShouldBe([typeof(Auction.Closed), typeof(Bid.Won), typeof(Auction.Settled)]);
        late.Recorded.ShouldAllBe(r => r.Context.Actor == job && r.RecordedAt == Start.AddDays(8));
    }

    [Fact]
    public async Task A_version_conflict_is_returned_not_retried()
    {
        var auction = NewAuction();
        var inner = new InMemoryAggregateStore(TimeProvider.System);
        var context = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        (await inner.Append(auction, 0, context, Option.None, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        AggregateChanges.Committed(auction);
        var racing = new RacingStore(inner, () =>
        {
            var other = Auction.Rehydrate(auction.Id, inner.History(auction.Id, Option.None));
            _ = other.PlaceBid("carol", new Amount(500), Start);
            _ = inner.Append(other, 1, context, Option.None, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        });
        var pipeline = new ChangePipeline(racing, ChangeTest.At(Start).Clock);

        var result = await pipeline.Run(new PlaceBid(auction.Id, new Amount(150)), context, cancellationToken: TestContext.Current.CancellationToken);

        StoreErrors.IsVersionConflict(result.ToResult()).ShouldBeTrue();
        inner.Loads.ShouldBe(1);
    }

    [Fact]
    public async Task An_expected_version_from_the_caller_is_checked_before_the_behaviour()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);

        var stale = await test.When(new CloseAuction(auction.Id), options: new ChangeOptions(ExpectedVersion: 7L));
        var current = await test.When(new CloseAuction(auction.Id), options: new ChangeOptions(ExpectedVersion: 1L));

        Failure(stale.Result).ShouldBe(StoreErrors.VersionConflict(7, 1));
        current.Result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_redelivery_with_the_same_key_returns_the_first_outcome_without_running()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);
        var key = new ChangeOptions(Key: new IdempotencyKey(Option.None, "payments", "evt_1"));

        var first = await test.When(new CloseAuction(auction.Id), options: key);
        var loads = test.Store.Loads;
        var again = await test.When(new CloseAuction(auction.Id), options: key);

        again.Result.GetValueOrThrow().ShouldBe(first.Result.GetValueOrThrow() with { Replayed = true });
        again.Events.ShouldBeEmpty();
        test.Store.Loads.ShouldBe(loads);
    }

    [Fact]
    public async Task A_failed_change_stores_no_key_so_a_retry_runs_again()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);
        var key = new ChangeOptions(Key: new IdempotencyKey(Option.None, "jobs", "settle-1"));

        var early = await test.When(new SettleAuction(auction.Id), options: key);
        var late = await test.At(Start.AddDays(8)).When(new SettleAuction(auction.Id), options: key);

        early.Result.IsSuccess.ShouldBeFalse();
        late.Result.GetValueOrThrow().Replayed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_broken_invariant_discards_the_change()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);

        var then = await test.When(new SabotageAuction(auction.Id));

        Failure(then.Result).ShouldBe(Error.Conflict("Auction.Reserve"));
        then.Events.ShouldBeEmpty();
        test.Load(auction.Id).Reserve.ShouldBe(100);
    }

    [Fact]
    public async Task An_exception_propagates_and_commits_nothing()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);

        await Should.ThrowAsync<InvalidOperationException>(async () => _ = await test.When(new ExplodeAuction(auction.Id)));

        test.Load(auction.Id).Version.ShouldBe(1);
    }

    [Fact]
    public async Task A_behaviour_that_raises_nothing_appends_nothing()
    {
        var auction = NewAuction();
        var test = Test().Given(auction);

        var then = await test.When(new TouchAuction(auction.Id));

        then.Result.GetValueOrThrow().ShouldBe(new ChangeOutcome(auction.Id.Value, 1, 0));
        then.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_explicit_target_wins()
    {
        var first = NewAuction("L-1");
        var second = NewAuction("L-2");
        var test = Test().Given(first).Given(second);

        (await test.When(new CloseOneOfTwo(first.Id, second.Id))).Result.IsSuccess.ShouldBeTrue();

        test.Load(first.Id).Status.ShouldBe(AuctionStatus.Open);
        test.Load(second.Id).Status.ShouldBe(AuctionStatus.Closed);
    }

    [Fact]
    public async Task A_creation_with_an_expected_version_other_than_0_conflicts()
    {
        var then = await Test().When(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7)), options: new ChangeOptions(ExpectedVersion: 3L));

        Failure(then.Result).ShouldBe(StoreErrors.VersionConflict(3, 0));
    }

    [Fact]
    public async Task The_authorization_runs_after_the_load_with_the_principal()
    {
        var auction = NewAuction();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mallory")], "test"));
        var authorization = new SellerOnly();
        var test = ChangeTest.At(Start, TestJson.Default.Options, authorization).Given(auction);

        var denied = await test.When(new CloseAuction(auction.Id), Actor.User("mallory"), new ChangeOptions(Principal: principal));
        var missing = await test.When(new CloseAuction(Id<Auction>.New()), Actor.User("mallory"));
        var allowed = await test.When(new CloseAuction(auction.Id), Actor.User("seller"));

        Failure(denied.Result).ShouldBe(Error.Forbidden("Auction.NotSeller"));
        authorization.Principals.ShouldContain(principal);
        Failure(missing.Result).ShouldBeOfType<Error>().Type.ShouldBe(ErrorType.NotFound);
        authorization.Checked.ShouldBe(2);
        allowed.Result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_authorization_sees_a_new_aggregate_before_its_creation_commits()
    {
        var authorization = new SellerOnly();
        var test = ChangeTest.At(Start, TestJson.Default.Options, authorization);

        var then = await test.When(new CreateAuction("L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7)), Actor.User("bob"));

        then.Result.IsSuccess.ShouldBeTrue();
        authorization.Checked.ShouldBe(1);
    }

    private sealed class SellerOnly : IChangeAuthorization
    {
        public int Checked { get; private set; }

        public List<ClaimsPrincipal?> Principals { get; } = [];

        public ValueTask<Result> Check<T>(object change, T aggregate, OperationContext context, ClaimsPrincipal? principal, CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T>
        {
            Checked++;
            Principals.Add(principal);
            return ValueTask.FromResult(aggregate is Auction auction && change is CloseAuction && auction.Seller != context.Actor.Id
                ? Result.Failure(Error.Forbidden("Auction.NotSeller"))
                : Result.Success());
        }
    }

    /// <summary>Lets "someone else" commit between the pipeline's load and its append.</summary>
    private sealed class RacingStore(InMemoryAggregateStore inner, Action beforeAppend) : IAggregateStore
    {
        public ValueTask<Option<T>> Load<T>(Id<T> id, OperationContext context, CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T> => inner.Load(id, context, cancellationToken);

        public ValueTask<Option<Id<T>>> Find<T>(string lookup, string key, OperationContext context, CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T> => inner.Find<T>(lookup, key, context, cancellationToken);

        public ValueTask<Result> Append<T>(T aggregate, long expectedVersion, OperationContext context, Option<IdempotencyKey> key, CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T>
        {
            beforeAppend();
            return inner.Append(aggregate, expectedVersion, context, key, cancellationToken);
        }

        public ValueTask<Option<StoredOutcome>> Seen(IdempotencyKey key, CancellationToken cancellationToken) => inner.Seen(key, cancellationToken);
    }
}
