using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarol.Messaging.SignalR;

/// <summary>What a UI client receives on hub method <c>message</c>.</summary>
/// <param name="Topic">The topic (a contract such as <c>artflow.auction-settled</c>, or a UI topic such as <c>artflow.chat</c>).</param>
/// <param name="Id">The message id (for a forwarded message the event id).</param>
/// <param name="Version">The contract version (1 for a client's publish).</param>
/// <param name="Subject">The stream id of a forwarded message; <see langword="null"/> for a client's publish.</param>
/// <param name="Time">When it was recorded or published.</param>
/// <param name="Sender">Who sent it, stamped by the server: the source system of a forwarded message, the user of a client's publish.</param>
/// <param name="Data">The payload (the contract JSON).</param>
/// <param name="Revision">
/// The stream's version after the event of a forwarded message (<c>ce-streamversion</c>), the number a view's strong
/// <c>ETag</c> of <see cref="Subject"/> carries: skip a message whose revision the client already has (its own change, or a
/// newer read). <see langword="null"/> for a client's publish, or a message of a publisher without it.
/// </param>
/// <param name="Position">
/// The event's position in the publisher's feed (<c>ce-position</c>), in the text of <c>Tarol-Position</c>: send it as
/// <c>Tarol-Read-After</c> to re-read a list whose projection has the event. <see langword="null"/> for a client's publish, or a
/// message of a publisher without it.
/// </param>
public sealed record PushedMessage(string Topic, Guid Id, int Version, string? Subject, DateTimeOffset Time, string Sender, JsonElement Data,
    long? Revision = null, string? Position = null);

/// <summary>
/// The source-generated JSON of the hub's arguments and messages, for both sides (Native AOT; the old F1): add it with
/// <c>AddJsonProtocol(o =&gt; o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, SignalRJson.Default))</c>, which
/// <c>PushToUi</c> and <see cref="MessagePushClient.Connect"/> do.
/// </summary>
[JsonSerializable(typeof(PushedMessage))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class SignalRJson : JsonSerializerContext;
