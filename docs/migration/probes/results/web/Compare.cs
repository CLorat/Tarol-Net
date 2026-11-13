using Microsoft.AspNetCore.Http.HttpResults;
using Probe;
namespace Compare;

public sealed record Rename(Guid Id, string Name);
public interface IRepoA { ValueTask<AResult<User>> Get(Guid id); }
public interface IRepoC { ValueTask<C2Result<User>> Get(Guid id); }
public interface IRepoD { ValueTask<DResult<User>> Get(Guid id); }

public static class ShapeA
{
    // validation pipeline
    public static AResult<string> ValidName(string? n) =>
        string.IsNullOrWhiteSpace(n) ? new Violation { Code = "Name.Required", Type = ViolationType.Validation, PropertyName = "Name" } : n;
    // handler
    public static async ValueTask<AResult<User>> Handle(IRepoA repo, Rename cmd)
    {
        var name = ValidName(cmd.Name);
        if (!name.IsSuccess) return name.Violation;
        var user = await repo.Get(cmd.Id);
        return user.Map(u => new User { Name = name.Value });
    }
    // endpoint
    public static async Task<Results<Ok<User>, ProblemHttpResult>> Endpoint(IRepoA repo, Rename cmd) =>
        (await Handle(repo, cmd)).Match<Results<Ok<User>, ProblemHttpResult>>(
            u => TypedResults.Ok(u),
            v => TypedResults.Problem(statusCode: v.Type == ViolationType.NotFound ? 404 : 400, title: v.Code));
}

public static class ShapeC
{
    public static C2Result<string> ValidName(string? n) =>
        string.IsNullOrWhiteSpace(n) ? Failure.Validation("Name.Required", "Name") : n;
    public static async ValueTask<C2Result<User>> Handle(IRepoC repo, Rename cmd)
    {
        if (ValidName(cmd.Name) is Failure invalid) return invalid;
        return await repo.Get(cmd.Id) switch
        {
            User => new User { Name = cmd.Name },
            Failure f => f,
        };
    }
    public static async Task<Results<Ok<User>, ProblemHttpResult>> Endpoint(IRepoC repo, Rename cmd) =>
        await Handle(repo, cmd) switch
        {
            User u => TypedResults.Ok(u),
            Failure f => TypedResults.Problem(statusCode: f.Kind == FailureKind.NotFound ? 404 : 400, title: f.Code),
        };
}

public static class ShapeD
{
    public static DResult<string> ValidName(string? n) => string.IsNullOrWhiteSpace(n) ? new Invalid("Name.Required", "Name") : n;
    public static async ValueTask<DResult<User>> Handle(IRepoD repo, Rename cmd)
    {
        if (ValidName(cmd.Name) is Problem invalid) return invalid;
        return await repo.Get(cmd.Id) switch { User => new User { Name = cmd.Name }, Problem p => p };
    }
    public static async Task<Results<Ok<User>, ProblemHttpResult>> Endpoint(IRepoD repo, Rename cmd) =>
        await Handle(repo, cmd) switch
        {
            User u => TypedResults.Ok(u),
            Probe.NotFound nf => TypedResults.Problem(statusCode: 404, title: nf.Code),
            Probe.Conflict c => TypedResults.Problem(statusCode: 409, title: c.Code),
            Invalid i => TypedResults.Problem(statusCode: 400, title: i.Code),
            Uninitialized u => TypedResults.Problem(statusCode: 500, title: u.Code),
        };
}

public static class ShapeB
{
    public static BResult<string> ValidName(string? n) => string.IsNullOrWhiteSpace(n) ? Failure.Validation("Name.Required", "Name") : n;
    public static BResult<User> Handle(BResult<User> loaded, Rename cmd) =>
        ValidName(cmd.Name) is Failure invalid ? invalid : loaded switch { User => new User { Name = cmd.Name }, Failure f => f, null => Failure.Uninitialized };
}
