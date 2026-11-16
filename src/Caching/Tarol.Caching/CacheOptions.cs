namespace Tarol.Caching;

/// <summary>
/// The bounds of <see cref="MemoryPageCache"/>. They only bound memory: an entry is valid exactly while the projection's
/// checkpoint it was read at is current, never by time (CA-5).
/// </summary>
public sealed class CacheOptions
{
    /// <summary>The most entries (default 10,000); a write above it evicts the least recently used.</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>
    /// The most bytes (default 64 MB), estimated per entry as the rows' UTF-8 JSON length plus the key (the objects in memory
    /// take more); a write above it evicts the least recently used. A page larger than this is not cached.
    /// </summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>How long an entry nobody asked for stays (default 5 min, on the registered <see cref="TimeProvider"/>).</summary>
    public TimeSpan IdleExpiry { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntries, 1, nameof(MaxEntries));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBytes, 1, nameof(MaxBytes));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleExpiry, TimeSpan.Zero, nameof(IdleExpiry));
    }

    internal CacheOptions Copy() => new() { MaxEntries = MaxEntries, MaxBytes = MaxBytes, IdleExpiry = IdleExpiry };
}
