// What the user writes (in comments) and what the generator would emit (code). Probe only.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
namespace Probe;

// USER: public readonly partial record struct Quantity([Range(1, 100)] int Value);
[JsonConverter(typeof(QuantityJsonConverter))]
public readonly partial record struct Quantity(int Value) : IComparable<Quantity>
{
    public const int Min = 1, Max = 100;
    public static void Check(int value, ref Errors e) { if (!Rules.InRange(value, Min, Max)) e.Add(Codes.Range); }
    public static Result<Quantity> Create(int value) { var e = new Errors(); Check(value, ref e); return e.ToInvalid() is { } i ? i : new Quantity(value); }
    public static implicit operator int(Quantity q) => q.Value;
    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);
    public static bool operator <(Quantity l, Quantity r) => l.Value < r.Value;
    public static bool operator >(Quantity l, Quantity r) => l.Value > r.Value;
    public static bool operator <=(Quantity l, Quantity r) => l.Value <= r.Value;
    public static bool operator >=(Quantity l, Quantity r) => l.Value >= r.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
public sealed class QuantityJsonConverter : JsonConverter<Quantity>
{   // trusting: replay must load values that today's rules reject
    public override Quantity Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetInt32());
    public override void Write(Utf8JsonWriter w, Quantity v, JsonSerializerOptions o) => w.WriteNumberValue(v.Value);
}

// USER: public readonly partial record struct Title([NotEmpty, Length(3, 10)] string Value);
[JsonConverter(typeof(TitleJsonConverter))]
public readonly partial record struct Title(string Value)
{
    public static void Check(string? value, ref Errors e)
    { if (!Rules.NotBlank(value)) { e.Add(Codes.Required); return; } if (!Rules.Length(value, 3, 10)) e.Add(Codes.Length, "3..10"); }
    public static Result<Title> Create(string? value) { var e = new Errors(); Check(value, ref e); return e.ToInvalid() is { } i ? i : new Title(value!); }
    public static implicit operator string(Title t) => t.Value;
    public override string ToString() => Value;
}
public sealed class TitleJsonConverter : JsonConverter<Title>
{
    public override Title Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString()!);
    public override void Write(Utf8JsonWriter w, Title v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

// USER: public readonly partial record struct Email([Pattern(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] string Value);
[JsonConverter(typeof(EmailJsonConverter))]
public readonly partial record struct Email(string Value)
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PatternRegex();
    public static void Check(string? value, ref Errors e) { if (value is null) { e.Add(Codes.Required); return; } if (!Rules.Matches(PatternRegex(), value)) e.Add(Codes.Pattern); }
    public static Result<Email> Create(string? value) { var e = new Errors(); Check(value, ref e); return e.ToInvalid() is { } i ? i : new Email(value!); }
}
public sealed class EmailJsonConverter : JsonConverter<Email>
{
    public override Email Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString()!);
    public override void Write(Utf8JsonWriter w, Email v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

// USER: public readonly partial record struct Evil([Pattern(@"^(a+)+$")] string Value);
public readonly partial record struct Evil(string Value)
{
    [GeneratedRegex(@"^(a+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)] private static partial Regex PatternRegex();
    public static Result<Evil> Create(string? value) { var e = new Errors(); if (!Rules.Matches(PatternRegex(), value)) e.Add(Codes.Pattern); return e.ToInvalid() is { } i ? i : new Evil(value!); }
}

// USER: public readonly partial record struct Money([Range(0, MaxAmount)] decimal Amount, Currency Currency)  (two fields)
public enum Currency { Eur, Usd }
public readonly partial record struct Money(decimal Amount, Currency Currency)
{
    public static void Check(decimal amount, Currency currency, ref Errors e) { if (amount < 0m) e.Add("Amount", Codes.Range, null); }
    public static Result<Money> Create(decimal amount, Currency currency) { var e = new Errors(); Check(amount, currency, ref e); return e.ToInvalid() is { } i ? i : new Money(amount, currency); }
}

public sealed class Order; public sealed class Product;

// USER: public partial record Line(Id<Product> Product, Quantity Quantity);
public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable
{
    public void Validate(ref Errors e)
    { if (Product.IsEmpty) e.Add("Product", Codes.Required, null); e.Enter("Quantity"); Quantity.Check(Quantity.Value, ref e); e.Exit(); }
}

// USER: [ValidatableInput] public partial record PlaceOrder(Id<Order> OrderId, Title Name, Email Contact, IReadOnlyList<Line> Lines, Option<Title> Nickname, Money Price);
public sealed partial record PlaceOrder(Id<Order> OrderId, Title Name, Email Contact, IReadOnlyList<Line> Lines, Option<Title> Nickname, Money Price) : IValidatable
{
    public void Validate(ref Errors e)
    {
        if (OrderId.IsEmpty) e.Add("OrderId", Codes.Required, null);
        e.Enter("Name"); Title.Check(Name.Value, ref e); e.Exit();
        e.Enter("Contact"); Email.Check(Contact.Value, ref e); e.Exit();
        if (Lines is null || Lines.Count is < 1 or > 50) e.Add("Lines", Codes.Count, "1..50");
        else { e.Enter("Lines"); for (var i = 0; i < Lines.Count; i++) { e.EnterIndex(i); if (Lines[i] is { } l) l.Validate(ref e); else e.Add(Codes.Required); e.Exit(); } e.Exit(); }
        if (Nickname is Title nick) { e.Enter("Nickname"); Title.Check(nick.Value, ref e); e.Exit(); }
        e.Enter("Price"); Money.Check(Price.Amount, Price.Currency, ref e); e.Exit();
    }
    public Result<PlaceOrder> Validated() { var e = new Errors(); Validate(ref e); return e.ToInvalid() is { } i ? i : this; }
}

// An event that embeds value objects (the repository path).
public sealed record OrderPlaced(Id<Order> OrderId, Title Name, Quantity Total, Option<Quantity> Bonus, Money Price, IReadOnlyList<Line> Lines);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(PlaceOrder))]
public partial class Ctx : JsonSerializerContext;
