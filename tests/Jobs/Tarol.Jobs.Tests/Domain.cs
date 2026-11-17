using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs.Tests;

// ArtFlow's books, reduced to what the jobs need: a day book closed every night, a counter that jobs tick, a notice that a
// reaction to the closed day opens.

public partial class DayBook : AggregateRoot<DayBook>
{
    public DateOnly Day { get; private set; }

    public static Result<DayBook> Close(DateOnly day, DateTimeOffset now)
    {
        var book = new DayBook(Id<DayBook>.New(now));
        book.Raise(new Closed(day));
        return book;
    }

    public sealed record Closed(DateOnly Day) : Event;

    private void On(Closed e) => Day = e.Day;
}

public partial class Counter : AggregateRoot<Counter>
{
    public static readonly Id<Counter> Main = (Id<Counter>)Guid.Parse("0196b5a1-0000-7000-8000-00000000c001");

    public List<DateTimeOffset> Ticks { get; } = [];
    public bool IsStarted { get; private set; }

    public static Counter Start(Id<Counter> id)
    {
        var counter = new Counter(id);
        counter.Raise(new Started());
        return counter;
    }

    public Result Tick(DateTimeOffset at) => Raise(new Ticked(at));

    public sealed record Started : Event;
    public sealed record Ticked(DateTimeOffset At) : Event;

    private void On(Started e) => IsStarted = true;

    private void On(Ticked e) => Ticks.Add(e.At);
}

public partial class Notice : AggregateRoot<Notice>
{
    public DateOnly Day { get; private set; }

    public static Result<Notice> Open(DateOnly day, DateTimeOffset now)
    {
        var notice = new Notice(Id<Notice>.New(now));
        notice.Raise(new Opened(day));
        return notice;
    }

    public sealed record Opened(DateOnly Day) : Event;

    private void On(Opened e) => Day = e.Day;
}

/// <summary>The design's example (jobs.md §3): close yesterday's books at 02:00 Vienna time.</summary>
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));

    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}

/// <summary>Ticks one counter; run by the job classes of the tests.</summary>
public sealed partial record TickCounter(Id<Counter> Counter, DateTimeOffset At) : IChange<Counter>
{
    public Result Execute(Counter counter, ChangeContext context) => counter.Tick(At);
}

/// <summary>A reaction the nightly job causes: drained by <see cref="JobTest"/>.</summary>
public sealed partial record OpenNotice(DateOnly Day) : ICreation<Notice>
{
    [OnEvent<DayBook.Closed>]
    public static OpenNotice From(DayBook.Closed e) => new(e.Day);

    public Result<Notice> Execute(ChangeContext context) => Notice.Open(Day, context.Now);
}

[JsonSerializable(typeof(DayBook.Closed))]
[JsonSerializable(typeof(Counter.Started))]
[JsonSerializable(typeof(Counter.Ticked))]
[JsonSerializable(typeof(Notice.Opened))]
[JsonSerializable(typeof(List<DateTimeOffset>))]
[JsonSerializable(typeof(DateOnly))]
public partial class TestJson : JsonSerializerContext;
