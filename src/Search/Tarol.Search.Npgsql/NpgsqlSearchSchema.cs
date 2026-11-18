using System.Globalization;
using System.Text.RegularExpressions;
using Tarol.Queries.Npgsql;

namespace Tarol.Search.Npgsql;

/// <summary>The DDL of the search index: extensions, text-search configurations, the versions table and one table per version.</summary>
public static partial class NpgsqlSearchSchema
{
    /// <summary>The table of an index version (without schema).</summary>
    /// <param name="version">The version.</param>
    /// <returns><c>search_v&lt;n&gt;</c>.</returns>
    public static string TableOf(int version) => "search_v" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text-search configuration of a language: <c>german</c> for "de", <c>english</c> for "en", else <c>simple</c> (all with <c>unaccent</c>).</summary>
    /// <param name="schema">The schema holding the configurations.</param>
    /// <param name="language">An ISO 639-1 code, or <see langword="null"/>.</param>
    /// <returns>The qualified configuration name.</returns>
    public static string Configuration(string schema, string? language) => schema + "." + (language?.ToUpperInvariant() switch
    {
        "DE" => "german",
        "EN" => "english",
        _ => "simple",
    });

    /// <summary>Everything one index version needs, idempotent, serialized by an advisory lock of the transaction.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="version">The index version.</param>
    /// <returns>The script.</returns>
    public static string Prepare(string schema, int version)
    {
        Validate(schema);
        var table = schema + "." + TableOf(version);
        var name = TableOf(version);
        return $$"""
            SELECT pg_advisory_xact_lock(hashtext('{{schema}}.search'));
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE EXTENSION IF NOT EXISTS btree_gin;
            CREATE EXTENSION IF NOT EXISTS unaccent;
            {{NpgsqlReadModelSchema.ProjectionsTable(schema)}}
            INSERT INTO {{schema}}.projections (name, version) VALUES ('{{SearchIndexDefinition.Name}}', {{version}}) ON CONFLICT DO NOTHING;
            CREATE TABLE IF NOT EXISTS {{schema}}.search_versions (
                version     integer     PRIMARY KEY,
                fingerprint text        NOT NULL,                    -- the generated shapes' fingerprint (GS-5)
                created_at  timestamptz NOT NULL DEFAULT now());
            DO $tarol$
            DECLARE dict text := (SELECT quote_ident(n.nspname) || '.unaccent' FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'unaccent');
            DECLARE lang text;
            BEGIN
              FOREACH lang IN ARRAY ARRAY['german', 'english', 'simple'] LOOP
                IF NOT EXISTS (SELECT 1 FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace WHERE n.nspname = '{{schema}}' AND c.cfgname = lang) THEN
                  EXECUTE format('CREATE TEXT SEARCH CONFIGURATION {{schema}}.%I (COPY = pg_catalog.%I)', lang, lang);
                  EXECUTE format('ALTER TEXT SEARCH CONFIGURATION {{schema}}.%I ALTER MAPPING FOR hword, hword_part, word WITH %s, %s', lang, dict,
                                 CASE lang WHEN 'simple' THEN 'simple' ELSE lang || '_stem' END);
                END IF;
              END LOOP;
            END $tarol$;
            CREATE TABLE IF NOT EXISTS {{table}} (
                tenant    uuid     NOT NULL,
                type      text     COLLATE "C" NOT NULL,         -- "artwork", "artwork.edition"
                id        uuid     NOT NULL,                     -- the aggregate's or the entity's id
                aggregate uuid     NOT NULL,                     -- shown and authorized through it
                version   bigint   NOT NULL,                     -- the stream version the document reflects
                title     text     NOT NULL,
                a text, b text, c text, d text,                  -- the text per weight
                exact     text[]   NOT NULL,                     -- blind tokens (GS-8), never text
                audience  text[]   NOT NULL,                     -- empty: everyone allowed to read the type (GS-7)
                lang      regconfig NOT NULL,                    -- the document's language (GS-10)
                tsv       tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector(lang, coalesce(a, '')), 'A') || setweight(to_tsvector(lang, coalesce(b, '')), 'B')
                    || setweight(to_tsvector(lang, coalesce(c, '')), 'C') || setweight(to_tsvector(lang, coalesce(d, '')), 'D')) STORED,
                PRIMARY KEY (tenant, type, id));
            CREATE INDEX IF NOT EXISTS {{name}}_tsv ON {{table}} USING gin (tenant, tsv);              -- the tenant leads (GS-6)
            CREATE INDEX IF NOT EXISTS {{name}}_typo ON {{table}} USING gin (tenant, a gin_trgm_ops);    -- the typo fallback: weight A (titles, names)
            CREATE INDEX IF NOT EXISTS {{name}}_exact ON {{table}} USING gin (tenant, exact);
            CREATE INDEX IF NOT EXISTS {{name}}_aggregate ON {{table}} (tenant, aggregate);
            """;
    }

    /// <summary>Checks that a schema name is a plain identifier.</summary>
    /// <param name="schema">The schema.</param>
    /// <exception cref="ArgumentException">Not a lowercase identifier.</exception>
    public static void Validate(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!Identifier().IsMatch(schema))
            throw new ArgumentException($"The schema '{schema}' must be a lowercase identifier (letters, digits, underscores).", nameof(schema));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
