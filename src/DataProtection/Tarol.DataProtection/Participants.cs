using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tarol.Persistence;

namespace Tarol.DataProtection;

/// <summary>The erasure participant of personal lookups (<c>StoreLookups.DefinePersonal</c>): the subject's streams' rows go (DP-5).</summary>
/// <param name="store">The store.</param>
public sealed class PersonalLookupsEraser(IDataProtectionStore store) : ErasureParticipant
{
    /// <inheritdoc />
    public override string Name => "personal-lookups";

    /// <inheritdoc />
    public override async ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
        => await store.DeletePersonalLookups(tenant, streams, cancellationToken).ConfigureAwait(false);
}

/// <summary>Relays the key store's erasure signal (raised in every process) to the participants' in-memory eviction.</summary>
internal sealed class ErasureSignals(IKeyStore keys, IEnumerable<ErasureParticipant> participants, ILogger<ErasureSignals>? logger = null) : IHostedService
{
    private readonly ErasureParticipant[] _participants = [.. participants];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        keys.Erased += OnErased;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        keys.Erased -= OnErased;
        return Task.CompletedTask;
    }

    private void OnErased(object? sender, KeyErasedEventArgs e)
    {
        foreach (var participant in _participants)
            participant.Evict(e.Tenant, e.Subject);
        if (logger is not null)
            DataProtectionLog.Evicted(logger, e.Tenant, _participants.Length);
    }
}

/// <summary>The system job <c>tarol.retention</c> (DP-7): deletes the expired streams of every stream type with a retention.</summary>
internal sealed class Retention(IDataProtectionStore store, RetentionPolicy policy, IEnumerable<ErasureParticipant> participants, int batch, DataProtectionMetrics? metrics,
    ILogger? logger)
{
    public const string JobName = "tarol.retention";

    private readonly ErasureParticipant[] _participants = [.. participants];

    public async Task<int> Run(DateTimeOffset at, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var (streamType, days) in policy.Days)
        {
            var cutoff = at.AddDays(-days);
            var deleted = 0;
            while (true)
            {
                var streams = await store.DeleteStreams(streamType, cutoff, batch, cancellationToken).ConfigureAwait(false);
                if (streams.Count == 0)
                    break;
                foreach (var participant in _participants)
                    await participant.Deleted(streams, cancellationToken).ConfigureAwait(false);
                await store.Audit("system:" + JobName, "retention", $"stream-type:{streamType}",
                    System.Text.Json.JsonSerializer.Serialize(new RetentionBatch(streams.Count, cutoff), DataProtectionJson.Default.RetentionBatch), cancellationToken).ConfigureAwait(false);
                deleted += streams.Count;
                metrics?.RetentionStreams.Add(streams.Count);
                if (streams.Count < batch)
                    break;
            }

            if (deleted > 0 && logger is not null)
                DataProtectionLog.RetentionDeleted(logger, deleted, streamType, cutoff);
            total += deleted;
        }

        return total;
    }
}

internal sealed record RetentionBatch(int Streams, DateTimeOffset Cutoff);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(RetentionBatch))]
internal sealed partial class DataProtectionJson : JsonSerializerContext;
