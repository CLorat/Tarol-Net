using System.Text.Json;
using System.Text.Json.Serialization;
J.Run(); J2.Run();

var s = AuctionStatus.Closed;
Console.WriteLine(Name(s));
Console.WriteLine(JsonSerializer.Serialize(new Holder(s), Ctx.Default.Holder));
Console.WriteLine(JsonSerializer.Deserialize("{\"Status\":\"settled\"}", Ctx.Default.Holder));
Console.WriteLine(JsonSerializer.Deserialize("{\"Status\":7}", Ctx.Default.Holder)); // undefined value: does it load?
foreach (var m in typeof(AuctionStatus).GetFields()) foreach (var a in m.GetCustomAttributesData()) Console.WriteLine($"{m.Name}: {a}");

static string Name(AuctionStatus s) => s switch
{
    AuctionStatus.Open => "open",
    AuctionStatus.Closed => "closed",
};
static string Name2(State s) => s switch
{
    State.Open => "open",
    State.Closed c => "closed " + c.EndTime,
};

[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
sealed class ToAttribute(params AuctionStatus[] to) : Attribute { public AuctionStatus[] To { get; } = to; }

[JsonConverter(typeof(JsonStringEnumConverter<AuctionStatus>))]
public enum AuctionStatus
{
    [To(Closed)] Open,
    [To(Settled)] Closed,
    [JsonStringEnumMemberName("settled")] Settled,
}

public closed record State
{
    public sealed record Open : State;
    public sealed record Closed(DateTimeOffset EndTime) : State;
    public sealed record Settled : State;
}

public record Holder(AuctionStatus Status);
[JsonSerializable(typeof(Holder))] partial class Ctx : JsonSerializerContext;
static class J { public static void Run() {
  try { Console.WriteLine(JsonSerializer.Serialize<State>(new State.Closed(DateTimeOffset.UnixEpoch))); } catch (Exception e) { Console.WriteLine(e.GetType().Name + ": " + e.Message[..Math.Min(120, e.Message.Length)]); }
  try { Console.WriteLine(JsonSerializer.Serialize<StateU>(new Closed2(DateTimeOffset.UnixEpoch))); } catch (Exception e) { Console.WriteLine(e.GetType().Name + ": " + e.Message[..Math.Min(160, e.Message.Length)]); }
}}
