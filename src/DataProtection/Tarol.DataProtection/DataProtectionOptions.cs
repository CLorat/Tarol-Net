namespace Tarol.DataProtection;

/// <summary>
/// How <see cref="DataProtectionTarolBuilderExtensions.AddDataProtection"/> behaves. Configuration overrides the code:
/// <c>Tarol:DataProtection:MasterKey</c> (base64, 32 bytes), <c>Tarol:DataProtection:MasterKeyId</c>,
/// <c>Tarol:DataProtection:PreviousKeys:&lt;id&gt;</c> (base64, during a rotation) and <c>Tarol:Retention:&lt;streamType&gt;:Days</c>.
/// </summary>
public sealed class DataProtectionOptions
{
    /// <summary>The master key (base64 of 32 bytes); keep it in a secret store. <see langword="null"/> = an <c>IKeyEncryptionKey</c> registered elsewhere.</summary>
    public string? MasterKey { get; set; }

    /// <summary>The master key's id, stored with every wrapped tenant key (default <c>"1"</c>).</summary>
    public string MasterKeyId { get; set; } = "1";

    /// <summary>Earlier master keys by id (base64), still accepted for unwrapping until <c>IKeyStore.Rewrap</c> has run.</summary>
    public IDictionary<string, string> PreviousKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Retention per stream type in days, over <c>[Retention(Days)]</c>; <see langword="null"/> = kept forever.</summary>
    public IDictionary<string, int?> Retention { get; } = new Dictionary<string, int?>(StringComparer.Ordinal);

    /// <summary>How often <c>tarol.retention</c> runs (default 1 hour).</summary>
    public TimeSpan RetentionEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Streams per delete statement (default 500; data-protection.md §4.7).</summary>
    public int RetentionBatch { get; set; } = 500;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(MasterKeyId);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetentionEvery, TimeSpan.FromMinutes(1), nameof(RetentionEvery));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetentionBatch, 1, nameof(RetentionBatch));
        foreach (var (type, days) in Retention)
            if (days is < 1)
                throw new ArgumentOutOfRangeException(nameof(Retention), $"The retention of '{type}' must be at least 1 day (or null for none).");
    }
}
