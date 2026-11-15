using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Results;
using Tarol.Security;
using Tarol.Security.Tests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var services = new ServiceCollection();
services.AddTarol(ShopJson.Default, codec => codec.Add<Auction>().Add<Artwork>())
    .UseInMemoryStore()
    .AddSecurity(roles =>
    {
        roles.Role("seller").May<CreateAuction>().May<CloseAuction>();
        roles.Role("reader").May<Read<Auction>>();
        roles.Authenticated().May<WatchAuction>();
    });
services.AddSingleton<IAuthorizationHandler, CloseAuctionRule>();
services.AddSingleton<IAuthorizationHandler, ReserveLimitRule>();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
var pipeline = provider.GetRequiredService<ChangePipeline>();
var denied = 0L;
using var listener = new MeterListener();
listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == ChangeAuthorization.MeterName) l.EnableMeasurementEvents(i); };
listener.SetMeasurementEventCallback<long>((_, v, _, _) => denied += v);
listener.Start();

ClaimsPrincipal User(string subject, params string[] roles)
    => new(new ClaimsIdentity([new Claim("sub", subject), .. roles.Select(r => new Claim("role", r))], "smoke", "sub", "role"));
OperationContext As(string subject) => OperationContext.Start(Actor.User(subject), Option.None, TimeProvider.System);
bool Refused(Result<ChangeOutcome> result, Error expected) => result.TryGetValue(out Violation? v) && v == expected;

var created = await pipeline.Run(new CreateAuction(10), As("alice"), new ChangeOptions(Principal: User("alice", "seller")));
Check("a role grants the creation", created.IsSuccess);
var auction = (Tarol.StronglyTypedId.Id<Auction>)created.GetValueOrThrow().StreamId;
Check("a row rule denies a creation on the new aggregate",
    Refused(await pipeline.Run(new CreateAuction(500_000), As("alice"), new ChangeOptions(Principal: User("alice", "seller"))), Error.Forbidden("Auction.ReserveLimit")));
Check("a row rule denies over the role's grant",
    Refused(await pipeline.Run(new CloseAuction(auction), As("bob"), new ChangeOptions(ExpectedVersion: 9L, Principal: User("bob", "seller"))), Error.Forbidden("Auction.NotSeller")));
Check("nothing granted: Auth.Forbidden",
    Refused(await pipeline.Run(new TouchAuction(auction), As("bob"), new ChangeOptions(Principal: User("bob", "seller"))), Error.Forbidden(ChangeAuthorization.ForbiddenCode)));
Check("anonymous: Auth.Required",
    Refused(await pipeline.Run(new WatchAuction(auction), As("x"), new ChangeOptions(Principal: new ClaimsPrincipal(new ClaimsIdentity()))), Error.Unauthorized(ChangeAuthorization.RequiredCode)));
Check("authenticated() grants", (await pipeline.Run(new WatchAuction(auction), As("zoe"), new ChangeOptions(Principal: User("zoe")))).IsSuccess);
Check("an internal run is not checked", (await pipeline.Run(new TouchAuction(auction), OperationContext.Start(Actor.System("Jobs.Touch"), Option.None, TimeProvider.System))).IsSuccess);
var loaded = (await provider.GetRequiredService<IAggregateReader>().Load(auction, As("rita"), default)).GetValueOrThrow();
Check("Read<T> granted to a role",
    (await provider.GetRequiredService<IChangeAuthorization>().Check(Read<Auction>.Instance, loaded, new ChangeContext(As("rita"), TimeProvider.System.GetUtcNow()), User("rita", "reader"), default)).IsSuccess);
Check("the seller closes", (await pipeline.Run(new CloseAuction(auction), As("alice"), new ChangeOptions(Principal: User("alice", "seller")))).IsSuccess);
Check("denials counted natively", denied == 4);

Console.WriteLine(failures == 0 ? "Tarol.Security AOT smoke: all checks passed" : $"Tarol.Security AOT smoke: {failures} failed");
return failures == 0 ? 0 : 1;
