using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PackageConsumer;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Messaging.RabbitMq;
using Tarol.Messaging.RabbitMq.PackageTests;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

// The transport from the packed package, registered and resolved (no broker needed to resolve).
var services = new ServiceCollection();
services.AddTarol(ConsumerJson.Default, codec => codec.Add<Board>())
    .UseInMemoryStore()
    .AddMessaging("consumer", m => m
        .Consume(GeneratedConsumers.Set)
        .Trust("billing")
        .UseRabbitMq("amqp://guest:guest@localhost:5672/")
        .UseInMemoryDeadLetters());
await using var provider = services.BuildServiceProvider();
var ok = provider.GetRequiredService<IMessageTransport>() is RabbitMqTransport t && t.ExchangeOf("billing.noted") == "billing";
Console.WriteLine(ok ? "ALL CHECKS PASSED" : "FAILED");
return ok ? 0 : 1;

namespace PackageConsumer
{
    public sealed record NotedV1(string Text);

    public partial class Board : AggregateRoot<Board>
    {
        public int Notes { get; private set; }
        public static Board For(Guid id) => new((Id<Board>)id);
        public Result Note(string text) => Raise(new Noted(text));
        public sealed record Noted(string Text) : Event;
        private void On(Noted e) => Notes++;
    }

    public sealed partial record NoteOnBoard(Guid Board, string Text) : ICreation<Board>
    {
        [Consumes("billing.noted", 1)]
        public static NoteOnBoard From(NotedV1 m) => new(Guid.Empty, m.Text);

        public Result<Board> Execute(ChangeContext context)
        {
            var board = PackageConsumer.Board.For(Guid.CreateVersion7());
            return board.Note(Text).Map(() => board);
        }
    }

    [JsonSerializable(typeof(NotedV1))]
    [JsonSerializable(typeof(Board.Noted))]
    public sealed partial class ConsumerJson : JsonSerializerContext;
}
