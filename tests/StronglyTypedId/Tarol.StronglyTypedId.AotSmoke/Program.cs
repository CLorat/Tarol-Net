using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Tarol.StronglyTypedId;
using Tarol.StronglyTypedId.AotSmoke;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
        failures++;
}

Check("running as native code", !RuntimeFeature.IsDynamicCodeSupported);

var user = Id<User>.New();
Check("New() is UUIDv7", user.Value.Version == 7);
Check("Parse(ToString()) round-trips", Id<User>.Parse(user.ToString()) == user);

Span<byte> utf8 = stackalloc byte[36];
Check("UTF-8 format + parse round-trips",
    user.TryFormat(utf8, out var written, default, null) && Id<User>.Parse(utf8[..written], null) == user);

var order = new OrderDto(Id<Order>.New(), user, [Id<Order>.New()], new() { [user] = 3 });
var json = JsonSerializer.Serialize(order, SmokeJsonContext.Default.OrderDto);
var back = JsonSerializer.Deserialize(json, SmokeJsonContext.Default.OrderDto)!;
Check("source-generated JSON round-trips ids, lists and dictionary keys",
    back.Id == order.Id && back.Customer == user && back.Related.SequenceEqual(order.Related) && back.Seats[user] == 3);
Check("JSON writes plain strings", json.Contains($"\"{user}\"", StringComparison.Ordinal));

Check("TypeDescriptor resolves the generated registration",
    TypeDescriptor.GetConverterFromRegisteredType(typeof(Id<User>)) is IdTypeConverter<User>);

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Owner"] = user.ToString() })
    .Build();
// The source-generated configuration binder treats Id<T> as an object and ignores TypeConverters (verified on
// .NET 11): bind a Guid and convert, or parse the value. Documented in the package README.
var settings = new Settings();
configuration.Bind(settings);
Check("AOT configuration pattern: bind Guid, convert to Id<T>", Id<User>.From(settings.Owner) == user);
Check("AOT configuration pattern: parse the raw value", Id<User>.Parse(configuration["Owner"]!) == user);

Check("Id<T> is a 16-byte value", Unsafe.SizeOf<Id<User>>() == 16);
Check("utf8 sanity", Encoding.UTF8.GetString(utf8[..written]) == user.ToString());

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.StronglyTypedId.AotSmoke
{
    public sealed class User;

    public sealed class Order;

    public sealed record OrderDto(Id<Order> Id, Id<User> Customer, List<Id<Order>> Related, Dictionary<Id<User>, int> Seats);

    public sealed class Settings
    {
        public Guid Owner { get; set; }
    }

    [JsonSerializable(typeof(OrderDto))]
    internal sealed partial class SmokeJsonContext : JsonSerializerContext;
}
