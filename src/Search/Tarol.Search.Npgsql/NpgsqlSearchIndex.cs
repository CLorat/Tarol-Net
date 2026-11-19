using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Results;

namespace Tarol.Search.Npgsql;

/// <summary>Options of the PostgreSQL search index.</summary>
public sealed record NpgsqlSearchOptions
{
    /// <summary>The schema of the index tables and of <c>projections</c>; <see langword="null"/> = the store's schema.</summary>
    public string? Schema { get; init; }

    /// <summary>How many matches are ranked (GS-2: 1,000; the cursor's depth). Higher ranks more exactly and costs more for common words.</summary>
    public int Rank { get; init; } = SearchLimits.MaxDepth;

    /// <summary>The <c>pg_trgm.word_similarity_threshold</c> of the typo fallback (0.4: one transposition in a 7-letter word still matches).</summary>
    public double TypoThreshold { get; init; } = 0.4;
}

/// <summary>
/// The search index in PostgreSQL (GS-2): <c>&lt;schema&gt;.search_v&lt;n&gt;</c> per index version with a generated
/// <c>tsvector</c> (weights A–D, the document's language through a Tarol text-search configuration with <c>unaccent</c>), a GIN
/// led by the tenant, a tenant-led trigram index on the weight-A text (titles and names), exact tokens and audience as arrays. The checkpoint is the row
/// (<c>search</c>, n) of <c>projections</c>; a batch moves it and replaces the streams' documents in one transaction.
/// </summary>
public sealed class NpgsqlSearchIndex : ISearchIndex
{
    private readonly NpgsqlDataSource _db;
    private readonly NpgsqlSearchOptions _options;
    private int _version = 1;
    private string _table = "";

