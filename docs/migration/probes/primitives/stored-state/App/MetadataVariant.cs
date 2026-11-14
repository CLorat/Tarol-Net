using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tarol.Options;
using Tarol.Options.Json;
using Tarol.StronglyTypedId;

namespace Probe;

/// <summary>
/// Candidate "generated JsonMetadataServices type infos", written by hand as the generator would emit it, write side only.
/// The read side cannot be expressed: an entity's creator needs the root and the identity maps (no way to pass them into
/// nested object creation but ambient state), and the root needs its id before its children.
/// </summary>
public static class MetadataVariant
{
    private static (JsonSerializerOptions Source, JsonTypeInfo<Gallery> Info)? _cache;

    public static string Write(Gallery gallery, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, gallery, options);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void Write(Utf8JsonWriter writer, Gallery gallery, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, gallery, Info(options));

    public static JsonTypeInfo<Gallery> Info(JsonSerializerOptions source)
    {
        if (_cache is { } c && ReferenceEquals(c.Source, source))
            return c.Info;
        var o = new JsonSerializerOptions(source);
        var guid = JsonMetadataServices.CreateValueInfo<Guid>(o, JsonMetadataServices.GuidConverter);
        var str = JsonMetadataServices.CreateValueInfo<string>(o, JsonMetadataServices.StringConverter);
        var dec = JsonMetadataServices.CreateValueInfo<decimal>(o, JsonMetadataServices.DecimalConverter);
        var lighting = JsonMetadataServices.CreateValueInfo<Lighting>(o, (JsonConverter<Lighting>)new JsonStringEnumConverter<Lighting>().CreateConverter(typeof(Lighting), o));

        var note = Object<Note>(o, [P<Note, Guid>(o, "id", static n => n.Id.Value, guid), P<Note, string>(o, "text", static n => n.Text, str)]);
        var notes = Set(o, note);
        var curator = Object<Curator>(o, [P<Curator, Guid>(o, "id", static n => n.Id.Value, guid), P<Curator, string>(o, "name", static n => n.Name, str)]);
        var artwork = Object<Artwork>(o,
        [
            P<Artwork, Guid>(o, "id", static a => a.Id.Value, guid),
            P<Artwork, Id<Room>>(o, "firstRoom", static a => a.FirstRoom, JsonMetadataServices.CreateValueInfo<Id<Room>>(o, new IdJsonConverter<Room>())),
            P<Artwork, string>(o, "name", static a => a.Name, str),
            P<Artwork, Lighting?>(o, "preferred", static a => a.Preferred, JsonMetadataServices.CreateValueInfo<Lighting?>(o, new NullableLighting())),
            P<Artwork, decimal>(o, "price", static a => a.Price, dec),
            P<Artwork, Option<decimal>>(o, "reserve", static a => a.Reserve, JsonMetadataServices.CreateValueInfo<Option<decimal>>(o, new OptionJsonConverter<decimal>())),
            P<Artwork, EntitySetOf<Note>>(o, "notes", static a => new(a.Notes), notes),
        ]);
        var room = Object<Room>(o,
        [
            P<Room, Guid>(o, "id", static r => r.Id.Value, guid),
            P<Room, Option<int>>(o, "capacity", static r => r.Capacity, JsonMetadataServices.CreateValueInfo<Option<int>>(o, new OptionJsonConverter<int>())),
            P<Room, Lighting>(o, "lighting", static r => r.Lighting, lighting),
            P<Room, string>(o, "title", static r => r.Title, str),
            P<Room, decimal>(o, "totalValue", static r => r.TotalValue, dec),
            P<Room, EntitySetOf<Artwork>>(o, "artworks", static r => new(r.Artworks), Set(o, artwork)),
            P<Room, EntitySetOf<Note>>(o, "notes", static r => new(r.Notes), notes),
        ]);
        var gallery = Object<Gallery>(o,
        [
            P<Gallery, Guid>(o, "id", static g => g.Id.Value, guid),
            P<Gallery, long>(o, "version", static g => g.Version, JsonMetadataServices.CreateValueInfo<long>(o, JsonMetadataServices.Int64Converter)),
            P<Gallery, int>(o, "schema", static _ => Gallery.StateSchema, JsonMetadataServices.CreateValueInfo<int>(o, JsonMetadataServices.Int32Converter)),
            P<Gallery, Address?>(o, "address", static g => g.Address, (JsonTypeInfo<Address?>)o.GetTypeInfo(typeof(Address))),
            P<Gallery, int>(o, "artworkCount", static g => g.ArtworkCount, JsonMetadataServices.CreateValueInfo<int>(o, JsonMetadataServices.Int32Converter)),
            P<Gallery, decimal?>(o, "budget", static g => GalleryBudget(g), JsonMetadataServices.CreateValueInfo<decimal?>(o, JsonMetadataServices.GetNullableConverter(dec))),
            P<Gallery, Label>(o, "label", static g => g.Label, (JsonTypeInfo<Label>)o.GetTypeInfo(typeof(Label))),
            P<Gallery, List<string>>(o, "log", static g => GalleryLog(g), (JsonTypeInfo<List<string>>)o.GetTypeInfo(typeof(List<string>))),
            P<Gallery, Option<string>>(o, "motto", static g => g.Motto, JsonMetadataServices.CreateValueInfo<Option<string>>(o, new OptionJsonConverter<string>())),
            P<Gallery, string>(o, "name", static g => g.Name, str),
            P<Gallery, DateTimeOffset>(o, "openedAt", static g => g.OpenedAt, JsonMetadataServices.CreateValueInfo<DateTimeOffset>(o, JsonMetadataServices.DateTimeOffsetConverter)),
            P<Gallery, List<string>>(o, "tags", static g => g.Tags, (JsonTypeInfo<List<string>>)o.GetTypeInfo(typeof(List<string>))),
            P<Gallery, long>(o, "visitors", static g => g.VisitorCount, JsonMetadataServices.CreateValueInfo<long>(o, JsonMetadataServices.Int64Converter)),
            P<Gallery, Curator?>(o, "curator", static g => g.Curator.Value, curator!),
            P<Gallery, EntitySetOf<Room>>(o, "rooms", static g => new(g.Rooms), Set(o, room)),
        ]);
        _cache = (source, gallery);
        return gallery;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_budget")]
    private static extern ref decimal? GalleryBudget(Gallery g);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_log")]
    private static extern ref List<string> GalleryLog(Gallery g);

    /// <summary>An EntitySet seen as an enumerable for the collection metadata (EntitySet has no public constructor or Add).</summary>
    public readonly struct EntitySetOf<T>(Tarol.Primitives.EntitySet<T> set) : IEnumerable<T>
        where T : Tarol.Primitives.IEntity<T>
    {
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)set).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // GetNullableConverter(lighting) resolves Lighting from the options again; a string-enum converter for T? is simpler.
    private sealed class NullableLighting : JsonConverter<Lighting?>
    {
        public override bool HandleNull => true;

