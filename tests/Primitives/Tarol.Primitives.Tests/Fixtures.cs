using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Primitives.Tests.Fixtures;

/// <summary>Replay fixture (S-3): today's rule is 1..10 characters; its custom rule counts how often validation runs.</summary>
public readonly partial record struct BoardName([Length(1, 10)] string Value)
{
    private static int _checks;

    public static int Checks => Volatile.Read(ref _checks);

    private static void Validate(string value, ref ValidationErrors errors)
    {
        _ = value;
        Interlocked.Increment(ref _checks);
        _ = errors.Count;
    }
}

/// <summary>An aggregate whose event embeds a value object: the live path validates, replay never does.</summary>
public partial class Board : AggregateRoot<Board>
{
    public BoardName Name { get; private set; }

    public static Board Create() => new(Id<Board>.New());

    public Result Rename(string raw)
    {
        var name = BoardName.Create(raw);
        if (name is Violation violation)
            return violation;
        return Raise(new Renamed(name.GetValueOrThrow()));
    }

    public sealed record Renamed(BoardName Name) : Event;

    private void On(Renamed e) => Name = e.Name;
}

/// <summary>Deliberately broken handlers, to test the runtime guards (the analyzer rejects them, hence the pragmas).</summary>
public partial class Faulty : AggregateRoot<Faulty>
{
    public int Applied { get; private set; }

    public static Faulty Create() => new(Id<Faulty>.New());

    public Success Ok() => Raise(new Fine());

    public Success Fail() => Raise(new Throws());

    public Success Nested() => Raise(new RaisesAgain());

    public sealed record Fine : Event;
    public sealed record Throws : Event;
    public sealed record RaisesAgain : Event;

    private void On(Fine e) => Applied++;

#pragma warning disable TPRM014 // fixture: a handler that throws after mutating, to prove the poison guard (F-9)
    private void On(Throws e)
    {
        Applied++;
        throw new InvalidOperationException("boom");
    }

    private void On(RaisesAgain e) => Raise(new Fine());
#pragma warning restore TPRM014
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Board.Renamed))]
[JsonSerializable(typeof(Galleries.Artwork.Repriced))]
[JsonSerializable(typeof(Galleries.Room.Added))]
[JsonSerializable(typeof(Galleries.Room.Closed))]
public partial class TestJson : JsonSerializerContext;

public static class TestJsonOptions
{
    // Same-assembly context: the value objects' generated converters come from ValueObjectJson (V-9).
    public static readonly JsonSerializerOptions Value = ValueObjectJson.Options(TestJson.Default);

    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> Info<T>() => Value.GetTypeInfo<T>();
}
