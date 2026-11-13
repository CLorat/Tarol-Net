using System.Runtime.CompilerServices;

namespace Tarol.Options.Tests;

// The C# 15 union behaviours Option<T> relies on (docs/migration/options.md §3).
public class OptionUnionTests
{
    public sealed record User(string Name);

    [Fact]
    public void Option_Should_BeAUnion() => typeof(IUnion).IsAssignableFrom(typeof(Option<int>)).ShouldBeTrue();

    [Fact] // §3.3: the value plus a flag
    public void Size_Should_BeTheValuePlusAFlag()
    {
        Unsafe.SizeOf<Option<int>>().ShouldBe(8);
        Unsafe.SizeOf<Option<Guid>>().ShouldBe(20);
        Unsafe.SizeOf<Option<string>>().ShouldBe(16);
    }

    [Fact] // §3.3: the non-boxing pattern; also in Debug (the null guard is skipped for value types)
    public void Switch_Should_NotAllocate()
    {
        static int Run()
        {
            var sum = 0;
            for (var i = 0; i < 1000; i++)
            {
                Option<int> option = i % 2 == 0 ? i : Option.None;
                sum += option switch { int v => v, None => 0 };
                sum += option.Map(static v => v + 1).Filter(static v => v > 0).GetValueOrDefault(0);
            }

            return sum;
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        total.ShouldBe(249_500 + 250_000);
        allocated.ShouldBe(0);
    }

    [Fact] // §3.5: default lands in the None arm (no SwitchExpressionException as with a language union)
    public void Default_Should_MatchTheNoneArm()
    {
        Option<User> option = default;

        (option switch { User u => u.Name, None => "none" }).ShouldBe("none");
    }

    [Fact] // N-4: a property or null pattern tests the struct itself (TOPT002 warns)
    public void NullPatterns_Should_TestTheStruct()
    {
        Option<User> none = default;
#pragma warning disable TOPT002 // the test checks the very behaviour TOPT002 warns about
        (none is { }).ShouldBeTrue();
        (none is null).ShouldBeFalse();
#pragma warning restore TOPT002
        (none is User).ShouldBeFalse();
    }

    [Fact] // N-3: the union protocol member
    public void Value_Should_ExposeTheActiveCaseAsObject()
    {
#pragma warning disable TOPT001 // the test checks the very member TOPT001 warns about
        Option.Some(5).Value.ShouldBe(5);
        default(Option<int>).Value.ShouldBe(default(None));
        Option.Some(5).HasValue.ShouldBeTrue();
        default(Option<int>).HasValue.ShouldBeTrue();
#pragma warning restore TOPT001
    }

    [Fact] // §3.9: the trap TOPT004 reports; written as Option.None it is target-typed
    public void ConditionalWithNone_Should_BeTargetTyped()
    {
        static Option<int> Find(bool found, int value) => found ? value : Option.None;

        Find(false, 5).IsNone.ShouldBeTrue();
        Find(true, 0).ShouldBe(Option.Some(0));
    }

    [Fact]
    public void NestedPatterns_Should_Work()
    {
        static string Classify(Option<int> option) => option switch
        {
            int v and > 0 => "positive",
            int => "not positive",
            None => "none",
        };

        Classify(3).ShouldBe("positive");
        Classify(0).ShouldBe("not positive");
        Classify(default).ShouldBe("none");
    }
}
