namespace Tarol.StronglyTypedId.Tests;

public class IdCoreTests
{
    [Fact]
    public void Default_And_Empty_Are_The_Same_Value()
    {
        default(Id<UserMarker>).ShouldBe(Id<UserMarker>.Empty);
        Id<UserMarker>.Empty.Value.ShouldBe(Guid.Empty);
        Id<UserMarker>.Empty.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void From_And_Explicit_Casts_Round_Trip()
    {
        var guid = Guid.NewGuid();

        var id = Id<UserMarker>.From(guid);
        id.Value.ShouldBe(guid);

        ((Guid)id).ShouldBe(guid);
        ((Id<UserMarker>)guid).ShouldBe(id);
    }

    [Fact]
    public void Equality_Is_Value_Based()
    {
        var guid = Guid.NewGuid();
        var a = Id<UserMarker>.From(guid);
        var b = Id<UserMarker>.From(guid);
        var c = Id<UserMarker>.From(Guid.NewGuid());

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        (a != c).ShouldBeTrue();
        a.GetHashCode().ShouldBe(guid.GetHashCode());
        a.Equals(guid).ShouldBeTrue(); // Guid convenience overload
        a.Equals(c.Value).ShouldBeFalse();
    }

    [Fact]
    public void Same_Guid_Different_Markers_Are_Distinct_Types_With_Same_Value()
    {
        var guid = Guid.NewGuid();
        var userId = Id<UserMarker>.From(guid);
        var orderId = userId.UnsafeAs<OrderMarker>();

        // Value survives the (deliberate) boundary crossing…
        orderId.Value.ShouldBe(guid);
        // …but the boxed types are unrelated: no accidental cross-type equality.
        userId.Equals(orderId).ShouldBeFalse();
    }

    [Fact]
    public void Comparison_Operators_Agree_With_Guid_Comparison()
    {
        var g1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var g2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var small = Id<UserMarker>.From(g1);
        var large = Id<UserMarker>.From(g2);

        small.CompareTo(large).ShouldBe(g1.CompareTo(g2));
        (small < large).ShouldBeTrue();
        (small <= large).ShouldBeTrue();
        (large > small).ShouldBeTrue();
        (large >= small).ShouldBeTrue();
    }

    [Fact]
    public void NonGeneric_IComparable_Sorts_Nulls_First_And_Rejects_Foreign_Types()
    {
        IComparable comparable = Id<UserMarker>.From(Guid.NewGuid());

        comparable.CompareTo(null).ShouldBe(1);
        Should.Throw<ArgumentException>(() => comparable.CompareTo("not an id"));
        Should.Throw<ArgumentException>(() => comparable.CompareTo(Id<OrderMarker>.Empty));
    }

    [Fact]
    public void New_Produces_Unique_Version7_Guids()
    {
        var a = Id<UserMarker>.New();
        var b = Id<UserMarker>.New();

        a.ShouldNotBe(b);
        a.Value.Version.ShouldBe(7);
        b.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void New_With_Timestamp_Is_Time_Ordered()
    {
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var earlier = Id<UserMarker>.New(t0);
        var later = Id<UserMarker>.New(t0.AddDays(1));

        (earlier < later).ShouldBeTrue();
    }

    [Fact]
    public void ThrowIfEmpty_Reports_The_Callers_Argument_Expression()
    {
        var ownerId = Id<UserMarker>.Empty;

        var ex = Should.Throw<ArgumentException>(() => ownerId.ThrowIfEmpty());

        ex.ParamName.ShouldBe("ownerId"); // CallerArgumentExpression captured the variable name
        ex.Message.ShouldContain(nameof(UserMarker));
    }

    [Fact]
    public void ThrowIfEmpty_Returns_Self_For_Valid_Ids()
    {
        var id = Id<UserMarker>.New();
        id.ThrowIfEmpty().ShouldBe(id);
    }
}
