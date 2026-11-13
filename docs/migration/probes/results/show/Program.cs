using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;
using Show;
using A = Show.A;
using M1 = Show.M1;
using M2 = Show.M2;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ShowJson.Default));
builder.Services.AddProblemDetails();
var app = builder.Build();
var store = new Dictionary<Guid, User> { [Guid.Empty] = new(Guid.Empty, "Ada") };

app.MapGet("/a/users/{id:guid}", (Guid id, string? name, string? email) => ShapeA.Endpoint(store, id, name, email));
app.MapGet("/m1/users/{id:guid}", (Guid id, string? name, string? email) => ShapeM1.Endpoint(store, id, name, email));
app.MapGet("/m2/users/{id:guid}", (Guid id, string? name, string? email) => ShapeM2.Endpoint(store, id, name, email));
app.MapGet("/d/users/{id:guid}", (Guid id, string? name, string? email) => ShapeD.Endpoint(store, id, name, email));
// the Result itself as JSON data (what a message, an event or a client SDK would see)
app.MapGet("/m1/data/{id:guid}", (Guid id, string? name, string? email) => ShapeM1.Rename(store, id, name, email));
app.MapGet("/m2/data/{id:guid}", (Guid id, string? name, string? email) => ShapeM2.Rename(store, id, name, email));
app.MapGet("/d/data/{id:guid}", (Guid id, string? name, string? email) => ShapeD.Rename(store, id, name, email));
app.Run();

// ======================= A: today =======================
static class ShapeA
{
    public static A.Result<User> Rename(Dictionary<Guid, User> store, Guid id, string? name, string? email)
    {
        var errors = new List<A.Violation>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add(A.Violation.Validation("Name.Required", "Name"));
        if (email is not null && !email.Contains('@')) errors.Add(A.Violation.Validation("Email.Invalid", "Email"));
        if (errors.Count > 0) return A.Violation.Aggregate("Validation.Multiple", errors);

        if (!store.TryGetValue(id, out var user)) return A.Violation.NotFound("User.NotFound");
        return user with { Name = name! };
    }

    public static Results<Ok<User>, ValidationProblem, ProblemHttpResult> Endpoint(Dictionary<Guid, User> store, Guid id, string? name, string? email)
    {
        var result = Rename(store, id, name, email);
        if (result.IsSuccess) return TypedResults.Ok(result.Value);
        var v = result.Violation;
        if (v.Type == A.ViolationType.Validation)
            return TypedResults.ValidationProblem((v.InnerErrors ?? [v]).GroupBy(e => e.PropertyName ?? "").ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray()));
        return TypedResults.Problem(statusCode: v.Type == A.ViolationType.NotFound ? 404 : 500, title: v.Code, detail: v.Detail);
    }
}

// ======================= C + M1: Violation sealed class =======================
static class ShapeM1
{
    public static M1.Result<User> Rename(Dictionary<Guid, User> store, Guid id, string? name, string? email)
    {
        List<M1.Violation> errors = [];
        if (string.IsNullOrWhiteSpace(name)) errors.Add(M1.Violation.Validation("Name.Required", "Name"));
        if (email is not null && !email.Contains('@')) errors.Add(M1.Violation.Validation("Email.Invalid", "Email"));
        if (errors.Count > 0) return M1.Violation.Aggregate(errors);

        return store.TryGetValue(id, out var user)
            ? user with { Name = name! }
            : M1.Violation.NotFound("User.NotFound", $"No user {id}.");
    }

    public static Results<Ok<User>, ValidationProblem, ProblemHttpResult> Endpoint(Dictionary<Guid, User> store, Guid id, string? name, string? email) =>
        Rename(store, id, name, email) switch
        {
            User user => TypedResults.Ok(user),
            M1.Violation { Type: M1.ViolationType.Validation } v => TypedResults.ValidationProblem(
                (v.InnerErrors.Count > 0 ? v.InnerErrors : [v]).GroupBy(e => e.PropertyName ?? "").ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
            M1.Violation v => TypedResults.Problem(statusCode: v.Type == M1.ViolationType.NotFound ? 404 : 500, title: v.Code, detail: v.Detail),
        };
}

// ======================= C + M2: validation errors | one light error =======================
static class ShapeM2
{
    public static M2.Result<User> Rename(Dictionary<Guid, User> store, Guid id, string? name, string? email)
    {
        List<M2.FieldError> errors = [];
        if (string.IsNullOrWhiteSpace(name)) errors.Add(new("Name", "Name.Required"));
        if (email is not null && !email.Contains('@')) errors.Add(new("Email", "Email.Invalid"));
        if (errors.Count > 0) return new M2.Invalid(errors);

        return store.TryGetValue(id, out var user)
            ? user with { Name = name! }
            : M2.Error.NotFound("User.NotFound", $"No user {id}.");
    }

    public static Results<Ok<User>, ValidationProblem, ProblemHttpResult> Endpoint(Dictionary<Guid, User> store, Guid id, string? name, string? email) =>
        Rename(store, id, name, email) switch
        {
            User user => TypedResults.Ok(user),
            M2.Invalid invalid => TypedResults.ValidationProblem(
                invalid.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
            M2.Error error => TypedResults.Problem(statusCode: error.Type == M2.ErrorType.NotFound ? 404 : 500, title: error.Code, detail: error.Detail),
        };
}

// ======================= D: one record per failure kind =======================
static class ShapeD
{
    public static Show.D.Result<User> Rename(Dictionary<Guid, User> store, Guid id, string? name, string? email)
    {
        if (string.IsNullOrWhiteSpace(name)) return new Show.D.InvalidField("Name.Required", "Name");   // one field only: the kind has no list
        if (email is not null && !email.Contains('@')) return new Show.D.InvalidField("Email.Invalid", "Email");
        return store.TryGetValue(id, out var user) ? user with { Name = name } : new Show.D.NotFound("User.NotFound", $"No user {id}.");
    }

    public static Results<Ok<User>, ValidationProblem, ProblemHttpResult> Endpoint(Dictionary<Guid, User> store, Guid id, string? name, string? email) =>
        Rename(store, id, name, email) switch
        {
            User user => TypedResults.Ok(user),
            Show.D.InvalidField i => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [i.Field] = [i.Code] }),
            Show.D.NotFound nf => TypedResults.Problem(statusCode: 404, title: nf.Code, detail: nf.Detail),
            Show.D.Conflict c => TypedResults.Problem(statusCode: 409, title: c.Code, detail: c.Detail),
            Show.D.Forbidden f => TypedResults.Problem(statusCode: 403, title: f.Code),
            Show.D.Unexpected u => TypedResults.Problem(statusCode: 500, title: u.Code, detail: u.Detail),
        };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(M1.Result<User>), TypeInfoPropertyName = "M1ResultUser")]
[JsonSerializable(typeof(M1.Violation), TypeInfoPropertyName = "M1Violation")]
[JsonSerializable(typeof(M2.Result<User>), TypeInfoPropertyName = "M2ResultUser")]
[JsonSerializable(typeof(M2.Failure), TypeInfoPropertyName = "M2Failure")]
[JsonSerializable(typeof(Show.D.Result<User>), TypeInfoPropertyName = "DResultUser")]
[JsonSerializable(typeof(Show.D.Failure), TypeInfoPropertyName = "DFailure")]
partial class ShowJson : JsonSerializerContext;
