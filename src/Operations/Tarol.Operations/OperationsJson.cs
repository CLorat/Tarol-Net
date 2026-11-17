using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.StronglyTypedId;

namespace Tarol.Operations;

/// <summary>A dead letter as the admin endpoints answer it.</summary>
/// <param name="Id">Its id within its kind.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The reaction, job or subscription.</param>
/// <param name="Tenant">The tenant; <see langword="null"/> for none.</param>
/// <param name="Subject">What failed.</param>
/// <param name="Attempts">How often it was tried.</param>
/// <param name="Error">Why the last attempt failed.</param>
/// <param name="DeadAt">When it was given up.</param>
public sealed record DeadLetterView(long Id, string Kind, string Name, Guid? Tenant, string Subject, int Attempts, string? Error, DateTimeOffset DeadAt)
{
    internal static DeadLetterView Of(DeadLetterItem item) => new(item.Id, item.Kind, item.Name, item.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : null, item.Subject,
        item.Attempts, item.Error.TryGetValue(out string? e) ? e : null, item.DeadAt);
}

/// <summary>A page of the admin endpoints.</summary>
/// <param name="Items">The dead letters.</param>
/// <param name="Next">The <c>after</c> of the next page; <see langword="null"/> on the last.</param>
public sealed record DeadLetterPageView(IReadOnlyList<DeadLetterView> Items, long? Next);

/// <summary>The answer of a filtered re-drive.</summary>
/// <param name="Redriven">How many were re-driven.</param>
public sealed record RedriveView(int Redriven);

/// <summary>An error answer of the admin endpoints.</summary>
/// <param name="Error">What went wrong.</param>
public sealed record AdminError(string Error);

/// <summary>The source-generated JSON of the admin endpoints (no reflection).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DeadLetterPageView))]
[JsonSerializable(typeof(RedriveView))]
[JsonSerializable(typeof(AdminError))]
[JsonSerializable(typeof(IReadOnlyList<ProjectionVersion>))]
public sealed partial class OperationsJson : JsonSerializerContext;
