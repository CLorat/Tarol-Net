using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

// ---- Candidate Kernel types (probe only) --------------------------------------------------------

/// Who acts. The id is the identity provider's subject ("sub"), not an aggregate id: the IdP owns it.
public enum ActorKind { Anonymous, User, Service, System }

public sealed record Actor(ActorKind Kind, string Id, Option<string> OnBehalfOf = default)
{
    public static readonly Actor Anonymous = new(ActorKind.Anonymous, "anonymous");
    public static Actor User(string subject) => new(ActorKind.User, subject);
    public static Actor Service(string clientId) => new(ActorKind.Service, clientId);
    /// A saga or job acting on its own; OnBehalfOf keeps the user who started the conversation.
    public static Actor System(string component, Option<string> onBehalfOf = default) => new(ActorKind.System, component, onBehalfOf);
}

public readonly record struct TenantId(string Value);

/// What every event records about its origin (step 6/8: columns, not payload).
public sealed record EventMetadata(
    Guid EventId,
    DateTimeOffset RecordedAt,
    Actor Actor,
    Option<TenantId> Tenant,
    Guid CorrelationId,
    Guid CausationId,
    Option<string> TraceParent);

// ---- A domain: auction cancel, resource-based ----------------------------------------------------

public sealed class Auction
{
    public required Id<Auction> Id { get; init; }
    public required TenantId Tenant { get; init; }
    public required string SellerSubject { get; init; }
    public bool IsCancelled { get; private set; }

    // Invariant (domain rule, not a permission): a cancelled auction cannot be cancelled again.
    public Result Cancel() => IsCancelled ? Error.Conflict("Auction.AlreadyCancelled") : Done();
    private Result Done() { IsCancelled = true; return Result.Success(); }
}

public sealed record CancelAuction(Id<Auction> Auction);

/// Tarol-side permission check: pure, no ASP.NET Core, returns a Result (denial = Error.Forbidden).
public interface IPermission<in TCommand, in TResource>
{
    Result Check(Actor actor, TenantId tenant, TCommand command, TResource resource);
}

public sealed class CancelAuctionPermission : IPermission<CancelAuction, Auction>
{
    public Result Check(Actor actor, TenantId tenant, CancelAuction command, Auction auction) => actor switch
    {
        _ when auction.Tenant != tenant => Error.NotFound("Auction.NotFound"), // another tenant's resource does not exist for you
        { Kind: ActorKind.Anonymous } => Error.Unauthorized("Auth.Required"),
        { Kind: ActorKind.User } u when u.Id == auction.SellerSubject => Result.Success(),
        { Kind: ActorKind.System } => Result.Success(),
        _ => Error.Forbidden("Auction.Cancel.NotSeller"),
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EventMetadata))]
[JsonSerializable(typeof(TenantId))]
internal sealed partial class MetadataJson : JsonSerializerContext;
