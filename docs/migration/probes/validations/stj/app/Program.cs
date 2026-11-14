using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using Lib;
var ev = new AppEvent(Quantity.Create(40).GetValueOrThrow());
Console.WriteLine("other assembly context: " + JsonSerializer.Serialize(ev, AppJson.Default.AppEvent));
try { Console.WriteLine("same assembly context:  " + JsonSerializer.Serialize(new LibEvent(ev.Total, ev.Total), LibJson.Default.LibEvent)); }
catch (Exception e) { Console.WriteLine("same assembly context:  " + e.GetType().Name); }
Console.WriteLine("read back: " + JsonSerializer.Deserialize("""{"Total":500}""", AppJson.Default.AppEvent));
Extra.Run();
public sealed record AppEvent(Quantity Total);
[JsonSerializable(typeof(AppEvent))]
public partial class AppJson : JsonSerializerContext;
public static class Extra
{
    public static void Run()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine(ValueObjectResolver.Instance, LibJson.Default) };
        var json = JsonSerializer.Serialize(new LibEvent(Quantity.Create(40).GetValueOrThrow(), default), options.GetTypeInfo(typeof(LibEvent)));
        Console.WriteLine("same assembly + resolver first: " + json);
        var ctxOptions = new JsonSerializerOptions(LibJson.Default.Options);
        ctxOptions.TypeInfoResolverChain.Insert(0, ValueObjectResolver.Instance);
        Console.WriteLine("context options + resolver:     " + JsonSerializer.Serialize(new LibEvent(Quantity.Create(41).GetValueOrThrow(), default), ctxOptions.GetTypeInfo(typeof(LibEvent))));
        Console.WriteLine("read back:                      " + JsonSerializer.Deserialize("""{"Total":500}""", (JsonTypeInfo<LibEvent>)options.GetTypeInfo(typeof(LibEvent))));
    }
}
