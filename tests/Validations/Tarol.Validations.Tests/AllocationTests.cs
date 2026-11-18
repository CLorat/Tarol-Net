using Tarol.Validations.Tests.Shop;

namespace Tarol.Validations.Tests;

/// <summary>The valid path allocates nothing (validation.md §8).</summary>
/// <remarks>
/// Each test measures several loops and asserts on the least allocating one: a tier-up or a runtime-internal allocation that
/// lands inside one loop under the parallel suite's load is not the code under test allocating.
/// </remarks>
[Collection(nameof(AllocationTests))]
public class AllocationTests
{
    private const int Loops = 5;

    private static long LeastAllocated(Action body)
    {
        for (var i = 0; i < 100; i++)
            body();

        var least = long.MaxValue;
        for (var loop = 0; loop < Loops; loop++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
                body();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    [Fact]
    public void Validating_A_Valid_Input_Allocates_Nothing()
    {
        var order = InputTests.ValidOrder();

        LeastAllocated(() => _ = order.Validated()).ShouldBe(0);
    }

    [Fact]
    public void Creating_A_Valid_Value_Object_Allocates_Nothing()
    {
        LeastAllocated(static () =>
        {
            _ = Quantity.Create(5);
            _ = Email.Create("a@b.cd");
            _ = Money.Create(1m, Currency.Eur);
        }).ShouldBe(0);
    }
}
