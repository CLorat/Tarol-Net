using System.Text.Json;
using Probe;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

static void P(string s) => Console.WriteLine(s);
static string Show<T>(Result<T> r) where T : notnull => r switch { T v => $"ok {v}", Violation v => v.ToString()! };

P("== math");
Quantity a = Quantity.Create(40).GetValueOrThrow(), b = Quantity.Create(70).GetValueOrThrow();
int sum = a + b; P($"a + b = {sum} (int; 110 is out of range, so it is not a Quantity)");
P($"a + 1 = {a + 1}, a * 2 = {a * 2}, a > b = {a > b}, a < 50 = {a < 50}, Math.Max(a, b) = {Math.Max(a, b)}");
var lines = new[] { new Line(Id<Product>.New(), a), new Line(Id<Product>.New(), b) };
P($"Sum(l => l.Quantity) = {lines.Sum(l => l.Quantity)}; Max = {lines.Max(l => l.Quantity)}; OrderBy works: {string.Join(",", lines.OrderBy(l => l.Quantity).Select(l => l.Quantity))}");
P($"Quantity.Create(a + b) = {Show(Quantity.Create(a + b))}");
var m = Money.Create(10.5m, Currency.Eur).GetValueOrThrow(); P($"money.Amount * 3 = {m.Amount * 3}");
P($"sizeof Quantity = {System.Runtime.CompilerServices.Unsafe.SizeOf<Quantity>()}, Title = {System.Runtime.CompilerServices.Unsafe.SizeOf<Title>()}, Money = {System.Runtime.CompilerServices.Unsafe.SizeOf<Money>()}");

P("== factories");
P($"Quantity.Create(0) = {Show(Quantity.Create(0))}");
P($"Title.Create(\"\") = {Show(Title.Create(""))}; Title.Create(\"a much too long title\") = {Show(Title.Create("a much too long title"))}");
P($"Email.Create(\"nope\") = {Show(Email.Create("nope"))}");
var sw = System.Diagnostics.Stopwatch.StartNew();
P($"Evil.Create(30*a+!) = {Show(Evil.Create(new string('a', 30) + "!"))} in {sw.ElapsedMilliseconds} ms (GeneratedRegex, 100 ms timeout caught)");

P("== repository serialization (source-generated context)");
var ev = new OrderPlaced(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), a, Option.Some(b), m, lines);
var json = JsonSerializer.Serialize(ev, Ctx.Default.OrderPlaced); P(json);
var back = JsonSerializer.Deserialize(json, Ctx.Default.OrderPlaced)!;
P($"round trip equal (scalars) = {back.Total == ev.Total && back.Bonus == ev.Bonus && back.Price == ev.Price && back.Name == ev.Name}");
var noBonus = JsonSerializer.Serialize(ev with { Bonus = Option.None }, Ctx.Default.OrderPlaced); P($"None -> {noBonus.Substring(noBonus.IndexOf("\"bonus\""), 12)}");
var old = """{"orderId":"0195f3a4-0000-7000-8000-000000000001","name":"A title that is far too long","total":500,"bonus":null,"price":{"amount":-1,"currency":0},"lines":[]}""";
var loaded = JsonSerializer.Deserialize(old, Ctx.Default.OrderPlaced)!;
P($"old event under stricter rules loads: name='{loaded.Name}', total={loaded.Total}, price={loaded.Price.Amount}");

P("== command validation (collect all, input paths)");
var bad = JsonSerializer.Deserialize("""{"orderId":"00000000-0000-0000-0000-000000000000","name":"x","contact":"nope","lines":[{"product":"0195f3a4-0000-7000-8000-000000000001","quantity":3},{"product":"00000000-0000-0000-0000-000000000000","quantity":0}],"nickname":"n","price":{"amount":-2,"currency":0}}""", Ctx.Default.PlaceOrder)!;
P(Show(bad.Validated()));
var missing = JsonSerializer.Deserialize("""{"orderId":"0195f3a4-0000-7000-8000-000000000001","lines":[{"product":"0195f3a4-0000-7000-8000-000000000001","quantity":3}],"price":{"amount":1,"currency":0}}""", Ctx.Default.PlaceOrder)!;
P($"missing name/contact -> {Show(missing.Validated())}");
var good = new PlaceOrder(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), Email.Create("a@b.cd").GetValueOrThrow(), lines, Option.None, m);

P("== allocations (1000 iterations after warm-up)");
long Alloc(Action act) { for (int i = 0; i < 100; i++) act(); var s = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) act(); return (GC.GetAllocatedBytesForCurrentThread() - s) / 1000; }
P($"Quantity.Create valid: {Alloc(() => Quantity.Create(5))} B; invalid: {Alloc(() => Quantity.Create(0))} B");
P($"Email.Create valid: {Alloc(() => Email.Create("a@b.cd"))} B");
P($"PlaceOrder.Validated valid: {Alloc(() => good.Validated())} B; invalid (7 errors): {Alloc(() => bad.Validated())} B");
