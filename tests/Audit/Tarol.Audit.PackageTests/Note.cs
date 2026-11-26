using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Audit.PackageTests;

public partial class Note : AggregateRoot<Note>
{
    public string Text { get; private set; } = "";

    public static Note Write(string text, string checksum)
    {
        var note = new Note(Id<Note>.New());
        note.Raise(new Written(text, checksum));
        return note;
    }

    public sealed record Written(string Text, [property: NotAudited(Reason = "no fact")] string Checksum) : Event;

    private void On(Written e) => Text = e.Text;
}

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Note.Written))]
public partial class ConsumerJson : JsonSerializerContext;
