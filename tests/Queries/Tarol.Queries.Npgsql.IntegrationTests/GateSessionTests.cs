using Npgsql;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>
/// Tatalu FB-INT3-3: the gate held one connection per projection (42 in Tatalu) and, with the store's pool and the bus's own
/// pool, PostgreSQL refused with 53300 "too many clients". All locks of a gate now share one session.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GateSessionTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Twelve_projections_hold_their_gates_on_one_connection_and_another_process_gets_none_until_released()
    {
        var setup = await postgres.Create();
        var name = $"gate-{Guid.NewGuid():N}";
        await using var db = new NpgsqlSlimDataSourceBuilder(postgres.ConnectionString + $";Application Name={name}").EnableArrays().Build();
        await using var gate = new NpgsqlProjectionGate(db, setup.Schema);
        await using var other = new NpgsqlProjectionGate(postgres.DataSource, setup.Schema);

        var leases = new List<IProjectionGateLease>();
        for (var i = 0; i < 12; i++)
            leases.Add((await gate.TryEnter($"projection-{i}", 1, Ct)).ShouldNotBeNull());

        (await postgres.Scalar($"SELECT count(*) FROM pg_stat_activity WHERE application_name = '{name}'")).ShouldBe(1L);
        gate.Locks.ShouldBe(12);
        (await gate.TryEnter("projection-3", 1, Ct)).ShouldBeNull();                 // a second host of this process: not re-entrant
        (await other.TryEnter("projection-3", 1, Ct)).ShouldBeNull();                // another process
        foreach (var lease in leases)
            (await lease.Held(Ct)).ShouldBeTrue();

        await leases[3].DisposeAsync();

        (await leases[3].Held(Ct)).ShouldBeFalse();
        (await leases[4].Held(Ct)).ShouldBeTrue();
        await using (var taken = (await other.TryEnter("projection-3", 1, Ct)).ShouldNotBeNull())
            (await gate.TryEnter("projection-3", 1, Ct)).ShouldBeNull();
        foreach (var lease in leases)
            await lease.DisposeAsync();
        gate.Locks.ShouldBe(0);
        await using var again = (await other.TryEnter("projection-7", 1, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task When_the_shared_session_ends_every_lease_reports_it_lost_and_the_gate_can_be_entered_again()
    {
        var setup = await postgres.Create();
        var name = $"gate-{Guid.NewGuid():N}";
        await using var db = new NpgsqlSlimDataSourceBuilder(postgres.ConnectionString + $";Application Name={name}").EnableArrays().Build();
        await using var gate = new NpgsqlProjectionGate(db, setup.Schema) { CheckEvery = TimeSpan.Zero };
        var a = (await gate.TryEnter("a", 1, Ct)).ShouldNotBeNull();
        var b = (await gate.TryEnter("b", 1, Ct)).ShouldNotBeNull();

        (await postgres.Scalar($"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = '{name}'")).ShouldBe(1L);

        (await a.Held(Ct)).ShouldBeFalse();
        (await b.Held(Ct)).ShouldBeFalse();
        await using var a2 = (await gate.TryEnter("a", 1, Ct)).ShouldNotBeNull();
        await a.DisposeAsync();                                                         // a lease of the old session releases nothing
        (await a2.Held(Ct)).ShouldBeTrue();
        await b.DisposeAsync();
    }
}
