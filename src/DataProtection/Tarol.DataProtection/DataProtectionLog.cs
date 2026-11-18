using Microsoft.Extensions.Logging;

namespace Tarol.DataProtection;

/// <summary>The log events of Tarol.DataProtection (16001–16008). They carry ids and counts, never personal values.</summary>
internal static partial class DataProtectionLog
{
    [LoggerMessage(EventId = 16001, Level = LogLevel.Information, Message = "Erased subject {Subject} of tenant {Tenant} by {Actor}: {Streams} stream(s), {Participants} participant(s)")]
    public static partial void SubjectErased(ILogger logger, Guid subject, Guid tenant, string actor, int streams, int participants);

    [LoggerMessage(EventId = 16002, Level = LogLevel.Warning, Message = "Erased tenant {Tenant} by {Actor}: no data key of the tenant opens any more")]
    public static partial void TenantErased(ILogger logger, Guid tenant, string actor);

    [LoggerMessage(EventId = 16003, Level = LogLevel.Information, Message = "Exported subject {Subject} of tenant {Tenant} for {Actor}: {Events} event(s), {States} state(s)")]
    public static partial void SubjectExported(ILogger logger, Guid subject, Guid tenant, string actor, int events, int states);

    [LoggerMessage(EventId = 16004, Level = LogLevel.Information, Message = "Retention deleted {Count} stream(s) of {StreamType} older than {Cutoff}")]
    public static partial void RetentionDeleted(ILogger logger, int count, string streamType, DateTimeOffset cutoff);

    [LoggerMessage(EventId = 16005, Level = LogLevel.Error, Message = "The erasure participant {Participant} failed for tenant {Tenant}; the erasure is incomplete, run it again")]
    public static partial void ParticipantFailed(ILogger logger, Exception exception, string participant, Guid tenant);

    [LoggerMessage(EventId = 16006, Level = LogLevel.Debug, Message = "Evicted the erased tenant {Tenant} from {Participants} in-memory participant(s)")]
    public static partial void Evicted(ILogger logger, Guid tenant, int participants);

    [LoggerMessage(EventId = 16007, Level = LogLevel.Information, Message = "Subject {Subject} of tenant {Tenant} was erased already; the forgetting ran again")]
    public static partial void ErasedAgain(ILogger logger, Guid subject, Guid tenant);

    [LoggerMessage(EventId = 16008, Level = LogLevel.Warning, Message = "Retention of {StreamType} skipped {Count} stream(s) with pending reactions")]
    public static partial void RetentionSkipped(ILogger logger, string streamType, int count);
}