        public override Lighting? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : Enum.Parse<Lighting>(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Lighting? value, JsonSerializerOptions options)
        {
            if (value is { } v)
                writer.WriteStringValue(v.ToString());
            else
                writer.WriteNullValue();
        }
    }

    private static JsonTypeInfo<EntitySetOf<T>> Set<T>(JsonSerializerOptions o, JsonTypeInfo<T> element)
        where T : Tarol.Primitives.IEntity<T>
        => JsonMetadataServices.CreateIEnumerableInfo<EntitySetOf<T>, T>(o, new JsonCollectionInfoValues<EntitySetOf<T>> { ElementInfo = element });

    private static JsonTypeInfo<T> Object<T>(JsonSerializerOptions o, JsonPropertyInfo[] properties)
        where T : notnull
        => JsonMetadataServices.CreateObjectInfo(o, new JsonObjectInfoValues<T> { PropertyMetadataInitializer = _ => properties });

    private static JsonPropertyInfo P<TOwner, TValue>(JsonSerializerOptions o, string name, Func<TOwner, TValue> get, JsonTypeInfo<TValue> type)
        => JsonMetadataServices.CreatePropertyInfo(o, new JsonPropertyInfoValues<TValue>
        {
            IsProperty = true,
            IsPublic = true,
            DeclaringType = typeof(TOwner),
            PropertyTypeInfo = type,
            Getter = owner => get((TOwner)owner),
            PropertyName = name,
            JsonPropertyName = name,
        });
}
