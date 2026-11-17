using Microsoft.CodeAnalysis;

namespace Tarol.Primitives.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.Primitives";

    /// <summary>TPRM001: the generator threw.</summary>
    public static readonly DiagnosticDescriptor GeneratorFailure = new(
        "TPRM001", "The aggregate generator failed",
        "The Tarol.Primitives generator failed for '{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A bug in the generator. The aggregate gets no generated code; please report it with the declaration that caused it.");

    /// <summary>TPRM002: an aggregate root, entity or one of their containing types is not partial.</summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        "TPRM002", "Aggregate or entity must be partial",
        "'{0}' must be declared partial so that its dispatch can be generated{1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The generator adds the dispatch, the identity maps and Rehydrate to a root, and the constructor and apply switch to an entity, as partial declarations. Every containing type must be partial too.");

    /// <summary>TPRM003: a shape the generator does not support.</summary>
    public static readonly DiagnosticDescriptor Unsupported = new(
        "TPRM003", "Unsupported aggregate shape",
        "'{0}' is not supported: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Generic and file-local aggregates and entities are not supported, and an event must derive from the Event, Created, CreatedIn<TParent> or Deleted base of the type it targets, not from DomainEvent.");

    /// <summary>TPRM004: an event without a handler on the type it targets.</summary>
    public static readonly DiagnosticDescriptor MissingHandler = new(
        "TPRM004", "Event has no On handler",
        "Event '{0}' targets '{1}', but '{1}' declares no On({0}) handler (or one for a base event); it would be recorded and replayed without changing any state",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Every event that targets an existing root or entity needs an On handler on that type. Creation and deletion events are exempt: creating and removing the entity is their effect.");

    /// <summary>TPRM005: a handler that is never called.</summary>
    public static readonly DiagnosticDescriptor HandlerNeverCalled = new(
        "TPRM005", "On handler is never called",
        "On({0}) on '{1}' is never called: {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A handler runs for an event of its own aggregate that targets the declaring type or one of the entities it owns (directly or deeper). Anything else is dead code that looks like state handling.");

    /// <summary>TPRM006: an entity that nothing owns.</summary>
    public static readonly DiagnosticDescriptor NoOwner = new(
        "TPRM006", "Entity has no owner",
        "Entity '{0}' is not owned by '{1}' or any of its entities: declare an EntitySet<{0}> or EntityRef<{0}> on the type that owns it",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A child entity lives in exactly one container of its owner. Without an owner its creation event cannot be applied.");

    /// <summary>TPRM007: an ambiguous owner.</summary>
    public static readonly DiagnosticDescriptor AmbiguousOwner = new(
        "TPRM007", "Ambiguous owner",
        "'{0}' cannot be placed: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "An owner holds at most one EntitySet or EntityRef of a child type. A child with several owner types is created by events that derive from CreatedIn<TParent>, which names the owner.");

    /// <summary>TPRM008: a child container that can be replaced.</summary>
    public static readonly DiagnosticDescriptor AssignableContainer = new(
        "TPRM008", "EntitySet or EntityRef must not be assignable",
        "'{0}' holds children and can be reassigned; declare it as a get-only property or a readonly field initialized with [] or new()",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Replacing the container would drop or inject children without an event (a bypass of event sourcing).");

    /// <summary>TPRM009: an invariant with the wrong shape.</summary>
    public static readonly DiagnosticDescriptor InvariantShape = new(
        "TPRM009", "Invariant has the wrong shape",
        "[Invariant] '{0}' must be an instance method without parameters or type parameters that returns Tarol.Results.Result",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Invariants are aggregated into the aggregate's check: Result Name() on the root or an entity, reading only its own state.");

    /// <summary>TPRM010: two events of one aggregate share an alias.</summary>
    public static readonly DiagnosticDescriptor DuplicateAlias = new(
        "TPRM010", "Duplicate event alias",
        "Events '{0}' and '{1}' of aggregate '{2}' are both stored as '{3}'; pin a unique alias with [EventType]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The alias names an event in the store. Two event types with one alias could not be told apart when they are read.");

    /// <summary>TPRM011: CreatedIn&lt;TParent&gt; names a type that does not own the child.</summary>
    public static readonly DiagnosticDescriptor NotAnOwner = new(
        "TPRM011", "CreatedIn names a type that does not own the child",
        "'{0}' creates '{1}' in '{2}', but '{2}' declares no EntitySet<{1}> or EntityRef<{1}>",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The type argument of CreatedIn<TParent> must be one of the child's owner types.");

    /// <summary>TPRM012: a container of a type that is not an entity of this aggregate.</summary>
    public static readonly DiagnosticDescriptor ForeignChild = new(
        "TPRM012", "Child is not an entity of this aggregate",
        "'{0}' holds '{1}', which is not a partial entity of aggregate '{2}' (declare it as Entity<{2}, {1}>)",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Children are entities of the same aggregate. Another aggregate is referenced by its id, never held as an object.");

    /// <summary>TPRM013: state changed outside an On handler (analyzer).</summary>
    public static readonly DiagnosticDescriptor StateOutsideApply = new(
        "TPRM013", "State changed outside an On handler",
        "'{0}' is assigned in '{1}', which is not an On handler; raise an event and assign it in its On handler",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "State that a behaviour assigns directly is not in any event: replay and every other instance disagree with the live one. Constructors, On handlers, private helpers called only from On handlers and [Transient] members are allowed.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>TPRM014: deciding inside an On handler (analyzer).</summary>
    public static readonly DiagnosticDescriptor DecisionInApply = new(
        "TPRM014", "On handler must not decide",
        "{0} in On({1}): {2}; an On handler only applies the event's data",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "On handlers run on replay, years later and under today's rules. A rule or guard (a call returning Result), a throw, a clock read or a Raise inside one can reject or change stored history.");

    /// <summary>TPRM015: generated plumbing used from user code (analyzer).</summary>
    public static readonly DiagnosticDescriptor PlumbingUsed = new(
        "TPRM015", "Generated aggregate plumbing used directly",
        "'{0}' is generated plumbing of the aggregate; calling it from your code bypasses events",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Dispatch, CheckInvariants and the generated maps, constructors and accessors are reached only by generated code. Raise events from behaviour methods, and let AggregateChanges.Prepare run the invariants.");

    /// <summary>TPRM016: a <c>[To]</c> argument that is not a member of the same enum.</summary>
    public static readonly DiagnosticDescriptor BadTarget = new(
        "TPRM016", "Transition target is not a state",
        "'{0}' in [To] on '{1}' is not a member of '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[To] lists the states a lifecycle state may change to: members of the same [Lifecycle] enum, such as [To(Closed, Cancelled)]. Attribute arguments cannot be typed by the enum, so the generator checks them.");

    /// <summary>TPRM017: a state that no path from the initial state reaches.</summary>
    public static readonly DiagnosticDescriptor Unreachable = new(
        "TPRM017", "State is unreachable",
        "State '{0}' of '{1}' cannot be reached from the initial state '{2}'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "No chain of [To] edges leads from the initial state to this state, so a live step can never enter it. Add the missing [To], or remove the state once no stored event or state row uses its name (a stored state name is forever).");

    /// <summary>TPRM018: a self-loop or an edge declared twice.</summary>
    public static readonly DiagnosticDescriptor RedundantEdge = new(
        "TPRM018", "Redundant transition",
        "[To({0})] on '{1}' is {2}",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "A step to the same state is no change and is never checked; an edge declared twice says nothing new. Remove it.");

    /// <summary>TPRM019: an enum that cannot be a lifecycle.</summary>
    public static readonly DiagnosticDescriptor UnusableLifecycle = new(
        "TPRM019", "Enum cannot be a lifecycle",
        "'{0}' cannot be a lifecycle: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A lifecycle has one state per member: no [Flags], no two members with one value, at least one member, an Initial that names a member, and an enum the generated namespace-level class can see (at least internal, not file-local, not nested in a generic type).");

    /// <summary>TPRM020: <c>[To]</c> on a member of an enum without <c>[Lifecycle]</c>.</summary>
    public static readonly DiagnosticDescriptor ToWithoutLifecycle = new(
        "TPRM020", "[To] outside a lifecycle",
        "[To] on '{0}' has no effect: '{1}' has no [Lifecycle]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[To] declares edges of a lifecycle. Without [Lifecycle] on the enum nothing is generated or checked; add [Lifecycle] or remove [To].");

    /// <summary>TPRM021: a <c>[Transition]</c> on an event that is not a declared edge.</summary>
    public static readonly DiagnosticDescriptor TransitionNotAnEdge = new(
        "TPRM021", "[Transition] is not a declared edge",
        "[Transition] on '{0}' labels {1} of '{2}', which is not a declared edge: {3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[To] on the states is the graph; [Transition<TEnum>(from, to)] on an event only labels one of its edges in the diagram. Declare the edge with [To] on the from-state, or correct the label.");

    /// <summary>TPRM022: a declared state that no <c>On</c> handler assigns (analyzer).</summary>
    public static readonly DiagnosticDescriptor NeverAssigned = new(
        "TPRM022", "State is never assigned",
        "State '{0}' of '{1}' is never assigned in an On handler; no event can enter it",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "The lifecycle declares the state, but no On handler of an aggregate or entity in this project assigns it to a field of the enum, so it is dead (as ArtFlow's Settled was). Add the event that enters it, or remove the state once nothing stored uses its name. The initial state is exempt; an enum assigned from a computed value is not checked.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>TPRM023: an event's <c>On</c> handler does not assign the <c>To</c> of its <c>[Transition]</c> (analyzer).</summary>
    public static readonly DiagnosticDescriptor TransitionMismatch = new(
        "TPRM023", "On handler does not perform its [Transition]",
        "On({0}) on '{1}' does not assign '{2}' to a '{3}' field, but '{0}' is labelled [Transition] {4} -> {2}",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "[Transition<TEnum>(from, to)] says which step an event performs. Its On handler should assign that state; otherwise the label and the diagram are wrong.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>TPRM024: a field the stored state cannot hold.</summary>
    public static readonly DiagnosticDescriptor Uncapturable = new(
        "TPRM024", "Field cannot be stored with the aggregate's state",
        "'{0}' of '{1}' cannot be stored with the aggregate's state: it holds {2}. {3}.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Every instance field of a root and its entities (auto-property backing fields included) is stored with each commit, and a load restores it without replay. A field holding an entity, a root, a collection of them, a delegate or an untyped object cannot be written as data. Hold children in an EntitySet or EntityRef, keep an Id<T>, mark derived data [Transient], or mark the aggregate [LoadByReplay].");

    /// <summary>TPRM025: two stored members share a JSON name, or one uses a reserved name.</summary>
    public static readonly DiagnosticDescriptor StateNameCollision = new(
        "TPRM025", "Stored state name used twice",
        "'{0}' of '{1}' is stored as \"{2}\", {3}; rename it or pin a name with [StateName]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The state of a root or entity is one JSON object: every stored field and child collection needs its own name. \"id\" is reserved on every object, \"version\" and \"schema\" on the root.");

    /// <summary>TPRM026: a state attribute that has no effect where it is.</summary>
    public static readonly DiagnosticDescriptor StateAttributeIgnored = new(
        "TPRM026", "State attribute has no effect",
        "{0} on '{1}' has no effect: {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[StateName] names a stored field, auto-property or child collection; [LoadByReplay] belongs on an aggregate root. Elsewhere they would silently do nothing.");

    /// <summary>TPRM027: a [Transient] member that an On handler fills from an event (analyzer).</summary>
    public static readonly DiagnosticDescriptor TransientIsState = new(
        "TPRM027", "Transient member is filled from an event",
        "[Transient] '{0}' is assigned from event data in On({1}); it is not stored, so an aggregate loaded from its state loses it. Remove [Transient], or only reset it (null or default) in On.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A [Transient] member is derived data that the stored state leaves out. An On handler that assigns it a value makes it state: after a load from the stored state it would be missing, and the aggregate would differ from a replay. Resetting a cache to null or default in On is allowed.");

    /// <summary>TPRM028: two aggregates of one compilation share a stream type.</summary>
    public static readonly DiagnosticDescriptor DuplicateStreamType = new(
        "TPRM028", "Duplicate stream type",
        "Aggregates '{0}' and '{1}' are both stored as stream type '{2}'; pin a unique one with [StreamType]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The stream type is part of every stored key. Two aggregates with one stream type would read each other's streams.");

    /// <summary>TPRM029: [StreamType] with an empty name.</summary>
    public static readonly DiagnosticDescriptor EmptyStreamType = new(
        "TPRM029", "Empty stream type",
        "[StreamType] on '{0}' needs a non-empty name",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "The stream type names the aggregate's streams in the store; it cannot be empty.");

    /// <summary>TPRM030: a <c>[PersonalData]</c> member that is not nullable (analyzer).</summary>
    public static readonly DiagnosticDescriptor PersonalNotNullable = new(
        "TPRM030", "Personal data must be nullable",
        "[PersonalData] '{0}' of '{1}' has the non-nullable type '{2}'; an erased subject's value reads as null, so declare it nullable",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A [PersonalData] member is sealed with its subject's key. Once the subject is erased, the key is gone and the member reads as null; a non-nullable member would break the event or state on every load after an erasure. Declare it as T? (or Option<T>).");

    /// <summary>TPRM031: <c>Subject</c> names no <c>Guid</c>/<c>Id&lt;T&gt;</c> member of the same type (analyzer).</summary>
    public static readonly DiagnosticDescriptor PersonalSubject = new(
        "TPRM031", "Personal data subject is not a Guid or Id<T> member",
        "[PersonalData(Subject = \"{0}\")] on '{1}' of '{2}': {3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Subject names the member whose value identifies whose data this is: a non-nullable Guid or Id<T> member of the same event, aggregate or entity (stored with it, for a state). Use nameof(...). Without Subject the stream id is the subject.");

    /// <summary>TPRM032: <c>[PersonalData]</c> outside an event, an aggregate or entity, or a projection row (analyzer).</summary>
    public static readonly DiagnosticDescriptor PersonalPlacement = new(
        "TPRM032", "Personal data mark has no effect here",
        "[PersonalData] on '{0}' of '{1}' has no effect: {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Tarol seals [PersonalData] members of events, of the stored state of aggregates and entities, and of projection rows. A member of a value object or any other type is never sealed on its own: mark the member that holds the value object instead (its whole value is sealed). Static members, [Transient] members and members of a [LoadByReplay] aggregate's state are not stored.");

    /// <summary>TPRM033: <c>[Retention]</c> on a type that is not an aggregate root, or with fewer than 1 day (analyzer).</summary>
    public static readonly DiagnosticDescriptor RetentionPlacement = new(
        "TPRM033", "Retention is invalid",
        "[Retention] on '{0}' {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "[Retention(Days = n)] keeps an aggregate's streams n days (n >= 1) after their last event, then tarol.retention deletes them. It belongs on an aggregate root.");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "TPRM001" => GeneratorFailure,
        "TPRM002" => NotPartial,
        "TPRM003" => Unsupported,
        "TPRM004" => MissingHandler,
        "TPRM005" => HandlerNeverCalled,
        "TPRM006" => NoOwner,
        "TPRM007" => AmbiguousOwner,
        "TPRM008" => AssignableContainer,
        "TPRM009" => InvariantShape,
        "TPRM010" => DuplicateAlias,
        "TPRM028" => DuplicateStreamType,
        "TPRM029" => EmptyStreamType,
        "TPRM011" => NotAnOwner,
        "TPRM012" => ForeignChild,
        "TPRM016" => BadTarget,
        "TPRM017" => Unreachable,
        "TPRM018" => RedundantEdge,
        "TPRM019" => UnusableLifecycle,
        "TPRM020" => ToWithoutLifecycle,
        "TPRM021" => TransitionNotAnEdge,
        "TPRM024" => Uncapturable,
        "TPRM025" => StateNameCollision,
        "TPRM026" => StateAttributeIgnored,
        "TPRM030" => PersonalNotNullable,
        "TPRM031" => PersonalSubject,
        "TPRM032" => PersonalPlacement,
        "TPRM033" => RetentionPlacement,
        _ => GeneratorFailure,
    };
}
