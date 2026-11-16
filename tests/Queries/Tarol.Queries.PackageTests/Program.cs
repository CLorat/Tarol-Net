using Tarol.Queries;
using Tarol.Queries.PackageTests;

var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
var test = ProjectionTest<NoteRow>.Create();
for (var i = 0; i < 5; i++)
    test.Given(Note.Write($"note {i}", now.AddMinutes(i)));
await test.CatchUp();

var result = await test.Search(new ListQuery(Guid.Empty) { Sort = "written_at", Descending = true, Limit = 2 });
if (!result.TryGetValue(out Page<NoteRow>? page) || page.Items.Count != 2 || page.Next is null || page.Items[0].Text != "note 4")
{
    Console.Error.WriteLine($"Tarol.Queries package consumer: unexpected page {result}");
    return 1;
}
Console.WriteLine($"Tarol.Queries package consumer: {test.Rows().Count} rows, first page {string.Join(", ", page.Items.Select(r => r.Text))}, ETag {page.ETag}");
return 0;
