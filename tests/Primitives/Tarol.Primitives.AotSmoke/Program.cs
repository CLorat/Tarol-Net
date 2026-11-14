using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Primitives.AotSmoke;
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

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

Check("running as native code", !RuntimeFeature.IsDynamicCodeSupported);

var gallery = Gallery.Open("Tate");
var room = gallery.AddRoom("Turner");
var artwork = room.Hang("Rain");
artwork.Reprice(1000m);
gallery.AddRoom("Closed").Close();
var history = gallery.UncommittedChanges.ToArray();

var replayed = Gallery.Rehydrate(gallery.Id, history);
Check("replay rebuilds the tree, ancestor handler included", replayed.Rooms.Single().TotalValue == 1000m && replayed.Version == gallery.Version);
Check("events are pure payload", history[3].Event == new Artwork.Repriced(1000m));
Check("aliases", Gallery.EventTypeOf(history[3].Event) == new EventType("gallery.artwork.repriced", 1));

var json = JsonSerializer.Serialize((Artwork.Repriced)history[3].Event, SmokeJson.Default.Repriced);
Check($"same-assembly JSON context: {json}", JsonSerializer.Deserialize(json, SmokeJson.Default.Repriced) == history[3].Event);

Check("invariants at the hand-off", AggregateChanges.Prepare(gallery).IsSuccess);
artwork.Reprice(-1);
Check("invariant violation", AggregateChanges.Prepare(gallery) == Result.Failure(Error.Conflict("Artwork.NegativePrice")));

room.Close();
Check("cascade", gallery.Rooms.Count == 0 && !((IRegistry<Artwork>)gallery).TryFind(artwork.Id, out _));
Check("zombie update throws", Throws(() => artwork.Reprice(1)));
Check("foreign history throws", Throws(() => Gallery.Rehydrate(Id<Gallery>.New(), history)));

var test = AggregateTest.Given(Gallery.Open("Given")).When(static g => Result.Success(g.AddRoom("A")));
Check("given/when/then helper", test.Events.SequenceEqual([new Room.Added("A")]));

// ── stored state (part B) ──
var options = SmokeJson.Default.Options;
var shown = Gallery.Open("Shown");
shown.Retag(["modern", "london"]);
shown.SetMood(Mood.Calm);
shown.SetMotto("Look closer");
var hall = shown.AddRoom("Hall");
hall.Hang("Weather").Reprice(10m);
shown.Feature(hall.Id);
var state = AggregateState.Write(shown, options);
Console.WriteLine("state: " + System.Text.Encoding.UTF8.GetString(state));
Check("state == state of a full replay", state.AsSpan().SequenceEqual(AggregateState.Write(Gallery.Rehydrate(shown.Id, shown.UncommittedChanges), options)));
var restored = AggregateState.Read(shown.Id, state, options).GetValueOrThrow();
Check("read(write(x)) writes the same state", state.AsSpan().SequenceEqual(AggregateState.Write(restored, options)));
Check("members restored (list, enum, Option, Id, nested child)", restored.Tags.Count == 2 && restored.Mood == Mood.Calm && restored.Motto.IsSome && restored.Featured == hall.Id && restored.Rooms[0].Artworks[0].Price == 10m);
restored.Rooms[0].Close();
Check("a restored aggregate continues: versions, cascade", restored.UncommittedChanges[0].Version == shown.Version + 1 && restored.Rooms.Count == 0);
var otherSchema = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(state).Replace($"\"schema\":{Gallery.StateSchema}", "\"schema\":1", StringComparison.Ordinal));
Check("another schema reads as None", AggregateState.Read(shown.Id, otherSchema, options).IsNone);
Check("the state helper", AggregateTest.StoredStateMatchesReplay(shown, options).Version == shown.Version);
Check("[LoadByReplay]: schema 0, no state", Tally.StateSchema == 0 && Throws(() => AggregateState.Write(Tally.Start(), options)));

// ── lifecycles (part C) ──
var lot = Lot.List("Rain");
var bid = lot.Bid(10m);
Check("guards: CanTransitionTo, RequireTransitionTo", lot.Status.CanTransitionTo(LotStatus.Sold) && lot.Sell().IsSuccess
    && lot.Sell() == Result.Failure(Error.Conflict("LotStatus.NotAllowed", "Sold -> Sold")));
Check("valid steps prepare", AggregateChanges.Prepare(lot).IsSuccess);
AggregateChanges.Committed(lot);
lot.RelistWithoutGuard();
Check("a forgotten guard is reported at commit", AggregateChanges.Prepare(lot)
    == Result.Failure(Error.Internal("Lifecycle.ForbiddenStep", "Lot.Status: Sold -> Listed by Relisted")));
bid.Withdraw();
Check("entity lifecycle: a withdrawn bid cannot be withdrawn again", bid.Withdraw() == Result.Failure(Error.Conflict("Bid.NotActive")));
var oldLot = Lot.Rehydrate(lot.Id, History2(lot));
Check("replay of a forbidden historical step loads", oldLot.Status == LotStatus.Listed && AggregateChanges.Prepare(oldLot).IsSuccess);
var lotState = AggregateState.Write(oldLot, options);
Check($"stored state holds the state's name: {System.Text.Encoding.UTF8.GetString(lotState)}", System.Text.Encoding.UTF8.GetString(lotState).Contains("\"status\":\"Listed\"", StringComparison.Ordinal));
var restoredLot = AggregateState.Read(oldLot.Id, lotState, options).GetValueOrThrow();
Check("a restored state continues with the step check", restoredLot.Sell().IsSuccess && AggregateChanges.Prepare(restoredLot).IsSuccess);
Check("diagram", LotStatusLifecycle.Diagram.Contains("Listed --> Sold : Sold", StringComparison.Ordinal));
Check("label switch over a lifecycle without a default arm (TPRMSPR01)", Lot.Label(LotStatus.Sold) == "sold");

