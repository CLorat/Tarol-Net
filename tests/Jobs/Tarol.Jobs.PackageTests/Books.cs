using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Jobs.PackageTests;

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

/// <summary>Closes yesterday's books at 02:00 Vienna time.</summary>
public sealed partial record CloseDay(DateOnly Day) : ICreation<DayBook>
{
    [Scheduled("0 2 * * *", TimeZone = "Europe/Vienna")]
    public static CloseDay From(JobOccurrence o) => new(DateOnly.FromDateTime(o.Local.DateTime).AddDays(-1));

    public Result<DayBook> Execute(ChangeContext context) => DayBook.Close(Day, context.Now);
}

/// <summary>A job class: one book per occurrence, keyed by its part.</summary>
public sealed class OpenBook(BookCounter counter) : IJob
{
    public async ValueTask<Result> Run(JobRun run, CancellationToken cancellationToken)
    {
        counter.Runs++;
        return (await run.Create(new CloseDay(DateOnly.FromDateTime(run.Occurrence.At.UtcDateTime)), "book", cancellationToken)).TryGetValue(out Violation? v)
            ? v
            : Result.Success();
    }
}

public sealed class BookCounter
{
    public int Runs { get; set; }
}

[JsonSerializable(typeof(DayBook.Closed))]
[JsonSerializable(typeof(DateOnly))]
public partial class SmokeJson : JsonSerializerContext;
