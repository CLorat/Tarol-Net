using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Tarol.DataProtection;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

var services = new ServiceCollection();
services.AddSingleton(TimeProvider.System);
var json = new JsonSerializerOptions(ConsumerJson.Default.Options);
_ = new TarolBuilder(services, json, new EventCodec(json).Add<Member>())
    .UseInMemoryStore().UseInMemoryKeyStore()
    .AddDataProtection(o => o.MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
await using var provider = services.BuildServiceProvider();

var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
var tenant = context.Tenant.GetValueOrThrow().Value;
var member = Member.Join("ada@example.org");
var store = provider.GetRequiredService<IAggregateStore>();
var subjects = provider.GetRequiredService<IDataSubjects>();
var appended = (await store.Append(member, 0, context, Option.None, default)).IsSuccess;
var exported = System.Text.Encoding.UTF8.GetString((await subjects.Export(tenant, member.Id.Value, "dpo", default)).Utf8Json.Span).Contains("ada@example.org", StringComparison.Ordinal);
await subjects.EraseSubject(tenant, member.Id.Value, "dpo", default);
var erased = (await store.Load(member.Id, context, default)).GetValueOrThrow().Email is null;
if (!appended || !exported || !erased)
{
    Console.Error.WriteLine($"Tarol.DataProtection package consumer: appended {appended}, exported {exported}, erased {erased}");
    return 1;
}
Console.WriteLine("Tarol.DataProtection package consumer: sealed, exported and erased");
return 0;

public partial class Member : AggregateRoot<Member>
{
    [PersonalData] public string? Email { get; private set; }

    public static Member Join(string email)
    {
        var member = new Member(Id<Member>.New());
        member.Raise(new Joined(email));
        return member;
    }

    public sealed record Joined([property: PersonalData] string? Email) : Event;

    private void On(Joined e) => Email = e.Email;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Member.Joined))]
internal sealed partial class ConsumerJson : JsonSerializerContext;
