using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Results;

namespace Tarol.Audit;

/// <summary>Reads the change log of the caller's tenant (TN-64, AU-4).</summary>
public interface IAuditLog
{
    /// <summary>One page, newest first; personal values opened with their subject's key (an erased subject's read as <c>null</c>).</summary>
    /// <param name="query">The filters and the cursor.</param>
    /// <param name="context">The caller: its tenant is the log's, never a request value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page, or 400 for a bad limit or cursor.</returns>
    ValueTask<Result<AuditPage>> Query(AuditQuery query, OperationContext context, CancellationToken cancellationToken);
}

internal sealed class AuditLog(AuditHost host, IKeyStore? keys) : IAuditLog
{
    public async ValueTask<Result<AuditPage>> Query(AuditQuery query, OperationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Limit is < 1 or > AuditQuery.MaxLimit)
            return Invalid.Field("limit", "Range", $"between 1 and {AuditQuery.MaxLimit}");
        (DateTimeOffset, Guid)? after = null;
        if (query.After is { } cursor)
        {
            if (!TryDecode(cursor, out var at, out var id))
                return Invalid.Field("after", "Query.Cursor", "the cursor is not valid; start again without 'after'");
            after = (at, id);
        }
        var tenant = AuditHost.TenantOf(context);
        var rows = await host.Store.Query(host.LogVersion, tenant, query, after, query.Limit + 1, cancellationToken).ConfigureAwait(false);
        var more = rows.Count > query.Limit;
        var items = new List<AuditEntry>(Math.Min(rows.Count, query.Limit));
        foreach (var row in rows.Take(query.Limit))
            items.Add(row with { Before = await Open(row.Before, tenant, cancellationToken).ConfigureAwait(false), After = await Open(row.After, tenant, cancellationToken).ConfigureAwait(false) });
        return new AuditPage(items, more && items.Count > 0 ? Encode(items[^1].At, items[^1].Id) : null);
    }

    private async ValueTask<string?> Open(string? stored, Guid tenant, CancellationToken cancellationToken)
    {
        if (stored is null)
            return null;
        var bytes = Encoding.UTF8.GetBytes(stored);
        if (!EventCodec.IsSealed(bytes))
            return stored;
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(bytes, subjects);
        var ring = keys is null ? null : await keys.Keys(tenant, subjects, create: false, cancellationToken).ConfigureAwait(false);
        var opened = new ArrayBufferWriter<byte>(bytes.Length);
        EventCodec.OpenState(bytes, ring, opened);
        var text = Encoding.UTF8.GetString(opened.WrittenSpan);
        return text == "null" ? null : text;
    }

    private static string Encode(DateTimeOffset at, Guid id)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}:{id:N}")));

    private static bool TryDecode(string cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        if (cursor.Length > 128)
            return false;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = text.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out id)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return false;
            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
