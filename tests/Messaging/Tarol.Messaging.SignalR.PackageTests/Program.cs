using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Messaging.SignalR;

// The UI push from the packed package: registered, the hub mapped, a client built (no server started).
var builder = WebApplication.CreateSlimBuilder();
builder.Services.AddTarol(ConsumerJson.Default, _ => { })
    .UseInMemoryStore()
    .AddMessaging("consumer", m => m.UseInMemoryTransport().PushToUi(o => { o.Forward("consumer.noted"); o.Rules.Subscribe("consumer.*"); }));
var app = builder.Build();
app.MapMessageHub();
await using var client = MessagePushClient.Connect(new Uri("http://localhost:1/tarol/messages"));
var ok = app.Services.GetRequiredService<ITopicAuthorizer>() is TopicRules
    && SignalRJson.Default.PushedMessage is not null
    && client.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected;
Console.WriteLine(ok ? "ALL CHECKS PASSED" : "FAILED");
return ok ? 0 : 1;

[JsonSerializable(typeof(int))]
internal sealed partial class ConsumerJson : JsonSerializerContext;
