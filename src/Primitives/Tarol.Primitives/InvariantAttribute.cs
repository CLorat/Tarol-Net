namespace Tarol.Primitives;

/// <summary>
/// Marks an aggregate invariant: an instance method without parameters that returns <c>Tarol.Results.Result</c>, on the
/// root or on an entity. The generator aggregates them into the aggregate's invariant check, which runs when the events
/// are handed to the store (<see cref="AggregateChanges.Prepare{TSelf}"/>), never on replay.
/// <code>[Invariant] private Result PriceNotNegative() => Price >= 0 ? Result.Success() : Error.Conflict("Artwork.NegativePrice");</code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class InvariantAttribute : Attribute;
