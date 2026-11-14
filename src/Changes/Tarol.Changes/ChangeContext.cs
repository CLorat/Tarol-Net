using Tarol.Kernel;

namespace Tarol.Changes;

/// <summary>What a change gets besides its aggregate.</summary>
/// <param name="Operation">Who acts, for which tenant, in which conversation.</param>
/// <param name="Now">The time, read once per change from the pipeline's <see cref="TimeProvider"/>.</param>
/// <param name="Reads">The read-only aggregates of the change's <c>[Read]</c> members, if it has any.</param>
public readonly record struct ChangeContext(OperationContext Operation, DateTimeOffset Now, Reads? Reads = null)
{
    /// <summary>The single read-only aggregate of type <typeparamref name="T"/> loaded for a <c>[Read]</c> member.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <returns>The aggregate.</returns>
    /// <exception cref="InvalidOperationException">The change has no <c>[Read]</c> member of that type, or several.</exception>
    public T Read<T>()
        where T : class
        => (Reads ?? throw new InvalidOperationException("This change declares no [Read] members.")).Get<T>();

    /// <summary>The read-only aggregate loaded for the <c>[Read]</c> member named <paramref name="member"/>.</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="member">The member's name (<c>nameof(Artwork)</c>).</param>
    /// <returns>The aggregate.</returns>
    public T Read<T>(string member)
        where T : class
        => (Reads ?? throw new InvalidOperationException("This change declares no [Read] members.")).Get<T>(member);
}
