using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>
/// An event-sourced aggregate root: the consistency boundary. Behaviour methods check their guards and call
/// <c>Raise</c>; <c>On</c> handlers apply events; the generator writes the dispatch that runs the handlers live and on
/// replay, the identity maps of the child entities and <see cref="IAggregateRoot{TSelf}.Rehydrate"/>.
/// </summary>
/// <typeparam name="TSelf">The aggregate type itself (declare it <c>partial</c>).</typeparam>
/// <remarks>
/// <code>
/// public partial class Gallery : AggregateRoot&lt;Gallery&gt;
/// {
///     public string Name { get; private set; } = "";
///     public static Gallery Open(string name) { var g = new Gallery(Id&lt;Gallery&gt;.New()); g.Raise(new Opened(name)); return g; }
///     public sealed record Opened(string Name) : Event;
///     private void On(Opened e) => Name = e.Name;
/// }
/// </code>
/// A failure of any kind (a behaviour that returns a failure, a broken invariant, an exception) means the instance is
/// discarded with its uncommitted events. After an <c>On</c> handler threw, the instance is broken: every further
/// <c>Raise</c> and <see cref="AggregateChanges.Prepare{TSelf}"/> throws.
/// </remarks>
public abstract class AggregateRoot<TSelf> : IAggregateRoot, IEntity<TSelf>
    where TSelf : AggregateRoot<TSelf>
{
    private List<StreamEvent>? _changes;
    private long _version;
    private bool _dispatching;
    private bool _broken;
    private LifecycleViolation? _violation;

    /// <summary>Creates the root with its id. A new aggregate passes <c>Id&lt;TSelf&gt;.New()</c>; replay passes the stored id.</summary>
    /// <param name="id">The id.</param>
    protected AggregateRoot(Id<TSelf> id) => Id = id;

    /// <summary>The base of every event that targets the root: <c>public sealed record Opened(string Name) : Event;</c></summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The short base name is the user syntax (sealed record Opened(...) : Event;), decided in docs/migration/primitives.md §9; Tarol targets C# only.")]
    public abstract record Event : DomainEvent;

    /// <inheritdoc />
    public Id<TSelf> Id { get; }

    /// <summary>The version of the last applied event (0 for a new aggregate); the expected version of the next commit.</summary>
    public long Version => _version;

    /// <summary>True after an <c>On</c> handler threw: the state is half-applied and the instance must be discarded.</summary>
    public bool IsBroken => _broken;

    /// <summary>
    /// The events raised since the last commit, in order, with their versions and routing. A store takes them only after
    /// <see cref="AggregateChanges.Prepare{TSelf}"/> succeeded. Do not raise while iterating the span.
    /// </summary>
    public ReadOnlySpan<StreamEvent> UncommittedChanges
        => _changes is null ? ReadOnlySpan<StreamEvent>.Empty : CollectionsMarshal.AsSpan(_changes);

    /// <summary>The root's id.</summary>
    /// <param name="aggregateRoot">The root.</param>
    public static implicit operator Id<TSelf>(AggregateRoot<TSelf> aggregateRoot)
    {
        ArgumentNullException.ThrowIfNull(aggregateRoot);
        return aggregateRoot.Id;
    }

    /// <summary>Raises an event that targets the root: it is applied at once and recorded as uncommitted.</summary>
    /// <param name="event">The event.</param>
    /// <returns><see cref="Success"/>, so a behaviour can write <c>return Raise(…);</c> where it returns a <c>Result</c>.</returns>
    protected Success Raise(Event @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Publish(Id.Value, Guid.Empty, @event);
        return default;
    }

    /// <summary>Creates a child of the root with a new id: raises its creation event and returns the child.</summary>
    /// <typeparam name="TChild">The child type; the root must own it (an <c>EntitySet</c> or <c>EntityRef</c> of it).</typeparam>
    /// <param name="event">The creation event.</param>
    /// <returns>The created child.</returns>
    protected TChild Raise<TChild>(Entity<TSelf, TChild>.Created @event)
        where TChild : Entity<TSelf, TChild>, IOwnedBy<TSelf>
        => Raise(@event, Id<TChild>.New());

    /// <summary>Creates a child of the root with the given id (deterministic tests, ids chosen by the caller).</summary>
    /// <typeparam name="TChild">The child type; the root must own it.</typeparam>
    /// <param name="event">The creation event.</param>
    /// <param name="id">The new child's id.</param>
    /// <returns>The created child.</returns>
    protected TChild Raise<TChild>(Entity<TSelf, TChild>.Created @event, Id<TChild> id)
        where TChild : Entity<TSelf, TChild>, IOwnedBy<TSelf>
    {
        ArgumentNullException.ThrowIfNull(@event);
        return Create(@event, id, Id.Value);
    }

    /// <summary>
    /// Routes one event to its handlers: generated in your partial class. Do not call or override it yourself (TPRM015).
    /// </summary>
    /// <param name="stream">The event with its version and routing.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected abstract void Dispatch(in StreamEvent stream);

    /// <summary>
    /// The aggregate's invariants: generated in your partial class from its <see cref="InvariantAttribute"/> methods and
    /// those of its entities. Do not call or override it yourself (TPRM015); <see cref="AggregateChanges.Prepare{TSelf}"/> runs it.
    /// </summary>
    /// <returns>Success, or the first failing invariant's failure.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected virtual Result CheckInvariants() => Result.Success();

    internal TChild Create<TChild>(DomainEvent @event, Id<TChild> id, Guid parentId)
        where TChild : IEntity<TChild>
    {
        Publish(id.Value, parentId, @event);
        if (this is not IRegistry<TChild> registry || !registry.TryFind(id, out var child))
            return ThrowHelper.ChildNotCreated<TChild>(typeof(TChild).Name);
        return child;
    }

    internal void Publish(Guid entityId, Guid parentId, DomainEvent @event)
    {
        if (_broken)
            ThrowHelper.Broken(typeof(TSelf).Name);
        if (_dispatching)
            ThrowHelper.RaiseDuringDispatch();

        var stream = new StreamEvent(_version + 1, entityId, parentId, @event);
        ApplyLive(in stream);
        (_changes ??= new List<StreamEvent>(8)).Add(stream);
    }

    /// <summary>Replays stored history (reached by the generated <c>Rehydrate</c> only).</summary>
    internal void ReplayHistory(ReadOnlySpan<StreamEvent> history)
    {
        if (_changes is { Count: > 0 })
            ThrowHelper.ReplayWithUncommittedChanges();
        foreach (ref readonly var stream in history)
        {
            if (stream.Version != _version + 1)
                ThrowHelper.OutOfOrderEvent(_version + 1, stream.Version);
            ArgumentNullException.ThrowIfNull(stream.Event, nameof(history));
            Apply(in stream);
        }
    }

    /// <summary>Sets the version of a state-restored aggregate (reached by the generated <c>ReadState</c> only).</summary>
    internal void RestoreVersion(long version)
    {
        if (_version != 0 || _changes is { Count: > 0 })
            ThrowHelper.RestoreOnUsedAggregate(typeof(TSelf).Name);
        if (version < 0)
            ThrowHelper.NegativeStateVersion(typeof(TSelf).Name, version);
        _version = version;
    }

    internal Result Prepare()
    {
        if (_broken)
            ThrowHelper.Broken(typeof(TSelf).Name);
        if (_violation is { } violation)
            return violation.ToError();
        return CheckInvariants();
    }

    /// <summary>
    /// Applies a raised event on the live path only (<c>Raise</c>, never replay or a state load). Generated in your partial
    /// class when the aggregate or one of its entities has a <see cref="LifecycleAttribute"/> field: the override marks the
    /// dispatch as live, so the generated step checks run, and calls <see cref="ApplyRaised"/>. Do not call or override it
    /// yourself (TPRM015).
    /// </summary>
    /// <param name="stream">The raised event.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected virtual void ApplyLive(in StreamEvent stream) => Apply(in stream);

    /// <summary>Applies a raised event: for the generated <see cref="ApplyLive"/> only (TPRM015).</summary>
    /// <param name="stream">The raised event.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected void ApplyRaised(in StreamEvent stream) => Apply(in stream);

    /// <summary>
    /// Remembers a step of a lifecycle field that is not a declared edge: for the generated dispatch only (TPRM015). The
    /// first one is kept and <see cref="AggregateChanges.Prepare{TSelf}"/> returns it before the invariants run; the
    /// instance must then be discarded, like after any failure.
    /// </summary>
    /// <param name="violation">The step.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected void ForbiddenStep(LifecycleViolation violation) => _violation ??= violation;

    internal void Committed() => _changes?.Clear();

    private void Apply(in StreamEvent stream)
    {
        _dispatching = true;
        try
        {
            Dispatch(in stream);
        }
        catch
        {
            _broken = true;
            throw;
        }
        finally
        {
            _dispatching = false;
        }

        _version = stream.Version;
    }
}
