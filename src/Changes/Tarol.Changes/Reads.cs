using System.ComponentModel;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Changes;

/// <summary>
/// On an <c>Id&lt;X&gt;</c> member of a change: the pipeline loads that aggregate read-only before <c>Execute</c> and
/// passes it in <see cref="ChangeContext.Read{T}()"/>. A missing one is <c>Error.UnprocessableEntity("X.NotFound")</c>
/// (422: the input refers to something that does not exist). It is never written; a rule across two aggregates is a saga.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
public sealed class ReadAttribute : Attribute;

/// <summary>The read-only aggregates loaded for a change's <c>[Read]</c> members, by member name.</summary>
public sealed class Reads
{
    private readonly IReadOnlyDictionary<string, object> _byMember;

    /// <summary>Creates the set. Called by generated code.</summary>
    /// <param name="byMember">The loaded aggregates by member name.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Reads(IReadOnlyDictionary<string, object> byMember)
    {
        ArgumentNullException.ThrowIfNull(byMember);
        _byMember = byMember;
    }

    /// <summary>The single aggregate of type <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">None or several of that type; use <see cref="Get{T}(string)"/>.</exception>
    public T Get<T>()
        where T : class
    {
        T? found = null;
        foreach (var value in _byMember.Values)
        {
            if (value is not T typed)
                continue;
            if (found is not null)
                throw new InvalidOperationException($"Several [Read] members are {typeof(T).Name}; use Read<{typeof(T).Name}>(nameof(member)).");
            found = typed;
        }
        return found ?? throw new InvalidOperationException($"No [Read] member is {typeof(T).Name}.");
    }

    /// <summary>The aggregate of the member named <paramref name="member"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="member">The member's name.</param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">No <c>[Read]</c> member of that name and type.</exception>
    public T Get<T>(string member)
        where T : class
        => _byMember.TryGetValue(member, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"No [Read] member '{member}' of type {typeof(T).Name}.");
}

/// <summary>Implemented by generated code for changes with <c>[Read]</c> members.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IHasReads
{
    /// <summary>Loads every <c>[Read]</c> aggregate, or returns the first missing one as <c>Error.UnprocessableEntity</c>.</summary>
    /// <param name="reader">The store.</param>
    /// <param name="context">The operation (tenant scope).</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    /// <returns>The loaded set, or the failure.</returns>
    ValueTask<Result<Reads>> LoadReads(IAggregateReader reader, OperationContext context, CancellationToken cancellationToken);
}
