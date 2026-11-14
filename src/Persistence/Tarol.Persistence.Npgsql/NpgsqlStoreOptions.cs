using System.Text.Json;

namespace Tarol.Persistence.Npgsql;

/// <summary>How an <see cref="NpgsqlAggregateStore"/> stores; the defaults fit most applications.</summary>
public sealed record NpgsqlStoreOptions
{
    private readonly string _schema = NpgsqlSchema.DefaultSchema;

    /// <summary>The PostgreSQL schema of the tables (default <c>tarol</c>).</summary>
    /// <exception cref="ArgumentException">Not a plain identifier.</exception>
    public string Schema
    {
        get => _schema;
        init
        {
            NpgsqlSchema.Validate(value);
            _schema = value;
        }
    }

    /// <summary>JSON options of the stored state; <see langword="null"/> = the codec's options.</summary>
    public JsonSerializerOptions? StateOptions { get; init; }

    /// <summary>The unique lookups; share one instance with the in-memory store of your tests.</summary>
    public StoreLookups Lookups { get; init; } = new();
}
