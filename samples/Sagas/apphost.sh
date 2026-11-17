#!/usr/bin/env bash
# Runs the built AppHost (PostgreSQL, Zitadel, NATS or RabbitMQ, zitadel-setup, api, worker, billing, dashboard), drives the
# API over HTTP with Zitadel tokens (alice the seller, bob the bidder), and reads the result from both databases and the
# dashboard. usage: apphost.sh <AppHost build dir> [auctions, default 5]
#   [ARTFLOW_BROKER=rabbitmq] [ARTFLOW_API_NATIVE / ARTFLOW_WORKER_NATIVE / ARTFLOW_BILLING_NATIVE=<binary>]
set -uo pipefail
dir="$1"; count="${2:-5}"; log="$(mktemp)"; traces="$(mktemp)"; zt="${TMPDIR:-/tmp}/artflow-zitadel"
export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_URLS=http://localhost:18888 ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:18890
( cd "$dir" && exec timeout 300 dotnet ArtFlow.AppHost.dll > "$log" 2>&1 ) &
apphost=$!
api=http://localhost:5080
for i in $(seq 1 240); do curl -sf "$api/health" >/dev/null && break; sleep 1; done
echo "api /health: $(curl -s -o /dev/null -w '%{http_code}' "$api/health") after ${i}s; zitadel: $(docker ps --format '{{.Image}}' | grep zitadel)"
token() { read -r _ _ cid sec _ < <(grep "^client $1 " "$zt/clients.txt")
  curl -s -u "$cid:$sec" -d grant_type=client_credentials --data-urlencode "scope=openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner urn:zitadel:iam:org:project:id:$(cat "$zt/project.txt"):aud" \
    http://localhost:8080/oauth/v2/token | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])'; }
alice=$(token alice); bob=$(token bob); headers="$(mktemp)"; listheaders="$(mktemp)"; stale_lists=0
for n in $(seq 1 "$count"); do
  id=$(curl -s -X POST "$api/auctions" -H "Authorization: Bearer $alice" -H 'Content-Type: application/json' -d '{"reserve":100}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  bid=$(curl -s -o /dev/null -D "$headers" -w '%{http_code}' -X POST "$api/auctions/$id/bids" -H "Authorization: Bearer $bob" -H 'Content-Type: application/json' -d '{"amount":150}')
  position=$(grep -i '^Tarol-Position:' "$headers" | tr -d '\r' | awk '{print $2}')
  # read your own write: the list waits (at most 2 s) until the worker's projection passed the bid's commit
  listed=$(curl -s -D "$listheaders" "$api/auctions?settled=false&limit=200" -H "Authorization: Bearer $bob" -H "Tarol-Read-After: $position" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); r=[i for i in d['items'] if i['id']=='$id']; print(('bids %d, highestBid %s' % (r[0]['bids'], r[0]['highestBid'])) if r else 'missing')")
  stale=$(grep -ci '^Tarol-Stale:' "$listheaders")
  wrong=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$api/auctions/$id/settle" -H "Authorization: Bearer $bob")
  settle=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$api/auctions/$id/settle" -H "Authorization: Bearer $alice")
  [ "$stale" = 0 ] || stale_lists=$((stale_lists + 1))
  [ "$n" = 1 ] && echo "auction 1: bid $bid (Tarol-Position $position), list read after it: $listed, stale $stale; settle by bob $wrong, settle by alice $settle; view: $(curl -s "$api/auctions/$id" -H "Authorization: Bearer $bob")"
done
# the cached list (step 13): the same request again revalidates by ETag (304 from the page cache), with the cache headers
cached="$(mktemp)"; curl -s -o /dev/null -D "$cached" "$api/auctions?limit=50" -H "Authorization: Bearer $bob"
etag=$(grep -i '^ETag:' "$cached" | tr -d '\r' | cut -d' ' -f2-)
echo "cached list: 304 by ETag $(curl -s -o /dev/null -w '%{http_code}' "$api/auctions?limit=50" -H "Authorization: Bearer $bob" -H "If-None-Match: $etag"); $(grep -iE '^(cache-control|vary):' "$cached" | tr -d '\r' | tr '\n' ';' )"
q() { docker exec -e PGPASSWORD="$pw" "$pg" psql -h localhost -U postgres -d "${2:-tarol}" -tAc "$1" 2>/dev/null; }
pg=$(docker ps --format '{{.ID}} {{.Names}}' | awk '$2 ~ /^pg-/ {print $1}' | head -1); pw=$(docker exec "$pg" printenv POSTGRES_PASSWORD)
for i in $(seq 1 60); do n=$(q "SELECT count(*) FROM tarol.events WHERE type = 'payout'"); [ "${n:-0}" -ge "$count" ] && break; sleep 1; done
echo "events by stream type (count/with traceparent): $(q "SELECT string_agg(type || ' ' || c || '/' || t, ', ' ORDER BY type) FROM (SELECT type, count(*) c, count(traceparent) t FROM tarol.events GROUP BY type) x")"
echo "payout actor: $(q "SELECT DISTINCT actor_kind || ' ' || actor_id || ' on behalf of <alice''s Zitadel user id>' FROM tarol.events WHERE type = 'payout'")"
for i in $(seq 1 60); do n=$(q "SELECT count(*) FROM tarol.events WHERE actor_id = 'Consumers.RecordPayment'"); [ "${n:-0}" -ge "$count" ] && break; sleep 1; done
echo "billing events (count/with traceparent): $(q "SELECT string_agg(alias || ' ' || c || '/' || t, ', ' ORDER BY alias) FROM (SELECT alias, count(*) c, count(traceparent) t FROM tarol.events GROUP BY alias) x" billingdb)"
echo "billing actors: $(q "SELECT string_agg(DISTINCT actor_kind || ' ' || actor_id || coalesce(' for ' || CASE WHEN on_behalf_of LIKE 'alice%' OR length(on_behalf_of) > 12 THEN '<alice>' ELSE on_behalf_of END, ''), ', ') FROM tarol.events" billingdb)"
echo "payments recorded in ArtFlow from billing: $(q "SELECT count(*) FROM tarol.events WHERE actor_id = 'Consumers.RecordPayment'") by $(q "SELECT DISTINCT actor_kind || ' ' || actor_id || ' for ' || on_behalf_of FROM tarol.events WHERE actor_id = 'Consumers.RecordPayment'")"
echo "settlement sagas completed: $(q "SELECT count(*) FROM tarol.events WHERE type = 'settlement' AND alias LIKE '%ompleted%'")"
echo "outbox rows left: $(q "SELECT count(*) FROM tarol.outbox") (the $count settlement deadlines, due in 3 days); billing: $(q "SELECT count(*) FROM tarol.outbox" billingdb)"
sleep 2
echo "auction list: $(curl -s "$api/auctions?count=true&limit=3" -H "Authorization: Bearer $bob" | python3 -c 'import json,sys; d=json.load(sys.stdin); print("total %d, settled %d of the first %d, next cursor %s" % (d["total"], sum(i["settled"] for i in d["items"]), len(d["items"]), "yes" if d["next"] else "no"))'); stale read-after answers: $stale_lists of $count"
echo "alice's own auctions (claim-bound): $(curl -s "$api/my/auctions?count=true" -H "Authorization: Bearer $alice" | python3 -c 'import json,sys; print(json.load(sys.stdin)["total"])'), bob's: $(curl -s "$api/my/auctions?count=true" -H "Authorization: Bearer $bob" | python3 -c 'import json,sys; print(json.load(sys.stdin)["total"])')"
etag=$(curl -s -o /dev/null -D - "$api/auctions" -H "Authorization: Bearer $bob" | grep -i '^etag:' | tr -d '\r' | cut -d' ' -f2-)
echo "list ETag $etag, again with If-None-Match: $(curl -s -o /dev/null -w '%{http_code}' "$api/auctions" -H "Authorization: Bearer $bob" -H "If-None-Match: $etag")"
echo "projection checkpoint: $(q "SELECT name || ' v' || version || ' ' || state FROM tarol.projections"), rows $(q "SELECT count(*) FROM tarol.rm_auction_list_v1")"
# step 14: the settlement report runs every minute here (Tarol:Jobs:SettlementReport:Cron); wait for its first occurrence
for i in $(seq 1 75); do n=$(q "SELECT count(*) FROM tarol.events WHERE type = 'day_report'"); [ "${n:-0}" -ge 1 ] && break; sleep 1; done
echo "job rows: $(q "SELECT string_agg(name || ' [' || schedule || '] next ' || to_char(next_at AT TIME ZONE 'UTC', 'HH24:MI:SS') || ' attempts ' || attempts, '; ' ORDER BY name) FROM tarol.jobs"); dead jobs: $(q "SELECT count(*) FROM tarol.jobs_dead")"
echo "settlement reports: $(q "SELECT count(*) || ' by ' || string_agg(DISTINCT actor_kind || ' ' || actor_id, ', ') FROM tarol.events WHERE type = 'day_report'"), settled in the last: $(q "SELECT payload->>'settled' FROM tarol.events WHERE type = 'day_report' ORDER BY recorded_at DESC LIMIT 1"), keys: $(q "SELECT count(*) FROM tarol.idempotency_keys WHERE scope = 'job:SettlementReport'")"
echo "dead letters: outbox $(q "SELECT count(*) FROM tarol.outbox_dead")/$(q "SELECT count(*) FROM tarol.outbox_dead" billingdb), messages $(q "SELECT count(*) FROM tarol.messages_dead")/$(q "SELECT count(*) FROM tarol.messages_dead" billingdb)"
sleep 6
curl -s "http://localhost:18888/api/telemetry/resources" | python3 -c 'import json,sys; print("dashboard resources with traces:", sorted(r["name"] for r in json.load(sys.stdin) if r["hasTraces"]))'
curl -s "http://localhost:18888/api/telemetry/traces?limit=5000" > "$traces"
python3 - "$traces" <<'PY'
import json, sys, collections
d = json.load(open(sys.argv[1])); names = collections.Counter(); linked = collections.Counter()
def walk(o):
    if isinstance(o, dict):
        if 'spanId' in o and 'name' in o:
            names[o['name']] += 1
            if o.get('links'): linked[o['name']] += 1
        for v in o.values(): walk(v)
    elif isinstance(o, list):
        for v in o: walk(v)
walk(d)
print("spans: %s" % ", ".join("%s %d" % kv for kv in sorted(names.items()) if "health" not in kv[0]))
print("spans with an ActivityLink: %s" % (", ".join("%s %d" % kv for kv in sorted(linked.items())) or "none"))
PY
kill $apphost 2>/dev/null; wait $apphost 2>/dev/null
grep -iE ' error|fail:|unhandled' "$log" | cut -c1-220 | head -5
for c in $(docker ps -aq --filter "name=^pg-") $(docker ps -aq --filter "name=^zitadel-") $(docker ps -aq --filter "name=^nats-") $(docker ps -aq --filter "name=^rabbitmq-"); do docker rm -f "$c" >/dev/null; done
rm -f "$log" "$traces" "$headers" "$listheaders"
