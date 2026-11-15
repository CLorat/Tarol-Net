using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.AotSmoke;

public partial class Wallet : AggregateRoot<Wallet>
{
    public string Owner { get; private set; } = "";
    public int Total { get; private set; }

    public static Wallet Open(string owner) { var w = new Wallet(Id<Wallet>.New()); w.Raise(new Opened(owner)); return w; }
    public Success Add(int amount) => Raise(new Added(amount));

    public sealed record Opened(string Owner) : Event;
    [EventType("wallet.added", 2)]
    public sealed record Added(int Amount) : Event;
    private void On(Opened e) => Owner = e.Owner;
    private void On(Added e) => Total += e.Amount;
}

/// <summary>"wallet.added" v1 stored the amount as "value".</summary>
public sealed class AddedV1ToV2 : IEventUpcaster
{
    public EventType From => new("wallet.added", 1);
    public EventType Into => new("wallet.added", 2);
    public void Upcast(System.Text.Json.Nodes.JsonObject payload)
    {
        payload["amount"] = payload["value"]?.DeepClone();
        payload.Remove("value");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Wallet.Opened))]
[JsonSerializable(typeof(Wallet.Added))]
internal sealed partial class SmokeJson : JsonSerializerContext;
