using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;

namespace Tarol.DataProtection;

/// <summary>
/// The <see cref="IDataSubjects"/> of Tarol (DP-5, DP-6): an erasure writes the key's tombstone (the key store's
/// <c>NOTIFY</c> evicts it in every process), finds the subject's streams, runs every <see cref="ErasureParticipant"/> in
/// registration order (projections re-fold, page caches drop the tenant, personal lookups go), audits the act and logs it.
/// An export returns one JSON document of every event holding the subject's data and the state of its own streams,
/// opened with that subject's key only.
/// </summary>
/// <remarks>
/// A participant that fails does not stop the others; the erasure then throws <see cref="ErasureIncompleteException"/>
/// after the audit row. The tombstone stays: run the erasure again, the forgetting is idempotent.
/// </remarks>
public sealed class DataSubjects : IDataSubjects
{
    private readonly IKeyStore _keys;
    private readonly IDataProtectionStore _store;
    private readonly IReadOnlyList<ErasureParticipant> _participants;
    private readonly JsonSerializerOptions _json;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly DataProtectionMetrics? _metrics;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="keys">The key store.</param>
    /// <param name="store">The store's data-protection port.</param>
    /// <param name="participants">The participants, in the order they run.</param>
    /// <param name="json">The JSON options of the events (the codec's), for the export.</param>
    /// <param name="time">The clock of the export's timestamp.</param>
    /// <param name="logger">Logs 16001–16007; <see langword="null"/> = none.</param>
    public DataSubjects(IKeyStore keys, IDataProtectionStore store, IEnumerable<ErasureParticipant> participants, JsonSerializerOptions json, TimeProvider time,
        ILogger<DataSubjects>? logger = null)
        : this(keys, store, participants, json, time, logger, null)
    {
    }

