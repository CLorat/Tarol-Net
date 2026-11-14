using Tarol.StronglyTypedId;

namespace Tarol.Validations.Tests.Fixtures;

/// <summary>A record class value object: <c>null</c> is <c>Required</c>.</summary>
public sealed partial record Iban([Length(15, 34)] string Value);

/// <summary>Finding 7: an internal value object.</summary>
internal readonly partial record struct InternalCode([Length(2, 2)] string Value);

/// <summary>Finding 8: a value object nested in a partial class.</summary>
public static partial class Catalog
{
    public readonly partial record struct Tag([Length(1, 20)] string Value);
}

/// <summary>Finding 16: a catastrophic pattern with a short timeout.</summary>
public readonly partial record struct Evil([Pattern(@"^(a+)+$", TimeoutMilliseconds = 50)] string Value);

/// <summary>Finding 21: [NotEmpty] on a Guid compiles and works.</summary>
public readonly partial record struct ExternalRef([NotEmpty] Guid Value);

/// <summary>Finding 4: several rules on one value give one error, the first failing.</summary>
public readonly partial record struct Code([NotEmpty, Length(3, 5), Pattern("^[A-Z]+$", IgnoreCase = true)] string Value);

/// <summary>Finding 3: a value object next to DataAnnotations; only Tarol attributes are read (TVAL007 is tested in the generator tests).</summary>
public sealed partial record Signup(Code Code, [Email] string Mail, [Range(18, 130)] int? Age) : IValidatable;

/// <summary>Replay fixture: its custom rule counts how often validation runs.</summary>
public readonly partial record struct CountedName([Length(1, 10)] string Value)
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

public sealed class Item;

/// <summary>Elements of every supported kind.</summary>
public sealed partial record Basket(Id<Item>[] Items, IReadOnlyList<string> Notes, List<Tarol.Validations.Tests.Shop.Quantity> Quantities) : IValidatable;
