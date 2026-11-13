using Microsoft.EntityFrameworkCore;
using Tarol.Results;

static string S<T>(Option<T> o) where T : notnull => o switch { T v => $"Some({v})", None => "None" };
static async Task<string> TryA(Func<Task<string>> f) { try { return await f(); } catch (Exception e) { return "THROWS " + e.GetType().Name + ": " + e.Message.Split('\n')[0]; } }

await using (var db = new Db()) { await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
    db.People.AddRange(new PersonRow { Id = 1, Nick = "ada", Age = 36 }, new PersonRow { Id = 2 }); await db.SaveChangesAsync(); }
await using var q = new Db();
// read model row keeps T?; the query result (DTO) exposes Option<T>
Console.WriteLine("Select(r => new PersonView(r.Id, r.Nick.ToOption(), r.Age.ToOption())) -> " + await TryA(async () =>
    string.Join("; ", (await q.People.OrderBy(r => r.Id).Select(r => new PersonView(r.Id, r.Nick.ToOption(), r.Age.ToOption())).ToListAsync()).Select(v => $"{v.Id}: {S(v.Nick)} {S(v.Age)}"))));
Console.WriteLine("SQL: " + q.People.Select(r => new PersonView(r.Id, r.Nick.ToOption(), r.Age.ToOption())).ToQueryString().Replace("\n", " "));
Console.WriteLine("filter on the T? column, project to Option: Where(r => r.Age > 30) -> " + await TryA(async () =>
    string.Join("; ", (await q.People.Where(r => r.Age > 30).Select(r => new PersonView(r.Id, r.Nick.ToOption(), r.Age.ToOption())).ToListAsync()).Select(v => $"{v.Id}: {S(v.Nick)}"))));
Console.WriteLine("FirstOrDefaultAsync(...) then ToOption() for a single lookup -> " + await TryA(async () =>
    S((await q.People.Where(r => r.Id == 3).Select(r => new PersonView(r.Id, r.Nick.ToOption(), r.Age.ToOption())).FirstOrDefaultAsync()).ToOption())));

public sealed class PersonRow { public int Id { get; set; } public string? Nick { get; set; } public int? Age { get; set; } }
public sealed record PersonView(int Id, Option<string> Nick, Option<int> Age);
public sealed class Db : DbContext
{
    public DbSet<PersonRow> People => Set<PersonRow>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseNpgsql("Host=127.0.0.1;Port=55432;Username=postgres;Database=tarol_query");
}