    internal DataSubjects(IKeyStore keys, IDataProtectionStore store, IEnumerable<ErasureParticipant> participants, JsonSerializerOptions json, TimeProvider time,
        ILogger? logger, DataProtectionMetrics? metrics)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(time);
        _keys = keys;
        _store = store;
        _participants = [.. participants];
        _json = json;
        _time = time;
        _log = logger;
        _metrics = metrics;
    }

    /// <summary>The participants, in the order an erasure runs them.</summary>
    public IReadOnlyList<ErasureParticipant> Participants => _participants;

    /// <inheritdoc />
    public async ValueTask<SubjectErasure> EraseSubject(Guid tenant, Guid subject, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var erasedNow = await _keys.EraseSubject(tenant, subject, cancellationToken).ConfigureAwait(false);
        if (!erasedNow && _log is not null)
            DataProtectionLog.ErasedAgain(_log, subject, tenant);
        var streams = await _store.StreamsOf(tenant, subject, cancellationToken).ConfigureAwait(false);
        var failed = new List<string>();
        foreach (var participant in _participants)
        {
            try
            {
                await participant.Forget(tenant, subject, streams, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add(participant.Name);
                if (_log is not null)
                    DataProtectionLog.ParticipantFailed(_log, e, participant.Name, tenant);
            }
        }

        await _store.Audit(actor, "erase", $"subject:{subject}", Detail(tenant, streams.Count, failed), cancellationToken).ConfigureAwait(false);
        _metrics?.Erased.Add(1);
        if (_log is not null)
            DataProtectionLog.SubjectErased(_log, subject, tenant, actor, streams.Count, _participants.Count);
        return failed.Count == 0
            ? new SubjectErasure(tenant, subject, !erasedNow, streams)
            : throw new ErasureIncompleteException(tenant, subject, failed);
    }

    /// <inheritdoc />
    public async ValueTask<SubjectErasure> EraseTenant(Guid tenant, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var erasedNow = await _keys.EraseTenant(tenant, cancellationToken).ConfigureAwait(false);
        var failed = new List<string>();
        foreach (var participant in _participants)
        {
            try
            {
                await participant.ForgetTenant(tenant, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add(participant.Name);
                if (_log is not null)
                    DataProtectionLog.ParticipantFailed(_log, e, participant.Name, tenant);
            }
        }

        await _store.Audit(actor, "erase", $"tenant:{tenant}", Detail(tenant, 0, failed), cancellationToken).ConfigureAwait(false);
        _metrics?.Erased.Add(1);
        if (_log is not null)
            DataProtectionLog.TenantErased(_log, tenant, actor);
        return failed.Count == 0
            ? new SubjectErasure(tenant, null, !erasedNow, [])
            : throw new ErasureIncompleteException(tenant, null, failed);
    }

    /// <inheritdoc />
    public async ValueTask<SubjectExport> Export(Guid tenant, Guid subject, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var events = await _store.EventsOf(tenant, subject, cancellationToken).ConfigureAwait(false);
        var own = (await _store.StreamsOf(tenant, subject, cancellationToken).ConfigureAwait(false)).Where(s => s.Id == subject).ToArray();
        var states = new List<(StreamRef Stream, byte[] State)>();
        foreach (var stream in own)
        {
            if (await _store.StateOf(stream, subject, cancellationToken).ConfigureAwait(false) is { } state)
                states.Add((stream, state));
        }

        var document = Document(tenant, subject, events, states);
        await _store.Audit(actor, "export", $"subject:{subject}",
            string.Create(CultureInfo.InvariantCulture, $$"""{"tenant":"{{tenant}}","events":{{events.Count}},"states":{{states.Count}}}"""), cancellationToken).ConfigureAwait(false);
        _metrics?.Exported.Add(1);
        if (_log is not null)
            DataProtectionLog.SubjectExported(_log, subject, tenant, actor, events.Count, states.Count);
        return new SubjectExport(tenant, subject, document);
    }

    private byte[] Document(Guid tenant, Guid subject, IReadOnlyList<PositionedEvent> events, List<(StreamRef Stream, byte[] State)> states)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", tenant);
            writer.WriteString("subject", subject);
            writer.WriteString("exportedAt", _time.GetUtcNow());
            writer.WriteStartArray("events");
            foreach (var e in events)
            {
                writer.WriteStartObject();
                writer.WriteString("streamType", e.StreamType);
                writer.WriteString("streamId", e.Event.StreamId);
                writer.WriteNumber("version", e.Event.Event.Version);
                writer.WriteString("eventId", e.Event.EventId);
                writer.WriteString("recordedAt", e.Event.RecordedAt);
                writer.WriteString("type", e.Event.Event.Event.GetType().Name);
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, e.Event.Event.Event, _json.GetTypeInfo(e.Event.Event.Event.GetType()));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("states");
            foreach (var (stream, state) in states)
            {
                writer.WriteStartObject();
                writer.WriteString("streamType", stream.StreamType);
                writer.WriteString("streamId", stream.Id);
                writer.WritePropertyName("state");
                writer.WriteRawValue(state, skipInputValidation: false);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string Detail(Guid tenant, int streams, List<string> failed)
        => failed.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $$"""{"tenant":"{{tenant}}","streams":{{streams}}}""")
            : string.Create(CultureInfo.InvariantCulture, $$"""{"tenant":"{{tenant}}","streams":{{streams}},"failed":[{{string.Join(",", failed.Select(static f => JsonSerializer.Serialize(f, DataProtectionJson.Default.String)))}}]}""");
}

/// <summary>An erasure wrote the tombstone, but a participant failed; run it again (the forgetting is idempotent).</summary>
public sealed class ErasureIncompleteException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject; <see langword="null"/> for a tenant.</param>
    /// <param name="failed">The failed participants.</param>
    public ErasureIncompleteException(Guid tenant, Guid? subject, IReadOnlyList<string> failed)
        : base($"The erasure of {(subject is { } s ? $"subject {s}" : "the tenant")} in tenant {tenant} is incomplete: {string.Join(", ", failed)} failed. The key is erased; run the erasure again.")
    {
        Tenant = tenant;
        Subject = subject;
        Failed = failed;
    }

    /// <summary>Creates the exception.</summary>
    public ErasureIncompleteException()
    {
        Failed = [];
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public ErasureIncompleteException(string message)
        : base(message)
    {
        Failed = [];
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ErasureIncompleteException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failed = [];
    }

    /// <summary>The tenant.</summary>
    public Guid Tenant { get; }

    /// <summary>The subject; <see langword="null"/> for a tenant.</summary>
    public Guid? Subject { get; }

    /// <summary>The names of the failed participants.</summary>
    public IReadOnlyList<string> Failed { get; }
}
