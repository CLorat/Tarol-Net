namespace Tarol.Primitives;

/// <summary>
/// Keeps an aggregate's streams for <see cref="Days"/> days after their last event; then the system job
/// <c>tarol.retention</c> deletes the whole stream (events, state, lookups and the read-model rows of its projections).
/// Configuration overrides it per stream type (<c>Tarol:Retention:&lt;streamType&gt;:Days</c>).
/// <code>[Retention(Days = 3650)] public partial class Order : AggregateRoot&lt;Order&gt; { … }</code>
/// </summary>
/// <remarks>Only on an aggregate root, with <see cref="Days"/> of at least 1 (TPRM033).</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RetentionAttribute : Attribute
{
    /// <summary>How many days a stream is kept after its last event (at least 1).</summary>
    public int Days { get; init; }
}
