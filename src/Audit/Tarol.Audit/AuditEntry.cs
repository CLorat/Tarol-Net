using Tarol.Kernel;

namespace Tarol.Audit;

/// <summary>What a row of the change log records (TN-64).</summary>
public enum AuditKind
{
    /// <summary>The first value of a field of a new object (the first event of a stream or entity that names it).</summary>
    Created,

    /// <summary>A field took another value.</summary>
    Changed,

    /// <summary>An <c>[AuditRemoved]</c> event removed the object.</summary>
    Removed,

    /// <summary>A granted read of a <c>Sensitive</c> view, list or row query (from Tarol.Security's access observer, AU-5).</summary>
    Read,

    /// <summary>A refused change or read of an authenticated caller (AU-5: anonymous refusals are only counted).</summary>
    Denied,
}

/// <summary>
/// One row of the change log: who changed which field of which object when, from what to what (TN-64). <see cref="Before"/> and
/// <see cref="After"/> are JSON values; a <c>[PersonalData]</c> value reads as <c>null</c> once its subject is erased.
/// </summary>
/// <param name="Id">The row's id: derived from the event and the field, so a rebuild writes the same rows.</param>
/// <param name="Tenant">The tenant (<see cref="Guid.Empty"/> for none).</param>
/// <param name="At">When the store recorded it.</param>
/// <param name="Kind">What it records.</param>
/// <param name="Actor">Who.</param>
/// <param name="StreamType">The aggregate's stream type.</param>
/// <param name="Target">The object: the entity of an entity event, else the stream.</param>
/// <param name="Field">The field (the member's JSON name); <see langword="null"/> for <see cref="AuditKind.Removed"/>, reads and denials.</param>
/// <param name="Before">The field's value before (JSON), or <see langword="null"/>.</param>
/// <param name="After">The field's value after (JSON), or <see langword="null"/>.</param>
public sealed record AuditEntry(Guid Id, Guid Tenant, DateTimeOffset At, AuditKind Kind, Actor Actor, string StreamType, Guid Target, string? Field,
    string? Before, string? After)
{
    /// <summary>The stream of the object (the object itself for a stream event).</summary>
    public Guid Stream { get; init; }

    /// <summary>The stored event type alias (<c>child.registered</c>), or the intent of a read or denial (<c>Read&lt;Child&gt;</c>).</summary>
    public string? Event { get; init; }

    /// <summary>When the fact happened on the device (AU-3 (b)): an <c>[AuditTime]</c> member, else <c>Tarol-Captured-At</c>.</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>The device that captured it (<c>Tarol-Device</c>), if any.</summary>
    public string? Device { get; init; }

    /// <summary>The view, list or row query of a read or denial.</summary>
    public string? View { get; init; }

    /// <summary>The refusal's code of a denial.</summary>
    public string? Code { get; init; }
}

/// <summary>
/// A query of the change log, newest first (TN-64, AU-4). The tenant is the caller's, never the request's; every filter is optional
/// and all must hold.
/// </summary>
public sealed record AuditQuery
{
    /// <summary>The largest page (200).</summary>
    public const int MaxLimit = 200;

    /// <summary>One object's history.</summary>
    public Guid? Target { get; init; }

    /// <summary>One actor's rows (<see cref="Actor.Id"/>).</summary>
    public string? Actor { get; init; }

    /// <summary>One aggregate type's rows.</summary>
    public string? StreamType { get; init; }

    /// <summary>One field's rows.</summary>
    public string? Field { get; init; }

    /// <summary>One kind of row.</summary>
    public AuditKind? Kind { get; init; }

    /// <summary>Rows recorded at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Rows recorded at or before this time.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>The cursor of the previous page (<see cref="AuditPage.Next"/>).</summary>
    public string? After { get; init; }

    /// <summary>The page size (1 … <see cref="MaxLimit"/>, default 50).</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>A page of the change log.</summary>
/// <param name="Items">The rows, newest first, personal values opened (erased: <c>null</c>).</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/>.</param>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, string? Next);
