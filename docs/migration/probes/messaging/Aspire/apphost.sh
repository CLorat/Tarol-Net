#!/usr/bin/env bash
# Runs the built AppHost (PostgreSQL with databases artflow and billing, NATS with JetStream, api, worker, billing, dashboard),
# settles auctions through the api, waits until billing's invoice-paid came back as payments in ArtFlow's database, and
# reads every resource's spans from the dashboard's telemetry API.
# usage: apphost.sh <AppHost build dir> [auctions]    [PROBE_APP_NATIVE=<native app binary>]
set -uo pipefail
dir="$1"; count="${2:-5}"; log="$(mktemp)"; traces="$(mktemp)"
export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_URLS=http://localhost:18888 ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:18890
( cd "$dir" && exec timeout 400 dotnet Probe.Messaging.AppHost.dll > "$log" 2>&1 ) &
apphost=$!
api=http://127.0.0.1:5199; start=$(date +%s)
for i in $(seq 1 240); do curl -sf "$api/health" >/dev/null && break; kill -0 $apphost 2>/dev/null || { echo "the AppHost exited:"; grep -iE "exception|error" "$log" | head -5; rm -f "$log" "$traces"; exit 1; }; sleep 1; done
echo "api /health: $(curl -s -o /dev/null -w '%{http_code}' "$api/health") after $(( $(date +%s) - start )) s"
sleep 5                                                                        # worker and billing declare streams and consumers
for i in $(seq 1 "$count"); do echo "POST /auctions: $(curl -s -X POST "$api/auctions")"; done
pg=$(docker ps --format '{{.ID}} {{.Names}}' | awk '$2 ~ /^pg-/ {print $1}' | head -1); pw=$(docker exec "$pg" printenv POSTGRES_PASSWORD)
q() { docker exec -e PGPASSWORD="$pw" "$pg" psql -h localhost -U postgres -d "$1" -tAc "$2" 2>/dev/null; }
for i in $(seq 1 90); do n=$(q artflow "SELECT count(*) FROM artflow_aspire.events WHERE alias = 'auction.payment_recorded'"); [ "${n:-0}" -ge "$count" ] && break; sleep 1; done
echo "resources: $(docker ps --format '{{.Image}} {{.Names}}' | awk '$2 ~ /^(pg|nats)-/' | sort | tr '\n' ';')"
echo "artflow events (alias: count/with traceparent): $(q artflow "SELECT string_agg(alias || ' ' || c || '/' || t, ', ' ORDER BY alias) FROM (SELECT alias, count(*) c, count(traceparent) t FROM artflow_aspire.events GROUP BY alias) x")"
echo "billing events (alias: count/with traceparent): $(q billing "SELECT string_agg(alias || ' ' || c || '/' || t, ', ' ORDER BY alias) FROM (SELECT alias, count(*) c, count(traceparent) t FROM billing_aspire.events GROUP BY alias) x")"
echo "invoice actor (billing): $(q billing "SELECT DISTINCT actor_kind || ' ' || actor_id FROM billing_aspire.events WHERE alias = 'invoice.issued'")"
echo "payment actor (artflow): $(q artflow "SELECT DISTINCT actor_kind || ' ' || actor_id FROM artflow_aspire.events WHERE alias = 'auction.payment_recorded'")"
echo "payments: $(q artflow "SELECT count(*) || ' with ' || count(DISTINCT correlation_id) || ' correlations (the payment provider started each conversation)' FROM artflow_aspire.events WHERE alias = 'auction.payment_recorded'")"
echo "outbox rows left: artflow $(q artflow "SELECT count(*) FROM artflow_aspire.outbox"), billing $(q billing "SELECT count(*) FROM billing_aspire.outbox WHERE reaction NOT LIKE 'publish:%' AND reaction <> 'RemindBuyer'")"
sleep 6   # the OTLP exporters' batch interval
curl -s "http://localhost:18888/api/telemetry/resources" | python3 -c 'import json,sys; print("dashboard resources:", sorted((r["name"], r["hasTraces"]) for r in json.load(sys.stdin)))'
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
print("dashboard spans: %s" % ", ".join("%s %d" % kv for kv in sorted(names.items())))
print("spans with an ActivityLink: %s" % (", ".join("%s %d" % kv for kv in sorted(linked.items())) or "none"))
PY
kill $apphost 2>/dev/null; wait $apphost 2>/dev/null
grep -iE " error|fail:|unhandled|Distributed application started" "$log" | cut -c1-220 | head -10
for c in $(docker ps -aq --filter "name=^pg-") $(docker ps -aq --filter "name=^nats-"); do docker rm -f "$c" >/dev/null; done
rm -f "$log" "$traces"
