using Tarol.Validations.Tests.Shop;

namespace Tarol.Validations.Tests;

/// <summary>The valid path allocates nothing (validation.md §8).</summary>
[Collection(nameof(AllocationTests))]
public class AllocationTests
{
    [Fact]
    public void Validating_A_Valid_Input_Allocates_Nothing()
    {
        var order = InputTests.ValidOrder();
        for (var i = 0; i < 100; i++)
            _ = order.Validated();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            _ = order.Validated();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
    }

    [Fact]
    public void Creating_A_Valid_Value_Object_Allocates_Nothing()
    {
        for (var i = 0; i < 100; i++)
            _ = Quantity.Create(5);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            _ = Quantity.Create(5);
            _ = Email.Create("a@b.cd");
            _ = Money.Create(1m, Currency.Eur);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }
}
