using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Security.Tests.Skill;

/// <summary>The example of skills/tarol-security/SKILL.md, verbatim (code identical, comments included).</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_runs_as_written()
    {
#pragma warning disable xUnit1051 // the skill's code as an application writes it: there is no test token in an app
        // ── verbatim ──
        var services = new ServiceCollection();
        services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>())
            .UseInMemoryStore()
            .AddSecurity(roles =>
            {
                roles.Role("seller").May<CloseAuction>();
                roles.Role("admin").May<CloseAuction>();
            });
        services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<ChangePipeline>();

        // alice's auction, created without a principal (an internal run: not checked)
        var alice = OperationContext.Start(Actor.User("alice"), Option.None, TimeProvider.System);
        var auction = (Id<Auction>)(await pipeline.Run(new CreateAuction(100), alice)).GetValueOrThrow().StreamId;

        // bob is a seller, but not this auction's seller: the row rule denies
        var bob = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "bob"), new Claim("role", "seller")], "jwt", "sub", "role"));
        var refused = await pipeline.Run(new CloseAuction(auction), OperationContext.Start(Actor.User("bob"), Option.None, TimeProvider.System),
            new ChangeOptions(Principal: bob));
        // refused: Error.Forbidden("Auction.NotSeller")
        // ── end ──
#pragma warning restore xUnit1051

        refused.ShouldBe(Result<ChangeOutcome>.Failure(Error.Forbidden("Auction.NotSeller")));
    }
}

public sealed class CloseAuctionRule : ChangeHandler<CloseAuction, Auction>
{
    protected override Task Handle(AuthorizationHandlerContext context, ChangeRequirement requirement, CloseAuction change, Auction auction)
    {
        if (context.User.FindFirst("sub")?.Value != auction.Seller && !context.User.IsInRole("admin"))
            Deny(context, "Auction.NotSeller");
        return Task.CompletedTask;
    }
}
