using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Queries.Npgsql.PackageTests;

public partial class Note : AggregateRoot<Note>
{
    public string Text { get; private set; } = "";

    public static Note Write(string text, DateTimeOffset at)
    {
        var note = new Note(Id<Note>.New(at));
        note.Raise(new Written(text, at));
        return note;
    }

    public sealed record Written(string Text, DateTimeOffset At) : Event;

    private void On(Written e) => Text = e.Text;
}

[Projection("notes")]
public sealed partial record NoteRow(Guid Id, string Text, [Sort] DateTimeOffset WrittenAt)
{
    public static NoteRow When(Note.Written e, RecordedEvent r) => new(r.StreamId, e.Text, e.At);
}

[JsonSerializable(typeof(Note.Written))]
[JsonSerializable(typeof(NoteRow))]
internal sealed partial class NotesJson : JsonSerializerContext;
