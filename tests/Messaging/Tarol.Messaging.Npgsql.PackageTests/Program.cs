using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PackageConsumer;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Messaging.Npgsql;
using Tarol.Messaging.Npgsql.PackageTests;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

// The bus and the dead letters from the packed package, registered and resolved (no database needed to resolve).
var services = new ServiceCollection();
services.AddTarol(ConsumerJson.Default, codec => codec.Add<Board>())
    .UseInMemoryStore()
    .AddMessaging("consumer", m => m
        .Consume(GeneratedConsumers.Set)
        .Trust("billing")
        .UseNpgsqlBus("Host=localhost;Username=nobody", new NpgsqlBusOptions { Notify = true })
        .UseNpgsqlDeadLetters(dataSource: static _ => NpgsqlDataSource.Create("Host=localhost;Username=nobody"), ensureSchema: false));
await using var provider = services.BuildServiceProvider();
var ok = provider.GetRequiredService<IMessageTransport>() is NpgsqlBus { Schema: "bus" }
    && provider.GetRequiredService<IMessageDeadLetters>() is NpgsqlMessageDeadLetters { Schema: "tarol" }
    && NpgsqlBusSchema.RowLevelSecurityScript().Contains("publish_own_prefix", StringComparison.Ordinal);
Console.WriteLine(ok ? "ALL CHECKS PASSED" : "FAILED");
return ok ? 0 : 1;

namespace PackageConsumer
{
    public sealed record NotedV1(string Text);

    public partial class Board : AggregateRoot<Board>
    {
        public static Board For(Guid id) => new((Id<Board>)id);
        public Result Note(string text) => Raise(new Noted(text));
        public sealed record Noted(string Text) : Event;
        private void On(Noted e) { }
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
