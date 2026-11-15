#!/usr/bin/env bash
# Runs the built AppHost: PostgreSQL container resource, worker ("serve 20": 20 settled auctions, one per second), dashboard.
# Reads the result from the database Aspire started and the worker's spans from the dashboard's telemetry API.
# usage: apphost.sh <AppHost build dir>   [PROBE_WORKER_NATIVE=<native worker binary>]
set -uo pipefail
dir="$1"; log="$(mktemp)"; traces="$(mktemp)"
export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_URLS=http://localhost:18888 ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:18890
( cd "$dir" && exec timeout 180 dotnet Probe.Sagas.AppHost.dll > "$log" 2>&1 ) &
apphost=$!
q() { docker exec -e PGPASSWORD="$pw" "$pg" psql -h localhost -U postgres -d tarol -tAc "$1" 2>/dev/null; }
pg=""; pw=""; n=0
for i in $(seq 1 120); do
  pg=$(docker ps --format '{{.ID}} {{.Names}}' | awk '$2 ~ /^pg-/ {print $1}' | head -1)
  if [ -n "$pg" ]; then pw=$(docker exec "$pg" printenv POSTGRES_PASSWORD); n=$(q "SELECT count(*) FROM tarol.events WHERE type = 'payout'"); fi
  [ "${n:-0}" -ge 20 ] && break; sleep 1; done
echo "postgres resource: $(docker ps --format '{{.Image}} {{.Names}}' | awk '$2 ~ /^pg-/')"
echo "events by stream type (count, with traceparent): $(q "SELECT string_agg(type || ' ' || c || '/' || t, ', ' ORDER BY type) FROM (SELECT type, count(*) c, count(traceparent) t FROM tarol.events GROUP BY type) x")"
echo "outbox rows left: $(q "SELECT count(*) FROM tarol.outbox") (the 20 settlement timeouts, due in 3 days)"
curl -s "http://localhost:18888/api/telemetry/resources" | python3 -c 'import json,sys; print("dashboard resources:", [(r["name"], r["hasTraces"]) for r in json.load(sys.stdin)])'
curl -s "http://localhost:18888/api/telemetry/traces?limit=2000" > "$traces"
python3 - "$traces" <<'PY'
import json, sys, collections
d = json.load(open(sys.argv[1])); names = collections.Counter(); linked = collections.Counter(); traces = set()
def walk(o):
    if isinstance(o, dict):
        if 'spanId' in o and 'name' in o:
            names[o['name']] += 1; traces.add(o.get('traceId'))
            if o.get('links'): linked[o['name']] += 1
        for v in o.values(): walk(v)
    elif isinstance(o, list):
        for v in o: walk(v)
walk(d)
print("dashboard spans (totalCount %s): %s" % (d.get('totalCount'), ", ".join("%s %d" % kv for kv in sorted(names.items()))))
print("spans with an ActivityLink: %s" % (", ".join("%s %d" % kv for kv in sorted(linked.items())) or "none"))
PY
kill $apphost 2>/dev/null; wait $apphost 2>/dev/null
grep -iE 'error|fail|exception|AppHost version|Distributed application started' "$log" | cut -c1-220 | head -10
for c in $(docker ps -aq --filter "name=^pg-"); do docker rm -f "$c" >/dev/null; done
rm -f "$log" "$traces"
