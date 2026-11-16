using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries.Npgsql;
using Tarol.StronglyTypedId;

namespace Probe.Caching;

internal static class Lab
{
    public static string Pg => Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=100";
    public static string Redis => Environment.GetEnvironmentVariable("PROBE_REDIS") ?? "localhost:56379";
    public static string Garnet => Environment.GetEnvironmentVariable("PROBE_GARNET") ?? "localhost:56380";
    private static NpgsqlDataSource? _db;
    public static NpgsqlDataSource Db => _db ??= new NpgsqlSlimDataSourceBuilder(Pg).EnableArrays().Build();
    public static readonly TimeProvider Time = TimeProvider.System;
    public static string Runtime => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";
    public static readonly JsonSerializerOptions Json = new(ProbeJson.Default.Options);
    public static readonly Guid TenantA = Guid.Parse("0196b000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0196b000-0000-7000-8000-00000000000b");

    public static EventCodec Codec() => new EventCodec(Json).Add<Auction>().Add<Ledger>();

    public static NpgsqlStoreOptions Options(string schema) => new()
    {
        Schema = schema,
        NotifyFeed = true,
        Lookups = new StoreLookups().Define<Auction>("lot", a => a.Lot.Length == 0 ? null : a.Lot),
    };

    public static OperationContext Context(Guid tenant, string user = "alice") => OperationContext.Start(Actor.User(user), (Id<ITenant>)tenant, Time);

    public static async Task Exec(string sql)
    {
        await using var c = Db.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(string sql, params object[] args)
    {
        await using var c = Db.CreateCommand(sql);
        foreach (var a in args) c.Parameters.Add(new() { Value = a });
        return (T)(await c.ExecuteScalarAsync())!;
    }

    /// <summary>A store in <paramref name="schema"/>; <paramref name="fresh"/> drops the schema first.</summary>
    public static async Task<NpgsqlAggregateStore> Store(string schema, bool fresh)
    {
        if (fresh) await Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        var store = new NpgsqlAggregateStore(Db, Codec(), Time, Options(schema));
        await store.EnsureSchema(default);
        return store;
    }

    public static NpgsqlReadModels ReadModels(string schema) => new(Db, Json, schema);

    /// <summary>The checkpoint row's update NOTIFYs (a trigger standing in for a host option): the API process can keep the checkpoint pushed.</summary>
    public static Task CheckpointTrigger(string schema) => Exec($"""
        CREATE OR REPLACE FUNCTION {schema}.checkpoint_notify() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN PERFORM pg_notify('{schema}_checkpoint', NEW.name || ':' || NEW.version || ':' || NEW.tx::text || ':' || NEW.pos); RETURN NULL; END $$;
        DROP TRIGGER IF EXISTS checkpoint_notify ON {schema}.projections;
        CREATE TRIGGER checkpoint_notify AFTER UPDATE ON {schema}.projections FOR EACH ROW EXECUTE FUNCTION {schema}.checkpoint_notify();
        """);

    public static async Task<GlobalPosition> Commit<T>(IAggregateStore store, T aggregate, long expected, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var r = await store.Append(aggregate, expected, context, Option.None, default);
        if (!r.TryGetValue(out GlobalPosition at)) throw new InvalidOperationException("append failed: " + r);
        AggregateChanges.Committed(aggregate);
        return at;
    }

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    public static double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;

    public static string Pct(List<double> xs, string unit = "ms")
    {
        if (xs.Count == 0) return "n/a";
        xs.Sort();
        double P(double p) => xs[Math.Min(xs.Count - 1, Math.Max(0, (int)Math.Ceiling(p * xs.Count) - 1))];
        var f = unit == "us" ? "F1" : "F3";
        return string.Create(CultureInfo.InvariantCulture, $"p50 {P(0.50).ToString(f, CultureInfo.InvariantCulture)} {unit}, p99 {P(0.99).ToString(f, CultureInfo.InvariantCulture)} {unit}, max {xs[^1].ToString(f, CultureInfo.InvariantCulture)} {unit} (n={xs.Count})");
    }

    public static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);
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
        Console.WriteLine(_failed == 0 ? $"ALL {_passed} CHECKS PASSED ({title}, {Lab.Runtime})" : $"CHECK {_failed} FAILED of {_passed + _failed} ({title}, {Lab.Runtime})");
        return _failed == 0 ? 0 : 1;
    }
}
