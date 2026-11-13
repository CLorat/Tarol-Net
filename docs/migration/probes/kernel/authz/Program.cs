using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

var failures = 0;
void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}"); if (!ok) failures++; }
Check("native", !RuntimeFeature.IsDynamicCodeSupported);

var t1 = new TenantId("t1");
Auction NewAuction() => new() { Id = Id<Auction>.New(), Tenant = t1, SellerSubject = "alice" };
var permission = new CancelAuctionPermission();

// 1. Without ASP.NET Core: handler = load, check permission, invoke behaviour, all as one Result pipeline.
Result Handle(Actor actor, TenantId tenant, CancelAuction cmd, Auction auction)
    => permission.Check(actor, tenant, cmd, auction) switch
    {
        Violation v => v,
        _ => auction.Cancel(),
    };

Check("seller may cancel", Handle(Actor.User("alice"), t1, new(default), NewAuction()).IsSuccess);
Check("other user: Error.Forbidden", Handle(Actor.User("bob"), t1, new(default), NewAuction()) is { IsSuccess: false } r1 && r1.TryGetValue(out Violation? v1) && v1 is Error { Type: ErrorType.Forbidden });
Check("anonymous: Error.Unauthorized", Handle(Actor.Anonymous, t1, new(default), NewAuction()).TryGetValue(out Violation? v2) && v2 is Error { Type: ErrorType.Unauthorized });
Check("other tenant: Error.NotFound (no existence leak)", Handle(Actor.User("alice"), new("t2"), new(default), NewAuction()).TryGetValue(out Violation? v3) && v3 is Error { Type: ErrorType.NotFound });
var once = NewAuction();
_ = Handle(Actor.User("alice"), t1, new(once.Id), once);
Check("invariant after permission: Error.Conflict", Handle(Actor.User("alice"), t1, new(once.Id), once).TryGetValue(out Violation? v4) && v4 is Error { Type: ErrorType.Conflict });
Check("saga system actor on behalf of alice may cancel", Handle(Actor.System("Sagas.AuctionTimeout", "alice"), t1, new(default), NewAuction()).IsSuccess);

// 2. ASP.NET Core authorization WITHOUT a web host: AddAuthorizationCore + resource-based handler.
var services = new ServiceCollection().AddLogging().AddAuthorizationCore(o =>
    o.AddPolicy("Auction.Cancel", p => p.AddRequirements(new OperationAuthorizationRequirement { Name = "Cancel" })));
services.AddSingleton<IAuthorizationHandler, AuctionCancelHandler>();
using var provider = services.BuildServiceProvider();
var authz = provider.GetRequiredService<IAuthorizationService>();
static ClaimsPrincipal Principal(string sub, string tenant) => new(new ClaimsIdentity([new("sub", sub), new("tenant", tenant)], "test"));
async ValueTask<Result> AuthorizeAsp(ClaimsPrincipal user, Auction auction)
    => (await authz.AuthorizeAsync(user, auction, "Auction.Cancel")).Succeeded
        ? Result.Success()
        : user.Identity?.IsAuthenticated == true ? Error.Forbidden("Auction.Cancel.NotSeller") : Error.Unauthorized("Auth.Required");
Check("IAuthorizationService (no web host): seller succeeds", (await AuthorizeAsp(Principal("alice", "t1"), NewAuction())).IsSuccess);
Check("IAuthorizationService (no web host): bob -> Forbidden", (await AuthorizeAsp(Principal("bob", "t1"), NewAuction())).TryGetValue(out Violation? v5) && v5 is Error { Type: ErrorType.Forbidden });
Check("IAuthorizationService: unauthenticated -> Unauthorized", (await AuthorizeAsp(new ClaimsPrincipal(new ClaimsIdentity()), NewAuction())).TryGetValue(out Violation? v6) && v6 is Error { Type: ErrorType.Unauthorized });

// 3. Actor in event metadata, JSON via the source generator (AOT).
var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
using var source = new ActivitySource("probe");
using var listener = new ActivityListener { ShouldListenTo = s => s.Name == "probe", Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded };
ActivitySource.AddActivityListener(listener);
using var activity = source.StartActivity("cancel");
var meta = new EventMetadata(Guid.CreateVersion7(clock.GetUtcNow()), clock.GetUtcNow(), Actor.System("Sagas.AuctionTimeout", "alice"),
    new TenantId("t1"), Guid.CreateVersion7(), Guid.CreateVersion7(), activity?.Id is { } id ? id : Option.None);
var json = JsonSerializer.Serialize(meta, MetadataJson.Default.EventMetadata);
Console.WriteLine("     " + json);
Check("metadata JSON round-trips (actor, Option tenant, traceparent)", JsonSerializer.Deserialize(json, MetadataJson.Default.EventMetadata) == meta);
Check("traceparent is W3C (00-<32 hex>-<16 hex>-<flags>)", activity?.Id is { Length: 55 } tp && tp.StartsWith("00-", StringComparison.Ordinal));
var anon = meta with { Actor = Actor.Anonymous, Tenant = Option.None, TraceParent = Option.None };
Check("absent tenant/trace are null in JSON", JsonSerializer.Serialize(anon, MetadataJson.Default.EventMetadata).Contains("\"tenant\":null", StringComparison.Ordinal));

// 4. Activity without a listener: no trace id to correlate by.
using var silent = new ActivitySource("nobody-listens").StartActivity("x");
Check("ActivitySource without listener returns null (no trace id)", silent is null);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} FAILED");
return failures;

sealed class AuctionCancelHandler : AuthorizationHandler<OperationAuthorizationRequirement, Auction>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, Auction auction)
    {
        if (context.User.FindFirst("tenant")?.Value == auction.Tenant.Value && context.User.FindFirst("sub")?.Value == auction.SellerSubject)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
