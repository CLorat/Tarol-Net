using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Nested
{
    /// <summary>Types nested in a static partial class, an EntityRef below a set, and an event declared in another namespace.</summary>
    public static partial class Space
    {
        public partial class Station : AggregateRoot<Station>
        {
            public EntitySet<Pod> Pods { get; } = [];
            public int Docked { get; private set; }

            public static Station Launch() => new(Id<Station>.New());

            public Pod Dock(string name) => Raise(new Pod.Docked(name));

            private void On(Pod.Docked e) => Docked++;
        }

        public partial class Pod : Entity<Station, Pod>
        {
            public EntityRef<Bay> Bay { get; } = new();
            public string Name { get; private set; } = "";
            public int Hours { get; private set; }

            public Success Repair(int hours) => Raise(new Elsewhere.Repaired(hours));

            public Success Undock() => Raise(new Undocked());

            public Bay Open(int size) => Raise(new Bay.Opened(size));

            public sealed record Docked(string Name) : Created;
            public sealed record Undocked : Deleted;

            private void On(Docked e) => Name = e.Name;
            private void On(Elsewhere.PodEvent e) => Hours += ((Elsewhere.Repaired)e).Hours;
        }

        public partial class Bay : Entity<Station, Bay>
        {
            public int Size { get; private set; }

            public Success Close() => Raise(new Closed());

            public sealed record Opened(int Size) : Created;
            public sealed record Closed : Deleted;

            private void On(Opened e) => Size = e.Size;
        }
    }
}

namespace Tarol.Primitives.Tests.Nested.Elsewhere
{
    public abstract record PodEvent : Space.Pod.Event;

    public sealed record Repaired(int Hours) : PodEvent;
}
