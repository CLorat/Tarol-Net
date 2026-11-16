using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Smoke;

public partial class Account : AggregateRoot<Account>
{
    public decimal Balance { get; private set; }

    public static Account Open(Guid id) => new((Id<Account>)id);
    public Result Credit(decimal amount) => amount <= 0 ? Error.Conflict("Account.Amount") : Raise(new Credited(amount));

    public sealed record Credited(decimal Amount) : Event;
    private void On(Credited e) => Balance += e.Amount;
}

public partial class Audit : AggregateRoot<Audit>
{
    public int Entries { get; private set; }

    public static Audit For(Guid id) => new((Id<Audit>)id);
    public Result Add(decimal amount) => Raise(new Logged(amount));

    public sealed record Logged(decimal Amount) : Event;
    private void On(Logged e) => Entries++;
}

public sealed record PaidV1(Guid Account, decimal Amount);
public sealed record AccountCreditedV1(Guid Account, decimal Amount);

/// <summary>billing.paid credits the account; the credit is published as smoke.account-credited.</summary>
public sealed partial record CreditAccount(Guid Account, decimal Amount) : ILoads<Account>
{
    [Consumes("billing.paid", 1)]
    public static CreditAccount From(PaidV1 m) => new(m.Account, m.Amount);

    public async ValueTask<Result<Account>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Account>)Account, context.Operation, cancellationToken)).TryGetValue(out Account? found) ? found : Smoke.Account.Open(Account);

    public Result Execute(Account account, ChangeContext context) => account.Credit(Amount);
}

/// <summary>Consumes the system's own publication: the round trip store → outbox → bus → consumer.</summary>
public sealed partial record AuditCredit(Guid Account, decimal Amount) : ILoads<Audit>
{
    [Consumes("smoke.account-credited", 1)]
    public static AuditCredit From(AccountCreditedV1 m) => new(m.Account, m.Amount);

    public async ValueTask<Result<Audit>> Load(IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Audit>)Account, context.Operation, cancellationToken)).TryGetValue(out Audit? found) ? found : Smoke.Audit.For(Account);

    public Result Execute(Audit audit, ChangeContext context) => audit.Add(Amount);
}

public static class Publications
{
    [Publishes<Account.Credited>("smoke.account-credited", 1)]
    public static AccountCreditedV1 Credited(Account.Credited e, RecordedEvent r) => new(r.StreamId, e.Amount);
}

[JsonSerializable(typeof(PaidV1))]
[JsonSerializable(typeof(AccountCreditedV1))]
[JsonSerializable(typeof(Account.Credited))]
[JsonSerializable(typeof(Audit.Logged))]
public sealed partial class SmokeJson : JsonSerializerContext;
