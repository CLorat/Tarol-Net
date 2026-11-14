using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Results;
using Tarol.Validations.Tests.Fixtures;

namespace Tarol.Validations.Tests;

/// <summary>
///     S-3: loading and replaying an aggregate never runs validation, so old data loads under stricter rules. A minimal
///     hand-written event-sourced aggregate stands in for Tarol.Primitives (step 6 repeats this on the real AggregateRoot).
/// </summary>
public class ReplayTests
{
    [Fact]
    public void Deserializing_And_Replaying_Old_Events_Runs_No_Rule()
    {
        // Recorded when the limit was 20 characters; today's rule is 1..10.
        const string history = """[{"name":"An old, long auction name"},{"name":"Renamed again, too long"}]""";
        var before = CountedName.Checks;

        var events = JsonSerializer.Deserialize<Renamed[]>(history, ReplayJsonOptions.Value)!;
        var auction = TestAuction.Replay(events);

        CountedName.Checks.ShouldBe(before);                     // no Check, no Create, no custom rule
        auction.Name.Value.ShouldBe("Renamed again, too long");
        auction.Version.ShouldBe(2);
    }

    [Fact]
    public void A_Snapshot_Loads_Without_Validation()
    {
        var before = CountedName.Checks;

        var auction = TestAuction.FromSnapshot(JsonSerializer.Deserialize<AuctionSnapshot>("""{"name":"A snapshot name, too long","version":7}""", ReplayJsonOptions.Value)!);

        CountedName.Checks.ShouldBe(before);
        auction.Version.ShouldBe(7);
    }

    [Fact]
    public void The_Live_Path_Validates()
    {
        var auction = TestAuction.Replay([]);
        var before = CountedName.Checks;

        auction.Rename("A new name, too long").ShouldBe(Invalid.Field("", ValidationCodes.Length, "1..10"));
        auction.Rename("Short").IsSuccess.ShouldBeTrue();

        CountedName.Checks.ShouldBeGreaterThan(before);         // the custom rule ran for the valid name
        auction.Name.Value.ShouldBe("Short");
        auction.Uncommitted.Count.ShouldBe(1);                   // only the valid change raised an event
    }
}

internal static class ReplayJsonOptions
{
    public static readonly JsonSerializerOptions Value = ValueObjectJson.Options(ReplayJson.Default);
}

public sealed record Renamed(CountedName Name);

public sealed record AuctionSnapshot(CountedName Name, int Version);

/// <summary>Event-sourced like an aggregate: behaviour validates and raises, <c>On</c> applies facts without rules.</summary>
public sealed class TestAuction
{
    private readonly List<Renamed> _uncommitted = [];

    public CountedName Name { get; private set; }

    public int Version { get; private set; }

    public IReadOnlyList<Renamed> Uncommitted => _uncommitted;

    public static TestAuction Replay(IEnumerable<Renamed> history)
    {
        var auction = new TestAuction();
        foreach (var e in history)
            auction.On(e);
        return auction;
    }

    public static TestAuction FromSnapshot(AuctionSnapshot snapshot) => new() { Name = snapshot.Name, Version = snapshot.Version };

    public Result Rename(string raw)
    {
        var name = CountedName.Create(raw);
        if (name is Violation violation)
            return violation;

        var e = new Renamed(name.GetValueOrThrow());
        _uncommitted.Add(e);
        On(e);
        return Result.Success();
    }

    private void On(Renamed e)
    {
        Name = e.Name;
        Version++;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Renamed[]))]
[JsonSerializable(typeof(AuctionSnapshot))]
public partial class ReplayJson : JsonSerializerContext;
