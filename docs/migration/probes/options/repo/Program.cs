using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using NpgsqlTypes;
using Probe;

static void P(string s) => Console.WriteLine(s);
static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "THROWS " + e.GetType().Name + ": " + (e.InnerException?.Message ?? e.Message).Split('\n')[0]; } }
static async Task<string> TryA(Func<Task<string>> f) { try { return await f(); } catch (Exception e) { return "THROWS " + e.GetType().Name + ": " + e.Message.Split('\n')[0]; } }
static string S<T>(Option<T> o) where T : notnull => o switch { T v => $"Some({v})", None => "None" };

const string Cs = "Host=127.0.0.1;Port=55432;Username=postgres;Database=postgres";
await using var ds = NpgsqlDataSource.Create(Cs);
await using (var c = ds.CreateCommand("drop table if exists users; drop table if exists events; create table users(id uuid primary key, nick text null, age int null); create table events(seq bigserial primary key, type text not null, payload jsonb not null)")) await c.ExecuteNonQueryAsync();

P("== raw Npgsql: Option<T> as a parameter value");
Option<string> nick = "ada"; Option<int> age = default;
P("AddWithValue(\"nick\", Option<string>) -> " + await TryA(async () => { await using var c = ds.CreateCommand("insert into users values (gen_random_uuid(), $1, $2)"); c.Parameters.Add(new NpgsqlParameter { Value = nick }); c.Parameters.Add(new NpgsqlParameter { Value = age }); return (await c.ExecuteNonQueryAsync()).ToString(); }));
P("NpgsqlParameter<Option<int>> -> " + await TryA(async () => { await using var c = ds.CreateCommand("insert into users values (gen_random_uuid(), null, $1)"); c.Parameters.Add(new NpgsqlParameter<Option<int>> { TypedValue = 5 }); return (await c.ExecuteNonQueryAsync()).ToString(); }));
P("helper: Value = o.TryGetValue(out v) ? v : DBNull.Value -> " + await TryA(async () => { await using var c = ds.CreateCommand("insert into users values ($1, $2, $3)"); c.Parameters.Add(new NpgsqlParameter { Value = Guid.Parse("00000000-0000-0000-0000-000000000001") }); c.Parameters.Add(new NpgsqlParameter { Value = Db.ToDb(nick) }); c.Parameters.Add(new NpgsqlParameter { Value = Db.ToDb(age) }); return (await c.ExecuteNonQueryAsync()).ToString(); }));
P("== raw Npgsql: reading");
P("reader.GetFieldValue<Option<int>>(i) -> " + await TryA(async () => { await using var c = ds.CreateCommand("select age from users limit 1"); await using var r = await c.ExecuteReaderAsync(); await r.ReadAsync(); return S(r.GetFieldValue<Option<int>>(0)); }));
P("helper: IsDBNull(i) ? None : GetFieldValue<T>(i) -> " + await TryA(async () => { await using var c = ds.CreateCommand("select nick, age from users limit 1"); await using var r = await c.ExecuteReaderAsync(); await r.ReadAsync(); return $"nick={S(Db.GetOption<string>(r, 0))} age={S(Db.GetOption<int>(r, 1))}"; }));

P("== event payloads in jsonb (source-generated context, the step-8 shape)");
var ctx = EventContext.Default;
async Task Append(string type, string json) { await using var c = ds.CreateCommand("insert into events(type, payload) values ($1, $2)"); c.Parameters.Add(new NpgsqlParameter { Value = type }); c.Parameters.Add(new NpgsqlParameter { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb }); await c.ExecuteNonQueryAsync(); }
await Append("v1", JsonSerializer.Serialize(new UserRenamedV1(Guid.Empty, "ada", null), ctx.UserRenamedV1));          // old event: T?
await Append("v2", JsonSerializer.Serialize(new UserRenamed(Guid.Empty, "ada", default, default), ctx.UserRenamed)); // new event: Option<T>
await Append("v0", "{\"id\":\"00000000-0000-0000-0000-000000000000\"}");                                           // older event: properties missing
await using (var c = ds.CreateCommand("select type, payload::text from events order by seq"))
await using (var r = await c.ExecuteReaderAsync())
    while (await r.ReadAsync())
    {
        var json = r.GetString(1);
        var e = JsonSerializer.Deserialize(json, ctx.UserRenamed)!;
        var old = JsonSerializer.Deserialize(json, ctx.UserRenamedV1)!;
        P($"{r.GetString(0)} stored {json} -> as Option: nick={S(e.Nick)} age={S(e.Age)} manager={S(e.Manager)} | as T?: nick={old.Nick ?? "null"} age={old.Age?.ToString() ?? "null"}");
    }

