using Microsoft.Extensions.Options;
using Probe;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args); builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(); builder.Logging.SetMinimumLevel(LogLevel.Error);
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Paging:Size"] = "20", ["Paging:Max"] = "50" });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, WebContext.Default));
builder.Services.Configure<PagingNullable>(builder.Configuration.GetSection("Paging"));
#if OPTCFG
builder.Services.Configure<PagingOption>(builder.Configuration.GetSection("Paging"));
#endif
var app = builder.Build();

// A: int? query parameter: missing -> null
app.MapGet("/a", (int? page) => page is { } p ? $"page {p}" : "no page");
// C: convert at the edge
app.MapGet("/c", (int? page) => Option.FromNullable(page) switch { int p => $"page {p}", None => "no page" });
#if OPTPARAM
// C: Option<int> as a parameter directly
app.MapGet("/c2", (Option<int> page) => page switch { int p => $"page {p}", None => "no page" });
#endif
app.MapGet("/cfg", (IOptions<PagingNullable> a) => $"A size={a.Value.Size?.ToString() ?? "null"} max={a.Value.Max?.ToString() ?? "null"} other={a.Value.Other?.ToString() ?? "null"}");
#if OPTCFG
app.MapGet("/cfgc", (IOptions<PagingOption> c) => $"C size={(c.Value.Size switch { int s => s.ToString(), None => "None" })} other={(c.Value.Other switch { int s => s.ToString(), None => "None" })}");
#endif
app.MapGet("/json", () => new CDto(1, "ada", default, default));
app.MapPost("/json", (CDto d) => $"nick={(d.Nick switch { string s => s, None => "None" })} age={(d.Age switch { int a => a.ToString(), None => "None" })}");
var t = app.RunAsync("http://127.0.0.1:5077");
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5077") };
await Task.Delay(500);
foreach (var u in new[] { "/a", "/a?page=3", "/c", "/c?page=3", "/c2", "/c2?page=3", "/cfg", "/cfgc", "/json" })
{
    var r = await http.GetAsync(u);
    Console.WriteLine($"GET {u} -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
}
foreach (var body in new[] { "{\"id\":1}", "{\"id\":1,\"nick\":null,\"age\":4}" })
{
    var r = await http.PostAsync("/json", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
    Console.WriteLine($"POST /json {body} -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
}
await app.StopAsync();

public sealed class PagingNullable { public int? Size { get; set; } public int? Max { get; set; } public int? Other { get; set; } }
public sealed class PagingOption { public Option<int> Size { get; set; } public Option<int> Other { get; set; } }
[JsonSerializable(typeof(CDto))] [JsonSerializable(typeof(string))] [JsonSerializable(typeof(int))] [JsonSerializable(typeof(Guid))] [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WebContext : JsonSerializerContext;
