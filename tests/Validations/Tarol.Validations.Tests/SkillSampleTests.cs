using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

// The example of skills/tarol-validations/SKILL.md, verbatim except for the namespace and the wrapping class.
namespace Tarol.Validations.Tests.SkillSample;

public sealed class Product;

public sealed class Order;

public readonly partial record struct Quantity([Range(1, 100)] int Value);

public readonly partial record struct Title([Length(3, 10)] string Value);

public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;

public sealed partial record PlaceOrder(Id<Order> OrderId, Title Name, [Count(1, 50)] IReadOnlyList<Line> Lines, Option<Title> Note) : IValidatable;

public class SkillSampleTests
{
    public static Result Handle(PlaceOrder command)
    {
        if (command.Validated() is Invalid invalid)
            return invalid;                                   // e.g. OrderId: Required; Lines[1].Quantity: Range "1..100"
        // load (404), permission (403), behaviour guards (409), commit
        return Result.Success();
    }

    [Fact]
    public void The_Skill_Example_Compiles_And_Behaves_As_Documented()
    {
        // tests: no host, no DI
        Quantity.Create(0).ShouldBe(Invalid.Field("", ValidationCodes.Range, "1..100"));

        var line = new Line(Id<Product>.New(), Quantity.Create(3).GetValueOrThrow());
        Handle(new PlaceOrder(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), [line], Option.None)).IsSuccess.ShouldBeTrue();
        Handle(new PlaceOrder(Id<Order>.Empty, Title.Create("Lamp").GetValueOrThrow(), [line], Option.None))
            .ShouldBe(Result.Failure(Invalid.Field("OrderId", ValidationCodes.Required)));
    }
}
