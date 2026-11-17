using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Tarol.Testing.Metrics;

/// <summary>
/// An <see cref="IMeterFactory"/> of one test and the measurements of its meters (step 15): the meters are scoped to this
/// factory, so tests running in parallel on the same meter names do not see each other's numbers.
/// </summary>
public sealed class Measurements : IMeterFactory
{
    private readonly ConcurrentDictionary<string, Meter> _meters = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Measured> _measured = new();
    private readonly MeterListener _listener = new();

    public Measurements()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, this))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        _listener.Start();
    }

    /// <summary>One measurement.</summary>
    public sealed record Measured(string Meter, string Instrument, string? Unit, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Everything measured so far.</summary>
    public IReadOnlyList<Measured> All => [.. _measured];

    /// <summary>The measurements of one instrument.</summary>
    public IReadOnlyList<Measured> Of(string instrument) => [.. _measured.Where(m => m.Instrument == instrument)];

    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _meters.GetOrAdd(options.Name, name =>
        {
            var meter = new Meter(new MeterOptions(name) { Version = options.Version, Tags = options.Tags, Scope = this });
            return meter;
        });
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            map[tag.Key] = tag.Value;
        _measured.Enqueue(new Measured(instrument.Meter.Name, instrument.Name, instrument.Unit, value, map));
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var meter in _meters.Values)
            meter.Dispose();
    }
}
