using System.Text.Json;
using Tarol.Primitives.Tests.Fixtures;
using Tarol.Results;
using Tarol.Validations;

namespace Tarol.Primitives.Tests;

/// <summary>
///     S-3 on the real AggregateRoot (repeats tests/Validations/…/ReplayTests): loading and replaying never runs a rule, so
///     events recorded under older, looser rules still load.
/// </summary>
public class ReplaySafetyTests
{
    [Fact]
    public void Old_Events_Deserialize_And_Replay_Without_Running_A_Rule()
    {
        // Recorded when the limit was 30 characters; today's rule is 1..10.
        var board = Board.Create();
        var before = BoardName.Checks;

        var payload = JsonSerializer.Deserialize("""{"name":"An old, long board name"}""", TestJsonOptions.Info<Board.Renamed>());
        var replayed = Board.Rehydrate(board.Id, [new StreamEvent(1, board.Id.Value, Guid.Empty, payload!)]);

        BoardName.Checks.ShouldBe(before);
        replayed.Name.Value.ShouldBe("An old, long board name");
        AggregateChanges.Prepare(replayed).IsSuccess.ShouldBeTrue(); // no invariant re-validates the value either
    }

    [Fact]
    public void The_Live_Path_Validates()
    {
        var board = Board.Create();
        var before = BoardName.Checks;

        board.Rename("A new name, too long").ShouldBe(Result.Failure(Invalid.Field("", ValidationCodes.Length, "1..10")));
        board.Rename("Short").IsSuccess.ShouldBeTrue();

        BoardName.Checks.ShouldBeGreaterThan(before);
        board.UncommittedChanges.Length.ShouldBe(1);
    }
}
