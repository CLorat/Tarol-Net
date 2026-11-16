using Tarol.Primitives;
using Tarol.Queries;

namespace Tarol.Caching.PackageTests;

public partial class Note : AggregateRoot<Note>
{
    public sealed record Written(string Text, DateTimeOffset At) : Event;

#pragma warning disable CA1822 // the text is only projected
    private void On(Written e) { }
#pragma warning restore CA1822
}

[Projection("notes")]
public sealed partial record NoteRow(Guid Id, string Text, [Sort] DateTimeOffset WrittenAt)
{
    public static NoteRow When(Note.Written e, RecordedEvent r) => new(r.StreamId, e.Text, e.At);
}
