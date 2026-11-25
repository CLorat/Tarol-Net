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
