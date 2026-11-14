using System.Text;
using System.Text.Json;
using Tarol.Primitives.Tests.Fixtures;
using Tarol.Primitives.Tests.Galleries;
using Tarol.Primitives.Tests.Nested;
using Tarol.Primitives.Tests.Orders;
using Tarol.Primitives.Tests.Showrooms;

namespace Tarol.Primitives.Tests;

/// <summary>
/// The proof of part B as a property, on a hand-written seeded random walk (no library; primitives.md §16.8, §16.10):
/// for every model, every seed and every prefix of random behaviours, the written state of the live aggregate, of a full
/// replay of its events and of an instance read back from its state (also in jsonb key order) is the same JSON. At random
/// steps the walk continues on the instance read back, so creates, cascades and deletes also run on restored trees.
/// A failure names the model, the seed, the step and the behaviours so far; rerun with that seed to reproduce.
/// </summary>
[Collection(StaticCounters.Name)]
public class StoredStatePropertyTests
{
    private const int Seeds = 150;
    private const int Steps = 40;
    private static readonly JsonSerializerOptions Options = TestJsonOptions.Value;
    private static readonly string[] Words = ["art", "modern", "london", "x", "a longer word", "é"];

    public static TheoryData<string> Models => ["Gallery", "Order", "Station", "Showroom", "Board"];

