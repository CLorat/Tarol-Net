namespace Tarol.Primitives;

/// <summary>
/// The aggregate has no stored state: the store loads it by replaying its events. Its <c>StateSchema</c> is <c>0</c>,
/// <c>WriteState</c> throws and <c>ReadState</c> returns <c>None</c>. Without it, every aggregate's current state is stored
/// with each commit and loaded without replay.
/// <code>[LoadByReplay] public partial class Ledger : AggregateRoot&lt;Ledger&gt; { … }</code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LoadByReplayAttribute : Attribute;
