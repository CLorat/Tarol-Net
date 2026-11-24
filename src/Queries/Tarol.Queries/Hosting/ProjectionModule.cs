using Tarol.Persistence;

namespace Tarol.Queries;

/// <summary>The projections of a <see cref="TarolModule"/> (TN-8; the generated module adds its <c>[Projection]</c> rows).</summary>
/// <param name="Add">Adds them: <c>p =&gt; p.Add&lt;MembershipRow&gt;()</c>.</param>
public sealed record ProjectionModule(Action<ProjectionsBuilder> Add);
