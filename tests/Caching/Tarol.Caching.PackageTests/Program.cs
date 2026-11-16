using Tarol.Caching;
using Tarol.Caching.PackageTests;
using Tarol.Persistence;
using Tarol.Queries;

var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
var inner = new InMemoryReadModels();
using var cache = new MemoryPageCache();
var store = new CachedReadModels(inner, new ProjectionPositions(inner), cache).Store<NoteRow>();
var rows = Enumerable.Range(0, 3)
    .Select(i => new Versioned<NoteRow>(new RowKey(Guid.Empty, Guid.CreateVersion7(now.AddMinutes(i))), 1, new NoteRow(Guid.CreateVersion7(now.AddMinutes(i)), $"note {i}", now.AddMinutes(i))))
    .ToList();
await store.Write(new ProjectionBatch<NoteRow>(rows, GlobalPosition.Start, new GlobalPosition(1, 3), ProjectionState.Current), default);

var query = new ListQuery(Guid.Empty) { Sort = "written_at", Descending = true };
var first = await store.Search(query, default);
var second = await store.Search(query, default);
if (!first.TryGetValue(out Page<NoteRow>? page) || !second.TryGetValue(out Page<NoteRow>? again) || !ReferenceEquals(page, again) || page.Items[0].Text != "note 2")
{
    Console.Error.WriteLine($"Tarol.Caching package consumer: unexpected pages {first} / {second}");
    return 1;
}
Console.WriteLine($"Tarol.Caching package consumer: {page.Items.Count} rows, the second search a hit, {cache.Count} entry of {cache.Bytes} bytes");
return 0;
