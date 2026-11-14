using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Validations.Tests.Shop;

public sealed class Order;

public sealed class Product;

public enum Currency { Eur, Usd }

public readonly partial record struct Quantity([Range(1, 100)] int Value);

public readonly partial record struct Title([Length(3, 10)] string Value);

public readonly partial record struct Email([Email] string Value);

public readonly partial record struct Zip([Pattern(@"^\d{5}$")] string Value);

public readonly partial record struct Price([Range("0.01", "9999.99")] decimal Value);

public readonly partial record struct Weight([Range(Min = 0)] double Kilograms);

public readonly partial record struct Money([Range(Min = 0)] decimal Amount, Currency Currency);

/// <summary>A custom rule after the attribute rules (V-2).</summary>
public readonly partial record struct Sku([Length(4, 12)] string Value)
{
    private static void Validate(string value, ref ValidationErrors errors)
    {
        if (!value.StartsWith("SKU-", StringComparison.Ordinal))
            errors.Add("Sku.Prefix");
    }
}

public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;

public sealed partial record Address([NotEmpty] string Street, Zip Zip) : IValidatable;

public sealed partial record PlaceOrder(
    Id<Order> OrderId,
    Title Name,
    Email Contact,
    [Count(1, 50)] IReadOnlyList<Line> Lines,
    Option<Title> Nickname,
    Money Total,
    Address Shipping,
    [Length(Max = 200)] string? Comment) : IValidatable;

/// <summary>Per-request limits on primitives (V-1 escape hatch, V-7).</summary>
public sealed partial record SearchOrders([Length(Max = 100)] string Text, [Range(1, 50)] int PageSize, Option<Id<Order>> After) : IValidatable;

/// <summary>An event that embeds value objects (the repository path).</summary>
public sealed record OrderPlaced(Id<Order> OrderId, Title Name, Quantity Total, Option<Quantity> Bonus, Money Price, IReadOnlyList<Line> Lines);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(PlaceOrder))]
public partial class ShopJson : JsonSerializerContext;
