using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Jobs;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Operations;

/// <summary>What the probe commits: an item that is touched; every touch has a reaction (outbox rows) and a projection row.</summary>
public partial class Item : AggregateRoot<Item>
{
    public int Touches { get; private set; }

    public static Item Create(DateTimeOffset now)
    {
        var i = new Item(Id<Item>.New(now));
        i.Raise(new Created());
        return i;
    }

    public Result Touch() => Raise(new Touched(Touches + 1));

    public sealed record Created : Event;
    public sealed record Touched(int N) : Event;

#pragma warning disable CA1822
    private void On(Created e) { }
#pragma warning restore CA1822
    private void On(Touched e) => Touches = e.N;
}

public sealed record TouchItem(Id<Item> Target) : IChange<Item>
{
    public Result Execute(Item aggregate, ChangeContext context) => aggregate.Touch();
}

/// <summary>A reaction that decides nothing: every touch writes one outbox row.</summary>
public sealed record AckTouch(Id<Item> Target) : IChange<Item>
{
    [OnEvent<Item.Touched>]
    public static AckTouch From(Item.Touched e, RecordedEvent recorded) => new((Id<Item>)recorded.StreamId);

    public Result Execute(Item aggregate, ChangeContext context) => Result.Success();
}

[Projection("item-list")]
public sealed partial record ItemRow(Guid Id, [Sort] int Touches)
{
    public static ItemRow When(Item.Created e, RecordedEvent r) => new(r.StreamId, 0);
    public ItemRow When(Item.Touched e) => this with { Touches = e.N };
}

/// <summary>A job that does nothing (the idle worker of the span count).</summary>
public sealed class Nightly : IJob
{
    public ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Item.Created))]
[JsonSerializable(typeof(Item.Touched))]
[JsonSerializable(typeof(ItemRow))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ProbeJson : JsonSerializerContext;
