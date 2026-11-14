using Tarol.Results;

// V-7 and finding 9: two value objects with the same name in different namespaces; each is generated.
namespace Tarol.Validations.Tests.Bidders;

public readonly partial record struct Name([Length(3, 60)] string Value);

/// <summary>"Color Color": a property and its type share the name.</summary>
public sealed class Bidder(Name name)
{
    public Name Name { get; private set; } = name;

    public Result Rename(string raw)
    {
        var created = Name.Create(raw);
        if (created is Violation violation)
            return violation;

        Name = created.GetValueOrThrow();
        return Result.Success();
    }
}
