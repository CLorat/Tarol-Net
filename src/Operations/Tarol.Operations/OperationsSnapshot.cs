using Tarol.Persistence;

namespace Tarol.Operations;

/// <summary>
/// The last poll of the registered <see cref="OperationsSignal"/>s (O-1): what the gauges publish and the checks judge. A failed
/// poll keeps the last values and counts <see cref="Failures"/>.
/// </summary>
/// <param name="Values">The values by signal name (a signal that failed keeps its last values).</param>
/// <param name="LastSuccess">When a poll last read every signal; <see langword="null"/> before the first.</param>
/// <param name="LastPoll">When the last poll ended.</param>
/// <param name="Failures">Failed polls in a row (0 after a good one).</param>
/// <param name="LastError">Why the last poll failed.</param>
public sealed record OperationsSnapshot(IReadOnlyDictionary<string, IReadOnlyList<SignalValue>> Values, DateTimeOffset? LastSuccess,
    DateTimeOffset? LastPoll, int Failures, string? LastError)
{
    /// <summary>Before the first poll.</summary>
    public static OperationsSnapshot Empty { get; } = new(new Dictionary<string, IReadOnlyList<SignalValue>>(StringComparer.Ordinal), null, null, 0, null);

    /// <summary>Whether a signal of this name was read.</summary>
    /// <param name="name">The signal.</param>
    /// <returns><see langword="true"/> when it has values.</returns>
    public bool Has(string name) => Values.TryGetValue(name, out var values) && values.Count > 0;

    /// <summary>The largest value of a signal (over its tags); 0 when it has none.</summary>
    /// <param name="name">The signal.</param>
    /// <returns>The value.</returns>
    public double Max(string name) => Values.TryGetValue(name, out var values) && values.Count > 0 ? values.Max(static v => v.Value) : 0;

    /// <summary>The sum of a signal's values; 0 when it has none.</summary>
    /// <param name="name">The signal.</param>
    /// <returns>The value.</returns>
    public double Sum(string name) => Values.TryGetValue(name, out var values) ? values.Sum(static v => v.Value) : 0;
}
