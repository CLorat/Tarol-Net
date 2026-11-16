using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Probe.Queries;

internal static class Lab
{
    public static string Pg => Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=60";
    private static NpgsqlDataSource? _db;
    public static NpgsqlDataSource Db => _db ??= new NpgsqlSlimDataSourceBuilder(Pg).EnableArrays().Build();
    public static readonly TimeProvider Time = TimeProvider.System;
    public static string Runtime => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";

    public static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(ProbeJson.Default.Options)).Add<Auction>().Add<Lot>();

    public static OperationContext Context(Guid tenant, string user = "alice") => OperationContext.Start(Actor.User(user), (Id<ITenant>)tenant, Time);

    public static async Task Exec(string sql, NpgsqlDataSource? db = null)
    {
        await using var c = (db ?? Db).CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(string sql, params object[] args)
    {
        await using var c = Db.CreateCommand(sql);
        foreach (var a in args) c.Parameters.Add(new() { Value = a });
        return (T)(await c.ExecuteScalarAsync())!;
    }

    /// <summary>A fresh schema with the store; <paramref name="notify"/> adds a statement trigger on events (stands in for a store option).</summary>
    public static async Task<NpgsqlAggregateStore> Store(string schema, bool notify = false, IOutboxReactions? reactions = null)
    {
        await Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        var store = new NpgsqlAggregateStore(Db, Codec(), Time, new NpgsqlStoreOptions { Schema = schema, Reactions = reactions });
        await store.EnsureSchema(default);
        if (notify)
            await Exec($"""
                CREATE FUNCTION {schema}.feed_notify() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_notify('{schema}_feed', ''); RETURN NULL; END $$;
                CREATE TRIGGER feed_notify AFTER INSERT ON {schema}.events FOR EACH STATEMENT EXECUTE FUNCTION {schema}.feed_notify();
                """);
        return store;
    }

    /// <summary>Appends and returns the commit's feed position (the implementation would return it from Append: RETURNING tx_id).</summary>
    public static async Task<GlobalPosition> Commit<T>(IAggregateStore store, T aggregate, long expected, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var r = await store.Append(aggregate, expected, context, Option.None, default);
        if (!r.IsSuccess) throw new InvalidOperationException("append failed: " + r);
        AggregateChanges.Committed(aggregate);
        if (store is NpgsqlAggregateStore pg)
        {
            await using var c = Db.CreateCommand($"SELECT tx_id::text, max(position) FROM {pg.Schema}.events WHERE stream_id = $1 AND version > $2 GROUP BY tx_id");
            c.Parameters.Add(new() { Value = aggregate.Id.Value });
            c.Parameters.Add(new() { Value = expected });
            await using var rd = await c.ExecuteReaderAsync();
            await rd.ReadAsync();
            return new GlobalPosition(ulong.Parse(rd.GetString(0), CultureInfo.InvariantCulture), rd.GetInt64(1));
        }
        return default;
    }

    public static Task<GlobalPosition> Do<T>(IAggregateStore store, T aggregate, OperationContext context, Func<T, Tarol.Results.Result> act)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var expected = aggregate.Version;
        var r = act(aggregate);
        if (!r.IsSuccess) throw new InvalidOperationException("behaviour failed: " + r);
        return Commit(store, aggregate, expected, context);
    }

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static string Pct(List<double> xs)
    {
        if (xs.Count == 0) return "n/a";
        xs.Sort();
        double P(double p) => xs[Math.Min(xs.Count - 1, (int)Math.Ceiling(p * xs.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"p50 {P(0.50):F2} ms, p99 {P(0.99):F2} ms, max {xs[^1]:F2} ms (n={xs.Count})");
    }
}

internal sealed class Checks(string title)
{
    private int _failed, _passed;
    public void Check(string name, bool ok, string? info = null)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(info is null ? "" : "  (" + info + ")")}");
    }
    public int Done()
    {
        Console.WriteLine(_failed == 0 ? $"ALL {_passed} CHECKS PASSED ({title})" : $"CHECK {_failed} FAILED of {_passed + _failed} ({title})");
        return _failed == 0 ? 0 : 1;
    }
}
