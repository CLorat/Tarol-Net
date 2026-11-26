using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Persistence;
using Tarol.Queries;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-54 (owner decision P-2): list and row endpoints over the rows an SQL projection writes.</summary>
public sealed class SqlRowEndpointTests : IAsyncLifetime
{
    private static readonly Guid Ada = Guid.Parse("0193a000-0000-7000-8000-0000000000ad");
    private static readonly Guid Bob = Guid.Parse("0193a000-0000-7000-8000-0000000000b0");

    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    // what the SQL projection's batch writes: rows and the checkpoint, Current
    private async Task Written(ProjectionState state = ProjectionState.Current)
    {
        var store = _app.App.Services.GetRequiredService<IReadModels>().Store<SellerTotalRow>();
        var batch = new ProjectionBatch<SellerTotalRow>(
        [
            new(new RowKey(TenantA, Ada), 3, new SellerTotalRow(Ada, "ada", 7)),
            new(new RowKey(TenantA, Bob), 4, new SellerTotalRow(Bob, "bob", 9)),
            new(new RowKey(TenantB, Ada), 5, new SellerTotalRow(Ada, "ada", 1)),
        ], GlobalPosition.Start, new GlobalPosition(1, 5), state);
        (await store.Write(batch, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private static string[] Sellers(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("seller").GetString()!)];

    [Fact]
    public async Task The_list_and_the_row_of_an_sql_projection_answer_with_the_aggregates_grant_and_tenant()
    {
        await Written();

        var list = await _app.Get("/seller-totals", Rita);
        list.Status.ShouldBe(HttpStatusCode.OK, list.Body);
        Sellers(list).ShouldBe(["bob", "ada"]);                                          // [Descending] Auctions
        Sellers(await _app.Get("/seller-totals?seller=ada", Rita)).ShouldBe(["ada"]);
        Sellers(await _app.Get("/seller-totals", Carol)).ShouldBe(["ada"]);               // tenant B's row only
        (await _app.Get("/seller-totals", Nobody)).Status.ShouldBe(HttpStatusCode.Unauthorized);

        var row = await _app.Get($"/seller-totals/{Bob}", Rita);
        row.Status.ShouldBe(HttpStatusCode.OK, row.Body);
        JsonElement.Parse(row.Body).GetProperty("auctions").GetInt32().ShouldBe(9);
        (await _app.Get($"/seller-totals/{Bob}", Carol)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task While_the_sql_projection_builds_the_list_answers_503()
    {
        await Written(ProjectionState.Building);

        var list = await _app.Get("/seller-totals", Rita);

        list.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, list.Body);
        list.Body.ShouldContain(ChangeEndpoints.BuildingCode);
    }
}
