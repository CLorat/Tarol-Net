-- Tries to make UPDATE … FROM (… ORDER BY … LIMIT … SKIP LOCKED) … RETURNING return a batch out of order (heap order
-- after reused free space) and compares a CTE that orders after RETURNING. Run with psql -f; see output.txt §11.
DROP SCHEMA IF EXISTS ord CASCADE; CREATE SCHEMA ord;
CREATE TABLE ord.messages (id uuid, type text, version int, headers jsonb, data bytea, publisher text, PRIMARY KEY (id, type, version));
CREATE TABLE ord.deliveries (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, subscription text, message_id uuid, type text, version int, due_at timestamptz, attempts int DEFAULT 0, claim_id uuid);
CREATE INDEX ON ord.deliveries (subscription, due_at, id);
INSERT INTO ord.messages SELECT gen_random_uuid(), 't', 1, '{}', '', 'p' FROM generate_series(1, 2000);
INSERT INTO ord.deliveries (subscription, message_id, type, version, due_at) SELECT 's', id, 't', 1, now() - interval '1 hour' FROM ord.messages ORDER BY random();
INSERT INTO ord.messages SELECT gen_random_uuid(), 'n', 1, '{}', '', 'p' FROM generate_series(1, 600);
-- the first half of a subscription's deliveries goes to the end of the table ...
INSERT INTO ord.deliveries (subscription, message_id, type, version, due_at)
  SELECT 'n', id, 'n', 1, now() - interval '1 hour' FROM (SELECT id FROM ord.messages WHERE type = 'n' ORDER BY id LIMIT 300) x;
-- ... then other subscriptions acknowledge rows on the first pages, and the second half lands in that space
DELETE FROM ord.deliveries WHERE subscription = 's' AND id < 700;
VACUUM ord.deliveries;
INSERT INTO ord.deliveries (subscription, message_id, type, version, due_at)
  SELECT 'n', id, 'n', 1, now() - interval '1 hour' FROM (SELECT id FROM ord.messages WHERE type = 'n' ORDER BY id OFFSET 300) x;
ANALYZE ord.messages; ANALYZE ord.deliveries;
CREATE TABLE ord.result (variant text, batch int, ordered bool);
DO $$
DECLARE b int; ids bigint[];
BEGIN
  FOR b IN 1..20 LOOP
    WITH u AS (UPDATE ord.deliveries d SET attempts = d.attempts + 1, claim_id = gen_random_uuid(), due_at = now() + interval '1 hour'
       FROM (SELECT id FROM ord.deliveries WHERE subscription = 'n' AND due_at <= now() ORDER BY due_at, id LIMIT 32 FOR UPDATE SKIP LOCKED) c, ord.messages m
       WHERE d.id = c.id AND m.id = d.message_id AND m.type = d.type AND m.version = d.version RETURNING d.id)
    SELECT array_agg(id) INTO ids FROM u;           -- array_agg without ORDER BY keeps the RETURNING order
    INSERT INTO ord.result VALUES ('UPDATE … RETURNING', b, ids = (SELECT array_agg(x ORDER BY x) FROM unnest(ids) x));
    WITH claimed AS (UPDATE ord.deliveries d SET attempts = d.attempts + 1, claim_id = gen_random_uuid(), due_at = now() + interval '1 hour'
       FROM (SELECT id FROM ord.deliveries WHERE subscription = 'n' AND due_at <= now() ORDER BY due_at, id LIMIT 32 FOR UPDATE SKIP LOCKED) c, ord.messages m
       WHERE d.id = c.id AND m.id = d.message_id AND m.type = d.type AND m.version = d.version RETURNING d.id),
         u AS (SELECT id FROM claimed ORDER BY id)
    SELECT array_agg(id) INTO ids FROM u;
    INSERT INTO ord.result VALUES ('WITH claimed … ORDER BY id', b, ids = (SELECT array_agg(x ORDER BY x) FROM unnest(ids) x));
  END LOOP;
END $$;
SELECT variant, count(*) AS batches, count(*) FILTER (WHERE NOT ordered) AS out_of_order FROM ord.result GROUP BY variant;
EXPLAIN (COSTS OFF) UPDATE ord.deliveries d SET attempts = d.attempts + 1
       FROM (SELECT id FROM ord.deliveries WHERE subscription = 'n' AND due_at <= now() ORDER BY due_at, id LIMIT 32 FOR UPDATE SKIP LOCKED) c, ord.messages m
       WHERE d.id = c.id AND m.id = d.message_id AND m.type = d.type AND m.version = d.version RETURNING d.id;
DROP SCHEMA ord CASCADE;