    /// <summary>Creates the index.</summary>
    /// <param name="dataSource">The data source (arrays enabled, as the store needs them).</param>
    /// <param name="schema">The schema.</param>
    /// <param name="options">Rank bound and typo threshold.</param>
    public NpgsqlSearchIndex(NpgsqlDataSource dataSource, string schema = NpgsqlReadModelSchema.DefaultSchema, NpgsqlSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        NpgsqlSearchSchema.Validate(schema);
        _db = dataSource;
        Schema = schema;
        _options = options ?? new NpgsqlSearchOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Rank, SearchLimits.MaxDepth, nameof(options));
    }

    /// <summary>The schema of the index.</summary>
    public string Schema { get; }

    /// <summary>The table of the prepared version (<c>search_v&lt;n&gt;</c>).</summary>
    public string Table => _table;

    /// <inheritdoc />
    public async ValueTask Prepare(SearchIndexDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Version, 1, nameof(definition));
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                try
                {
                    await Exec(connection, tx, NpgsqlSearchSchema.Prepare(Schema, definition.Version), cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UndefinedFile or "0A000")
                {
                    throw new InvalidOperationException(
                        "Tarol.Search.Npgsql needs the extensions pg_trgm, btree_gin and unaccent. They are trusted: the owner of the database can create them "
                        + "(CREATE EXTENSION pg_trgm; CREATE EXTENSION btree_gin; CREATE EXTENSION unaccent;), or an administrator does once. " + e.MessageText, e);
                }
                await using var check = new NpgsqlCommand($"""
                    WITH added AS (INSERT INTO {Schema}.search_versions (version, fingerprint) VALUES ($1, $2) ON CONFLICT (version) DO NOTHING RETURNING fingerprint)
                    SELECT fingerprint FROM added UNION ALL SELECT fingerprint FROM {Schema}.search_versions WHERE version = $1 LIMIT 1
                    """, connection, tx);
                check.Parameters.Add(new() { Value = definition.Version });
                check.Parameters.Add(new() { Value = definition.Fingerprint });
                var stored = (string?)await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (stored != definition.Fingerprint)
                    throw new InvalidOperationException(
                        $"The search index v{definition.Version} was built for other searchable members (fingerprint {stored}, now {definition.Fingerprint}): raise the version.");
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        _version = definition.Version;
        _table = Schema + "." + NpgsqlSearchSchema.TableOf(definition.Version);
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken)
        => (await Checkpoint(_version, cancellationToken).ConfigureAwait(false)) ?? default;

    private async Task<ProjectionCheckpoint?> Checkpoint(int version, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"SELECT tx::text, pos, state FROM {Schema}.projections WHERE name = '{SearchIndexDefinition.Name}' AND version = $1");
        command.Parameters.Add(new() { Value = version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new ProjectionCheckpoint(new GlobalPosition(ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1)),
            reader.GetString(2) == "current" ? ProjectionState.Current : ProjectionState.Building);
    }

    /// <inheritdoc />
    public async ValueTask<bool> Write(SearchBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                await using (var guard = new NpgsqlCommand($"""
                    UPDATE {Schema}.projections SET tx = $4::text::xid8, pos = $5, state = $6, updated_at = now()
                    WHERE name = '{SearchIndexDefinition.Name}' AND version = $1 AND tx = $2::text::xid8 AND pos = $3
                    """, connection, tx))
                {
                    guard.Parameters.Add(new() { Value = _version });
                    guard.Parameters.Add(new() { Value = batch.From.Transaction.ToString(CultureInfo.InvariantCulture) });
                    guard.Parameters.Add(new() { Value = batch.From.Position });
                    guard.Parameters.Add(new() { Value = batch.To.Transaction.ToString(CultureInfo.InvariantCulture) });
                    guard.Parameters.Add(new() { Value = batch.To.Position });
                    guard.Parameters.Add(new() { Value = batch.State == ProjectionState.Current ? "current" : "building" });
                    if (await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                        return false;
                }
                await Apply(connection, tx, batch.Streams, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask Replace(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
            return;
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                await Apply(connection, tx, streams, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task Apply(NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        if (streams.Count == 0)
            return;
        await using (var delete = new NpgsqlCommand($"""
            DELETE FROM {_table} d USING unnest($1::uuid[], $2::uuid[]) k(t, a) WHERE d.tenant = k.t AND d.aggregate = k.a
            """, connection, tx))
        {
            delete.Parameters.Add(new() { Value = streams.Select(static s => s.Tenant).ToArray() });
            delete.Parameters.Add(new() { Value = streams.Select(static s => s.Aggregate).ToArray() });
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var rows = streams.SelectMany(static s => s.Documents.Select(d => (s.Tenant, s.Version, Document: d))).ToList();
        if (rows.Count == 0)
            return;
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_table} (tenant, type, id, aggregate, version, title, a, b, c, d, exact, audience, lang)
            SELECT x.tenant, x.type, x.id, x.aggregate, x.version, x.title, x.a, x.b, x.c, x.d,
                   coalesce(string_to_array(nullif(x.exact, ''), chr(31)), ARRAY[]::text[]), coalesce(string_to_array(nullif(x.audience, ''), chr(31)), ARRAY[]::text[]),
                   x.lang::regconfig
            FROM unnest($1::uuid[], $2::text[], $3::uuid[], $4::uuid[], $5::bigint[], $6::text[], $7::text[], $8::text[], $9::text[], $10::text[],
                        $11::text[], $12::text[], $13::text[]) x(tenant, type, id, aggregate, version, title, a, b, c, d, exact, audience, lang)
            ON CONFLICT (tenant, type, id) DO UPDATE SET aggregate = excluded.aggregate, version = excluded.version, title = excluded.title,
              a = excluded.a, b = excluded.b, c = excluded.c, d = excluded.d, exact = excluded.exact, audience = excluded.audience, lang = excluded.lang
            """, connection, tx);
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Tenant).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Document.Type).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Document.Id).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Document.Aggregate).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Version).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => r.Document.Title).ToArray() });
        insert.Parameters.Add(Texts(rows.Select(static r => r.Document.A)));
        insert.Parameters.Add(Texts(rows.Select(static r => r.Document.B)));
        insert.Parameters.Add(Texts(rows.Select(static r => r.Document.C)));
        insert.Parameters.Add(Texts(rows.Select(static r => r.Document.D)));
        insert.Parameters.Add(new() { Value = rows.Select(static r => string.Join('\u001f', r.Document.Exact)).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(static r => string.Join('\u001f', r.Document.Audience)).ToArray() });
        insert.Parameters.Add(new() { Value = rows.Select(r => NpgsqlSearchSchema.Configuration(Schema, r.Document.Language)).ToArray() });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter Texts(IEnumerable<string?> values)
        => new() { Value = values.Select(static v => (object?)v ?? DBNull.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    /// <inheritdoc />
    public async ValueTask<Result<SearchPage>> Search(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SearchText.Validate(request) is { } invalid)
            return invalid;
        var (version, checkpoint) = await Answering(cancellationToken).ConfigureAwait(false);
        if (SearchText.Offset(request, version, out var offset) is { } bad)
            return bad;
        var table = Schema + "." + NpgsqlSearchSchema.TableOf(version);
        var configs = request.Language is null
            ? new[] { Schema + ".simple", Schema + ".english", Schema + ".german" }        // no language: match each document's own stemming
            : [NpgsqlSearchSchema.Configuration(Schema, request.Language), Schema + ".simple"];
        var clauses = SearchText.Parse(request.Text);
        var text = TextQuery(configs, clauses, request.Prefix);
        var hits = new List<SearchHit>(request.Limit + 1);
        var hasText = clauses.Any(static c => !c.Excluded);
        if (hasText || request.ExactToken is not null)
        {
            await using (var command = _db.CreateCommand(Ranked(table, configs, request, text.Sql, offset)))
            {
                Bind(command, request, text.Words);
                await Read(command, hits, cancellationToken).ConfigureAwait(false);
            }
            if (hits.Count == 0 && offset == 0 && hasText && request.Text.Length > 2)
                await Typos(table, request, hits, cancellationToken).ConfigureAwait(false);
        }
        var more = hits.Count > request.Limit;
        if (more)
            hits.RemoveAt(hits.Count - 1);
        Dictionary<string, long>? counts = null;
        var capped = false;
        if (request.Counts && (hasText || request.ExactToken is not null))
        {
            counts = [];
            await using var command = _db.CreateCommand($"""
                SELECT type, count(*) FROM (SELECT d.type FROM {table} d, (SELECT {text.Sql} AS q) x
                WHERE {Where(request)} AND ({Match(request)}) LIMIT {SearchLimits.CountCap + 1}) m GROUP BY type
                """);
            Bind(command, request, text.Words);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                counts[reader.GetString(0)] = reader.GetInt64(1);
            capped = counts.Values.Sum() > SearchLimits.CountCap;
        }
        var next = more ? SearchText.Cursor(request, version, offset + hits.Count) : null;
        return new SearchPage(hits, next, counts, capped, version, checkpoint.Position, checkpoint.State);
    }

    /// <summary>The newest current version up to the prepared one (GS-5), else the prepared one while it builds.</summary>
    private async Task<(int Version, ProjectionCheckpoint Checkpoint)> Answering(CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand($"""
            SELECT p.version, p.tx::text, p.pos FROM {Schema}.projections p
            WHERE p.name = '{SearchIndexDefinition.Name}' AND p.version <= $1 AND p.state = 'current'
              AND to_regclass('{Schema}.search_v' || p.version) IS NOT NULL
            ORDER BY p.version DESC LIMIT 1
            """);
        command.Parameters.Add(new() { Value = _version });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return (reader.GetInt32(0), new ProjectionCheckpoint(new GlobalPosition(ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(2)),
                    ProjectionState.Current));
        }
        return (_version, await Checkpoint(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The tsquery from Tarol's own parser (the in-memory index reads the same clauses): each clause is its alternatives in every
    /// configuration, OR-ed (a word matches in the form any of them stems it to); an excluded clause is negated in all of them; the
    /// clauses are AND-ed; the last positive word is a prefix when asked. Words are parameters from $7 on.
    /// </summary>
    private static (string Sql, List<string> Words) TextQuery(string[] configs, IReadOnlyList<SearchClause> clauses, bool prefix)
    {
        var words = new List<string>();
        var last = prefix ? clauses.LastOrDefault(static c => !c.Excluded) : null;
        var parts = new List<string>();
        foreach (var clause in clauses)
        {
            var forms = new List<string>();
            foreach (var alternative in clause.Alternatives)
            {
                var asPrefix = ReferenceEquals(clause, last) && ReferenceEquals(alternative, clause.Alternatives[^1]);
                var phrase = string.Join(' ', asPrefix ? alternative.Take(alternative.Count - 1) : alternative);
                var pIndex = 7 + words.Count;
                if (phrase.Length > 0)
                    words.Add(phrase);
                var tIndex = 7 + words.Count;
                if (asPrefix)
                    words.Add(alternative[^1]);
                foreach (var c in configs)
                {
                    var whole = phrase.Length > 0 ? $"phraseto_tsquery('{c}', ${pIndex})" : null;
                    var tail = asPrefix ? $"to_tsquery('{c}', quote_literal(${tIndex}) || ':*')" : null;
                    forms.Add(whole is not null && tail is not null ? $"({whole} <-> {tail})" : whole ?? tail!);
                }
            }
            var any = "(" + string.Join(" || ", forms) + ")";
            parts.Add(clause.Excluded ? "!!" + any : any);
        }
        return (parts.Count == 0 ? "''::tsquery" : "(" + string.Join(" && ", parts) + ")", words);
    }

    private static string Where(SearchRequest request)
        => "d.tenant = $1"
           + (request.Types is null ? "" : " AND d.type = ANY($4)")
           + (request.Audience is null ? "" : " AND (cardinality(d.audience) = 0 OR d.audience && $5)");

    private static string Match(SearchRequest request)
        => "d.tsv @@ x.q" + (request.ExactToken is null ? "" : " OR d.exact @> ARRAY[$6]");

    private string Ranked(string table, string[] configs, SearchRequest request, string query, int offset)
    {
        var snippet = request.Snippets
            ? $"ts_headline('{configs[0]}', coalesce(m.b, m.a, m.c, m.d, ''), m.q, 'MaxFragments=1, MaxWords=12, MinWords=4, StartSel={SearchSnippets.Open}, StopSel={SearchSnippets.Close}')"
            : "NULL";
        var exactBoost = request.ExactToken is null ? "" : " + CASE WHEN m.exact @> ARRAY[$6] THEN 1.0 ELSE 0 END";
        return $"""
            SELECT m.type, m.id, m.aggregate, m.title, ts_rank(m.tsv, m.q){exactBoost}, {snippet}
            FROM (SELECT d.*, x.q FROM {table} d, (SELECT {query} AS q) x WHERE {Where(request)} AND ({Match(request)}) LIMIT {_options.Rank}) m
            ORDER BY 5 DESC, m.id LIMIT {request.Limit + 1} OFFSET {offset}
            """;
    }

    private static void Bind(NpgsqlCommand command, SearchRequest request, List<string> words)
    {
        command.Parameters.Add(new() { Value = request.Tenant });
        command.Parameters.Add(new() { Value = request.Text });
        command.Parameters.Add(new() { Value = "", NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)request.Types?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)request.Audience?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)request.ExactToken ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        foreach (var word in words)
            command.Parameters.Add(new() { Value = word, NpgsqlDbType = NpgsqlDbType.Text });
    }

    private const int TypoCandidates = 200;

    private static async Task Read(NpgsqlCommand command, List<SearchHit> hits, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            hits.Add(SearchSnippets.Hit(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture), reader.IsDBNull(5) ? null : reader.GetString(5)));
    }

    /// <summary>Nothing matched: the trigram match on weight A, the titles and names (one batch: the threshold for this transaction, then the query).</summary>
    private async Task Typos(string table, SearchRequest request, List<SearchHit> hits, CancellationToken cancellationToken)
    {
        var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                await using var batch = new NpgsqlBatch(connection, tx);
                var threshold = new NpgsqlBatchCommand("SELECT set_config('pg_trgm.word_similarity_threshold', $1, true)");
                threshold.Parameters.Add(new() { Value = _options.TypoThreshold.ToString(CultureInfo.InvariantCulture) });
                batch.BatchCommands.Add(threshold);
                var search = new NpgsqlBatchCommand($"""
                    SELECT d.type, d.id, d.aggregate, d.title, word_similarity($2, d.a), d.a FROM {table} d
                    WHERE {Where(request)} AND $2 <% d.a ORDER BY 5 DESC, d.id LIMIT {TypoCandidates}
                    """);
                search.Parameters.Add(new() { Value = request.Tenant });
                search.Parameters.Add(new() { Value = string.Join(' ', SearchText.Words(request.Text)) });
                search.Parameters.Add(new() { Value = "", NpgsqlDbType = NpgsqlDbType.Text });
                search.Parameters.Add(new() { Value = (object?)request.Types?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                search.Parameters.Add(new() { Value = (object?)request.Audience?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                batch.BatchCommands.Add(search);
                await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                // trigrams find candidates (a prefix looks similar too); the typo rule every index shares decides
                var clauses = SearchText.Parse(request.Text).Where(static c => !c.Excluded).ToList();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && hits.Count <= request.Limit)
                {
                    var words = SearchText.Words(reader.IsDBNull(5) ? null : reader.GetString(5));
                    if (clauses.All(c => c.Alternatives.Any(a => SearchText.TypoMatch(a, words))))
                        hits.Add(new SearchHit(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                            Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture)));
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SearchHit>> Documents(Guid tenant, IReadOnlyCollection<Guid> aggregates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        await using var command = _db.CreateCommand($"SELECT type, id, aggregate, title FROM {_table} WHERE tenant = $1 AND aggregate = ANY($2) ORDER BY type, id");
        command.Parameters.Add(new() { Value = tenant });
        command.Parameters.Add(new() { Value = aggregates.ToArray() });
        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            hits.Add(new SearchHit(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), 0));
        return hits;
    }

    private static async Task Exec(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
