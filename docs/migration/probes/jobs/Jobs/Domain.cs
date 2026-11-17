using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Probe.Jobs;

/// <summary>
/// What a job changes: one stream per (tenant, job). Every run of an occurrence appends <see cref="Recorded"/>, so the
/// events table shows how many times an occurrence committed (a duplicate = two events with the same occurrence).
/// </summary>
public partial class Tally : AggregateRoot<Tally>
{
    public string Job { get; private set; } = "";
    public int Count { get; private set; }
    public DateTimeOffset Last { get; private set; }

    public static Tally Open(Guid id, string job)
    {
        var t = new Tally((Id<Tally>)id);
        t.Raise(new Opened(job));
        return t;
    }

    public Result Record(DateTimeOffset occurrence, string by) => Raise(new Recorded(occurrence, by));

    /// <summary>A no-op event with an unscheduled reaction: the reaction backlog of the contention bench.</summary>
    public Result Ping() => Raise(new Pinged());

    public sealed record Opened(string Job) : Event;
    public sealed record Recorded(DateTimeOffset Occurrence, string By) : Event;
    public sealed record Pinged : Event;

    private void On(Opened e) => Job = e.Job;
    private void On(Recorded e) { Count++; Last = e.Occurrence; }
#pragma warning disable CA1822
    private void On(Pinged e) { }
#pragma warning restore CA1822
}

/// <summary>A job's effect as a change: record the occurrence (variants A and B run it with the occurrence key).</summary>
public sealed record RecordOccurrence(Id<Tally> Target, DateTimeOffset Occurrence, string By) : IChange<Tally>
{
    public Result Execute(Tally aggregate, ChangeContext context) => aggregate.Record(Occurrence, By);
}

/// <summary>
/// Variant D: the schedule as an aggregate (same Guid as its Tally, another stream type). Each <see cref="Due"/> event is
/// one occurrence; two scheduled reactions run at its time: the job (<see cref="RunScheduled"/>) and the advance
/// (<see cref="AdvanceSchedule"/>), which raises the next <see cref="Due"/>.
/// </summary>
public partial class Schedule : AggregateRoot<Schedule>
{
    public string Job { get; private set; } = "";
    public DateTimeOffset Next { get; private set; }

    public static Schedule Start(Guid id, string job, DateTimeOffset first)
    {
        var s = new Schedule((Id<Schedule>)id);
        s.Raise(new Started(job));
        s.Raise(new Due(first));
        return s;
    }

    /// <summary>Raises the next occurrence after <paramref name="from"/>; a stale or repeated advance is a no-op.</summary>
    public Result Advance(DateTimeOffset from, DateTimeOffset next) => Next != from ? Result.Success() : Raise(new Due(next));

    /// <summary>The cron changed (a new deploy): the next occurrence moves. The row of the old one is already in the outbox.</summary>
    public Result Reschedule(DateTimeOffset next) => Raise(new Due(next));

    public sealed record Started(string Job) : Event;
    public sealed record Due(DateTimeOffset At) : Event;

    private void On(Started e) => Job = e.Job;
    private void On(Due e) => Next = e.At;
}

public sealed record RunScheduled(Id<Tally> Target, DateTimeOffset Occurrence) : IChange<Tally>
{
    [OnEvent<Schedule.Due>(DueAt = nameof(Schedule.Due.At))]
    public static RunScheduled From(Schedule.Due e, RecordedEvent recorded) => new((Id<Tally>)recorded.StreamId, e.At);

    public Result Execute(Tally aggregate, ChangeContext context) => aggregate.Record(Occurrence, Lab.Me);
}

public sealed record AdvanceSchedule(Id<Schedule> Target, DateTimeOffset At) : IChange<Schedule>
{
    [OnEvent<Schedule.Due>(DueAt = nameof(Schedule.Due.At))]
    public static AdvanceSchedule From(Schedule.Due e, RecordedEvent recorded) => new((Id<Schedule>)recorded.StreamId, e.At);

    // the probe's schedules fire every second: the next occurrence is At + 1 s (every missed one runs: policy All)
    public Result Execute(Schedule aggregate, ChangeContext context) => aggregate.Advance(At, At.AddSeconds(1));
}

/// <summary>The reaction backlog of the contention bench: a reaction that decides nothing.</summary>
public sealed record AckPing(Id<Tally> Target) : IChange<Tally>
{
    [OnEvent<Tally.Pinged>]
    public static AckPing From(Tally.Pinged e, RecordedEvent recorded) => new((Id<Tally>)recorded.StreamId);

    public Result Execute(Tally aggregate, ChangeContext context) => Result.Success();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Tally.Opened))]
[JsonSerializable(typeof(Tally.Recorded))]
[JsonSerializable(typeof(Tally.Pinged))]
[JsonSerializable(typeof(Schedule.Started))]
[JsonSerializable(typeof(Schedule.Due))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class ProbeJson : JsonSerializerContext;
