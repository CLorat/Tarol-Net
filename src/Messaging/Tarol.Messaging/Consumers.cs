using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Results;

namespace Tarol.Messaging;

/// <summary>
/// On a static factory of a change: the change <b>consumes</b> <paramref name="contract"/> in <paramref name="version"/>
/// (docs/architecture/messaging.md §3). The factory is the anti-corruption layer: it maps another system's contract record
/// to the change and does nothing else; the pipeline validates the result.
/// </summary>
/// <example>
/// <code>
/// public sealed partial record RecordPayment(Id&lt;Auction&gt; Auction, string Invoice, decimal Amount) : IChange&lt;Auction&gt;
/// {
///     [Consumes("billing.invoice-paid", 1)]
///     public static RecordPayment From(InvoicePaidV1 m) => new((Id&lt;Auction&gt;)m.Auction, m.Invoice, m.Amount);
///     public Result Execute(Auction auction, ChangeContext context) => auction.RecordPayment(Invoice, Amount);
/// }
/// </code>
/// </example>
/// <remarks>
/// The factory is <c>public static</c>, returns its change, and takes <c>(TPayload)</c> or <c>(TPayload, Message)</c> (TMSG001).
/// One factory per (consumer, contract, version) (TMSG002); the contract is <c>&lt;system&gt;.&lt;name&gt;</c> and the version 1 or
/// more (TMSG003). Every factory of one change is one consumer: one subscription, one key scope.
/// </remarks>
/// <param name="contract">The contract, prefixed by the system that owns it, e.g. <c>billing.invoice-paid</c>.</param>
/// <param name="version">The contract version this factory reads.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ConsumesAttribute(string contract, int version) : Attribute
{
    /// <summary>The contract.</summary>
    public string Contract { get; } = contract;

    /// <summary>The contract version.</summary>
    public int Version { get; } = version;

    /// <summary>
    /// The consumer's stable name: its subscription <c>&lt;application&gt;.&lt;name&gt;</c> and its key scope
    /// <c>consumer:&lt;name&gt;</c> (M-12); default the change's type name. Pin it before renaming the change. Every factory
    /// of one change must agree (TMSG007).
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>One consumer of an application: its name and the contracts it receives.</summary>
/// <param name="Name">The consumer's name.</param>
/// <param name="Topics">The contracts.</param>
public sealed record ConsumerInfo(string Name, IReadOnlyList<string> Topics);

/// <summary>What the generated switch did with a message.</summary>
public enum Mapped
{
    /// <summary>The factory ran and the pipeline answered.</summary>
    Ran,

    /// <summary>The data does not match the contract record: buried.</summary>
    Malformed,

    /// <summary>A known contract in a version no factory of the consumer reads: buried (R-10).</summary>
    UnknownVersion,

    /// <summary>A contract the consumer does not read: acknowledged and dropped.</summary>
    UnknownContract,
}

/// <summary>The generated switch's answer.</summary>
/// <param name="Mapped">Whether the pipeline ran.</param>
/// <param name="Result">The pipeline's answer when it ran.</param>
public readonly record struct ConsumerRun(Mapped Mapped, Result<ChangeOutcome> Result)
{
    /// <summary>The switch did not run the pipeline.</summary>
    /// <param name="mapped">Why.</param>
    /// <returns>The answer.</returns>
    public static ConsumerRun Not(Mapped mapped) => new(mapped, Error.Internal("Messaging.NotRun"));
}

/// <summary>
/// The scope one consumed message runs in, built by <see cref="MessageConsumers"/> from the envelope and the trust rules:
/// the operation context, the change options with the key, and the application's JSON.
/// </summary>
public sealed class MessageScope
{
    private readonly JsonSerializerOptions _json;

    /// <summary>Creates a scope. Called by <see cref="MessageConsumers"/> (and tests).</summary>
    /// <param name="operation">The operation the change runs in.</param>
    /// <param name="options">The key.</param>
    /// <param name="json">The application's JSON options (its source-generated context).</param>
    public MessageScope(OperationContext operation, ChangeOptions options, JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(json);
        Operation = operation;
        Options = options;
        _json = json;
    }

    /// <summary><c>System("Consumers.&lt;name&gt;")</c> on behalf of the envelope's actor, the envelope's tenant and correlation, caused by the message.</summary>
    public OperationContext Operation { get; }

    /// <summary>The key (tenant, <c>consumer:&lt;name&gt;</c>, message id).</summary>
    public ChangeOptions Options { get; }

    /// <summary>The contract record from the data, through the application's JSON; <see langword="null"/> when it does not match.</summary>
    /// <typeparam name="T">The consumer's own record of the contract version.</typeparam>
    /// <param name="data">The body.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The application's JSON context does not list <typeparamref name="T"/> (TMSG006).</exception>
    public T? Payload<T>(ReadOnlyMemory<byte> data)
        where T : class
    {
        if (_json.TypeInfoResolver?.GetTypeInfo(typeof(T), _json) is not JsonTypeInfo<T> info)
            throw new InvalidOperationException($"The application's JSON context has no metadata for {typeof(T).Name}: add [JsonSerializable(typeof({typeof(T).Name}))].");
        try
        {
            return JsonSerializer.Deserialize(data.Span, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The generated switch (consumer, contract, version) → payload → factory → pipeline.</summary>
/// <param name="consumer">The consumer's name.</param>
/// <param name="pipeline">The pipeline.</param>
/// <param name="message">The message.</param>
/// <param name="scope">Context, key and JSON.</param>
/// <param name="cancellationToken">Cancels the run.</param>
/// <returns>What the switch did and the pipeline's answer.</returns>
public delegate ValueTask<ConsumerRun> ConsumerRunner(string consumer, ChangePipeline pipeline, Message message, MessageScope scope, CancellationToken cancellationToken);

/// <summary>
/// The consumers of an application: each assembly with <see cref="ConsumesAttribute"/> factories has a generated
/// <c>GeneratedConsumers.Set</c>; <see cref="Combine"/> joins several.
/// </summary>
public sealed class ConsumerSet
{
    private readonly Dictionary<string, ConsumerRunner> _runners;

    /// <summary>Creates the set of one assembly. Called by generated code.</summary>
    /// <param name="consumers">The consumers.</param>
    /// <param name="runner">The switch.</param>
    /// <exception cref="ArgumentException">A name is empty or listed twice, or a consumer has no topic.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ConsumerSet(IReadOnlyList<ConsumerInfo> consumers, ConsumerRunner runner)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(runner);
        _runners = new(StringComparer.Ordinal);
        foreach (var consumer in consumers)
        {
            ArgumentNullException.ThrowIfNull(consumer, nameof(consumers));
            ArgumentException.ThrowIfNullOrWhiteSpace(consumer.Name, nameof(consumers));
            if (consumer.Topics.Count == 0)
                throw new ArgumentException($"The consumer '{consumer.Name}' receives no contract.", nameof(consumers));
            if (!_runners.TryAdd(consumer.Name, runner))
                throw new ArgumentException($"The consumer '{consumer.Name}' is listed twice.", nameof(consumers));
        }
        Consumers = [.. consumers];
    }

    private ConsumerSet(IReadOnlyList<ConsumerInfo> consumers, Dictionary<string, ConsumerRunner> runners)
    {
        Consumers = consumers;
        _runners = runners;
    }

    /// <summary>The consumers.</summary>
    public IReadOnlyList<ConsumerInfo> Consumers { get; }

    /// <summary>Joins the sets of several assemblies.</summary>
    /// <param name="sets">The sets.</param>
    /// <returns>The combined set.</returns>
    /// <exception cref="InvalidOperationException">A consumer name is in two sets (names are subscriptions and key scopes).</exception>
    public static ConsumerSet Combine(params ConsumerSet[] sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var consumers = new List<ConsumerInfo>();
        var runners = new Dictionary<string, ConsumerRunner>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set, nameof(sets));
            foreach (var consumer in set.Consumers)
            {
                if (!runners.TryAdd(consumer.Name, set._runners[consumer.Name]))
                    throw new InvalidOperationException($"The consumer '{consumer.Name}' is declared in two assemblies; consumer names must be unique.");
                consumers.Add(consumer);
            }
        }
        return new(consumers, runners);
    }

    /// <summary>Runs <paramref name="consumer"/>'s switch for <paramref name="message"/>.</summary>
    /// <param name="consumer">The consumer's name.</param>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="message">The message.</param>
    /// <param name="scope">Context, key and JSON.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="InvalidOperationException">No consumer has that name.</exception>
    public ValueTask<ConsumerRun> Run(string consumer, ChangePipeline pipeline, Message message, MessageScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        return _runners.TryGetValue(consumer, out var runner)
            ? runner(consumer, pipeline, message, scope, cancellationToken)
            : throw new InvalidOperationException($"No consumer named '{consumer}' is registered (removed, or renamed without pinning its Name).");
    }
}
