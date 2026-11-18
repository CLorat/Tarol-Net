using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Probe.Search;

/// <summary>
/// PostgreSQL candidates on one table: full-text search (<c>tsvector</c> generated from a per-row <c>regconfig</c>, title weight A,
/// body weight B, GIN), pg_trgm (GIN on the title) and their combination. <paramref name="Index"/> picks the tenant strategy:
/// <c>gin</c> = GIN(tsv) and the btree primary key (tenant, id); <c>btree_gin</c> = GIN(tenant, tsv).
/// </summary>
public sealed class Pg(NpgsqlDataSource db, string schema = "search_probe", string index = "gin", string config = "english", string mode = "fts") : IEngine
{
    public string Name => mode switch { "trgm" => "pg_trgm", "combined" => "pg fts+trgm", _ => $"pg fts ({IndexKind})" };
    public string IndexKind { get; set; } = index;
    public NpgsqlDataSource Db => db;
    public string Schema => schema;

    public async Task Exec(string sql)
    {
        await using var c = db.CreateCommand(sql);
        c.CommandTimeout = 3600;
        await c.ExecuteNonQueryAsync();
    }

    public async Task<T> Scalar<T>(string sql)
    {
        await using var c = db.CreateCommand(sql);
        c.CommandTimeout = 3600;
        return (T)(await c.ExecuteScalarAsync())!;
    }

    public async Task Reset()
    {
        await Exec($"""
            CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS btree_gin; CREATE EXTENSION IF NOT EXISTS unaccent;
            DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema};
            CREATE TABLE {schema}.docs (
              tenant uuid NOT NULL, id uuid NOT NULL, type text NOT NULL, title text NOT NULL, body text NOT NULL,
              lang regconfig NOT NULL DEFAULT '{config}',
              tsv tsvector GENERATED ALWAYS AS (setweight(to_tsvector(lang, title), 'A') || setweight(to_tsvector(lang, body), 'B')) STORED,
              PRIMARY KEY (tenant, id));
            """);
    }

    /// <summary>Creates the indexes of <see cref="IndexKind"/> (and the trigram index); returns the build time.</summary>
    public async Task<TimeSpan> CreateIndexes()
    {
        var sw = Stopwatch.StartNew();
        await Exec(IndexKind == "btree_gin"
            ? $"CREATE INDEX IF NOT EXISTS docs_tenant_tsv ON {schema}.docs USING gin (tenant, tsv)"
            : $"CREATE INDEX IF NOT EXISTS docs_tsv ON {schema}.docs USING gin (tsv)");
        await Exec($"CREATE INDEX IF NOT EXISTS docs_title_trgm ON {schema}.docs USING gin (title gin_trgm_ops)");
        await Exec($"ANALYZE {schema}.docs");
        return sw.Elapsed;
    }

    public async Task<TimeSpan> SwitchIndex(string kind)
    {
        await Exec($"DROP INDEX IF EXISTS {schema}.docs_tsv; DROP INDEX IF EXISTS {schema}.docs_tenant_tsv");
        IndexKind = kind;
        return await CreateIndexes();
    }

