using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Primitives.AotSmoke
{
    public partial class Gallery : AggregateRoot<Gallery>
    {
        public EntitySet<Room> Rooms { get; } = [];
        public string Name { get; private set; } = "";

        public static Gallery Open(string name)
        {
            var gallery = new Gallery(Id<Gallery>.New());
            gallery.Raise(new Opened(name));
            return gallery;
        }

        public Room AddRoom(string title) => Raise(new Room.Added(title));

        public sealed record Opened(string Name) : Event;

        private void On(Opened e) => Name = e.Name;
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

    [JsonSerializable(typeof(Artwork.Repriced))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
