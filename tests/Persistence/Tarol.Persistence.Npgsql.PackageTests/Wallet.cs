using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.PackageTests;

public partial class Wallet : AggregateRoot<Wallet>
{
    public string Owner { get; private set; } = "";
    public int Total { get; private set; }

    public static Wallet Open(string owner) { var w = new Wallet(Id<Wallet>.New()); w.Raise(new Opened(owner)); return w; }
    public Success Add(int amount) => Raise(new Added(amount));

    public sealed record Opened(string Owner) : Event;
    public sealed record Added(int Amount) : Event;
    private void On(Opened e) => Owner = e.Owner;
    private void On(Added e) => Total += e.Amount;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Wallet.Opened))]
[JsonSerializable(typeof(Wallet.Added))]
internal sealed partial class SmokeJson : JsonSerializerContext;
