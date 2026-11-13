using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Results;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));
builder.Services.AddOpenApi(o =>
{
#if FIX
    o.AddSchemaTransformer<OptionSchema>();
#endif
});
var app = builder.Build();
app.MapOpenApi();
app.MapGet("/profiles/{id}", (int id) => new Profile(id, "ada", default, default));
app.MapPost("/profiles", (Profile p) => p);
var run = app.RunAsync("http://127.0.0.1:5088");
using var http = new HttpClient();
await Task.Delay(500);
var doc = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync("http://127.0.0.1:5088/openapi/v1.json"))!;
Console.WriteLine(doc["components"]!["schemas"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
await app.StopAsync();

public sealed record Address(string City);
public sealed record Profile(int Id, Option<string> Nickname, Option<int> Age, Option<Address> Address);
[JsonSerializable(typeof(Profile))] [JsonSerializable(typeof(Address))] [JsonSerializable(typeof(string))] [JsonSerializable(typeof(int))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class ApiJson : JsonSerializerContext;

#if FIX
// Option<T> is described as T, nullable, and not required (the converter writes the value or null; a missing property is None).
public sealed class OptionSchema : Microsoft.AspNetCore.OpenApi.IOpenApiSchemaTransformer
{
    public async Task TransformAsync(Microsoft.OpenApi.OpenApiSchema schema, Microsoft.AspNetCore.OpenApi.OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>))
        {
            var inner = await context.GetOrCreateSchemaAsync(type.GetGenericArguments()[0], null, cancellationToken);
            schema.Description = null;
            schema.OneOf = [inner, new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Null }];
            return;
        }

        if (schema.Required is { Count: > 0 } required)
        {
            foreach (var property in context.JsonTypeInfo.Properties)
            {
                if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Option<>))
                {
                    required.Remove(property.Name);
                }
            }
        }
    }
}
#endif
