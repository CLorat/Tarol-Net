using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;

namespace Tarol.Security.Tests;

public sealed record AuctionCard;

public sealed record AuctionAccounts;

/// <summary>Only on weekdays (a row rule for one view): denies the accounts view to everyone on a Sunday.</summary>
public sealed class AccountsOnWeekdays : ViewHandler<AuctionAccounts, Auction>
{
    protected override Task HandleView(AuthorizationHandlerContext context, ChangeRequirement requirement, Auction aggregate)
    {
        if (requirement.Context.Now.DayOfWeek == DayOfWeek.Sunday)
            Deny(context, "Auction.AccountsClosed");
        return Task.CompletedTask;
    }
}

/// <summary>TN-43 (VA-2): grants and row rules per view.</summary>
public sealed class ViewAccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Sunday = new(2026, 11, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Monday = Sunday.AddDays(1);

    private static ClaimsPrincipal User(string subject, params string[] roles)
        => new(new ClaimsIdentity([new Claim("sub", subject), .. roles.Select(r => new Claim("role", r))], "test", "sub", "role"));

    private static async Task<Result> Read(IChangeAuthorization authorization, ClaimsPrincipal user, Type view, bool restricted, DateTimeOffset now)
    {
        var auction = Auction.Create("alice", 100, now);
        var context = new ChangeContext(OperationContext.Start(Actor.User(user.FindFirst("sub")!.Value), Option.None, TimeProvider.System), now);
        return await authorization.Check(Read<Auction>.Instance, auction, context, user, new ReadScope(view, restricted), Ct);
    }

    [Fact]
    public async Task Read_grants_unrestricted_views_MayRead_one_view_and_a_view_rule_runs_for_its_view_only()
    {
        var services = new ServiceCollection();
        services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>()).UseInMemoryStore().AddSecurity(roles =>
        {
            roles.Role("reader").May<Read<Auction>>();
            roles.Role("accountant").MayRead<Auction, AuctionAccounts>();
        });
        services.AddSingleton<IAuthorizationHandler, AccountsOnWeekdays>();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IChangeAuthorization>();
        var reader = User("rita", "reader");
        var accountant = User("anna", "accountant");

        (await Read(authorization, reader, typeof(AuctionCard), restricted: false, Monday)).IsSuccess.ShouldBeTrue();
        (await Read(authorization, reader, typeof(AuctionAccounts), restricted: true, Monday)).IsSuccess.ShouldBeFalse();
        (await Read(authorization, accountant, typeof(AuctionAccounts), restricted: true, Monday)).IsSuccess.ShouldBeTrue();
        (await Read(authorization, accountant, typeof(AuctionCard), restricted: false, Monday)).IsSuccess.ShouldBeFalse();
        var sunday = await Read(authorization, accountant, typeof(AuctionAccounts), restricted: true, Sunday);
        sunday.TryGetValue(out Violation? closed).ShouldBeTrue();
        closed.ShouldBe(Error.Forbidden("Auction.AccountsClosed"));
        (await Read(authorization, reader, typeof(AuctionCard), restricted: false, Sunday)).IsSuccess.ShouldBeTrue();   // another view
    }
}

/// <summary>TN-43 (VA-3): whether anything could grant an intent, asked once per endpoint at startup.</summary>
public sealed class GrantCoverageTests
{
    [Fact]
    public async Task The_role_map_and_the_row_rules_cover_intents_and_nothing_else_does()
    {
        var services = new ServiceCollection();
        services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>()).UseInMemoryStore().AddSecurity(roles =>
        {
            roles.Role("seller").May<CreateAuction>();
            roles.Role("accountant").MayRead<Auction, AuctionAccounts>();
            roles.Authenticated().May<WatchAuction>();
        });
        services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
        services.AddSingleton<IAuthorizationHandler, AccountsOnWeekdays>();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IChangeAuthorization>();

        authorization.MayBeGranted(typeof(CreateAuction), null).ShouldBe(true);           // a role
        authorization.MayBeGranted(typeof(WatchAuction), null).ShouldBe(true);            // authenticated
        authorization.MayBeGranted(typeof(CloseAuction), null).ShouldBe(true);            // a row rule
        authorization.MayBeGranted(typeof(TouchAuction), null).ShouldBe(false);           // nobody
        authorization.MayBeGranted(typeof(Read<Auction>), new ReadScope(typeof(AuctionAccounts), true)).ShouldBe(true);
        authorization.MayBeGranted(typeof(Read<Auction>), new ReadScope(typeof(AuctionCard), false)).ShouldBe(false);   // no Read<Auction>
        authorization.MayBeGranted(typeof(Read<Auction>), new ReadScope(typeof(AuctionCard), true)).ShouldBe(false);
    }

    [Fact]
    public void An_authorization_without_the_role_map_cannot_tell()
        => new ChangeAuthorization(new ServiceCollection().AddAuthorizationCore().AddLogging().BuildServiceProvider().GetRequiredService<IAuthorizationService>())
            .MayBeGranted(typeof(TouchAuction), null).ShouldBeNull();
}
