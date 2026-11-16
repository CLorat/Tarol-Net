using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Tarol.Persistence;
using Tarol.Primitives;

namespace Probe.Queries;

// ── User code (candidate D-A): the row is a record, one static/instance When per event; the generator writes the switch ──

/// <summary>[Projection("auction-list", Version = 1)] — a read model row per auction (the API's contract, like an IView).</summary>
public sealed record AuctionRow(Guid Id, string Title, string Seller, DateTimeOffset EndsAt, AuctionStatus Status, decimal HighestBid, int Bids)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m, 0);
    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount, Bids = Bids + 1 };
    public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
}

// ── "Generated" (hand-written here, as the generator would emit it) ──

public sealed class AuctionList(int version = 1) : IProjection<AuctionRow>
{
    public string Name => "auction-list";
    public int Version { get; } = version;
    public string StreamType => "auction";

    public AuctionRow? Apply(AuctionRow? row, PositionedEvent e) => (row, e.Event.Event.Event) switch
    {
        (null, Auction.Opened x) => AuctionRow.When(x, e.Event),
        ({ } r, Auction.BidPlaced x) => r.When(x),
        ({ } r, Auction.Closed x) => r.When(x),
        _ => row,
    };
}

public enum AuctionSort { EndsAt, HighestBidDesc }

/// <summary>[Endpoint("GET /auctions")] — the query record: filters, a whitelisted sort, a cursor or an offset, a bounded limit.</summary>
public sealed record AuctionSearch(AuctionStatus? Status = null, string? Seller = null, AuctionSort Sort = AuctionSort.EndsAt,
    string? After = null, int? Offset = null, int Limit = 50)
{
    public const int MaxLimit = 200;
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? Next, string ETag);

/// <summary>An opaque keyset cursor: sort, a hash of the filters, the last row's sort value and id (base64url text, no JSON, AOT-safe).</summary>
public static class Cursor
{
    public static string Encode(AuctionSort sort, uint filters, string value, Guid id)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{(int)sort}|{filters:x8}|{value}|{id:N}"));

    public static bool TryDecode(string text, AuctionSort sort, uint filters, out string value, out Guid id)
    {
        (value, id) = ("", Guid.Empty);
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(text); } catch (FormatException) { return false; }
        var parts = Encoding.UTF8.GetString(bytes).Split('|');
        if (parts.Length != 4 || parts[0] != ((int)sort).ToString(CultureInfo.InvariantCulture) || parts[1] != filters.ToString("x8", CultureInfo.InvariantCulture))
            return false;                                              // a cursor of another sort or other filters: 400, never a wrong page
        value = parts[2];
        return Guid.TryParseExact(parts[3], "N", out id);
    }

    public static uint Filters(AuctionSearch q) => (uint)Fnv(Encoding.UTF8.GetBytes($"{q.Status}|{q.Seller}"), 14695981039346656037UL);

    private static ulong Fnv(ReadOnlySpan<byte> data, ulong h)
    {
        foreach (var b in data) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    public static string SortValue(AuctionSort sort, AuctionRow r) => sort == AuctionSort.EndsAt
        ? r.EndsAt.UtcTicks.ToString(CultureInfo.InvariantCulture) : r.HighestBid.ToString(CultureInfo.InvariantCulture);

    /// <summary>Weak ETag of a page: a hash of its (id, version) pairs: exact for the page, costs nothing beyond the page.</summary>
    public static string ETag(IEnumerable<(Guid Id, long Version)> rows)
    {
        var h = 14695981039346656037UL;
        Span<byte> b = stackalloc byte[24];
        foreach (var (id, v) in rows) { id.TryWriteBytes(b); BitConverter.TryWriteBytes(b[16..], v); h = Fnv(b, h); }
        return $"W/\"{h:x16}\"";
    }
}
