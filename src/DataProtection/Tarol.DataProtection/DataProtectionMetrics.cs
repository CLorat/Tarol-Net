using System.Diagnostics.Metrics;

namespace Tarol.DataProtection;

/// <summary>The meter <c>Tarol.DataProtection</c> (no tenant or subject tag, O-1).</summary>
internal sealed class DataProtectionMetrics : IDisposable
{
    public const string MeterName = "Tarol.DataProtection";

    private readonly Meter _meter;

    public DataProtectionMetrics(IMeterFactory? meters)
    {
        _meter = meters?.Create(MeterName) ?? new Meter(MeterName);
        Erased = _meter.CreateCounter<long>("tarol.subjects.erased", "{subject}", "Subjects (and tenants) erased.");
        Exported = _meter.CreateCounter<long>("tarol.subjects.exported", "{subject}", "Subjects exported.");
        RetentionStreams = _meter.CreateCounter<long>("tarol.retention.streams", "{stream}", "Streams deleted by retention.");
    }

    public Counter<long> Erased { get; }

    public Counter<long> Exported { get; }

    public Counter<long> RetentionStreams { get; }

    public void Dispose() => _meter.Dispose();
}
