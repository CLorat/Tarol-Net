namespace Tarol.Primitives;

/// <summary>
/// Pins the stored stream type of an aggregate: the name the store keys its streams by. Without it the stream type is
/// the aggregate's name in snake case (<c>Auction</c> → <c>auction</c>, <c>PayoutRun</c> → <c>payout_run</c>), the first
/// segment of its default event aliases. Pin it before you rename an aggregate that is already stored.
/// <code>[StreamType("auction")] public partial class Auction : AggregateRoot&lt;Auction&gt; { … }</code>
/// </summary>
/// <param name="name">The stream type; unique in the compilation (TPRM028), not empty (TPRM029).</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StreamTypeAttribute(string name) : Attribute
{
    /// <summary>The stream type.</summary>
    public string Name { get; } = name;
}
