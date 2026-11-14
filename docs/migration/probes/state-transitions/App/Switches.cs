namespace ArtFlow.A;

public static class Switches
{
    // complete: no default arm, no CS8524 (suppressed for [Lifecycle] enums), no CS8509
    public static string Label(AuctionStatus s) => s switch
    {
        AuctionStatus.Open => "open",
        AuctionStatus.Closed => "closed",
        AuctionStatus.Settled => "settled",
    };
}
