-- Claim plans on synthetic backlogs (psql variables: timeouts, streams, per): the shipped O-2 anti-join vs a maintained
-- head flag with a partial index (candidate H). Run by claims.sh.
DROP SCHEMA IF EXISTS plan CASCADE; CREATE SCHEMA plan;
CREATE TABLE plan.outbox (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, tenant uuid NOT NULL, reaction text NOT NULL, type text NOT NULL,
  stream_id uuid NOT NULL, version bigint NOT NULL, event_id uuid NOT NULL, due_at timestamptz NOT NULL, attempts int NOT NULL DEFAULT 0,
  claim_id uuid, lease_until timestamptz, last_error text, head boolean NOT NULL DEFAULT false, UNIQUE (reaction, event_id));
CREATE INDEX outbox_due ON plan.outbox (due_at, id);
CREATE INDEX outbox_stream ON plan.outbox (tenant, type, stream_id, reaction, id);
CREATE INDEX outbox_heads ON plan.outbox (due_at, id) WHERE head;
-- (1) :timeouts sagas, each with one future timeout row (own stream, reaction ExpireSettlement)
INSERT INTO plan.outbox (tenant, reaction, type, stream_id, version, event_id, due_at, head)
SELECT '00000000-0000-0000-0000-000000000001', 'ExpireSettlement', 'settlement', gen_random_uuid(), 2, gen_random_uuid(), now() + interval '3 days', true
FROM generate_series(1, :timeouts);
-- (2) :streams streams × :per due rows, one stream after the other; the first row of each stream is its head
INSERT INTO plan.outbox (tenant, reaction, type, stream_id, version, event_id, due_at, head)
SELECT '00000000-0000-0000-0000-000000000001', 'AcknowledgeNote', 'auction', md5('s' || s)::uuid, v, gen_random_uuid(), now() - interval '1 s', v = 1
FROM generate_series(1, :streams) s, generate_series(1, :per) v ORDER BY s, v;
ANALYZE plan.outbox;
\echo '-- O-2 as shipped (anti-join)'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY ON)
SELECT o.id FROM plan.outbox o
WHERE o.due_at <= now() AND (o.lease_until IS NULL OR o.lease_until <= now())
  AND NOT EXISTS (SELECT 1 FROM plan.outbox p WHERE p.tenant = o.tenant AND p.type = o.type AND p.stream_id = o.stream_id
                  AND p.reaction = o.reaction AND p.id < o.id)
ORDER BY o.id LIMIT 32 FOR UPDATE SKIP LOCKED;
\echo '-- H: maintained head flag, partial index (due_at, id) WHERE head'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY ON)
SELECT o.id FROM plan.outbox o
WHERE o.head AND o.due_at <= now() AND (o.lease_until IS NULL OR o.lease_until <= now())
ORDER BY o.due_at, o.id LIMIT 32 FOR UPDATE SKIP LOCKED;
\echo '-- H: complete = delete the head and promote the next row of its (tenant, type, stream, reaction), one statement'
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY ON)
WITH d AS (DELETE FROM plan.outbox WHERE id = (SELECT id FROM plan.outbox WHERE head AND reaction = 'AcknowledgeNote' ORDER BY due_at, id LIMIT 1) RETURNING tenant, type, stream_id, reaction)
UPDATE plan.outbox o SET head = true FROM d
WHERE o.id = (SELECT min(n.id) FROM plan.outbox n WHERE (n.tenant, n.type, n.stream_id, n.reaction) = (d.tenant, d.type, d.stream_id, d.reaction) AND NOT n.head);
