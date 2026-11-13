using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.StronglyTypedId;

namespace Tarol.Kernel.Json;

/// <summary>
/// Source-generated JSON metadata for the Kernel types, for serializing them on their own
/// (<c>JsonSerializer.Serialize(context, KernelJsonContext.Default.OperationContext)</c>). In your own types you need
/// nothing: <see cref="OperationContext"/> and <see cref="Actor"/> carry their converters.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OperationContext))]
[JsonSerializable(typeof(Actor))]
[JsonSerializable(typeof(Id<ITenant>))]
[JsonSerializable(typeof(Id<ICorrelation>))]
[JsonSerializable(typeof(Id<ICausation>))]
public sealed partial class KernelJsonContext : JsonSerializerContext;
