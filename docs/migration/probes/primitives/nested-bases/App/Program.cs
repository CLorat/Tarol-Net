using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core;
namespace App;
public partial class Gallery : AggregateRoot<Gallery>
{
    public sealed record Opened(string Name) : Event;
    public void Open(string n) => Raise(new Opened(n));
}
public partial class Artwork : Entity<Gallery, Artwork>
{
    public sealed record Hung(string Name) : Created;
    public sealed record Repriced(decimal Price) : Event;
}
public sealed record ArtworkRenamed(string Name) : Artwork.Event;   // top-level form
public closed record RoomEvent : Entity<Gallery, Artwork>.Event;     // optional user-declared closed family
public sealed record A1 : RoomEvent; public sealed record A2 : RoomEvent;

[JsonSerializable(typeof(Gallery.Opened))][JsonSerializable(typeof(Artwork.Hung))][JsonSerializable(typeof(Artwork.Repriced))][JsonSerializable(typeof(ArtworkRenamed))]
public partial class AppJson : JsonSerializerContext;

public static class Program
{
    static int W(RoomEvent e) => e switch { A1 => 1, A2 => 2 };
    public static void Main()
    {
        var g = new Gallery(); g.Open("Tate");
        Console.WriteLine($"changes={g.Count}");
        var json = JsonSerializer.Serialize(new Artwork.Repriced(12.5m), AppJson.Default.Repriced);
        var back = JsonSerializer.Deserialize(json, AppJson.Default.Repriced);
        Console.WriteLine($"{json} roundtrip={back == new Artwork.Repriced(12.5m)}");
        Console.WriteLine(typeof(Artwork.Hung).BaseType);
        Console.WriteLine(JsonSerializer.Serialize(new ArtworkRenamed("x"), AppJson.Default.ArtworkRenamed) + " " + W(new A2()));
    }
}
