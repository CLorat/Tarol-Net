using System.Text.Json.Serialization;
using Tarol.Changes;
using Tarol.Messaging;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

namespace Smoke;

public readonly partial record struct Amount([Range(1, 1_000)] decimal Value);

public partial class Account : AggregateRoot<Account>
{
    public decimal Balance { get; private set; }

    public static Account Open(Guid id) => new((Id<Account>)id);
    public Result Credit(decimal amount) => Raise(new Credited(amount));

    public sealed record Credited(decimal Amount) : Event;
    private void On(Credited e) => Balance += e.Amount;
}

public sealed record PaidV1(Guid Account, decimal Amount);

public sealed partial record CreditAccount(Guid Account, Amount Amount) : ILoads<Account>, IValidatable
{
    [Consumes("billing.paid", 1)]
    public static CreditAccount From(PaidV1 m) => new(m.Account, new Amount(m.Amount));

    public async ValueTask<Result<Account>> Load(Tarol.Persistence.IAggregateReader reader, ChangeContext context, CancellationToken cancellationToken)
        => (await reader.Load((Id<Account>)Account, context.Operation, cancellationToken)).TryGetValue(out Account? found) ? found : Smoke.Account.Open(Account);

    public Result Execute(Account account, ChangeContext context) => account.Credit(Amount.Value);
}

public sealed record AccountCreditedV1(Guid Account, decimal Amount);

public static class Publications
{
    [Publishes<Account.Credited>("smoke.account-credited", 1)]
    public static AccountCreditedV1 Credited(Account.Credited e, RecordedEvent r) => new(r.StreamId, e.Amount);
}

public sealed class Notifier
{
    private readonly List<string> _keys = [];

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_keys)
                return [.. _keys];
        }
    }

    public void Add(string key)
    {
        lock (_keys)
            _keys.Add(key);
    }
}

public sealed class Notify(Notifier notifier) : IEffect<Account.Credited>
{
    public ValueTask<Result> Run(Account.Credited domainEvent, EffectContext context, CancellationToken cancellationToken)
    {
        notifier.Add(context.IdempotencyKey);
        return ValueTask.FromResult(Result.Success());
    }
}

[JsonSerializable(typeof(PaidV1))]
[JsonSerializable(typeof(AccountCreditedV1))]
[JsonSerializable(typeof(Account.Credited))]
public sealed partial class SmokeJson : JsonSerializerContext;
