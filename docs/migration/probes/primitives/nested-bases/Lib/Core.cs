namespace Core;
public abstract record DomainEvent;
public abstract class AggregateRoot<TSelf> where TSelf : AggregateRoot<TSelf>
{
    public abstract record Event : DomainEvent;
    internal readonly System.Collections.Generic.List<(System.Guid Entity, DomainEvent E)> Changes = new();
    protected void Raise(Event e) => Changes.Add((System.Guid.Empty, e));
    public int Count => Changes.Count;
}
public abstract class Entity<TRoot, TSelf> where TRoot : AggregateRoot<TRoot> where TSelf : Entity<TRoot, TSelf>
{
    public abstract record Event : DomainEvent;
    public abstract record Created : DomainEvent;
    public abstract record Deleted : DomainEvent;
}
