using Tarol.Kernel;

namespace Tarol.Changes;

/// <summary>What a change gets besides its aggregate.</summary>
/// <param name="Operation">Who acts, for which tenant, in which conversation.</param>
/// <param name="Now">The time, read once per change from the pipeline's <see cref="TimeProvider"/>.</param>
/// <param name="Reads">The read-only aggregates of the change's <c>[Read]</c> members, if it has any.</param>
public readonly record struct ChangeContext(OperationContext Operation, DateTimeOffset Now, Reads? Reads = null)
{
    /// <summary>The codes the caller confirmed (<see cref="ChangeOptions.Confirmed"/>, HTTP <c>Tarol-Confirm</c>; TN-37).</summary>
    public IReadOnlyCollection<string>? Confirmations { get; init; }

    /// <summary>
    /// Whether the caller confirmed the warning <paramref name="code"/>: answer <c>Error.ConfirmationRequired(code, …)</c>
    /// when it did not (TN-37). A reaction or job confirms nothing unless its runner passes the codes.
    /// </summary>
    /// <param name="code">The warning's code.</param>
    /// <returns><see langword="true"/> when confirmed.</returns>
    public bool Confirmed(string code) => Confirmations is { } codes && codes.Contains(code, StringComparer.Ordinal);

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

    /// <summary>
    /// The single read-only aggregate of type <typeparamref name="T"/>, if one was loaded (TN-40, Tatalu FB-F03.3-2): an
    /// optional read (a hand-written <c>IHasReads</c> that skips a missing aggregate) without catching an exception.
    /// </summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate, or <see langword="null"/>.</param>
    /// <returns>Whether one was loaded.</returns>
    /// <exception cref="InvalidOperationException">Several of that type were loaded; use <see cref="Read{T}(string)"/>.</exception>
    public bool TryRead<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? aggregate)
        where T : class
    {
        aggregate = Reads?.Find<T>();
        return aggregate is not null;
    }
}
