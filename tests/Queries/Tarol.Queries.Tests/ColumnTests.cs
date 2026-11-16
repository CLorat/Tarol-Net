namespace Tarol.Queries.Tests;

public sealed class ColumnTests
{
    private static IReadOnlyList<ProjectionColumn<AuctionRow>> Columns() => Columns<AuctionRow>();

    private static IReadOnlyList<ProjectionColumn<T>> Columns<T>()
        where T : class, IProjection<T>
        => T.Columns;

    private static string Name<T>()
        where T : class, IProjection<T>
        => T.Name;

    [Fact]
    public void The_generated_column_map_has_every_scalar_member_except_the_key()
    {
        Columns().Select(c => (c.Name, c.Member, c.Type, c.Nullable, c.Sortable)).ShouldBe(
        [
            ("title", "Title", ColumnType.Text, false, false),
            ("seller", "Seller", ColumnType.Text, false, false),
            ("ends_at", "EndsAt", ColumnType.TimestampTz, false, true),
            ("status", "Status", ColumnType.Int4, false, false),
            ("highest_bid", "HighestBid", ColumnType.Numeric, false, true),
            ("bids", "Bids", ColumnType.Int4, false, false),
            ("winner", "Winner", ColumnType.Text, true, false),
        ]);
        Name<AuctionRow>().ShouldBe("auction-list");
        Projections.TableOf<AuctionRow>().ShouldBe("rm_auction_list_v1");
    }

    [Fact]
    public void Columns_store_enums_as_numbers_and_times_in_utc_microseconds()
    {
        var endsAt = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        var row = new AuctionRow(Guid.NewGuid(), "t", "s", endsAt, AuctionStatus.Closed, 1.5m, 2, null);

        Columns().Single(c => c.Name == "status").Value(row).ShouldBe(1);
        var stored = (DateTimeOffset)Columns().Single(c => c.Name == "ends_at").Value(row)!;
        stored.Offset.ShouldBe(TimeSpan.Zero);
        stored.UtcTicks.ShouldBe(endsAt.UtcTicks - 7);
        Columns().Single(c => c.Name == "winner").Values([row, row with { Winner = "w" }]).ShouldBe(new string?[] { null, "w" });
        Columns().Single(c => c.Name == "bids").Values([row]).ShouldBeOfType<int[]>();
    }

    [Fact]
    public void Ids_compare_as_PostgreSQL_orders_uuids()
    {
        // .NET's Guid.CompareTo reads the first field little-endian on some versions; PostgreSQL compares the 16 bytes as written
        var low = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var high = Guid.Parse("01000000-0000-0000-0000-000000000000");

        ColumnValues.CompareIds(low, high).ShouldBeLessThan(0);
        ColumnValues.CompareIds(Guid.Parse("ffffffff-0000-0000-0000-000000000000"), Guid.Parse("00000000-0000-0000-0000-0000000000ff")).ShouldBeGreaterThan(0);
        ColumnValues.CompareIds(high, high).ShouldBe(0);
    }

    [Theory]
    [InlineData(ColumnType.Text, "a|b")]
    [InlineData(ColumnType.Int2, (short)-7)]
    [InlineData(ColumnType.Int4, 42)]
    [InlineData(ColumnType.BigInt, 1L << 40)]
    [InlineData(ColumnType.Float8, 0.25)]
    [InlineData(ColumnType.Boolean, true)]
    public void Values_round_trip_through_the_cursor_text(ColumnType type, object value)
    {
        ColumnValues.TryParse(type, ColumnValues.Format(type, value), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(value);
    }

    [Fact]
    public void Times_decimals_and_ids_round_trip_and_bad_text_is_refused()
    {
        var time = ColumnValues.Timestamp(DateTimeOffset.UnixEpoch.AddTicks(123456789));
        ColumnValues.TryParse(ColumnType.TimestampTz, ColumnValues.Format(ColumnType.TimestampTz, time), out var t).ShouldBeTrue();
        t.ShouldBe(time);
        ColumnValues.TryParse(ColumnType.Numeric, ColumnValues.Format(ColumnType.Numeric, 12.345m), out var d).ShouldBeTrue();
        d.ShouldBe(12.345m);
        var id = Guid.NewGuid();
        ColumnValues.TryParse(ColumnType.Uuid, ColumnValues.Format(ColumnType.Uuid, id), out var g).ShouldBeTrue();
        g.ShouldBe(id);
        ColumnValues.TryParse(ColumnType.Int4, "x", out _).ShouldBeFalse();
        ColumnValues.TryParse(ColumnType.TimestampTz, "-1", out _).ShouldBeFalse();
        ColumnValues.TryParse(ColumnType.Boolean, "true", out _).ShouldBeFalse();
    }

    [Fact]
    public void Normalize_converts_filter_values_to_the_stored_type()
    {
        ColumnValues.Normalize(ColumnType.Int4, AuctionStatus.Closed).ShouldBe(1);
        ColumnValues.Normalize(ColumnType.BigInt, 3).ShouldBe(3L);
        ColumnValues.Normalize(ColumnType.Numeric, 2).ShouldBe(2m);
        ColumnValues.Normalize(ColumnType.Text, null).ShouldBeNull();
        ((DateTimeOffset)ColumnValues.Normalize(ColumnType.TimestampTz, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)))!).Offset.ShouldBe(TimeSpan.Zero);
        Should.Throw<ArgumentException>(() => ColumnValues.Normalize(ColumnType.Uuid, "not a guid"));
    }

    [Fact]
    public void A_cursor_round_trips_and_carries_no_readable_structure_the_caller_could_rely_on()
    {
        var id = Guid.NewGuid();
        var cursor = Cursor.Encode("-highest_bid", 0xabcdef, "12.5", id);

        cursor.ShouldNotContain("highest_bid");
        Cursor.TryDecode(cursor, "-highest_bid", 0xabcdef, out var value, out var decoded).ShouldBeTrue();
        (value, decoded).ShouldBe(("12.5", id));
        Cursor.TryDecode(cursor, "highest_bid", 0xabcdef, out _, out _).ShouldBeFalse();
        Cursor.TryDecode(cursor, "-highest_bid", 1, out _, out _).ShouldBeFalse();
        Cursor.TryDecode("%%%", "-highest_bid", 0xabcdef, out _, out _).ShouldBeFalse();
        Cursor.TryDecode(new string('a', 3000), "-highest_bid", 0xabcdef, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_filter_hash_ignores_the_order_of_the_filters_but_not_their_values()
    {
        var a = Cursor.Hash([("status", ColumnType.Int4, 1), ("seller", ColumnType.Text, "ada")]);
        var b = Cursor.Hash([("seller", ColumnType.Text, "ada"), ("status", ColumnType.Int4, 1)]);

        a.ShouldBe(b);
        Cursor.Hash([("seller", ColumnType.Text, "bob"), ("status", ColumnType.Int4, 1)]).ShouldNotBe(a);
        Cursor.Hash([("seller", ColumnType.Text, null)]).ShouldNotBe(Cursor.Hash([("seller", ColumnType.Text, "")]));
    }
}