static StreamEvent[] History2(Lot lot)
{
    // Sold -> Listed is forbidden today: a stream as stored in 2025
    var id = lot.Id.Value;
    return
    [
        new(1, id, Guid.Empty, new Lot.Listed("Rain")),
        new(2, id, Guid.Empty, new Lot.Sold()),
        new(3, id, Guid.Empty, new Lot.Relisted()),
    ];
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Primitives.AotSmoke
{
    public partial class Gallery : AggregateRoot<Gallery>
    {
        public EntitySet<Room> Rooms { get; } = [];
        public string Name { get; private set; } = "";
        public List<string> Tags { get; private set; } = [];
        public Mood Mood { get; private set; }
        public Option<string> Motto { get; private set; }
        public Id<Room>? Featured { get; private set; }

        public static Gallery Open(string name)
        {
            var gallery = new Gallery(Id<Gallery>.New());
            gallery.Raise(new Opened(name));
            return gallery;
        }

        public Room AddRoom(string title) => Raise(new Room.Added(title));

        public Success Retag(List<string> tags) => Raise(new Retagged(tags));

        public Success SetMood(Mood mood) => Raise(new MoodSet(mood));

        public Success SetMotto(string motto) => Raise(new MottoSet(motto));

        public Success Feature(Id<Room> room) => Raise(new Featuring(room));

        public sealed record Opened(string Name) : Event;
        public sealed record Retagged(List<string> Tags) : Event;
        public sealed record MoodSet(Mood Mood) : Event;
        public sealed record MottoSet(string Motto) : Event;
        public sealed record Featuring(Id<Room> Room) : Event;

        private void On(Opened e) => Name = e.Name;
        private void On(Retagged e) => Tags = [.. e.Tags];
        private void On(MoodSet e) => Mood = e.Mood;
        private void On(MottoSet e) => Motto = new Option<string>(e.Motto);
        private void On(Featuring e) => Featured = e.Room;
    }

    public enum Mood { Busy, Calm }

    [LoadByReplay]
    public partial class Tally : AggregateRoot<Tally>
    {
        public int Count { get; private set; }

        public static Tally Start() => new(Id<Tally>.New());

        public Success Add() => Raise(new Added());

        public sealed record Added : Event;

        private void On(Added e) => Count++;
    }

    public partial class Room : Entity<Gallery, Room>
    {
        public EntitySet<Artwork> Artworks { get; } = [];
        public string Title { get; private set; } = "";
        public decimal TotalValue { get; private set; }

        public Artwork Hang(string name) => Raise(new Artwork.Hung(name));

        public Success Close() => Raise(new Closed());

        public sealed record Added(string Title) : Created;
        public sealed record Closed : Deleted;

        private void On(Added e) => Title = e.Title;
        private void On(Artwork.Repriced e) => TotalValue = Artworks.Sum(static a => a.Price);
    }

    public partial class Artwork : Entity<Gallery, Artwork>
    {
        public string Name { get; private set; } = "";
        public decimal Price { get; private set; }

        public Success Reprice(decimal price) => Raise(new Repriced(price));

        public sealed record Hung(string Name) : Created;
        public sealed record Repriced(decimal Price) : Event;

        private void On(Hung e) => Name = e.Name;
        private void On(Repriced e) => Price = e.Price;

        [Invariant]
        private Result PriceNotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Artwork.NegativePrice");
    }

    [Lifecycle]
    public enum LotStatus
    {
        [To(Sold)] Listed,
        Sold,
    }

    [Lifecycle]
    public enum BidStatus
    {
        [To(Withdrawn)] Active,
        Withdrawn,
    }

    public partial class Lot : AggregateRoot<Lot>
    {
        public LotStatus Status { get; private set; }
        public EntitySet<Bid> Bids { get; } = [];

        public string Title { get; private set; } = "";

        public static Lot List(string title)
        {
            var lot = new Lot(Id<Lot>.New());
            lot.Raise(new Listed(title));
            return lot;
        }

        public static string Label(LotStatus status) => status switch
        {
            LotStatus.Listed => "listed",
            LotStatus.Sold => "sold",
        };

        public Bid Bid(decimal amount) => Raise(new Bid.Placed(amount));

        public Result Sell() => Status.RequireTransitionTo(LotStatus.Sold).Bind(() => Raise(new Sold()));

        public Success RelistWithoutGuard() => Raise(new Relisted());

        public sealed record Listed(string Title) : Event;

        [Transition<LotStatus>(LotStatus.Listed, LotStatus.Sold)]
        public sealed record Sold : Event;

        public sealed record Relisted : Event;

        private void On(Listed e) => Title = e.Title;

        private void On(Sold e)
        {
            _ = e;
            Status = LotStatus.Sold;
        }

        private void On(Relisted e)
        {
            _ = e;
            Status = LotStatus.Listed;
        }
    }

    public partial class Bid : Entity<Lot, Bid>
    {
        public decimal Amount { get; private set; }
        public BidStatus Status { get; private set; }

        public Result Withdraw() => Status.CanTransitionTo(BidStatus.Withdrawn) ? Raise(new Withdrawn()) : Error.Conflict("Bid.NotActive");

        public sealed record Placed(decimal Amount) : Created;
        public sealed record Withdrawn : Event;

        private void On(Placed e) => Amount = e.Amount;

        private void On(Withdrawn e)
        {
            _ = e;
            Status = BidStatus.Withdrawn;
        }
    }

    [JsonSerializable(typeof(Artwork.Repriced))]
    [JsonSerializable(typeof(List<string>))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
