#!/usr/bin/env bash
# Runs the built AppHost (PostgreSQL resource, api, worker, dashboard), drives the API over HTTP with a JWT, waits for the
# worker's reaction in the database Aspire started, and reads both resources' spans from the dashboard's telemetry API.
# usage: apphost.sh <AppHost build dir>   [PROBE_APP_NATIVE=<native app binary>] [PROBE_ZITADEL_DIR=<empty dir> PROBE_ZITADEL_SETUP=<setup.py>]
set -uo pipefail
dir="$1"; log="$(mktemp)"; traces="$(mktemp)"
export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_URLS=http://localhost:18888 ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:18890
( cd "$dir" && exec timeout 300 dotnet Probe.Rest.AppHost.dll > "$log" 2>&1 ) &
apphost=$!
api=http://127.0.0.1:5099
for i in $(seq 1 240); do curl -sf "$api/health" >/dev/null && curl -sf http://127.0.0.1:5098/health >/dev/null && break; sleep 1; done
echo "api /health: $(curl -s -o /dev/null -w '%{http_code}' "$api/health"), worker /health: $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5098/health)"
if [ -n "${PROBE_ZITADEL_DIR:-}" ]; then   # a real token from the Zitadel resource (client credentials of the machine user alice)
  read -r _ _ cid sec _ < <(grep '^client alice ' "$PROBE_ZITADEL_DIR/clients.txt")
  pid=$(cat "$PROBE_ZITADEL_DIR/project.txt")
  token=$(curl -s -u "$cid:$sec" -d grant_type=client_credentials --data-urlencode "scope=openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner urn:zitadel:iam:org:project:id:$pid:aud" http://localhost:8080/oauth/v2/token | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
  echo "zitadel resource: $(docker ps --format '{{.Image}} {{.Names}}' | awk '$2 ~ /^zitadel-/'); token for alice from http://localhost:8080 (${#token} chars)"
else
  token=$(python3 - <<'PY'
import base64, hashlib, hmac, json, time
b = lambda d: base64.urlsafe_b64encode(d).rstrip(b"=").decode()
h = b(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
p = b(json.dumps({"sub": "alice", "role": ["seller"], "scope": "auctions:write auctions:bid", "tenant": "0193a000-0000-7000-8000-00000000000a",
                  "iss": "probe", "aud": "artflow", "exp": int(time.time()) + 3600}).encode())
s = b(hmac.new(b"probe-signing-key-0123456789abcdef-0123456789abcdef", f"{h}.{p}".encode(), hashlib.sha256).digest())
print(f"{h}.{p}.{s}")
PY
)
fi
ends=$(date -u -d '+7 days' +%Y-%m-%dT%H:%M:%SZ)
created=$(curl -s -X POST "$api/auctions" -H "Authorization: Bearer $token" -H 'Content-Type: application/json' -d "{\"title\":\"Aspire\",\"reserve\":100,\"endsAt\":\"$ends\"}")
id=$(echo "$created" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
echo "POST /auctions: $created"
echo "POST /auctions/{id}/close: $(curl -s -o /dev/null -w '%{http_code}' -X POST "$api/auctions/$id/close" -H "Authorization: Bearer $token")"
q() { docker exec -e PGPASSWORD="$pw" "$pg" psql -h localhost -U postgres -d tarol -tAc "$1" 2>/dev/null; }
pg=$(docker ps --format '{{.ID}} {{.Names}}' | awk '$2 ~ /^pg-/ {print $1}' | head -1); pw=$(docker exec "$pg" printenv POSTGRES_PASSWORD); n=0
for i in $(seq 1 60); do n=$(q "SELECT count(*) FROM tarol.events WHERE type = 'payout'"); [ "${n:-0}" -ge 1 ] && break; sleep 1; done
echo "postgres resource: $(docker ps --format '{{.Image}} {{.Names}}' | awk '$2 ~ /^pg-/')"
echo "events (stream type: count/with traceparent): $(q "SELECT string_agg(type || ' ' || c || '/' || t, ', ' ORDER BY type) FROM (SELECT type, count(*) c, count(traceparent) t FROM tarol.events GROUP BY type) x")"
echo "payout actor: $(q "SELECT actor_kind || ' ' || actor_id || ' on behalf of ' || coalesce(on_behalf_of, '-') FROM tarol.events WHERE type = 'payout'")"
echo "outbox rows left: $(q "SELECT count(*) FROM tarol.outbox")"
sleep 6   # the OTLP exporters' batch interval
curl -s "http://localhost:18888/api/telemetry/resources" | python3 -c 'import json,sys; print("dashboard resources:", sorted((r["name"], r["hasTraces"]) for r in json.load(sys.stdin)))'
curl -s "http://localhost:18888/api/telemetry/traces?limit=2000" > "$traces"
python3 - "$traces" <<'PY'
import json, sys, collections
d = json.load(open(sys.argv[1])); names = collections.Counter(); linked = collections.Counter(); spans = {}
def walk(o):
    if isinstance(o, dict):
        if 'spanId' in o and 'name' in o:
            names[o['name']] += 1; spans[o['spanId']] = o
            if o.get('links'): linked[o['name']] += 1
        for v in o.values(): walk(v)
    elif isinstance(o, list):
        for v in o: walk(v)
walk(d)
print("dashboard spans: %s" % ", ".join("%s %d" % kv for kv in sorted(names.items()) if not kv[0].startswith("GET /health")))
print("spans with an ActivityLink: %s" % (", ".join("%s %d" % kv for kv in sorted(linked.items())) or "none"))
PY
kill $apphost 2>/dev/null; wait $apphost 2>/dev/null
grep -iE " error|fail:|exception|Distributed application started" "$log" | cut -c1-220 | head -10
for c in $(docker ps -aq --filter "name=^pg-") $(docker ps -aq --filter "name=^zitadel-"); do docker rm -f "$c" >/dev/null; done
rm -f "$log" "$traces"
