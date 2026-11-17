using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Operations.Tests;

/// <summary>Signals whose values the test sets, and that can fail on demand.</summary>
public sealed class Knobs
{
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    public bool Fail { get; set; }

    public int Reads { get; private set; }

    public OperationsSignal Signal(string name, string unit = "s", string? tag = null)
        => new(OperationsSignals.Meter, name, unit, name, (_, _) =>
        {
            Reads++;
            if (Fail)
                throw new TimeoutException("the database does not answer");
            return Task.FromResult<IReadOnlyList<SignalValue>>(Values.TryGetValue(name, out var v) ? [new SignalValue(v, tag, tag is null ? null : "auction-list")] : []);
        });
}

/// <summary>Dead letters in memory, of one kind, with the acts it saw.</summary>
public sealed class FakeSource(string kind, int count, Id<ITenant>? tenant = null) : IDeadLetterSource
{
    private readonly List<DeadLetterItem> _items = [.. Enumerable.Range(1, count).Select(i => new DeadLetterItem(i, kind, i % 2 == 0 ? "Even" : "Odd",
        tenant is { } t && i % 3 == 0 ? t : Option.None, $"s/{i}", 10, "boom", DateTimeOffset.UnixEpoch.AddSeconds(i)))];

    public List<(string Act, long Id, string Actor)> Acts { get; } = [];

    public string Kind => kind;

    public Task<DeadLetterPage> List(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        lock (_items)
        {
            var rows = _items.Where(i => i.Id > query.After && (query.Name is null || i.Name == query.Name) && (query.Tenant.IsNone || i.Tenant == query.Tenant))
                .Take(query.Limit).ToList();
            return Task.FromResult(new DeadLetterPage(rows, rows.Count == query.Limit ? rows[^1].Id : null));
        }
    }

    public Task<bool> Redrive(long id, string actor, CancellationToken cancellationToken) => Act("redrive", id, actor);

    public Task<bool> Discard(long id, string actor, CancellationToken cancellationToken) => Act("discard", id, actor);

    private Task<bool> Act(string act, long id, string actor)
    {
        lock (_items)
        {
            var removed = _items.RemoveAll(i => i.Id == id) == 1;
            if (removed)
                Acts.Add((act, id, actor));
            return Task.FromResult(removed);
        }
    }
}

/// <summary>Read-model versions in memory; <c>v2</c> is the newest, v1 superseded, v3 is "still read".</summary>
public sealed class FakeReadModels : IReadModelAdmin
{
    public List<(string Name, int Version, string Actor)> Dropped { get; } = [];

    public Task<IReadOnlyList<ProjectionVersion>> Versions(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ProjectionVersion>>([new("auction-list", 1, "current", 10, 8192, true), new("auction-list", 2, "current", 10, 8192, false)]);

    public Task DropVersion(string name, int version, string actor, CancellationToken cancellationToken)
    {
        if (version == 2)
            throw new InvalidOperationException("the newest version");
        if (version == 3)
            throw new TimeoutException("still read");
        Dropped.Add((name, version, actor));
        return Task.CompletedTask;
    }
}

/// <summary>Authenticates from the headers <c>X-User</c> and <c>X-Role</c> (tests only).</summary>
public sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Name = "header";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-User"] is not [{ } user])
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Name, user) };
        if (Request.Headers["X-Role"] is [{ } role])
            claims.Add(new Claim(ClaimTypes.Role, role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Name)), Name)));
    }
}

public static class Services
{
    public static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Tarol with AddOperations over the knobs' signals, a fake clock and the given sources.</summary>
    public static ServiceCollection With(Knobs knobs, FakeTimeProvider time, Action<OperationsOptions>? configure = null, params IDeadLetterSource[] sources)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        foreach (var name in new[] { OperationsSignals.ReactionsOldestDue, OperationsSignals.ReactionsDead, OperationsSignals.JobsOverdue, OperationsSignals.JobsDead,
                     OperationsSignals.MessagesDead })
            services.AddSingleton(knobs.Signal(name, name.EndsWith("dead", StringComparison.Ordinal) ? "{row}" : "s"));
        services.AddSingleton(knobs.Signal(OperationsSignals.ProjectionsLag, tag: "tarol.projection"));
        foreach (var source in sources)
            services.AddSingleton(source);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = OperationsJson.Default };
        _ = new TarolBuilder(services, json, new EventCodec(json)).AddOperations(configure);
        return services;
    }
}
