using System.Text.Json.Serialization;
using PackageConsumer;
using Tarol.Messaging.PackageTests;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

// A consumer, generated from the packed package, run through MessageTest.
var test = MessageTest.At(DateTimeOffset.UnixEpoch, GeneratedConsumers.Set, ConsumerJson.Default.Options, "consumer").Trust("billing");
await test.Send(test.Message("billing.noted", 1, new NotedV1("hello"), ConsumerJson.Default.NotedV1));
var attempts = await test.Drain();
var consumed = attempts is [{ Verdict: Verdict.Done }] && test.Load((Id<Board>)Board.Main).Notes == 1;

// A publication and an effect from the packed package's generator, drained through MessagingTest.
var keys = new List<string>();
var two = MessagingTest.At(DateTimeOffset.UnixEpoch).System("consumer", ConsumerJson.Default.Options, o =>
{
    o.Consumers = GeneratedConsumers.Set;
    o.Publications = GeneratedPublications.Set;
    o.Effects = new EffectSet().Add<Board.Noted>("log", _ => new LogNote(keys));
    o.Trust("billing");
});
await two.Transport.Publish(test.Message("billing.noted", 1, new NotedV1("again"), ConsumerJson.Default.NotedV1), default);
await two.Drain();
var published = two.Transport.Published is [_, { Type: "consumer.board-noted" }] && keys.Count == 1;

Console.WriteLine(consumed && published ? "ALL CHECKS PASSED" : $"FAILED (consumed {consumed}, published {published})");
return consumed && published ? 0 : 1;

namespace PackageConsumer
{
    public sealed record NotedV1(string Text);

    public partial class Board : AggregateRoot<Board>
    {
        public static readonly Guid Main = Guid.Parse("0193a000-0000-7000-8000-000000000001");
        public int Notes { get; private set; }
        public static Board For(Guid id) => new((Id<Board>)id);
        public Result Note(string text) => Raise(new Noted(text));
        public sealed record Noted(string Text) : Event;
        private void On(Noted e) => Notes++;
    }

    public sealed partial record NoteOnBoard(string Text) : ILoads<Board>
    {
        [Consumes("billing.noted", 1)]
        public static NoteOnBoard From(NotedV1 m) => new(m.Text);

        public async ValueTask<Result<Board>> Load(Tarol.Persistence.IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
            => (await reader.Load((Id<Board>)Board.Main, context.Operation, cancellationToken)).TryGetValue(out Board? found) ? found : Board.For(Board.Main);

        public Result Execute(Board board, ChangeContext context) => board.Note(Text);
    }

    public sealed record BoardNotedV1(string Text);

    public static class Publications
    {
        [Publishes<Board.Noted>("consumer.board-noted", 1)]
        public static BoardNotedV1 Noted(Board.Noted e) => new(e.Text);
    }

    public sealed class LogNote(List<string> keys) : IEffect<Board.Noted>
    {
        public ValueTask<Result> Run(Board.Noted domainEvent, EffectContext context, CancellationToken cancellationToken)
        {
            keys.Add(context.IdempotencyKey);
            return ValueTask.FromResult(Result.Success());
        }
    }

    [JsonSerializable(typeof(BoardNotedV1))]
    [JsonSerializable(typeof(NotedV1))]
    [JsonSerializable(typeof(Board.Noted))]
    public sealed partial class ConsumerJson : JsonSerializerContext;
}
