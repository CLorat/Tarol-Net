using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Fixtures;

/// <summary>A behaviour that bypasses its event (TPRM013 suppressed on purpose): its state differs from a replay.</summary>
public partial class Leaky : AggregateRoot<Leaky>
{
    private static int _applied;

    public static int Applied => Volatile.Read(ref _applied);

    public int Count { get; private set; }

    public static Leaky Create() => new(Id<Leaky>.New());

    public Success Bump() => Raise(new Bumped());

#pragma warning disable TPRM013 // fixture: state written outside On, to prove StoredStateMatchesReplay catches it
    public void Cheat() => Count = 99;
#pragma warning restore TPRM013

    public sealed record Bumped : Event;

    private void On(Bumped e)
    {
        Interlocked.Increment(ref _applied);
        Count++;
    }
}