    [Theory]
    [MemberData(nameof(Models))]
    public void Stored_State_Equals_Replay_For_Every_Prefix(string model)
    {
        for (var seed = 1; seed <= Seeds; seed++)
        {
            switch (model)
            {
                case "Gallery":
                    Walk(seed, model, _ => Gallery.Open("G"), GallerySteps);
                    break;
                case "Order":
                    Walk(seed, model, _ => Order.Place(), OrderSteps);
                    break;
                case "Station":
                    Walk(seed, model, _ => Space.Station.Launch(), StationSteps);
                    break;
                case "Showroom":
                    Walk(seed, model, _ => Showroom.Open("S", DateTimeOffset.UnixEpoch), ShowroomSteps);
                    break;
                case "Board":
                    Walk(seed, model, _ => Board.Create(), BoardSteps);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(model));
            }
        }
    }

    private static void Walk<T>(int seed, string model, Func<Random, T> create, (string Name, Action<T, Random> Run)[] steps)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var random = new Random(seed);
        var live = create(random);
        var committed = new List<StreamEvent>();
        var log = new List<string> { "create" };
        for (var step = 1; step <= Steps; step++)
        {
            var (name, run) = steps[random.Next(steps.Length)];
            log.Add(name);
            run(live, random);

            var history = committed.Concat(live.UncommittedChanges.ToArray()).ToArray();
            var state = Json(live);
            Same(state, Json(T.Rehydrate(live.Id, history)), "a full replay");
            var loaded = AggregateState.Read(live.Id, Encoding.UTF8.GetBytes(state), Options).GetValueOrThrow();
            Same(state, Json(loaded), "the instance read back");
            Same(state, Json(AggregateState.Read(live.Id, Encoding.UTF8.GetBytes(StoredStateTests.JsonbOrder(state)), Options).GetValueOrThrow()), "the instance read back in jsonb key order");

            if (random.Next(3) == 0)
            {
                // commit, and continue on the restored instance
                committed.AddRange(live.UncommittedChanges.ToArray());
                live = loaded;
                log.Add("(load)");
            }

            void Same(string expected, string actual, string what)
            {
                if (expected != actual)
                    throw new InvalidOperationException(
                        $"{model}, seed {seed}, step {step}: the state differs from {what}.{Environment.NewLine}steps: {string.Join(", ", log)}{Environment.NewLine}live: {expected}{Environment.NewLine}{what}: {actual}");
            }
        }
    }

    private static string Json<T>(T aggregate) where T : AggregateRoot<T>, IAggregateRoot<T>
        => Encoding.UTF8.GetString(AggregateState.Write(aggregate, Options));

    private static bool Pick<TItem>(IEnumerable<TItem> items, Random random, out TItem item)
    {
        var list = items.ToList();
        item = list.Count == 0 ? default! : list[random.Next(list.Count)];
        return list.Count > 0;
    }

    private static string Word(Random random) => Words[random.Next(Words.Length)];

    private static readonly (string, Action<Gallery, Random>)[] GallerySteps =
    [
        ("tag", static (g, r) => g.Tag(Word(r))),
        ("add room", static (g, _) => g.AddRoom("Room")),
        ("hang", static (g, r) => { if (Pick(g.Rooms, r, out var room)) room.Hang(Word(r)); }),
        ("reprice", static (g, r) => { if (Pick(g.Rooms.SelectMany(static x => x.Artworks), r, out var a)) a.Reprice(r.Next(0, 1000)); }),
        ("rename", static (g, r) => { if (Pick(g.Rooms.SelectMany(static x => x.Artworks), r, out var a)) a.Rename(Word(r)); }),
        ("remove artwork", static (g, r) => { if (Pick(g.Rooms.SelectMany(static x => x.Artworks), r, out var a)) a.Remove(); }),
        ("close room", static (g, r) => { if (Pick(g.Rooms, r, out var room)) g.CloseRoom(room.Id); }),
    ];

    private static readonly (string, Action<Order, Random>)[] OrderSteps =
    [
        ("add line", static (o, r) => o.Add(Word(r), r.Next(1, 5))),
        ("ship", static (o, r) => { if (!o.Shipping.HasValue) o.ShipTo(Word(r)); }),
        ("cancel shipping", static (o, _) => o.Shipping.Value?.Cancel()),
        ("annotate order", static (o, r) => o.Annotate(Word(r))),
        ("annotate line", static (o, r) => { if (Pick(o.Lines, r, out var line)) line.Annotate(Word(r)); }),
        ("remove line", static (o, r) => { if (Pick(o.Lines, r, out var line)) line.Remove(); }),
        ("delete note", static (o, r) => { if (Pick(o.Notes.Concat(o.Lines.SelectMany(static l => l.Notes)), r, out var note)) note.Delete(); }),
    ];

    private static readonly (string, Action<Space.Station, Random>)[] StationSteps =
    [
        ("dock", static (s, r) => s.Dock(Word(r))),
        ("repair", static (s, r) => { if (Pick(s.Pods, r, out var pod)) pod.Repair(r.Next(1, 9)); }),
        ("open bay", static (s, r) => { if (Pick(s.Pods.Where(static p => !p.Bay.HasValue), r, out var pod)) pod.Open(r.Next(1, 9)); }),
        ("close bay", static (s, r) => { if (Pick(s.Pods.Where(static p => p.Bay.HasValue), r, out var pod)) pod.Bay.Value!.Close(); }),
        ("undock", static (s, r) => { if (Pick(s.Pods, r, out var pod)) pod.Undock(); }),
    ];

    private static readonly (string, Action<Showroom, Random>)[] ShowroomSteps =
    [
        ("relabel", static (s, r) => s.Relabel(r.Next(4) == 0 ? new string('x', 30) : Word(r))), // sometimes invalid: no event
        ("tag", static (s, r) => s.Tag(Word(r))),
        ("motto", static (s, r) => s.SetMotto(r.Next(2) == 0 ? null : Word(r))),
        ("move", static (s, r) => s.Move(Word(r), Word(r))),
        ("count", static (s, r) => s.Count(r.Next(0, 100))),
        ("budget", static (s, r) => s.SetBudget(r.Next(2) == 0 ? null : r.Next(0, 10_000) / 100m)),
        ("add hall", static (s, _) => s.AddHall("Hall")),
        ("light", static (s, r) => { if (Pick(s.Halls, r, out var h)) h.Light((Lighting)r.Next(3)); }),
        ("limit", static (s, r) => { if (Pick(s.Halls, r, out var h)) h.Limit(r.Next(2) == 0 ? null : r.Next(1, 500)); }),
        ("hang", static (s, r) => { if (Pick(s.Halls, r, out var h)) h.Hang(Word(r), r.Next(1, 1000)); }),
        ("annotate hall", static (s, r) => { if (Pick(s.Halls, r, out var h)) h.Annotate(Word(r)); }),
        ("close hall", static (s, r) => { if (Pick(s.Halls, r, out var h)) h.Close(); }),
        ("reprice", static (s, r) => { if (Pick(s.Halls.SelectMany(static h => h.Exhibits), r, out var e)) e.Reprice(r.Next(1, 1000)); }),
        ("prefer", static (s, r) => { if (Pick(s.Halls.SelectMany(static h => h.Exhibits), r, out var e)) e.Prefer(r.Next(2) == 0 ? null : (Lighting)r.Next(3)); }),
        ("reserve", static (s, r) => { if (Pick(s.Halls.SelectMany(static h => h.Exhibits), r, out var e)) e.SetReserve(r.Next(2) == 0 ? null : r.Next(1, 100)); }),
        ("annotate exhibit", static (s, r) => { if (Pick(s.Halls.SelectMany(static h => h.Exhibits), r, out var e)) e.Annotate(Word(r)); }),
        ("remove exhibit", static (s, r) => { if (Pick(s.Halls.SelectMany(static h => h.Exhibits), r, out var e)) e.Remove(); }),
        ("appoint", static (s, r) => { if (!s.Curator.HasValue) s.Appoint(Word(r)); }),
        ("resign", static (s, _) => s.Curator.Value?.Resign()),
        ("summary", static (s, _) => GC.KeepAlive(s.Summary)), // fills the [Transient] cache on one side only
    ];

    private static readonly (string, Action<Board, Random>)[] BoardSteps =
    [
        ("rename", static (b, r) => b.Rename(r.Next(4) == 0 ? "" : Word(r))), // sometimes invalid: no event
    ];

    [Fact]
    public void The_Walk_Reaches_Every_Shape()
    {
        // guard against a walk that never builds deep trees: one seed must reach the deepest level of each model
        var random = new Random(1);
        var showroom = Showroom.Open("S", DateTimeOffset.UnixEpoch);
        var reached = false;
        for (var i = 0; i < 400 && !reached; i++)
        {
            ShowroomSteps[random.Next(ShowroomSteps.Length)].Item2(showroom, random);
            reached = showroom.Halls.Any(static h => h.Exhibits.Any(static e => e.Notes.Count > 0)) && showroom.Curator.HasValue;
        }

        reached.ShouldBeTrue();
    }
}
