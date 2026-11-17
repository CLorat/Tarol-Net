using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Security.Tests;

/// <summary>AZ-D (R-5): the app's role map + row rules, through ASP.NET Core's IAuthorizationService and the pipeline.</summary>
public sealed class AuthorizationTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ServiceProvider _provider;
    private readonly InMemoryAggregateStore _store;
    private readonly ChangePipeline _pipeline;
    private readonly OwnArtworkRule _artworkRule = new();

    public AuthorizationTests()
    {
        var services = new ServiceCollection();
        services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>().Add<Artwork>())
            .UseInMemoryStore()
            .AddSecurity(roles =>
            {
                roles.Role("seller").May<CreateAuction>().May<CloseAuction>().May<AuctionArtwork>();
                roles.Role("admin").May<CloseAuction>().May<Read<Auction>>();
                roles.Role("reader").May<Read<Auction>>();
                roles.Authenticated().May<WatchAuction>();
            });
        services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
        services.AddSingleton<IAuthorizationHandler, ReserveLimitRule>();
        services.AddSingleton<IAuthorizationHandler, ReadOwnAuction>();
        services.AddSingleton<IAuthorizationHandler>(_artworkRule);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _store = _provider.GetRequiredService<InMemoryAggregateStore>();
        _pipeline = _provider.GetRequiredService<ChangePipeline>();
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private static ClaimsPrincipal User(string subject, params string[] roles)
        => new(new ClaimsIdentity([new Claim("sub", subject), .. roles.Select(r => new Claim("role", r))], "test", "sub", "role"));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static OperationContext As(string subject) => OperationContext.Start(Actor.User(subject), Option.None, TimeProvider.System);

    private async Task<Auction> Seeded(string seller = "alice")
    {
        var auction = Auction.Create(seller, 100, TimeProvider.System.GetUtcNow());
        (await _store.Append(auction, 0, As(seller), Option.None, Ct)).IsSuccess.ShouldBeTrue();
        return auction;
    }

    private Task<Result<ChangeOutcome>> Close(Auction auction, ClaimsPrincipal user, Option<long> expected = default)
        => _pipeline.Run(new CloseAuction(auction.Id), As(user.FindFirst("sub")?.Value ?? "anonymous"), new ChangeOptions(ExpectedVersion: expected, Principal: user), Ct).AsTask();

    private static Violation Failure(Result<ChangeOutcome> result)
        => result.TryGetValue(out Violation? violation) ? violation : throw new InvalidOperationException($"Expected a failure, got {result}.");

    [Fact]
    public async Task A_role_that_grants_the_change_allows_it()
        => (await Close(await Seeded(), User("alice", "seller"))).IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task Without_a_granting_role_the_change_is_forbidden()
        => Failure(await Close(await Seeded(), User("alice", "bidder"))).ShouldBe(Error.Forbidden(ChangeAuthorization.ForbiddenCode));

    [Fact]
    public async Task An_anonymous_caller_is_unauthorized()
        => Failure(await Close(await Seeded(), Anonymous)).ShouldBe(Error.Unauthorized(ChangeAuthorization.RequiredCode));

    [Fact]
    public async Task A_row_rule_denies_over_the_roles_grant_with_its_code()
        => Failure(await Close(await Seeded("alice"), User("bob", "seller"))).ShouldBe(Error.Forbidden("Auction.NotSeller"));

    [Fact]
    public async Task A_role_the_row_rule_accepts_passes_both()
        => (await Close(await Seeded("alice"), User("ada", "admin"))).IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task A_denied_caller_with_a_stale_expected_version_gets_403_not_the_version()
        => Failure(await Close(await Seeded("alice"), User("bob", "seller"), 9L)).ShouldBe(Error.Forbidden("Auction.NotSeller"));

    [Fact]
    public async Task A_rule_on_a_creation_sees_the_new_aggregate()
    {
        var small = await _pipeline.Run(new CreateAuction(50_000), As("alice"), new ChangeOptions(Principal: User("alice", "seller")), Ct);
        var large = await _pipeline.Run(new CreateAuction(500_000), As("alice"), new ChangeOptions(Principal: User("alice", "seller")), Ct);
        var dealer = await _pipeline.Run(new CreateAuction(500_000), As("dora"), new ChangeOptions(Principal: User("dora", "seller", "dealer")), Ct);

        small.IsSuccess.ShouldBeTrue();
        Failure(large).ShouldBe(Error.Forbidden("Auction.ReserveLimit"));
        dealer.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_rule_reads_the_changes_read_aggregates_and_time_from_the_requirement()
    {
        var artwork = Artwork.Register("alice");
        (await _store.Append(artwork, 0, As("alice"), Option.None, Ct)).IsSuccess.ShouldBeTrue();

        var own = await _pipeline.Run(new AuctionArtwork(artwork.Id, 10), As("alice"), new ChangeOptions(Principal: User("alice", "seller")), Ct);
        var foreign = await _pipeline.Run(new AuctionArtwork(artwork.Id, 10), As("bob"), new ChangeOptions(Principal: User("bob", "seller")), Ct);

        own.IsSuccess.ShouldBeTrue();
        Failure(foreign).ShouldBe(Error.Forbidden("Artwork.NotOwner"));
        _artworkRule.SawNow.ShouldNotBeNull();
    }

    [Fact]
    public async Task Authenticated_grants_every_signed_in_caller_but_not_an_anonymous_one()
    {
        var auction = await Seeded();

        (await _pipeline.Run(new WatchAuction(auction.Id), As("zoe"), new ChangeOptions(Principal: User("zoe")), Ct)).IsSuccess.ShouldBeTrue();
        Failure(await _pipeline.Run(new WatchAuction(auction.Id), As("x"), new ChangeOptions(Principal: Anonymous), Ct))
            .ShouldBe(Error.Unauthorized(ChangeAuthorization.RequiredCode));
    }

    [Fact]
    public async Task A_list_is_granted_by_the_role_map_only_row_rules_do_not_run()
    {
        var authorization = _provider.GetRequiredService<IChangeAuthorization>();
        var context = new ChangeContext(As("x"), TimeProvider.System.GetUtcNow());

        (await authorization.CheckList<Auction>(context, User("rita", "reader"), Ct)).IsSuccess.ShouldBeTrue();
        (await authorization.CheckList<Auction>(context, User("alice"), Ct))                    // ReadOwnAuction would grant one of hers
            .ShouldBe(Result.Failure(Error.Forbidden(ChangeAuthorization.ForbiddenCode)));
        (await authorization.CheckList<Auction>(context, Anonymous, Ct)).ShouldBe(Result.Failure(Error.Unauthorized(ChangeAuthorization.RequiredCode)));
    }

    [Fact]
    public async Task An_authorization_without_lists_refuses_them()
    {
        IChangeAuthorization legacy = new CheckOnly();
        var context = new ChangeContext(As("x"), TimeProvider.System.GetUtcNow());

        (await legacy.CheckList<Auction>(context, User("rita", "reader"), Ct)).ShouldBe(Result.Failure(Error.Forbidden("Auth.Forbidden")));
        (await legacy.CheckList<Auction>(context, Anonymous, Ct)).ShouldBe(Result.Failure(Error.Unauthorized("Auth.Required")));
    }

    private sealed class CheckOnly : IChangeAuthorization
    {
        public ValueTask<Result> Check<T>(object change, T aggregate, ChangeContext context, ClaimsPrincipal principal, CancellationToken cancellationToken)
            where T : AggregateRoot<T>, IAggregateRoot<T>
            => ValueTask.FromResult(Result.Success());
    }

    [Fact]
    public async Task Read_is_granted_by_a_role_or_by_an_ownership_rule()
    {
        var auction = await Seeded("alice");
        var authorization = _provider.GetRequiredService<IChangeAuthorization>();
        var context = new ChangeContext(As("x"), TimeProvider.System.GetUtcNow());
        async Task<Result> Read(ClaimsPrincipal user) => await authorization.Check(Read<Auction>.Instance, auction, context, user, Ct);

        (await Read(User("rita", "reader"))).IsSuccess.ShouldBeTrue();
        (await Read(User("alice"))).IsSuccess.ShouldBeTrue();                                    // the seller: ownership grant
        (await Read(User("nora"))).ShouldBe(Result.Failure(Error.Forbidden(ChangeAuthorization.ForbiddenCode)));
    }

    [Fact]
    public async Task Internal_runs_are_not_checked_and_a_system_principal_opts_in()
    {
        var auction = await Seeded("alice");
        var job = OperationContext.Start(Actor.System("Jobs.Close"), Option.None, TimeProvider.System);

        (await _pipeline.Run(new TouchAuction(auction.Id), job, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();          // no principal
        Failure(await _pipeline.Run(new TouchAuction(auction.Id), job, new ChangeOptions(Principal: SystemPrincipal.For(job.Actor)), Ct))
            .ShouldBe(Error.Forbidden(ChangeAuthorization.ForbiddenCode));                       // opted in, nothing grants it
        (await _pipeline.Run(new CloseAuction(auction.Id), job, new ChangeOptions(Principal: SystemPrincipal.For(job.Actor, "admin")), Ct))
            .IsSuccess.ShouldBeTrue();                                                          // opted in, granted to its role
    }

    [Fact]
    public void A_system_principal_is_authenticated_with_its_roles()
    {
        var principal = SystemPrincipal.For(Actor.System("Jobs.Refunds"), "refunds");

        principal.Identity!.IsAuthenticated.ShouldBeTrue();
        principal.Identity.AuthenticationType.ShouldBe(SystemPrincipal.AuthenticationType);
        principal.FindFirst("sub")!.Value.ShouldBe("Jobs.Refunds");
        principal.IsInRole("refunds").ShouldBeTrue();
    }

    [Fact]
    public async Task Every_denial_is_counted_with_change_reason_and_actor_kind()
    {
        var measured = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ChangeAuthorization.MeterName && instrument.Name == "tarol.authorization.denied")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags)
                map[tag.Key] = tag.Value;
            if (map["change"] is "TouchAuction")                                                // only this test's change (other tests run in parallel)
                lock (measured)
                    measured.Add((value, map));
        });
        listener.Start();
        var auction = await Seeded();

        _ = await _pipeline.Run(new TouchAuction(auction.Id), As("bob"), new ChangeOptions(Principal: User("bob", "seller")), Ct);

        var (count, tagsOf) = measured.ShouldHaveSingleItem();
        count.ShouldBe(1);
        tagsOf["reason"].ShouldBe(ChangeAuthorization.ForbiddenCode);
        tagsOf["actor.kind"].ShouldBe("User");
    }

    [Fact]
    public async Task The_denial_counter_has_its_unit_and_comes_from_the_hosts_meter_factory()
    {
        using var meters = new Tarol.Testing.Metrics.Measurements();
        var authorization = new ChangeAuthorization(_provider.GetRequiredService<IAuthorizationService>(), meters: meters);
        var auction = await Seeded("alice");

        _ = await authorization.Check(new CloseAuction(auction.Id), auction, new ChangeContext(As("bob"), TimeProvider.System.GetUtcNow()), User("bob", "seller"), Ct);

        var denied = meters.Of("tarol.authorization.denied").ShouldHaveSingleItem();                     // step 15: {request}, IMeterFactory
        (denied.Meter, denied.Unit, denied.Value).ShouldBe((ChangeAuthorization.MeterName, "{request}", 1));
        denied.Tags["change"].ShouldBe("CloseAuction");
    }

    [Fact]
    public async Task Every_denial_is_logged_as_a_warning_without_personal_data()
    {
        var log = new Recording();
        var authorization = new ChangeAuthorization(_provider.GetRequiredService<IAuthorizationService>(), log);
        var auction = await Seeded("alice");

        var result = await authorization.Check(new CloseAuction(auction.Id), auction, new ChangeContext(As("bob"), TimeProvider.System.GetUtcNow()),
            User("bob", "seller"), Ct);

        result.ShouldBe(Result.Failure(Error.Forbidden("Auction.NotSeller")));
        var (level, id, message) = log.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        id.ShouldBe(1001);
        message.ShouldBe($"Refused CloseAuction on Auction {auction.Id.Value} for User bob: Auction.NotSeller");
    }

    [Fact]
    public void AddSecurity_twice_is_refused()
    {
        var services = new ServiceCollection();
        var tarol = services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>()).UseInMemoryStore().AddSecurity(_ => { });

        Should.Throw<InvalidOperationException>(() => tarol.AddSecurity(_ => { })).Message.ShouldContain("twice");
    }

    private sealed class Recording : ILogger<ChangeAuthorization>
    {
        public List<(LogLevel Level, int Id, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }
}