    public async Task Load(IEnumerable<Doc> docs)
    {
        await using var c = await db.OpenConnectionAsync();
        await using (var w = await c.BeginBinaryImportAsync($"COPY {schema}.docs (tenant, id, type, title, body) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var d in docs)
            {
                await w.StartRowAsync();
                await w.WriteAsync(d.Tenant, NpgsqlDbType.Uuid);
                await w.WriteAsync(d.Id, NpgsqlDbType.Uuid);
                await w.WriteAsync(d.Type, NpgsqlDbType.Text);
                await w.WriteAsync(d.Title, NpgsqlDbType.Text);
                await w.WriteAsync(d.Body, NpgsqlDbType.Text);
            }
            await w.CompleteAsync();
        }
    }

    /// <summary>The feeder's write: upserts in batches of 500 inside one transaction per batch.</summary>
    public async Task UpsertBatch(IReadOnlyList<Doc> docs)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {schema}.docs (tenant, id, type, title, body)
            SELECT * FROM unnest($1::uuid[], $2::uuid[], $3::text[], $4::text[], $5::text[])
            ON CONFLICT (tenant, id) DO UPDATE SET type = excluded.type, title = excluded.title, body = excluded.body
            """, c, tx);
        cmd.Parameters.Add(new() { Value = docs.Select(d => d.Tenant).ToArray() });
        cmd.Parameters.Add(new() { Value = docs.Select(d => d.Id).ToArray() });
        cmd.Parameters.Add(new() { Value = docs.Select(d => d.Type).ToArray() });
        cmd.Parameters.Add(new() { Value = docs.Select(d => d.Title).ToArray() });
        cmd.Parameters.Add(new() { Value = docs.Select(d => d.Body).ToArray() });
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public Task Upsert(Doc doc) => UpsertBatch([doc]);

    public async Task Delete(IReadOnlyCollection<Guid> ids)
    {
        await using var c = db.CreateCommand($"DELETE FROM {schema}.docs WHERE id = ANY($1)");
        c.Parameters.Add(new() { Value = ids.ToArray() });
        await c.ExecuteNonQueryAsync();
    }

    public Task<Hits> Search(Query q) => mode switch
    {
        "trgm" => Trigram(q),
        "combined" => Combined(q),
        _ => Fts(q),
    };

    private static string Letters(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetter)]);

    private string TsQuery(Query q)
    {
        if (q.Match != Match.Prefix)
            return $"websearch_to_tsquery('{config}', $2)";
        return $"(websearch_to_tsquery('{config}', $2) && to_tsquery('{config}', $3 || ':*'))";
    }

    private void Bind(NpgsqlCommand c, Query q)
    {
        c.Parameters.Add(new() { Value = (object?)q.Tenant ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var words = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Match == Match.Prefix)
        {
            c.Parameters.Add(new() { Value = string.Join(' ', words[..^1]) });
            c.Parameters.Add(new() { Value = Letters(words[^1]) });
        }
        else
            c.Parameters.Add(new() { Value = q.Text });
        if (q.Types is { Length: > 0 })
            c.Parameters.Add(new() { Value = q.Types, ParameterName = "" });
    }

    private string Where(Query q, string match)
    {
        var tenant = q.Tenant is null ? "$1::uuid IS NULL" : "d.tenant = $1";
        var types = q.Types is { Length: > 0 } ? $" AND d.type = ANY(${(q.Match == Match.Prefix ? 4 : 3)})" : "";
        return $"{tenant} AND {match}{types}";
    }

    private async Task<Hits> Fts(Query q)
    {
        var tsq = TsQuery(q);
        var snippet = q.Snippets ? $", ts_headline('{config}', d.body, x.q, 'MaxFragments=1, MaxWords=12, MinWords=4')" : ", NULL";
        var sql = $"""
            SELECT d.id, d.type, ts_rank(d.tsv, x.q){snippet} FROM {schema}.docs d, (SELECT {tsq} AS q) x
            WHERE {Where(q, "d.tsv @@ x.q")} ORDER BY 3 DESC, d.id LIMIT {q.Limit}
            """;
        var items = new List<Hit>(q.Limit);
        string? first = null;
        await using (var c = db.CreateCommand(sql))
        {
            Bind(c, q);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                items.Add(new Hit(r.GetGuid(0), r.GetString(1), r.GetFloat(2)));
                first ??= r.IsDBNull(3) ? null : r.GetString(3);
            }
        }
        if (!q.Facets)
            return new Hits(items, -1, Snippet: first);
        var per = new Dictionary<string, long>();
        await using (var c = db.CreateCommand($"SELECT d.type, count(*) FROM {schema}.docs d, (SELECT {tsq} AS q) x WHERE {Where(q, "d.tsv @@ x.q")} GROUP BY d.type"))
        {
            Bind(c, q);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
                per[r.GetString(0)] = r.GetInt64(1);
        }
        return new Hits(items, per.Values.Sum(), per, first);
    }

    private async Task<Hits> Trigram(Query q)
    {
        var sql = q.Match == Match.Prefix
            ? $"SELECT d.id, d.type, 1.0::real FROM {schema}.docs d WHERE {Where(q, "d.title ILIKE '%' || $2 || '%'")} ORDER BY d.id LIMIT {q.Limit}"
            : $"SELECT d.id, d.type, word_similarity($2, d.title) FROM {schema}.docs d WHERE {Where(q, "$2 <% d.title")} ORDER BY 3 DESC, d.id LIMIT {q.Limit}";
        var items = new List<Hit>(q.Limit);
        await using var c = db.CreateCommand(sql);
        c.Parameters.Add(new() { Value = (object?)q.Tenant ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        c.Parameters.Add(new() { Value = q.Text });
        if (q.Types is { Length: > 0 }) c.Parameters.Add(new() { Value = q.Types });
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync())
            items.Add(new Hit(r.GetGuid(0), r.GetString(1), Convert.ToDouble(r.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)));
        return new Hits(items, -1);
    }

    /// <summary>Full-text first; when it finds nothing (a typo), the trigram match on the title.</summary>
    private async Task<Hits> Combined(Query q)
    {
        var fts = await Fts(q with { Match = q.Match == Match.Typo ? Match.Words : q.Match });
        if (fts.Items.Count > 0 || q.Match != Match.Typo)
            return fts;
        return await Trigram(q);
    }

    public async Task<long> Size() => await Scalar<long>($"SELECT pg_total_relation_size('{schema}.docs')");

    public async Task<(long Table, long Tsv, long Trgm)> Sizes()
    {
        var gin = IndexKind == "btree_gin" ? "docs_tenant_tsv" : "docs_tsv";
        return (await Scalar<long>($"SELECT pg_table_size('{schema}.docs')"),
            await Scalar<long>($"SELECT pg_relation_size('{schema}.{gin}')"),
            await Scalar<long>($"SELECT pg_relation_size('{schema}.docs_title_trgm')"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
