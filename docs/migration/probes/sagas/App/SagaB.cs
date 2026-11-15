using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ArtFlow;

// Candidate SA-B stores the changes a saga step issues inside the saga's event (Saga.Stepped(state, commands)), and a
// reaction dispatches them later. The commands are typed by a library interface the user cannot annotate, so their JSON
// needs polymorphism metadata the generator would have to emit. This checks what the source generator does without it.

/// <summary>The library's marker for a command a saga issues (SA-B).</summary>
public interface ISagaCommand;

public sealed record RequestPayment(Guid Auction, string Buyer, decimal Amount) : ISagaCommand;
public sealed record CancelSale(Guid Auction) : ISagaCommand;

public sealed record SagaStepped(string State, ISagaCommand[] Commands);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SagaStepped))]
[JsonSerializable(typeof(RequestPayment))]
[JsonSerializable(typeof(CancelSale))]
internal sealed partial class SagaBJson : JsonSerializerContext;

internal static class SagaBCheck
{
    public static (string Plain, string Modified, bool RoundTrip) Run()
    {
        var stepped = new SagaStepped("{\"amount\":120}", [new RequestPayment(Guid.Empty, "bob", 120), new CancelSale(Guid.Empty)]);
        var plain = JsonSerializer.Serialize(stepped, SagaBJson.Default.SagaStepped);

        // what a generator would have to add: the derived types of the library interface, per application
        var options = new JsonSerializerOptions(SagaBJson.Default.Options)
        {
            TypeInfoResolver = SagaBJson.Default.WithAddedModifier(info =>
            {
                if (info.Type == typeof(ISagaCommand))
                    info.PolymorphismOptions = new JsonPolymorphismOptions
                    {
                        DerivedTypes = { new(typeof(RequestPayment), "request-payment"), new(typeof(CancelSale), "cancel-sale") },
                    };
            }),
        };
        var info = (JsonTypeInfo<SagaStepped>)options.GetTypeInfo(typeof(SagaStepped));
        var modified = JsonSerializer.Serialize(stepped, info);
        var back = JsonSerializer.Deserialize(modified, info)!;
        return (plain, modified, back.Commands is [RequestPayment { Buyer: "bob" }, CancelSale]);
    }
}
