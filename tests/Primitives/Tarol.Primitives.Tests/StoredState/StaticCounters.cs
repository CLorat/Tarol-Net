namespace Tarol.Primitives.Tests;

/// <summary>
/// Tests that read a static rule or handler counter (BoardName.Checks, Leaky.Applied), and tests that move it, run in one
/// collection, so they never run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StaticCounters
{
    public const string Name = "Static counters";
}
