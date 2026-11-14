using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

/// <summary>A stored-state aggregate with a unique number.</summary>
public partial class Account : AggregateRoot<Account>
{
    public string Number { get; private set; } = "";
    public decimal Balance { get; private set; }

    public static Account Open(string number) => Open(Id<Account>.New(), number);

    public static Account Open(Id<Account> id, string number)
    {
        var account = new Account(id);
        account.Raise(new Opened(number));
        return account;
    }

    public Result Deposit(decimal amount) => amount <= 0 ? Error.Conflict("Account.Amount") : Raise(new Deposited(amount));

    public Success Renumber(string number) => Raise(new Renumbered(number));

    public sealed record Opened(string Number) : Event;
    public sealed record Deposited(decimal Amount) : Event;
    public sealed record Renumbered(string Number) : Event;

    private void On(Opened e) => Number = e.Number;
    private void On(Deposited e) => Balance += e.Amount;
    private void On(Renumbered e) => Number = e.Number;
}

/// <summary>An aggregate without stored state.</summary>
[LoadByReplay]
public partial class Counter : AggregateRoot<Counter>
{
    public int Count { get; private set; }

    public static Counter Start() { var c = new Counter(Id<Counter>.New()); c.Raise(new Started()); return c; }

    public Success Bump() => Raise(new Bumped());

    public sealed record Started : Event;
    public sealed record Bumped : Event;
    private void On(Started e) => Count = 0;
    private void On(Bumped e) => Count++;
}

[JsonSerializable(typeof(decimal))]
public partial class TestJson : JsonSerializerContext;
