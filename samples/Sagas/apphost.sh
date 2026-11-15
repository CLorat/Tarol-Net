#!/usr/bin/env bash
# Runs the built AppHost (PostgreSQL, Zitadel, zitadel-setup, api, worker, dashboard), drives the API over HTTP with
# Zitadel tokens (alice the seller, bob the bidder), and reads the result from the database and the dashboard.
# usage: apphost.sh <AppHost build dir> [auctions, default 5]   [ARTFLOW_API_NATIVE / ARTFLOW_WORKER_NATIVE=<binary>]
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
alice=$(token alice); bob=$(token bob)
for n in $(seq 1 "$count"); do
  id=$(curl -s -X POST "$api/auctions" -H "Authorization: Bearer $alice" -H 'Content-Type: application/json' -d '{"reserve":100}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  bid=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$api/auctions/$id/bids" -H "Authorization: Bearer $bob" -H 'Content-Type: application/json' -d '{"amount":150}')
  wrong=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$api/auctions/$id/settle" -H "Authorization: Bearer $bob")
  settle=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$api/auctions/$id/settle" -H "Authorization: Bearer $alice")
  [ "$n" = 1 ] && echo "auction 1: bid $bid, settle by bob $wrong, settle by alice $settle; view: $(curl -s "$api/auctions/$id" -H "Authorization: Bearer $bob")"
done
q() { docker exec -e PGPASSWORD="$pw" "$pg" psql -h localhost -U postgres -d tarol -tAc "$1" 2>/dev/null; }
pg=$(docker ps --format '{{.ID}} {{.Names}}' | awk '$2 ~ /^pg-/ {print $1}' | head -1); pw=$(docker exec "$pg" printenv POSTGRES_PASSWORD)
for i in $(seq 1 60); do n=$(q "SELECT count(*) FROM tarol.events WHERE type = 'payout'"); [ "${n:-0}" -ge "$count" ] && break; sleep 1; done
echo "events by stream type (count/with traceparent): $(q "SELECT string_agg(type || ' ' || c || '/' || t, ', ' ORDER BY type) FROM (SELECT type, count(*) c, count(traceparent) t FROM tarol.events GROUP BY type) x")"
echo "payout actor: $(q "SELECT DISTINCT actor_kind || ' ' || actor_id || ' on behalf of <alice''s Zitadel user id>' FROM tarol.events WHERE type = 'payout'")"
echo "outbox rows left: $(q "SELECT count(*) FROM tarol.outbox") (the $count settlement deadlines, due in 3 days)"
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
for c in $(docker ps -aq --filter "name=^pg-") $(docker ps -aq --filter "name=^zitadel-"); do docker rm -f "$c" >/dev/null; done
rm -f "$log" "$traces"
