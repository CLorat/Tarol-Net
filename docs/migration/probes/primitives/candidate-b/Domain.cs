using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.StronglyTypedId;
namespace Eval.B;
public partial class Gallery : AggregateRoot<Gallery>
{
    public EntitySet<Artwork> Artworks { get; } = new();
    public string Name { get; private set; } = "";
    public static Gallery Open(string name) { var g = new Gallery(Id<Gallery>.New()); g.Raise(new Opened(name)); return g; }
    public Artwork Hang(string n) => Raise(new ArtworkHung(n));
    private void On(Opened e) => Name = e.Name;
}
// hand-written closed families (candidate b): one line per entity and lifecycle kind
public closed record GalleryEvent : DomainEvent<Gallery>;
public closed record ArtworkEvent : DomainEvent<Gallery, Artwork>;
public sealed record Opened(string Name) : GalleryEvent;
public sealed record ArtworkHung(string Name) : CreateDomainEvent<Gallery, Gallery, Artwork>;
public sealed record ArtworkRepriced(decimal Price) : ArtworkEvent;
public sealed record ArtworkRenamed(string Name) : ArtworkEvent;
public partial class Artwork : Entity<Gallery, Artwork>
{
    public decimal Price { get; private set; }
    public string Name { get; private set; } = "";
    public void Reprice(decimal p) => Raise(new ArtworkRepriced(p));
    private void On(ArtworkHung e) => Name = e.Name;
    private void On(ArtworkRepriced e) => Price = e.Price;
    private void On(ArtworkRenamed e) => Name = e.Name;
    // user-visible exhaustiveness: the user's own switch over the closed family
    public static string Describe(ArtworkEvent e) => e switch { ArtworkRepriced => "repriced", ArtworkRenamed => "renamed" };
#if MISSING
    public static string Missing(ArtworkEvent e) => e switch { ArtworkRepriced => "repriced" };
#endif
}
[JsonSerializable(typeof(ArtworkRepriced))] internal sealed partial class BJson : JsonSerializerContext;
