using Tarol.Kernel;

namespace Tarol.Primitives;

/// <summary>
/// A stored event as readers get it from the store (projections, sagas, messaging): the <see cref="StreamEvent"/> plus the
/// facts about its commit. The aggregate never sees this type; the store fills it at commit (step 8 of the migration).
/// </summary>
/// <param name="StreamId">The aggregate's id (the stream).</param>
/// <param name="Event">The event, its version and routing.</param>
/// <param name="EventId">The event's own id (a UUIDv7 from the store's clock), for idempotency and as causation of what follows.</param>
/// <param name="RecordedAt">When the commit was recorded; one value for every event of a commit.</param>
/// <param name="Context">Who caused it, for which tenant, in which conversation and because of what.</param>
public sealed record RecordedEvent(Guid StreamId, StreamEvent Event, Guid EventId, DateTimeOffset RecordedAt, OperationContext Context);
