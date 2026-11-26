using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Changes.Tests;

#pragma warning disable TVAL006 // inputs built as JSON would

/// <summary>C-16: the default auto-loads; special changes load themselves (ILoads) or read other aggregates ([Read]).</summary>
public sealed class SpecialChangeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static Violation Failure(Result<ChangeOutcome> result)
        => result.TryGetValue(out Violation? violation) ? violation : throw new InvalidOperationException($"Expected a failure, got {result}.");

    private static CreateAuctionOfArtwork Of(Artwork artwork) => new(artwork.Id, "L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7));

    [Fact]
    public async Task A_read_aggregate_is_handed_to_the_factory()
    {
        var unverified = Artwork.Register(verified: false);
        var test = ChangeTest.At(Start, TestJson.Default.Options).Given(unverified);

        var then = await test.When(Of(unverified));

        Failure(then.Result).ShouldBe(Error.Conflict("Artwork.NotVerified"));
    }

    [Fact]
    public async Task A_missing_read_aggregate_is_UnprocessableEntity()
    {
        var then = await ChangeTest.At(Start).When(Of(Artwork.Register(verified: true)));

        Failure(then.Result).ShouldBe(Error.UnprocessableEntity("Artwork.NotFound", "Artwork"));
    }

    [Fact]
    public async Task A_read_aggregate_is_never_written()
    {
        var artwork = Artwork.Register(verified: true);
        var test = ChangeTest.At(Start, TestJson.Default.Options).Given(artwork);

        var then = await test.When(Of(artwork));

        then.Result.IsSuccess.ShouldBeTrue();
        then.Recorded.ShouldAllBe(r => r.StreamId != artwork.Id.Value);
        test.Load(artwork.Id).Version.ShouldBe(2);
    }

    [Fact]
    public async Task A_change_of_an_existing_aggregate_can_read_another()
    {
        var artwork = Artwork.Register(verified: true);
        var auction = Auction.Create("seller", "L-1", new Title("Sunflowers"), new Amount(10), Start.AddDays(7), Start).GetValueOrThrow();
        var test = ChangeTest.At(Start).Given(artwork).Given(auction);

        var then = await test.When(new CloseIfArtworkVerified(auction.Id, artwork.Id));

        then.Events.ShouldBe([new Auction.Closed(Start)]);
    }

    [Fact]
    public async Task ILoads_finds_its_target_by_another_key()
    {
        var auction = Auction.Create("seller", "L-42", new Title("Sunflowers"), new Amount(10), Start.AddDays(7), Start).GetValueOrThrow();
        var test = ChangeTest.At(Start);
        test.Store.DefineLookup<Auction>("lot", a => a.Lot);
        test.Given(auction);

        var found = await test.When(new CloseByLot("L-42"));
        var unknown = await test.When(new CloseByLot("L-0"));

        (found.Result.GetValueOrThrow() with { Position = default }).ShouldBe(new ChangeOutcome(auction.Id.Value, 2, 1));
        Failure(unknown.Result).ShouldBe(Error.NotFound("Auction.NotFound", "lot L-0"));
    }

    [Fact]
    public async Task ILoads_can_load_or_create()
    {
        var id = Id<Payout>.New();
        var test = ChangeTest.At(Start);

        var first = await test.When(new ApprovePayout(id, 150), Actor.System("Jobs.Payouts"));
        var second = await test.When(new ApprovePayout(id, 150), Actor.System("Jobs.Payouts"));

        (first.Result.GetValueOrThrow() with { Position = default }).ShouldBe(new ChangeOutcome(id.Value, 2, 2) { Created = true });   // TN-42: the load-or-create started the stream
        first.Events.ShouldBe([new Payout.BecameDue(150), new Payout.WasApproved()]);
        Failure(second.Result).ShouldBe(Error.Conflict("Payout.AlreadyApproved"));
    }

    [Fact]
    public void Read_without_reads_names_the_mistake()
    {
        var context = new ChangeContext(OperationContext.Start(Actor.Anonymous, Option.None, TimeProvider.System), Start);

        Should.Throw<InvalidOperationException>(() => context.Read<Artwork>()).Message.ShouldContain("no [Read] members");
    }

    [Fact]
    public void TryRead_answers_without_an_exception_when_nothing_was_loaded()
    {
        // TN-40 (Tatalu FB-F03.3-2): an optional read caught the InvalidOperationException of Read<T>()
        var operation = OperationContext.Start(Actor.Anonymous, Option.None, TimeProvider.System);
        var artwork = Artwork.Register(verified: true);

        new ChangeContext(operation, Start).TryRead(out Artwork? none).ShouldBeFalse();
        none.ShouldBeNull();
        new ChangeContext(operation, Start, new Reads(new Dictionary<string, object>())).TryRead(out Artwork? _).ShouldBeFalse();
        new ChangeContext(operation, Start, new Reads(new Dictionary<string, object> { ["Art"] = artwork })).TryRead(out Artwork? found).ShouldBeTrue();
        found.ShouldBeSameAs(artwork);
    }

    [Fact]
    public void Reads_finds_by_type_and_by_member()
    {
        var a = Artwork.Register(verified: true);
        var b = Artwork.Register(verified: false);
        var reads = new Reads(new Dictionary<string, object> { ["First"] = a, ["Second"] = b });

        reads.Get<Artwork>("Second").ShouldBeSameAs(b);
        Should.Throw<InvalidOperationException>(() => reads.Get<Artwork>()).Message.ShouldContain("Several [Read] members");
        Should.Throw<InvalidOperationException>(() => reads.Get<Payout>()).Message.ShouldContain("No [Read] member");
        Should.Throw<InvalidOperationException>(() => reads.Get<Artwork>("Third"));
    }
}
