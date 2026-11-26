using System.Data.Common;
using Npgsql;
using Tarol.Persistence;
using Tarol.StronglyTypedId;
using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>TN-54 (owner decision P-2): an SQL projection writes the rows of a [Projection(Sql = true)] record; queries read them.</summary>
[Trait("Category", "Integration")]
public sealed class SqlRowsTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Auctions per (tenant, seller), counted in SQL, written as SellerRow rows in the batch transaction.</summary>
    internal sealed class SellerRows(string schema) : ISqlProjection<SellerRow>
    {
        private readonly NpgsqlRows<SellerRow> _rows = new(Postgres.Json, schema);

        public IReadOnlyCollection<string>? StreamTypes => ["auction"];

        public ValueTask Prepare(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
            => _rows.Prepare(connection, transaction, cancellationToken);

        public async ValueTask<int> Apply(IReadOnlyList<PositionedEvent> events, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            var opened = events.Where(e => e.Event.Event.Event is Auction.Opened).ToList();
            if (opened.Count == 0)
                return 0;
            var added = opened.GroupBy(e => (Tenant: TenantOf(e), ((Auction.Opened)e.Event.Event.Event).Seller))
                .Select(g => (g.Key.Tenant, g.Key.Seller, Count: g.Count(), Position: g.Max(e => e.Position.Position))).ToList();
            // the counts so far, read in the batch transaction from the row table's typed column
            var known = new Dictionary<(Guid, Guid), int>();
            await using (var read = new NpgsqlCommand($"SELECT tenant, id, auctions FROM {_rows.Table} WHERE (tenant, id) IN (SELECT * FROM unnest($1::uuid[], $2::uuid[]))",
                             (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
            {
                read.Parameters.Add(new NpgsqlParameter { Value = added.Select(a => a.Tenant).ToArray() });
                read.Parameters.Add(new NpgsqlParameter { Value = added.Select(a => IdOf(a.Seller)).ToArray() });
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    known[(reader.GetGuid(0), reader.GetGuid(1))] = reader.GetInt32(2);
            }
            var rows = added.Select(a =>
            {
                var id = IdOf(a.Seller);
                var total = known.GetValueOrDefault((a.Tenant, id)) + a.Count;
                return new Versioned<SellerRow>(new RowKey(a.Tenant, id), a.Position, new SellerRow(id, a.Seller, total));
            }).ToList();
            return await _rows.Write(rows, connection, transaction, cancellationToken);
        }

        public static Guid IdOf(string seller) => Id<SellerRow>.Derive("seller-rows", seller).Value;

        private static Guid TenantOf(PositionedEvent e) => e.Event.Context.Tenant.TryGetValue(out Id<Tarol.Kernel.ITenant> t) ? t.Value : Guid.Empty;
    }

    [Fact]
    public async Task List_and_row_reads_answer_what_the_sql_projection_wrote_with_its_checkpoint()
    {
        var setup = await postgres.Create();
        await setup.Seed(40, TenantA);
        await setup.Seed(10, TenantB);
        var projection = new SellerRows(setup.Schema);
        var host = new SqlProjectionHost(projection, setup.Store, setup.ReadModels, new ProjectionOptions { Batch = 7 });

        await setup.CatchUp(host);

        // what IQuery<SellerRow> and IRowQuery<SellerRow> endpoints call: the row store and the positions
        var store = setup.ReadModels.Store<SellerRow>();
        var page = (await store.Search(Query(TenantA) with { Sort = "auctions", Descending = true, Count = true }, Ct)).Ok();
        page.Total.ShouldBe(2);
        page.Items.Sum(r => r.Auctions).ShouldBe(40);
        page.Items.Select(r => r.Auctions).ShouldBeInOrder(SortDirection.Descending);
        var ada = await store.Find(new RowKey(TenantA.Value, SellerRows.IdOf("ada")), Ct);
        ada.ShouldNotBeNull().Row.Auctions.ShouldBe(20);
        (await store.Search(Query(TenantB), Ct)).Ok().Items.Sum(r => r.Auctions).ShouldBe(10);   // the tenant's rows only
        var checkpoint = await new NpgsqlProjectionPositions(postgres.DataSource, setup.Schema).Checkpoint<SellerRow>(Ct);
        checkpoint.State.ShouldBe(ProjectionState.Current);
        checkpoint.Position.ShouldBeGreaterThanOrEqualTo(setup.Last);
    }

    [Fact]
    public void NpgsqlRows_refuses_a_row_projections_row()
        => Should.Throw<InvalidOperationException>(() => new NpgsqlRows<AuctionRow>(Postgres.Json)).Message.ShouldContain("Sql = true");
}
