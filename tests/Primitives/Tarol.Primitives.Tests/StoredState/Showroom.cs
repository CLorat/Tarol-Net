using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Tarol.Primitives.Tests.Showrooms;

public enum Lighting { Daylight, Warm, Dim }

public sealed record Address(string Street, string City);

/// <summary>A value object whose rule counts how often it runs (replay safety on state: restoring never validates).</summary>
public readonly partial record struct Label([Length(1, 20)] string Value)
{
    private static int _checks;

    public static int Checks => Volatile.Read(ref _checks);

    private static void Validate(string value, ref ValidationErrors errors)
    {
        _ = value;
        Interlocked.Increment(ref _checks);
        _ = errors.Count;
    }
}

/// <summary>Stored state: Showroom → Hall → Exhibit, an EntityRef (Curator), a child with two owner types (Note), and every member kind.</summary>
public partial class Showroom : AggregateRoot<Showroom>
{
    private readonly List<string> _log = [];
    private decimal? _budget;
    [Transient] private string? _summary;

    public EntitySet<Hall> Halls { get; } = [];
    public EntityRef<Curator> Curator { get; } = new();
    public string Name { get; private set; } = "";
    public List<string> Tags { get; private set; } = [];
    public int ExhibitCount { get; private set; }
    public Option<string> Motto { get; private set; }
    public Address? Address { get; private set; }
    public Label Label { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    [StateName("visitors")] public long VisitorCount { get; private set; }
    public IReadOnlyList<string> Log => _log;
    public decimal? Budget => _budget;
    public string Summary => _summary ??= $"{Name} ({ExhibitCount})";

    public static Showroom Open(string name, DateTimeOffset at, Id<Showroom>? id = null)
    {
        var gallery = new Showroom(id ?? Id<Showroom>.New());
        gallery.Raise(new Opened(name, at));
        return gallery;
    }

    public Result Relabel(string raw)
    {
        var label = Label.Create(raw);
        if (label is Violation violation)
            return violation;
        return Raise(new Relabelled(label.GetValueOrThrow()));
    }

    public Success Tag(string tag) => Raise(new Tagged(tag));
    public Success SetMotto(string? motto) => Raise(new MottoSet(motto));
    public Success Move(string street, string city) => Raise(new Moved(new Address(street, city)));
    public Success Count(long visitors) => Raise(new Counted(visitors));
    public Success SetBudget(decimal? budget) => Raise(new Budgeted(budget));
    public Hall AddHall(string title, Id<Hall>? id = null) => id is { } i ? Raise(new Hall.Added(title), i) : Raise(new Hall.Added(title));
    public Curator Appoint(string name) => Raise(new Curator.Appointed(name));

    public sealed record Opened(string Name, DateTimeOffset At) : Event;
    public sealed record Relabelled(Label Label) : Event;
    public sealed record Tagged(string Tag) : Event;
    public sealed record MottoSet(string? Motto) : Event;
    public sealed record Moved(Address Address) : Event;
    public sealed record Counted(long Visitors) : Event;
    public sealed record Budgeted(decimal? Budget) : Event;

    private void On(Opened e) => (Name, OpenedAt) = (e.Name, e.At);
    private void On(Relabelled e) => Label = e.Label;
    private void On(Tagged e)
    {
        Tags = [.. Tags, e.Tag];
        _log.Add("tag:" + e.Tag);
    }

    private void On(MottoSet e) => Motto = e.Motto.ToOption();
    private void On(Moved e) => Address = e.Address;
    private void On(Counted e) => VisitorCount += e.Visitors;
    private void On(Budgeted e) => _budget = e.Budget;
    private void On(Exhibit.Hung e)
    {
        ExhibitCount++;
        _summary = null; // resetting a [Transient] cache in On is allowed
    }
}

public partial class Hall : Entity<Showroom, Hall>
{
    public EntitySet<Exhibit> Exhibits { get; } = [];
    public EntitySet<Note> Notes { get; } = [];
    public string Title { get; private set; } = "";
    public decimal TotalValue { get; private set; }
    public Lighting Lighting { get; private set; }
    public Option<int> Capacity { get; private set; }
    public double Humidity { get; private set; }

    public Exhibit Hang(string name, decimal price, Id<Exhibit>? id = null)
        => id is { } i ? Raise(new Exhibit.Hung(name, price, Id), i) : Raise(new Exhibit.Hung(name, price, Id));

    public Note Annotate(string text) => Raise(new Note.OnHall(text));
    public Success Light(Lighting lighting) => Raise(new Lit(lighting));
    public Success Limit(int? capacity) => Raise(new Limited(capacity));
    public Success Close() => Raise(new Closed());

    public sealed record Added(string Title) : Created;
    public sealed record Lit(Lighting Lighting) : Event;
    public sealed record Limited(int? Capacity) : Event;
    public sealed record Closed : Deleted;

    private void On(Added e) => Title = e.Title;
    private void On(Lit e) => (Lighting, Humidity) = (e.Lighting, 0.45 + ((int)e.Lighting / 10.0));
    private void On(Limited e) => Capacity = e.Capacity is { } c ? new Option<int>(c) : default;
    private void On(Exhibit.Repriced e) => TotalValue = Exhibits.Sum(static a => a.Price);
}

public partial class Exhibit : Entity<Showroom, Exhibit>
{
    public EntitySet<Note> Notes { get; } = [];
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }
    public Id<Hall> FirstHall { get; private set; }
    public Lighting? Preferred { get; private set; }
    public Option<decimal> Reserve { get; private set; }

    public Success Reprice(decimal price) => Raise(new Repriced(price));
    public Success Prefer(Lighting? lighting) => Raise(new PreferenceSet(lighting));
    public Success SetReserve(decimal? reserve) => Raise(new Reserved(reserve));
    public Note Annotate(string text) => Raise(new Note.OnExhibit(text));
    public Success Remove() => Raise(new Removed());

    public sealed record Hung(string Name, decimal Price, Id<Hall> Hall) : Created;
    public sealed record Repriced(decimal Price) : Event;
    public sealed record PreferenceSet(Lighting? Lighting) : Event;
    public sealed record Reserved(decimal? Reserve) : Event;
    public sealed record Removed : Deleted;

    private void On(Hung e) => (Name, Price, FirstHall) = (e.Name, e.Price, e.Hall);
    private void On(Repriced e) => Price = e.Price;
    private void On(PreferenceSet e) => Preferred = e.Lighting;
    private void On(Reserved e) => Reserve = e.Reserve is { } r ? new Option<decimal>(r) : default;
}

public partial class Curator : Entity<Showroom, Curator>
{
    public string Name { get; private set; } = "";

    public Success Resign() => Raise(new Resigned());

    public sealed record Appointed(string Name) : Created;
    public sealed record Resigned : Deleted;

    private void On(Appointed e) => Name = e.Name;
}

public partial class Note : Entity<Showroom, Note>
{
    public string Text { get; private set; } = "";

    public sealed record OnHall(string Text) : CreatedIn<Hall>;
    public sealed record OnExhibit(string Text) : CreatedIn<Exhibit>;

    private void On(OnHall e) => Text = e.Text;
    private void On(OnExhibit e) => Text = e.Text;
}

/// <summary>Loads by replay: no state code.</summary>
[LoadByReplay]
public partial class Ledger : AggregateRoot<Ledger>
{
    public decimal Total { get; private set; }

    public static Ledger Start() => new(Id<Ledger>.New());

    public Success Book(decimal amount) => Raise(new Booked(amount));

    public sealed record Booked(decimal Amount) : Event;

    private void On(Booked e) => Total += e.Amount;
}
