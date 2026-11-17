using Tarol.Changes;
using Tarol.Changes.AotSmoke;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var start = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
var test = ChangeTest.At(start, SmokeJson.Default.Options);
test.Store.DefineLookup<Order>("number", o => o.Number);
var friend = Customer.Register(blocked: false);
var blocked = Customer.Register(blocked: true);
test.Given(friend).Given(blocked);

var opened = await test.When(new OpenOrder("O-1"));
Check("creation through the pipeline", opened.Result.IsSuccess && opened.Events.Count == 1);
var order = (Id<Order>)opened.Result.GetValueOrThrow().StreamId;

#pragma warning disable TVAL006 // built as JSON would; the pipeline validates
Check("Invalid before any read", (await test.When(new AddItems(order, new Quantity(0), friend.Id))).Result.TryGetValue(out Violation? invalid) && invalid is Invalid);
#pragma warning restore TVAL006
Check("[Read] aggregate decides", (await test.When(new AddItems(order, Quantity.Create(3).GetValueOrThrow(), blocked.Id))).Result.TryGetValue(out Violation? refused)
                                  && refused is Error { Code: "Customer.Blocked" });
Check("auto-loaded target changes", (await test.When(new AddItems(order, Quantity.Create(3).GetValueOrThrow(), friend.Id))).Result.IsSuccess
                                    && test.Load(order).Items == 3);
Check("ILoads by number", (await test.When(new CloseByNumber("O-1"))).Events is [Order.WasClosed { At: var at }] && at == start);
var dispatched = await test.Pipeline.Dispatch(new CloseByNumber("O-1"), test.Context());
Check("generated Dispatch(object)", dispatched.TryGetValue(out Violation? closed) && closed is Error { Code: "Order.Closed" });
var key = new ChangeOptions(Key: new IdempotencyKey(default, "smoke", "k-1"));
var first = await test.When(new OpenOrder("O-2"), options: key);
var again = await test.When(new OpenOrder("O-2"), options: key);
Check("idempotency key replays", again.Result.TryGetValue(out ChangeOutcome replay) && replay.Replayed && replay.StreamId == first.Result.GetValueOrThrow().StreamId);
Check("recorded context", test.Store.Recorded.All(r => r.Context.Actor.Kind == ActorKind.User));

var wasClosed = test.Store.Recorded.Single(r => r.Event.Event is Order.WasClosed);
Check("generated reaction table and schedule", GeneratedReactions.Set.Table(wasClosed.Event.Event).ToArray() is ["OpenFollowUp"]
                                              && GeneratedReactions.Set.DueAt(wasClosed.Event.Event, "OpenFollowUp") == start);
var reacted = await GeneratedReactions.Set.Run(test.Pipeline, "OpenFollowUp", wasClosed);
var followUp = test.Store.Recorded[^1];
Check("reaction runs as the system, caused by the event", reacted.IsSuccess && followUp.Context.Actor.Id == "Reactions.OpenFollowUp"
                                                         && followUp.Context.CausationId == (Id<ICausation>)wasClosed.EventId);
Check("reaction redelivery replays", (await GeneratedReactions.Set.Run(test.Pipeline, "OpenFollowUp", wasClosed)).TryGetValue(out ChangeOutcome replayed)
                                     && replayed.Replayed);

var nightly = new JobOccurrence("OpenNightlyOrder", new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero),
    TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"), default);
Check("generated job definitions", GeneratedJobs.Set.Definitions is [{ Name: "OpenNightlyOrder", Cron: "0 2 * * *", TimeZone: "Europe/Vienna", Missed: Missed.Latest }]);
var ran = await GeneratedJobs.Set.Run(test.Pipeline, nightly, test.Clock);
var nightlyOpened = test.Store.Recorded[^1];
Check("job runs as the system with the occurrence key", ran.IsSuccess && nightlyOpened.Context.Actor.Id == "Jobs.OpenNightlyOrder"
                                                       && nightlyOpened.Event.Event is Order.Opened { Number: "nightly-20261116" });
Check("job occurrence replays", (await GeneratedJobs.Set.Run(test.Pipeline, nightly, test.Clock)).TryGetValue(out ChangeOutcome rerun) && rerun.Replayed);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
