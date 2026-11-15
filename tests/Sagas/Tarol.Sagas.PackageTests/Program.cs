using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Sagas;
using Tarol.Sagas.PackageTests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
var test = ReactionTest.At(start, GeneratedReactions.Set, SmokeJson.Default.Options);
var order = Order.Place(start);
_ = await test.Pipeline.Run(new PlaceGiven(order), test.Context());
Check("generated reaction set from the packed generator", GeneratedReactions.Set.Names.Count == 2);
Check("reaction runs", await test.Drain() is [{ Row.Reaction: "ShipOrder", Verdict: Verdict.Done }]);
Check("scheduled reaction runs later", await test.Later(TimeSpan.FromHours(1)).Drain() is [{ Row.Reaction: "CancelUnshipped" }]
                                       && test.Load(order.Id).Shipped);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

internal sealed record PlaceGiven(Order New) : ILoads<Order>
{
    public ValueTask<Tarol.Results.Result<Order>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken) => ValueTask.FromResult<Tarol.Results.Result<Order>>(New);
    public Tarol.Results.Result Execute(Order order, ChangeContext context) => Tarol.Results.Result.Success();
}
