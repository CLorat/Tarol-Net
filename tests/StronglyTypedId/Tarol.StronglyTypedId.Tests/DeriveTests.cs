namespace Tarol.StronglyTypedId.Tests;

/// <summary>TN-42 (Tatalu FB-F04.1-3): a stream keyed by business data (child × month) needs a deterministic id.</summary>
public sealed class DeriveTests
{
    private sealed class Month;

    private static readonly Guid Child = Guid.Parse("0196f000-0000-7000-8000-000000000001");

    [Fact]
    public void The_same_key_gives_the_same_id_and_any_difference_another()
    {
        var id = Id<Month>.Derive("attendance-month", Child, 2026, 11);

        Id<Month>.Derive("attendance-month", Child, 2026, 11).ShouldBe(id);
        Id<Month>.Derive("attendance-month", Child, 2026, 12).ShouldNotBe(id);
        Id<Month>.Derive("attendance-months", Child, 2026, 11).ShouldNotBe(id);
        Id<Month>.Derive("attendance-month", Child, 2026L, 11).ShouldNotBe(id);           // a long is not an int
        Id<Month>.Derive("ab", "c").ShouldNotBe(Id<Month>.Derive("a", "bc"));              // length-prefixed
        id.Value.Version.ShouldBe(8);
        (id.Value.Variant & 0xC).ShouldBe(0x8);                                          // RFC 9562: 10xx
    }

    [Fact]
    public void The_id_is_stable_across_releases()
        => Id<Month>.Derive("attendance-month", Child, 2026, 11, new DateOnly(2026, 11, 26), "x").Value.ShouldBe(Guid.Parse("7f2337c9-1247-84f8-9236-ed8f8a13f828"));

    [Fact]
    public void An_empty_scope_or_a_default_part_is_refused()
    {
        Should.Throw<ArgumentException>(() => Id<Month>.Derive("", Child));
        Should.Throw<ArgumentException>(() => Id<Month>.Derive("s", default(IdPart)));
    }
}