P("== EF Core 10 (Npgsql provider): EF maps private T? fields, the entity exposes Option<T>");
await using (var db = new AppDb())
{
    await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
    db.People.Add(new Person { Id = 1, Nick = "ada", Age = 36 });
    db.People.Add(new Person { Id = 2, Nick = Option.None, Age = Option.None });
    await db.SaveChangesAsync();
}
await using (var db = new AppDb())
{
    P("read back: " + string.Join("; ", (await db.People.OrderBy(p => p.Id).ToListAsync()).Select(p => $"{p.Id}: nick={S(p.Nick)} age={S(p.Age)}")));
    P("raw columns: " + await TryA(async () => { await using var efds = NpgsqlDataSource.Create("Host=127.0.0.1;Port=55432;Username=postgres;Database=tarol_ef"); await using var c = efds.CreateCommand("select \"Id\", nick, age from \"People\" order by \"Id\""); await using var r = await c.ExecuteReaderAsync(); var l = new List<string>(); while (await r.ReadAsync()) l.Add($"{r.GetInt32(0)}: nick={(r.IsDBNull(1) ? "NULL" : r.GetString(1))} age={(r.IsDBNull(2) ? "NULL" : r.GetInt32(2))}"); return string.Join("; ", l); }));
    P("Where(p => p.Age == Option.Some(36)) -> " + await TryA(async () => string.Join(",", await db.People.Where(p => p.Age == Option.Some(36)).Select(p => p.Id).ToListAsync())));
    Option<int> none = default;
    P("Where(p => p.Age == none) -> " + await TryA(async () => string.Join(",", await db.People.Where(p => p.Age == none).Select(p => p.Id).ToListAsync())));
    P("Where(p => p.Age.IsSome) -> " + await TryA(async () => string.Join(",", await db.People.Where(p => p.Age.IsSome).Select(p => p.Id).ToListAsync())));
    P("Where(p => p.Age.GetValueOrDefault(0) > 30) -> " + await TryA(async () => string.Join(",", await db.People.Where(p => p.Age.GetValueOrDefault(0) > 30).Select(p => p.Id).ToListAsync())));
    P("Where(p => EF.Property<int?>(p, \"_age\") > 30) -> " + await TryA(async () => string.Join(",", await db.People.Where(p => EF.Property<int?>(p, "_age") > 30).Select(p => p.Id).ToListAsync())));
    P("SQL for == none: " + Try(() => db.People.Where(p => p.Age == none).ToQueryString().Replace("\n", " ")));
}

public sealed record UserRenamedV1(Guid Id, string? Nick, int? Age);
public sealed record UserRenamed(Guid Id, Option<string> Nick, Option<int> Age, Option<Guid> Manager);
[JsonSerializable(typeof(UserRenamedV1))][JsonSerializable(typeof(UserRenamed))][JsonSerializable(typeof(string))][JsonSerializable(typeof(int))][JsonSerializable(typeof(Guid))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class EventContext : JsonSerializerContext;

static class Db
{
    public static object ToDb<T>(Option<T> o) where T : notnull => o.TryGetValue(out T? v) ? v : DBNull.Value;
    public static Option<T> GetOption<T>(NpgsqlDataReader r, int i) where T : notnull => r.IsDBNull(i) ? Option.None : r.GetFieldValue<T>(i);
}

public sealed class Person
{
    private string? _nick; private int? _age;            // what EF maps
    public int Id { get; set; }
    public Option<string> Nick { get => _nick is null ? Option.None : _nick; set => _nick = value.TryGetValue(out string? v) ? v : null; }
    public Option<int> Age { get => _age is { } a ? a : Option.None; set => _age = value.TryGetValue(out int v) ? v : null; }
}
public sealed class OptionClassConverter<T>() : ValueConverter<Option<T>, T?>(o => o.IsSome ? o.GetValueOrThrow() : null, v => v == null ? default : new Option<T>(v)) where T : class;
public sealed class OptionStructConverter<T>() : ValueConverter<Option<T>, T?>(o => o.IsSome ? o.GetValueOrThrow() : null, v => v.HasValue ? new Option<T>(v.Value) : default) where T : struct;
public sealed class AppDb : DbContext
{
    public DbSet<Person> People => Set<Person>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseNpgsql("Host=127.0.0.1;Port=55432;Username=postgres;Database=tarol_ef");
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
    }
    protected override void OnModelCreating(ModelBuilder m)
    {
        // without this: 23502 null value in column "Nick" violates not-null constraint (a struct property is required by convention)
        m.Entity<Person>().Ignore(p => p.Nick).Ignore(p => p.Age);
        m.Entity<Person>().Property<string?>("_nick").HasColumnName("nick");
        m.Entity<Person>().Property<int?>("_age").HasColumnName("age");
    }
}
