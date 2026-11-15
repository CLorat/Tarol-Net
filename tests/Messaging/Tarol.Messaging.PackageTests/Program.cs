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
Console.WriteLine(attempts is [{ Verdict: Verdict.Done }] && test.Load((Id<Board>)Board.Main).Notes == 1 ? "ALL CHECKS PASSED" : "FAILED");
return attempts is [{ Verdict: Verdict.Done }] ? 0 : 1;

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

    [JsonSerializable(typeof(NotedV1))]
    [JsonSerializable(typeof(Board.Noted))]
    public sealed partial class ConsumerJson : JsonSerializerContext;
}
