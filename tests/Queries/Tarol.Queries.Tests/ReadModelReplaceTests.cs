namespace Tarol.Queries.Tests;

/// <summary>Step 16C: <c>Replace</c> and <c>Remove</c> of the in-memory read store (parity with Npgsql).</summary>
public sealed class ReadModelReplaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuctionRow Row(Guid id, string seller) => new(id, "Lamp", seller, DateTimeOffset.UnixEpoch, AuctionStatus.Open, 0m, 0, null);

    [Fact]
    public async Task Replace_Rewrites_At_The_Same_Version_Remove_Deletes_And_The_Checkpoint_Stays()
    {
        var store = new InMemoryReadModelStore<AuctionRow>();
        var key = new RowKey(Guid.CreateVersion7(), Guid.CreateVersion7());
        (await store.Write(new ProjectionBatch<AuctionRow>([new Versioned<AuctionRow>(key, 3, Row(key.Id, "ada"))], default, default, ProjectionState.Current), Ct)).ShouldBeTrue();
        var checkpoint = await store.Checkpoint(Ct);

        await store.Replace([new Versioned<AuctionRow>(key, 3, Row(key.Id, "(erased)"))], Ct);
        (await store.Find(key, Ct)).ShouldNotBeNull().Row.Seller.ShouldBe("(erased)");
        (await store.Remove([key, new RowKey(key.Tenant, Guid.CreateVersion7())], Ct)).ShouldBe(1);

        (await store.Find(key, Ct)).ShouldBeNull();
        (await store.Checkpoint(Ct)).ShouldBe(checkpoint);
    }
}
