using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Changes.Tests;

public sealed class DeliveryTests
{
    public static TheoryData<string, Verdict> Answers => new()
    {
        { "committed", Verdict.Done },
        { "replayed", Verdict.Done },
        { "invalid", Verdict.Bury },
        { "guard", Verdict.Retry },
        { "version", Verdict.Retry },
        { "notfound", Verdict.Retry },
        { "read", Verdict.Retry },
        { "unavailable", Verdict.Retry },
        { "timeout", Verdict.Retry },
        { "throttled", Verdict.Retry },
        { "internal", Verdict.Retry },
        { "forbidden", Verdict.Bury },
        { "unauthorized", Verdict.Bury },
        { "gone", Verdict.Bury },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public void Every_at_least_once_entry_classifies_the_pipelines_answer_alike(string answer, Verdict verdict)
    {
        Result<ChangeOutcome> result = answer switch
        {
            "committed" => new ChangeOutcome(Guid.CreateVersion7(), 2, 1),
            "replayed" => new ChangeOutcome(Guid.CreateVersion7(), 2, 1, Replayed: true),
            "invalid" => Invalid.Field("Amount", "Range"),
            "guard" => Error.Conflict("Bid.TooLow"),
            "version" => StoreErrors.VersionConflict(1, 2),
            "notfound" => Error.NotFound("Auction.NotFound"),
            "read" => Error.UnprocessableEntity("Artwork.NotFound"),
            "unavailable" => Error.Unavailable("Db.Down"),
            "timeout" => Error.Timeout("Db.Slow"),
            "throttled" => Error.TooManyRequests("Api.Throttled"),
            "internal" => Error.Internal("Bug"),
            "forbidden" => Error.Forbidden("Auction.NotSeller"),
            "unauthorized" => Error.Unauthorized(),
            _ => Error.Gone("Auction.Deleted"),
        };

        Delivery.Classify(result).ShouldBe(verdict);
    }
}
