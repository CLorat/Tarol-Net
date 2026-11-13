// Reproduces finding 14: one class swaps the global DomainClock, the other class creates events at the same time.
public class A_SwapsStaticClock
{
    [Fact]
    public async Task Swap()
    {
        Assert.True(await Overlap.Meet(), "classes did not run in parallel");
        var original = DomainClock.Provider;
        DomainClock.Provider = Fixed.Clock();
        try { await Task.Delay(300, TestContext.Current.CancellationToken); }
        finally { DomainClock.Provider = original; }
    }
}

public class B_ReadsClockWhileOtherSwaps
{
    [Fact]
    public async Task StaticClock_IsPolluted_AmbientClock_IsNot()
    {
        Assert.True(await Overlap.Meet(), "classes did not run in parallel");
        int pollutedStatic = 0, pollutedAmbient = 0, n = 0;
        var end = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < end)
        {
            var e = new Event();
            n++;
            if (e.StaticStamp.Year == 2000) pollutedStatic++;
            if (e.AmbientStamp.Year == 2000) pollutedAmbient++;
            await Task.Yield();
        }
        TestContext.Current.SendDiagnosticMessage($"events={n} staticPolluted={pollutedStatic} ambientPolluted={pollutedAmbient}");
        Console.WriteLine($"RESULT events={n} staticPolluted={pollutedStatic} ambientPolluted={pollutedAmbient}");
        Assert.True(pollutedStatic > 0, "expected the static clock to leak into this class");
        Assert.Equal(0, pollutedAmbient);
    }
}
