using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests.Lifecycles;

/// <summary>A root whose initial state is named (<c>Initial</c>) and is not value 0, with a nested lifecycle enum.</summary>
[Lifecycle(Initial = nameof(Draft))]
public enum TicketStatus
{
    [To(Draft)] Archived = 0,
    [To(Open)] Draft = 1,
    [To(Done)] Open = 2,
    [To(Archived)] Done = 3,
}

public partial class Ticket : AggregateRoot<Ticket>
{
    [Lifecycle]
    public enum Priority
    {
        [To(High)] Normal,
        [To(Normal)] High,
    }

    public TicketStatus Status { get; private set; }
    public Priority Level { get; private set; }
    public string Title { get; private set; } = "";

    /// <summary>Correct: the creation event enters the initial state.</summary>
    public static Ticket File(string title)
    {
        var ticket = new Ticket(Id<Ticket>.New());
        ticket.Raise(new Filed(title));
        return ticket;
    }

    /// <summary>Forgot that a ticket starts as a draft: its creation event leaves it Archived (value 0), not Draft.</summary>
    public static Ticket FileWithoutState(string title)
    {
        var ticket = new Ticket(Id<Ticket>.New());
        ticket.Raise(new Noted(title));
        return ticket;
    }

    /// <summary>Forgot the draft: created directly in Open.</summary>
    public static Ticket FileOpen(string title)
    {
        var ticket = new Ticket(Id<Ticket>.New());
        ticket.Raise(new FiledOpen(title));
        return ticket;
    }

    public Result Publish() => Status.RequireTransitionTo(TicketStatus.Open).Bind(() => Raise(new Published()));

    public Result Finish() => Status.CanTransitionTo(TicketStatus.Done) ? Raise(new Finished()) : Error.Conflict("Ticket.NotOpen");

    public Result Archive() => Status.RequireTransitionTo(TicketStatus.Archived).Bind(() => Raise(new Shelved()));

    public Result Escalate() => Level.RequireTransitionTo(Priority.High).Bind(() => Raise(new Escalated()));

    public sealed record Filed(string Title) : Event;

    public sealed record Noted(string Title) : Event;

    public sealed record FiledOpen(string Title) : Event;

    public sealed record Published : Event;

    public sealed record Finished : Event;

    public sealed record Escalated : Event;

    public sealed record Deescalated : Event;

    public sealed record Shelved : Event;

    private void On(Filed e)
    {
        Title = e.Title;
        Status = TicketStatus.Draft;
    }

    private void On(Noted e) => Title = e.Title;

    private void On(FiledOpen e)
    {
        Title = e.Title;
        Status = TicketStatus.Open;
    }

    private void On(Published e)
    {
        _ = e;
        Status = TicketStatus.Open;
    }

    private void On(Finished e)
    {
        _ = e;
        Status = TicketStatus.Done;
    }

    private void On(Escalated e)
    {
        _ = e;
        Level = Priority.High;
    }

    private void On(Deescalated e)
    {
        _ = e;
        Level = Priority.Normal;
    }

    /// <summary>Raises a prepared event (the allocation test raises the same instances again and again).</summary>
    internal Success Replay(Event e) => Raise(e);

    private void On(Shelved e)
    {
        _ = e;
        Status = TicketStatus.Archived;
    }
}
