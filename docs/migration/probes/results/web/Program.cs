using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Probe;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, WebJson.Default));
builder.Services.AddProblemDetails();
var app = builder.Build();
var users = new Dictionary<Guid, User> { [Guid.Empty] = new User { Name = "Ada" } };

static C2Result<User> Find(Dictionary<Guid, User> users, Guid id) => users.TryGetValue(id, out var u) ? u : Failure.NotFound("User.NotFound");

// 1. union switch straight into TypedResults
app.MapGet("/users/{id:guid}", Results<Ok<User>, ProblemHttpResult> (Guid id) => Find(users, id) switch
{
    User u => TypedResults.Ok(u),
    Failure f => f.ToProblem(),
});
// 2. return the union itself (STJ writes the active case)
// 3. explicit envelope contract
app.MapGet("/env/{id:guid}", (Guid id) => Find(users, id) switch { User u => (EResult<User>)u, Failure f => (EResult<User>)f });
app.Run();

static class Http
{
    public static ProblemHttpResult ToProblem(this Failure f) => TypedResults.Problem(statusCode: f.Kind switch
    {
        FailureKind.Validation => 400, FailureKind.Unauthorized => 401, FailureKind.Forbidden => 403, FailureKind.NotFound => 404,
        FailureKind.Conflict => 409, FailureKind.Gone => 410, FailureKind.Unprocessable => 422, FailureKind.TooManyRequests => 429,
        FailureKind.Unavailable => 503, FailureKind.Timeout => 504, _ => 500,
    }, title: f.Code, detail: f.Detail, extensions: new Dictionary<string, object?> { ["code"] = f.Code });
}

[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Failure))]
[JsonSerializable(typeof(EResult<User>))]
partial class WebJson : JsonSerializerContext;
