using Tarol.Queries;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>
/// The auctions of the sellers a caller follows for a family (TN-6, Tatalu's "parents see their own children" on the auction
/// list): the set is domain data the scope reads, not a claim, and a missing scope fails at startup.
/// </summary>
[Endpoint("GET /family/auctions", RequiresScope = true)]
public sealed record FamilyAuctions(AuctionStatus? Status = null) : IQuery<AuctionRow>;

/// <summary>
/// The scope of <see cref="FamilyAuctions"/>: a parent follows two sellers; a substitute follows three until a day; an expired
/// substitute and a stranger follow none (an empty page, never an unfiltered one).
/// </summary>
public sealed class FamilyScope : IListScope<FamilyAuctions>
{
    public static readonly Dictionary<string, (string[] Sellers, DateTimeOffset? Until)> Directory = new(StringComparer.Ordinal)
    {
        ["paula"] = (["alice", "bob"], null),
        ["vera"] = (["alice", "bob", "carl"], TimeProvider.System.GetUtcNow().AddDays(14)),
        ["vince"] = (["alice"], TimeProvider.System.GetUtcNow().AddDays(-1)),
    };

    public int Calls { get; private set; }

    public ValueTask<ListScope> Scope(ChangeContext context, System.Security.Claims.ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        Calls++;
        var subject = principal.FindFirst("sub")?.Value;
        if (subject is null || !Directory.TryGetValue(subject, out var access) || access.Until < context.Now)
            return ValueTask.FromResult(ListScope.None);
        return ValueTask.FromResult(ListScope.In(nameof(AuctionRow.Seller), access.Sellers));
    }
}
