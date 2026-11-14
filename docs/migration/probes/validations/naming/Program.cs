using ArtFlow.Bidders;
using Experts = ArtFlow.Experts;

var b = new Bidder(new Name("Ada Lovelace"));
var e = new Experts.Expert(new Experts.Name("Grace"));
Console.WriteLine($"{b.Name} {b.Rename("Ada")} | {e.Name} | {Name.Max} vs {Experts.Name.Max}");

namespace ArtFlow.Bidders
{
    public readonly record struct Name(string Value) { public const int Max = 60; public static Name Create(string v) => new(v); public override string ToString() => Value; }
    public sealed class Bidder(Name name)
    {
        public Name Name { get; private set; } = name;               // "Color Color": property and type share the name
        public string Rename(string raw) { Name = Name.Create(raw); return Name.ToString(); }   // static via the type, instance via the property
    }
}
namespace ArtFlow.Experts
{
    public readonly record struct Name(string Value) { public const int Max = 120; public override string ToString() => Value; }
    public sealed class Expert(Name name) { public Name Name { get; } = name; }
}
