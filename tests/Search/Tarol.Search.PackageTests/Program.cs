using Tarol.Search;
using Tarol.Search.PackageTests;

var test = SearchTest.Create(s => s.Add<Artwork>());
var art = Artwork.Create("Sunset over harbour", "boats at dusk");
var edition = art.AddEdition("museum edition");
test.Given(art);
await test.CatchUp();

var harbour = await test.Find("harbour");
var museum = await test.Find("museum");
if (!harbour.TryGetValue(out SearchPage? h) || h.Items.Count != 1 || h.Items[0].Id != art.Id.Value
    || !museum.TryGetValue(out SearchPage? m) || m.Items.Count != 1 || m.Items[0].Id != edition.Id.Value || m.Items[0].Aggregate != art.Id.Value)
{
    Console.Error.WriteLine($"Tarol.Search package consumer: unexpected pages {harbour} / {museum}");
    return 1;
}
Console.WriteLine("Tarol.Search package consumer: OK");
return 0;
