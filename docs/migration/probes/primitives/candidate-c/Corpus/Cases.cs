using C.Core;
using Tarol.Results;
using Tarol.StronglyTypedId;
namespace C.Domain;

public readonly record struct Money(decimal Amount)
{
    public static Result<Money> Create(decimal amount) => amount >= 0 ? new Money(amount) : Error.Conflict("neg");
    public Result<Money> Add(Money other) => new Money(Amount + other.Amount);   // pure, returns Result
}

public partial class Shop : AggregateRoot<Shop>
{
    private Shop(Id<Shop> id) : base(id) { Name = "init"; }                         // ctor: allowed
    protected override void Dispatch(in StreamEvent se) { switch (se.Event) { case Priced e: On(e); break; case Noted e: On(e); break; case Totalled e: On(e); break; case Timed e: On(e); break; case Raised e: On(e); break; } }

    public string Name { get; private set; }
    public decimal Price { get; private set; }
    public Money Total { get; private set; }
    public int Count { get; private set; }
    public DateTimeOffset At { get; private set; }
    private string? _cache;

    // ---- true positives (expected) ----
    public void RenameDirect(string n) => Name = n;                                 // TP1 state outside apply
    public void Bump() => Count++;                                                  // TP2 increment outside apply
    private void On(Priced e) { Price = Money.Create(e.Price).IsSuccess ? e.Price : 0; }   // TP3 rule in apply
    private void On(Noted e) { if (e.Note.Length > 10) throw new InvalidOperationException(); Name = e.Note; } // TP4 throw in apply
    private void On(Timed e) => At = TimeProvider.System.GetUtcNow();               // TP5 clock in apply
    private void On(Raised e) => Raise(new Noted("x"));                             // TP6 raise in apply

    // ---- should stay silent ----
    private void On(Totalled e) { Recalculate(e.Amount); }                          // N1 helper only called from On
    private void Recalculate(decimal a) { Price = a; Count = Count + 1; }
    public string Describe() { var local = Name; local += "!"; return local; }       // N2 local only
    public Result Price2(decimal p) => p < 0 ? Error.Conflict("neg") : Raise(new Priced(p));   // N3 behaviour with guard

    // ---- likely false positives ----
    public string Cached => _cache ??= Name.ToUpperInvariant();                    // FP1 lazy cache in a getter
    private void On2(Totalled e) => Total = Total.Add(new Money(e.Amount)).Value is Money m ? m : default;  // not an On name: ignored; see FP2 below

    // ---- likely false negatives ----
    public static void Sneak(Shop other) => other.Name = "x";                       // FN1 write through another instance

    public sealed record Priced(decimal Price) : Event;
    public sealed record Noted(string Note) : Event;
    public sealed record Totalled(decimal Amount) : Event;
    public sealed record Timed : Event;
    public sealed record Raised : Event;
}

public partial class Till : AggregateRoot<Till>
{
    private Till(Id<Till> id) : base(id) { }
    protected override void Dispatch(in StreamEvent se) { if (se.Event is Added e) On(e); }
    public Money Total { get; private set; }
    private void On(Added e) => Total = Total.Add(new Money(e.Amount)).Value is Money m ? m : default;      // FP2 pure Result-returning arithmetic in apply
    private void On2(Added e) => ArgumentOutOfRangeException.ThrowIfNegative(e.Amount); // FN2 (not an On name, and a throw helper is not detected anyway)
    public sealed record Added(decimal Amount) : Event;
}
