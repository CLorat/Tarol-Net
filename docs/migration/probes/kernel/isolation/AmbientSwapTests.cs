// The same swap with the AsyncLocal clock: only this test's flow sees the fixed time.
public class C_SwapsAmbientClock
{
    [Fact]
    public async Task Swap()
    {
        using var _ = AmbientClock.SetProvider(Fixed.Clock());
        Assert.Equal(2000, new Event().AmbientStamp.Year);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2000, new Event().AmbientStamp.Year);  // survives await
        Assert.Equal(2000, await Task.Run(() => new Event().AmbientStamp.Year, TestContext.Current.CancellationToken)); // flows into Task.Run
    }
}

// Where can a test set the ambient clock? xUnit v3 calls these in different async flows.
public class D_WhereToSetTheAmbientClock : IAsyncLifetime
{
    private readonly int _seenInCtor;
    public D_WhereToSetTheAmbientClock()
    {
        AmbientClock.SetProvider(Fixed.Clock()); // not disposed: does it reach the test method?
        _seenInCtor = new Event().AmbientStamp.Year;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Constructor()
    {
        var year = new Event().AmbientStamp.Year;
        Console.WriteLine($"RESULT set-in-constructor: ctor={_seenInCtor} test={year}");
    }
}

public class E_SetInInitializeAsync : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await Task.Yield();
        AmbientClock.SetProvider(Fixed.Clock());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void InitializeAsync_()
    {
        Console.WriteLine($"RESULT set-in-InitializeAsync(async): test={new Event().AmbientStamp.Year}");
    }
}

public class F_SetInSyncInitializeAsync : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        AmbientClock.SetProvider(Fixed.Clock());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void SyncInitializeAsync()
    {
        Console.WriteLine($"RESULT set-in-InitializeAsync(sync): test={new Event().AmbientStamp.Year}");
    }
}

// Injected: nothing ambient, nothing shared.
public class G_Injected
{
    private sealed class Order(TimeProvider clock) { public DateTimeOffset PlacedAt { get; } = clock.GetUtcNow(); }

    [Fact]
    public void Injected()
    {
        var clock = Fixed.Clock();
        Assert.Equal(Fixed.Y2000, new Order(clock).PlacedAt);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(Fixed.Y2000.AddDays(1), new Order(clock).PlacedAt);
    }
}
