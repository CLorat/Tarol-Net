using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

/// <summary>An aggregate whose events changed: "subscription.shut" v1 was renamed, then "subscription.cancelled" got a reason.</summary>
public partial class Subscription : AggregateRoot<Subscription>
{
    public string Plan { get; private set; } = "";
    public string? CancelledFor { get; private set; }

    public static Subscription Start(string plan)
    {
        var s = new Subscription(Id<Subscription>.New());
        s.Raise(new Started(plan));
        return s;
    }

    public Success Cancel(string reason) => Raise(new Cancelled(reason));

    /// <summary>Currency and Trial were added after v1 events were stored (Tatalu FB-E07-1): optional, with defaults.</summary>
    public sealed record Started(string Plan, string Currency = "EUR", bool Trial = false) : Event;

    [EventType("subscription.cancelled", 2)]
    public sealed record Cancelled(string Reason) : Event;

    private void On(Started e) => Plan = e.Plan;
    private void On(Cancelled e) => CancelledFor = e.Reason;
}

/// <summary>v1 of the rename: "subscription.shut" became "subscription.cancelled" v1 (same payload).</summary>
public sealed class ShutToCancelled : IEventUpcaster
{
    public EventType From => new("subscription.shut", 1);
    public EventType Into => new("subscription.cancelled", 1);
    public void Upcast(JsonObject payload) { }
}

/// <summary>"subscription.cancelled" v1 had no reason.</summary>
public sealed class CancelledV1ToV2 : IEventUpcaster
{
    public EventType From => new("subscription.cancelled", 1);
    public EventType Into => new("subscription.cancelled", 2);
    public void Upcast(JsonObject payload) => payload["reason"] = "unknown";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Subscription.Started))]
[JsonSerializable(typeof(Subscription.Cancelled))]
[JsonSerializable(typeof(Account.Opened))]
[JsonSerializable(typeof(Account.Deposited))]
[JsonSerializable(typeof(Account.Renumbered))]
public partial class CodecJson : JsonSerializerContext;
