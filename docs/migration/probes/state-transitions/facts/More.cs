static class More
{
    public static string Full(AuctionStatus s) => s switch
    {
        AuctionStatus.Open => "o", AuctionStatus.Closed => "c", AuctionStatus.Settled => "s",
    };
    public static string FullState(State s) => s switch
    {
        State.Open => "o", State.Closed => "c", State.Settled => "s",
    };
    public static string U(StateU s) => s switch { Open2 => "o", Closed2 c => "c" };
}
public sealed record Open2;
public sealed record Closed2(DateTimeOffset EndTime);
public union StateU(Open2, Closed2);
