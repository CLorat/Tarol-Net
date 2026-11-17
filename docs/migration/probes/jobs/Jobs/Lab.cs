using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Probe.Jobs;

internal static class Lab
{
    public static string Pg => Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=50";
    private static NpgsqlDataSource? _db;
    public static NpgsqlDataSource Db => _db ??= new NpgsqlSlimDataSourceBuilder(Pg).EnableArrays().Build();
    public static string Runtime => Environment.GetEnvironmentVariable("PROBE_RUNTIME") ?? "JIT";
    /// <summary>This process, as recorded in every run and event (worker name and pid).</summary>
    public static string Me { get; set; } = "main:" + Environment.ProcessId;
    public static readonly JsonSerializerOptions Json = new(ProbeJson.Default.Options);
    public static readonly Guid TenantA = Guid.Parse("0196b000-0000-7000-8000-00000000000a");

    public static EventCodec Codec() => new EventCodec(Json).Add<Tally>().Add<Schedule>();

    /// <summary>A deterministic id per (tenant, job): the Tally and (variant D) the Schedule stream share it.</summary>
    public static Guid StreamOf(Guid tenant, string job)
    {
        Span<byte> b = stackalloc byte[16];
        var h1 = (ulong)StableHash(tenant.ToString("N") + "/" + job);
        var h2 = (ulong)StableHash(job + "/" + tenant.ToString("N"));
        BitConverter.TryWriteBytes(b, h1);
        BitConverter.TryWriteBytes(b[8..], h2);
        b[6] = (byte)((b[6] & 0x0F) | 0x80);                  // version 8 (custom)
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b);
    }

    private static long StableHash(string s)
    {
        unchecked
        {
            long h = (long)14695981039346656037UL;
            foreach (var ch in s) { h ^= ch; h *= 1099511628211L; }
            return h;
        }
    }

    public static NpgsqlStoreOptions StoreOptions(string schema, IOutboxReactions? reactions = null) => new() { Schema = schema, Reactions = reactions };

    public static async Task Exec(string sql, params object[] args)
    {
        await using var c = Db.CreateCommand(sql);
        foreach (var a in args) c.Parameters.Add(new() { Value = a });
        await c.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(string sql, params object[] args)
    {
        await using var c = Db.CreateCommand(sql);
        foreach (var a in args) c.Parameters.Add(new() { Value = a });
        var v = await c.ExecuteScalarAsync();
        return v is DBNull or null ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
    }

    public static async Task<NpgsqlAggregateStore> Store(string schema, bool fresh, IOutboxReactions? reactions = null)
    {
        if (fresh) await Exec($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        var store = new NpgsqlAggregateStore(Db, Codec(), TimeProvider.System, StoreOptions(schema, reactions));
        await store.EnsureSchema(default);
        await Exec($"""
            CREATE TABLE IF NOT EXISTS {schema}.runs (occurrence timestamptz NOT NULL, job text NOT NULL, worker text NOT NULL,
                                                      started_at timestamptz NOT NULL DEFAULT clock_timestamp());
            """);
        return store;
    }

    public static async Task Commit<T>(IAggregateStore store, T aggregate, long expected, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var r = await store.Append(aggregate, expected, context, Option.None, default);
        if (!r.IsSuccess) throw new InvalidOperationException("append failed: " + r);
        AggregateChanges.Committed(aggregate);
    }

    /// <summary>Logs that this process starts the run of an occurrence (outside any transaction): the attempted runs.</summary>
    public static Task LogRun(string schema, DateTimeOffset occurrence, string job)
        => Exec($"INSERT INTO {schema}.runs (occurrence, job, worker) VALUES ($1, $2, $3)", occurrence, job, Me);

    public static OperationContext System(string job, Option<Id<ITenant>> tenant, TimeProvider time)
        => OperationContext.Start(Actor.System("Jobs." + job), tenant, time);

    /// <summary>A time truncated to whole seconds: occurrences must round-trip through timestamptz (µs) unchanged, or the cursor and the key differ.</summary>
    public static DateTimeOffset Second(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Offset);

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

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
