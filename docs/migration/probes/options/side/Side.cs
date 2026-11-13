using Probe;
using Tarol.Results;
namespace Side;
// The same use case per candidate and payload: find, check, match, map, fall back, unwrap, to/from Result.
public static class A
{
    public static int? FindInt() => 5; public static Guid? FindGuid() => Guid.Empty; public static Id<User>? FindId() => default(Id<User>);
    public static string? FindStr() => "a"; public static User? FindUser() => new("Ada");
    public static void Int(Result<int> r)
    {
        int? x = FindInt(); int? none = null;
        bool has = x.HasValue;                         // struct only (class: x is not null)
        string m = x switch { { } v => $"{v}", null => "none" };
        int? mapped = x is { } y ? y * 2 : null;       // or x * 2 (lifted operator: struct only)
        int f = x ?? 0;
        int u = x ?? throw new InvalidOperationException();                            // CS8629 if unchecked; class: x! (silent)
        Result<int> res = x.ToResult(Error.NotFound("Int.NotFound"));
        int? back = r is int z ? z : null;             // no one-call conversion
        _ = (none, has, m, mapped, f, u, res, back);
    }
    public static void Guid_(Result<Guid> r)
    {
        Guid? x = FindGuid(); bool has = x.HasValue;
        string m = x switch { { } v => $"{v}", null => "none" };
        string? mapped = x?.ToString();
        Guid f = x ?? Guid.Empty; Guid u = x ?? throw new InvalidOperationException();
        Result<Guid> res = x.ToResult(Error.NotFound("Guid.NotFound"));
        Guid? back = r is Guid z ? z : null;
        _ = (has, m, mapped, f, u, res, back);
    }
    public static void Id(Result<Id<User>> r)
    {
        Id<User>? x = FindId(); bool has = x.HasValue;
        string m = x switch { { } v => $"{v}", null => "none" };
        Guid? mapped = x?.Value;
        Id<User> f = x ?? default; Id<User> u = x ?? throw new InvalidOperationException();
        Result<Id<User>> res = x.ToResult(Error.NotFound("User.NotFound"));
        Id<User>? back = r is Id<User> z ? z : null;
        _ = (has, m, mapped, f, u, res, back);
    }
    public static void Str(Result<string> r)
    {
        string? x = FindStr(); bool has = x is not null;
        string m = x switch { { } v => v, null => "none" };
        int? mapped = x?.Length;
        string f = x ?? ""; string u = x!;             // '!' is silent
        Result<string> res = x.ToResult(Error.NotFound("Str.NotFound"));
        string? back = r is string z ? z : null;
        _ = (has, m, mapped, f, u, res, back);
    }
    public static void Usr(Result<User> r)
    {
        User? x = FindUser(); bool has = x is not null;
        string m = x switch { { } v => v.Name, null => "none" };
        string? mapped = x?.Name;
        User f = x ?? new User("Guest"); User u = x!;
        Result<User> res = x.ToResult(Error.NotFound("User.NotFound"));
        User? back = r is User z ? z : null;
        _ = (has, m, mapped, f, u, res, back);
    }
}

public static class C
{
    public static Option<int> FindInt() => 5; public static Option<Guid> FindGuid() => Guid.Empty; public static Option<Id<User>> FindId() => default(Id<User>);
    public static Option<string> FindStr() => "a"; public static Option<User> FindUser() => new User("Ada");
    public static void Int(Result<int> r)
    {
        Option<int> x = FindInt(); Option<int> none = Option.None;
        bool has = x.IsSome;
        string m = x switch { int v => $"{v}", None => "none" };
        Option<int> mapped = x.Map(v => v * 2);
        int f = x.GetValueOrDefault(0);
        int u = x.GetValueOrThrow();
        Result<int> res = x.ToResult(Error.NotFound("Int.NotFound"));
        Option<int> back = r.ToOption();
        _ = (none, has, m, mapped, f, u, res, back);
    }
    public static void Guid_(Result<Guid> r)
    {
        Option<Guid> x = FindGuid(); bool has = x.IsSome;
        string m = x switch { Guid v => $"{v}", None => "none" };
        Option<string> mapped = x.Map(v => v.ToString());
        Guid f = x.GetValueOrDefault(Guid.Empty); Guid u = x.GetValueOrThrow();
        Result<Guid> res = x.ToResult(Error.NotFound("Guid.NotFound"));
        Option<Guid> back = r.ToOption();
        _ = (has, m, mapped, f, u, res, back);
    }
    public static void Id(Result<Id<User>> r)
    {
        Option<Id<User>> x = FindId(); bool has = x.IsSome;
        string m = x switch { Id<User> v => $"{v}", None => "none" };
        Option<Guid> mapped = x.Map(v => v.Value);
        Id<User> f = x.GetValueOrDefault(default); Id<User> u = x.GetValueOrThrow();
        Result<Id<User>> res = x.ToResult(Error.NotFound("User.NotFound"));
        Option<Id<User>> back = r.ToOption();
        _ = (has, m, mapped, f, u, res, back);
    }
    public static void Str(Result<string> r)
    {
        Option<string> x = FindStr(); bool has = x.IsSome;
        string m = x switch { string v => v, None => "none" };
        Option<int> mapped = x.Map(v => v.Length);
        string f = x.GetValueOrDefault(""); string u = x.GetValueOrThrow();
        Result<string> res = x.ToResult(Error.NotFound("Str.NotFound"));
        Option<string> back = r.ToOption();
        _ = (has, m, mapped, f, u, res, back);
    }
    public static void Usr(Result<User> r)
    {
        Option<User> x = FindUser(); bool has = x.IsSome;
        string m = x switch { User v => v.Name, None => "none" };
        Option<string> mapped = x.Map(v => v.Name);
        User f = x.GetValueOrDefault(new User("Guest")); User u = x.GetValueOrThrow();
        Result<User> res = x.ToResult(Error.NotFound("User.NotFound"));
        Option<User> back = r.ToOption();
        _ = (has, m, mapped, f, u, res, back);
    }
    // generic code keeps absence for every T
    public static Option<T> First<T>(IEnumerable<T> items) where T : notnull { foreach (var i in items) return i; return Option.None; }
}

public static class E
{
    public static Result<int> FindInt() => 5; public static Result<User> FindUser() => new User("Ada");
    public static void Int()
    {
        Result<int> x = FindInt(); Result<int> none = Error.NotFound("Int.NotFound");   // absence needs a code
        string m = x switch { int v => $"{v}", Violation => "none" };                  // absence = any failure
        Result<int> mapped = x.Map(v => v * 2); int f = x.GetValueOrDefault(0); int u = x.GetValueOrThrow();
        _ = (none, m, mapped, f, u);
    }
    public static void Usr()
    {
        Result<User> x = FindUser();
        string m = x switch { User v => v.Name, Error { Type: ErrorType.NotFound } => "none", Violation v => v.ToString() };
        Result<string> mapped = x.Map(v => v.Name); User f = x.GetValueOrDefault(new User("Guest")); User u = x.GetValueOrThrow();
        _ = (m, mapped, f, u);
    }
}
