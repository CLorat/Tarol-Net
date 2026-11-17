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

namespace Probe.Operations;

internal static class Lab
{
    public static string Pg => Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=60";
    private static NpgsqlDataSource? _db;
    public static NpgsqlDataSource Db => _db ??= new NpgsqlSlimDataSourceBuilder(Pg).EnableArrays().Build();
    public static readonly TimeProvider Time = TimeProvider.System;
    public static string Runtime => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";
    public static readonly JsonSerializerOptions Json = new(ProbeJson.Default.Options);
    public static readonly Guid TenantA = Guid.Parse("0196b000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0196b000-0000-7000-8000-00000000000b");

    private static int _checks, _failed;

    public static EventCodec Codec() => new EventCodec(Json).Add<Item>();

    public static OperationContext Context(Guid tenant, string user = "alice") => OperationContext.Start(Actor.User(user), (Id<ITenant>)tenant, Time);

    public static async Task<NpgsqlAggregateStore> Store(string schema, bool fresh, NpgsqlDataSource? db = null)
    {
        if (fresh) await Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        var store = new NpgsqlAggregateStore(db ?? Db, Codec(), Time, new NpgsqlStoreOptions { Schema = schema, Reactions = GeneratedReactions.Set });
        await store.EnsureSchema(default);
        return store;
    }

    public static async Task<GlobalPosition> Commit<T>(IAggregateStore store, T aggregate, long expected, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var r = await store.Append(aggregate, expected, context, Option.None, default);
        if (!r.TryGetValue(out GlobalPosition at)) throw new InvalidOperationException("append failed: " + r);
        AggregateChanges.Committed(aggregate);
        return at;
    }

    /// <summary>Commits <paramref name="items"/> items with <paramref name="touches"/> touches each (one commit per item), <paramref name="parallel"/> at a time.</summary>
    public static async Task Seed(IAggregateStore store, int items, int touches, int parallel = 8, Guid? tenant = null)
    {
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(w => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) < items)
            {
                var i = Item.Create(Time.GetUtcNow());
                for (var t = 0; t < touches; t++) _ = i.Touch();
                await Commit(store, i, 0, Context(tenant ?? (next % 5 == 0 ? TenantB : TenantA)));
            }
        })));
    }

    public static async Task Exec(string sql, params object[] args)
    {
        await using var c = Db.CreateCommand(sql);
        c.CommandTimeout = 600;
        foreach (var a in args) c.Parameters.Add(new() { Value = a });
        await c.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(string sql, params object[] args)
    {
        await using var c = Db.CreateCommand(sql);
        c.CommandTimeout = 600;
        foreach (var a in args) c.Parameters.Add(new() { Value = a });
        var v = await c.ExecuteScalarAsync();
        return v is DBNull or null ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>The median time of <paramref name="runs"/> calls (after one warm-up), in ms.</summary>
    public static async Task<double> Median(Func<Task> call, int runs = 15)
    {
        await call();
        var t = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await call();
            t[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(t);
        return t[runs / 2];
    }

    public static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    public static string F(double v) => double.IsNaN(v) ? "–" : v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v >= 10 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture);
    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static void Check(bool ok, string what, string detail = "")
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    }

    public static int Summary(string area)
    {
        Console.WriteLine(_failed == 0 ? $"ALL {_checks} CHECKS PASSED ({area}, {Runtime})" : $"FAIL {_failed} of {_checks} checks ({area}, {Runtime})");
        var r = _failed == 0 ? 0 : 1;
        _checks = _failed = 0;
        return r;
    }

    public static void Info(string text) => Console.WriteLine("INFO " + text);
}
