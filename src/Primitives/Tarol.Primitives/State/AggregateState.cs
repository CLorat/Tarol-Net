using System.Buffers;
using System.Text.Json;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives;

/// <summary>
/// Convenience over the generated <c>WriteState</c>/<c>ReadState</c> of an aggregate: its current state as UTF-8 JSON
/// bytes, and back. A store with its own buffers calls the static members directly.
/// <code>
/// byte[] json = AggregateState.Write(order, options);
/// Option&lt;Order&gt; loaded = AggregateState.Read(order.Id, json, options);   // None: another schema, replay instead
/// </code>
/// </summary>
public static class AggregateState
{
    /// <summary>Writes the aggregate's current state as UTF-8 JSON.</summary>
    /// <typeparam name="TSelf">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="options">The JSON options for member values (value objects need <c>ValueObjectJson.Resolver</c> first).</param>
    /// <returns>The JSON.</returns>
    /// <exception cref="InvalidOperationException">The aggregate is broken, or it is <see cref="LoadByReplayAttribute">[LoadByReplay]</see>.</exception>
    public static byte[] Write<TSelf>(TSelf aggregate, JsonSerializerOptions options)
        where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        Write(buffer, aggregate, options);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes the aggregate's current state as UTF-8 JSON into <paramref name="buffer"/>.</summary>
    /// <typeparam name="TSelf">The aggregate type.</typeparam>
    /// <param name="buffer">The destination.</param>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="options">The JSON options for member values.</param>
    /// <exception cref="InvalidOperationException">The aggregate is broken, or it is <see cref="LoadByReplayAttribute">[LoadByReplay]</see>.</exception>
    public static void Write<TSelf>(IBufferWriter<byte> buffer, TSelf aggregate, JsonSerializerOptions options)
        where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
    {
        ArgumentNullException.ThrowIfNull(buffer);
        using var writer = new Utf8JsonWriter(buffer);
        TSelf.WriteState(writer, aggregate, options);
    }

    /// <summary>Restores an aggregate from its stored state, without events; <see cref="None"/> for another schema.</summary>
    /// <typeparam name="TSelf">The aggregate type.</typeparam>
    /// <param name="id">The aggregate's id.</param>
    /// <param name="utf8Json">The stored state.</param>
    /// <param name="options">The JSON options the state was written with.</param>
    /// <returns>The aggregate, or <see cref="None"/> when the state has another shape (or the aggregate loads by replay).</returns>
    /// <exception cref="JsonException">The state is malformed.</exception>
    /// <exception cref="InvalidOperationException">The state belongs to another aggregate or holds an entity twice.</exception>
    public static Option<TSelf> Read<TSelf>(Id<TSelf> id, ReadOnlySpan<byte> utf8Json, JsonSerializerOptions options)
        where TSelf : AggregateRoot<TSelf>, IAggregateRoot<TSelf>
    {
        var reader = new Utf8JsonReader(utf8Json);
        return TSelf.ReadState(id, ref reader, options);
    }
}
