using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace ArtFlow;

public sealed class Bidder;

/// <summary>Shared by all three candidates: the lifecycle of a bid.</summary>
[Lifecycle]
public enum BidStatus
{
    [To(Withdrawn, Won, Lost)] Active,
    Withdrawn,
    Won,
    Lost,
}
