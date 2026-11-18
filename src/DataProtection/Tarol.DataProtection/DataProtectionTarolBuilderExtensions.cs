using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;

namespace Tarol.DataProtection;

/// <summary>Registers data protection on a Tarol application (step 16C).</summary>
public static class DataProtectionTarolBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="IDataSubjects"/> (<see cref="DataSubjects"/>), the master key from configuration
    /// (<c>Tarol:DataProtection:MasterKey</c>) as the <see cref="IKeyEncryptionKey"/>, the personal-lookups participant, the
    /// eviction of in-memory participants on every process's erasure signal, and the system job <c>tarol.retention</c>
    /// (runs with a job host, <c>AddJobs</c>). The key store comes from the store package (<c>UseNpgsqlKeyStore()</c>) or
    /// <see cref="UseInMemoryKeyStore"/>; register every aggregate with the codec first.
    /// <code>
    /// builder.Services.AddTarol(json, tarol => tarol.Add&lt;Customer&gt;()
    ///     .UseNpgsqlStore(cs).UseNpgsqlKeyStore()
    ///     .AddDataProtection(o => o.Retention["order"] = 3650));
    /// </code>
    /// </summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <param name="configure">Code defaults; configuration overrides them.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder AddDataProtection(this TarolBuilder builder, Action<DataProtectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton(provider =>
        {
            var options = new DataProtectionOptions();
            configure?.Invoke(options);
            Read(provider.GetService<IConfiguration>(), options);
            options.Validate();
            return options;
        });
        services.TryAddSingleton<IKeyEncryptionKey>(static provider =>
        {
            var options = provider.GetRequiredService<DataProtectionOptions>();
            if (string.IsNullOrWhiteSpace(options.MasterKey))
                throw new InvalidOperationException("Data protection needs a master key: set Tarol:DataProtection:MasterKey (base64 of 32 bytes) from a secret store, or register an IKeyEncryptionKey.");
            var key = Convert.FromBase64String(options.MasterKey);
            try
            {
                return new ConfiguredKeyEncryptionKey(options.MasterKeyId, key,
                    options.PreviousKeys.Select(static p => new KeyValuePair<string, byte[]>(p.Key, Convert.FromBase64String(p.Value))));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        });
        services.TryAddSingleton(static provider => new DataProtectionMetrics(provider.GetService<IMeterFactory>()));
        services.AddSingleton<ErasureParticipant>(static provider => new PersonalLookupsEraser(provider.GetRequiredService<IDataProtectionStore>()));
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<DataProtectionOptions>();
            var policy = new RetentionPolicy();
            foreach (var (streamType, days) in builder.Codec.RetentionDays)
                policy.Set(streamType, days);
            foreach (var (streamType, days) in options.Retention)
                policy.Set(streamType, days);
            return policy;
        });
        services.TryAddSingleton(provider => new DataSubjects(
            provider.GetRequiredService<IKeyStore>(),
            provider.GetRequiredService<IDataProtectionStore>(),
            provider.GetServices<ErasureParticipant>(),
            builder.Codec.Options,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<DataSubjects>(),
            provider.GetRequiredService<DataProtectionMetrics>()));
        services.TryAddSingleton<IDataSubjects>(static provider => provider.GetRequiredService<DataSubjects>());
        services.AddHostedService(static provider => new ErasureSignals(provider.GetRequiredService<IKeyStore>(), provider.GetServices<ErasureParticipant>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<ErasureSignals>()));
        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<DataProtectionOptions>();
            return new MaintenanceTask(Retention.JobName, options.RetentionEvery, static (services, at, ct) =>
            {
                var options = services.GetRequiredService<DataProtectionOptions>();
                return new Retention(services.GetRequiredService<IDataProtectionStore>(), services.GetRequiredService<RetentionPolicy>(),
                    services.GetServices<ErasureParticipant>(), options.RetentionBatch, services.GetRequiredService<DataProtectionMetrics>(),
                    services.GetService<ILoggerFactory>()?.CreateLogger("Tarol.DataProtection.Retention")).Run(at, ct);
            });
        });
        return builder;
    }

    /// <summary>Registers an <see cref="InMemoryKeyStore"/> as the <see cref="IKeyStore"/> (tests and samples; the in-memory store then seals).</summary>
    /// <param name="builder">The Tarol builder.</param>
    /// <returns>The builder.</returns>
    public static TarolBuilder UseInMemoryKeyStore(this TarolBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(static provider => new InMemoryKeyStore(provider.GetRequiredService<IKeyEncryptionKey>(), provider.GetRequiredService<TimeProvider>()));
        builder.Services.TryAddSingleton<IKeyStore>(static provider => provider.GetRequiredService<InMemoryKeyStore>());
        return builder;
    }

    private static void Read(IConfiguration? configuration, DataProtectionOptions options)
    {
        if (configuration is null)
            return;
        var section = configuration.GetSection("Tarol:DataProtection");
        if (section["MasterKey"] is { Length: > 0 } key)
            options.MasterKey = key;
        if (section["MasterKeyId"] is { Length: > 0 } id)
            options.MasterKeyId = id;
        foreach (var previous in section.GetSection("PreviousKeys").GetChildren())
            if (previous.Value is { Length: > 0 } value)
                options.PreviousKeys[previous.Key] = value;
        foreach (var type in configuration.GetSection("Tarol:Retention").GetChildren())
        {
            var days = type["Days"];
            options.Retention[type.Key] = string.IsNullOrWhiteSpace(days) || days == "0"
                ? null
                : int.Parse(days, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
    }
}
