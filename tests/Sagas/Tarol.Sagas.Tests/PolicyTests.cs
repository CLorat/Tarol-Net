using Tarol.Changes;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Sagas.Tests;

public sealed class PolicyTests
{
    [Fact]
    public void The_verdicts_follow_the_delivery_table()
    {
        static Verdict V(Result<ChangeOutcome> r) => DeliveryPolicy.Classify(r);

        V(new ChangeOutcome(Guid.Empty, 1, 1)).ShouldBe(Verdict.Done);
        V(new ChangeOutcome(Guid.Empty, 1, 1, Replayed: true)).ShouldBe(Verdict.Done);
        V(Invalid.Field("Amount", "Range")).ShouldBe(Verdict.Bury);
        V(Error.Conflict("Settlement.Underpaid")).ShouldBe(Verdict.Retry);
        V(StoreErrors.VersionConflict(1, 2)).ShouldBe(Verdict.Retry);
        V(StoreErrors.DuplicateKey(new IdempotencyKey(Option.None, "s", "k"))).ShouldBe(Verdict.Retry);
        V(Error.NotFound("Settlement.NotFound")).ShouldBe(Verdict.Retry);
        V(Error.UnprocessableEntity("Artwork.NotFound")).ShouldBe(Verdict.Retry);
        V(Error.Unavailable("Payments.Down")).ShouldBe(Verdict.Retry);
        V(new Error(ErrorType.Timeout, "Payments.Slow")).ShouldBe(Verdict.Retry);
        V(new Error(ErrorType.TooManyRequests, "Payments.Busy")).ShouldBe(Verdict.Retry);
        V(new Error(ErrorType.Internal, "Bug")).ShouldBe(Verdict.Retry);
        V(Error.Forbidden("Auction.NotYours")).ShouldBe(Verdict.Bury);
        V(new Error(ErrorType.Unauthorized, "x")).ShouldBe(Verdict.Bury);
        V(new Error(ErrorType.Gone, "x")).ShouldBe(Verdict.Bury);
    }

    [Fact]
    public void Backoff_doubles_from_one_second_with_bounded_jitter_and_stops_at_five_minutes()
    {
        var policy = new DeliveryPolicy();

        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var nominal = Math.Min(Math.Pow(2, attempt - 1), 300);
            policy.Backoff(42, attempt).TotalSeconds.ShouldBeInRange(nominal * 0.8, nominal * 1.2);
        }
        policy.Backoff(42, 3).ShouldBe(policy.Backoff(42, 3));                                  // predictable in tests
        policy.Backoff(42, 3).ShouldNotBe(policy.Backoff(43, 3));                               // spread across rows
        new DeliveryPolicy { FirstDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromSeconds(1) }.Backoff(1, 50).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1.2));
    }

    [Fact]
    public void The_defaults_are_ten_attempts_one_second_and_five_minutes()
    {
        var policy = new DeliveryPolicy();
        (policy.MaxAttempts, policy.FirstDelay, policy.MaxDelay).ShouldBe((10, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)));
        var options = new ReactionOptions();
        (options.Workers, options.Batch, options.Lease, options.PollInterval, options.KeyRetention).ShouldBe(
            (4, 32, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void Nonsensical_settings_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new DeliveryPolicy { MaxAttempts = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new DeliveryPolicy { FirstDelay = TimeSpan.Zero });
        Should.Throw<ArgumentOutOfRangeException>(() => new ReactionOptions { Workers = 0 }.Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new ReactionOptions { Lease = TimeSpan.Zero }.Validate());
    }
}
