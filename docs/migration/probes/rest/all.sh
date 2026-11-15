#!/usr/bin/env bash
# Runs every REST probe against a PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>     (needs a running Docker daemon: dockerd &)
# ALL_FROM=<n> resumes at section n: keeps the work dir (sections 7-10 reuse the builds of 5) and appends to output.txt
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; from="${ALL_FROM:-1}"
if [ "$from" -le 1 ]; then rm -rf "$w"; fi; mkdir -p "$w"
at() { [ "$from" -le "$1" ]; }
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g" | cut -c1-400; }
docker rm -f tarol-probe-pg >/dev/null 2>&1
docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
until docker exec tarol-probe-pg pg_isready -q; do sleep 1; done; sleep 2
PG="Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=150"
selftest() { grep -E '^#|PASS|FAIL|INFO|ALL|xception' | clean; }
median() { sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }
startup() { # binary-or-dll, label: median of 5 starts (process start -> first 200 on /health)
  for i in 1 2 3 4 5; do "$@" startup 2>/dev/null | grep -oE '[0-9]+ ms' | grep -oE '[0-9]+'; done | median; }
{
at 1 && echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(docker exec tarol-probe-pg postgres --version)"
if at 1; then
echo; echo "## 1. JIT, in-memory store, binding B1 (record), problem details through IProblemDetailsService"
"$here/run.sh" "$w/a" App build -p:ProbeOpenApiMethod=true > "$w/a.log" 2>&1; grep -E ' error ' "$w/a.log" | clean | sort -u
(cd "$w/a/src/App" && dotnet run --no-build -- selftest 2>&1) | selftest
fi
if at 2; then
echo; echo "## 2. JIT, in-memory, problem details written by hand (no AddProblemDetails): the differing rows"
(cd "$w/a/src/App" && PROBE_PROBLEMS=writer dotnet run --no-build -- selftest 2>&1) | grep -E '^#|invalid body|low bid|close by another|ALL|FAIL' | clean
fi
if at 3; then
echo; echo "## 3. JIT, in-memory, binding B2 (generated member binder): the differing rows"
"$here/run.sh" "$w/b" App build -p:ProbeBinding=members > "$w/b.log" 2>&1; grep -E ' error ' "$w/b.log" | clean | sort -u
(cd "$w/b/src/App" && dotnet run --no-build -- selftest 2>&1) | grep -E '^#|wrong JSON|missing members|broken JSON|ALL|FAIL|INFO OpenAPI:' | clean
fi
if at 4; then
echo; echo "## 4. JIT, Npgsql store (PostgreSQL 17)"
(cd "$w/a/src/App" && PROBE_PG="$PG;Database=postgres" dotnet run --no-build -- selftest 2>&1) | selftest
fi
if at 5; then
echo; echo "## 5. Native AOT publish (B1, OpenAPI with MethodInfo metadata): warnings, size, native runs"
"$here/run.sh" "$w/c" App publish -c Release -r linux-x64 -p:ProbeOpenApiMethod=true -o "$w/c/out" > "$w/c.log" 2>&1
grep -E 'warning|error' "$w/c.log" | clean | sort -u
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/c.log")"
ls -l "$w/c/out/Probe.Rest.App" | awk '{print "native binary: " $5 " bytes"}'
PROBE_RUNTIME="Native AOT" PROBE_OPENAPI_DUMP="$w/openapi.json" "$w/c/out/Probe.Rest.App" selftest 2>&1 | selftest
echo "### native, Npgsql"
PROBE_RUNTIME="Native AOT" PROBE_PG="$PG;Database=postgres" "$w/c/out/Probe.Rest.App" selftest 2>&1 | grep -E '^#|FAIL|ALL|traceparent|xception' | clean
echo "### native, Npgsql, problem details by hand"
PROBE_RUNTIME="Native AOT" PROBE_PROBLEMS=writer PROBE_PG="$PG;Database=postgres" "$w/c/out/Probe.Rest.App" selftest 2>&1 | grep -E '^#|FAIL|ALL|xception' | clean
echo "### native OpenAPI document (generated endpoints): POST /auctions/{auction}/bids and GET /auctions/{auction}/summary"
python3 - "$w/openapi.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for path, verb in [("/auctions/{auction}/bids", "post"), ("/auctions/{auction}/summary", "get")]:
    op = d["paths"][path][verb]
    print(path, verb, "operationId:", op.get("operationId"), "| parameters:", [(p["name"], p["in"]) for p in op.get("parameters", [])],
          "| body:", list(op.get("requestBody", {}).get("content", {}).keys()), "| responses:", {k: list(v.get("content", {}).keys()) for k, v in op["responses"].items()},
          "| security in doc:", "security" in op)
print("PlaceBid schema:", json.dumps(d["components"]["schemas"]["PlaceBid"])[:300])
PY
fi
if at 6; then
echo; echo "## 6. Native AOT publish (B2 member binder): warnings, size, native run on Npgsql"
"$here/run.sh" "$w/d" App publish -c Release -r linux-x64 -p:ProbeBinding=members -o "$w/d/out" > "$w/d.log" 2>&1
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/d.log")"; ls -l "$w/d/out/Probe.Rest.App" | awk '{print "native binary: " $5 " bytes"}'
PROBE_RUNTIME="Native AOT" PROBE_PG="$PG;Database=postgres" "$w/d/out/Probe.Rest.App" selftest 2>&1 | grep -E '^#|FAIL|ALL|missing members|xception' | clean
fi
if at 7; then
echo; echo "## 7. Startup: process start -> first 200 on /health (median of 5)"
"$here/run.sh" "$w/e" App build -c Release > "$w/e.log" 2>&1
echo "| runtime | in-memory | Npgsql |"; echo "|---|---:|---:|"
echo "| JIT (Release) | $(startup dotnet "$w/e/src/App/bin/Release/net11.0/Probe.Rest.App.dll") ms | $(PROBE_PG="$PG;Database=postgres" startup dotnet "$w/e/src/App/bin/Release/net11.0/Probe.Rest.App.dll") ms |"
echo "| Native AOT | $(PROBE_RUNTIME="Native AOT" startup "$w/c/out/Probe.Rest.App") ms | $(PROBE_RUNTIME="Native AOT" PROBE_PG="$PG;Database=postgres" startup "$w/c/out/Probe.Rest.App") ms |"
fi
if at 8; then
echo; echo "## 8. Throughput and latency: generated endpoint vs hand-written minimal API (POST /…/watch, JWT, 16 clients, 5 s each, after two 3 s warm-ups)"
(dotnet "$w/e/src/App/bin/Release/net11.0/Probe.Rest.App.dll" bench 5 16 2>&1 | grep -E '^\|' | clean)
(PROBE_PG="$PG;Database=postgres" dotnet "$w/e/src/App/bin/Release/net11.0/Probe.Rest.App.dll" bench 5 16 2>&1 | grep -E '^\| (gen|hand)' | clean)
(PROBE_RUNTIME="Native AOT" "$w/c/out/Probe.Rest.App" bench 5 16 2>&1 | grep -E '^\| (gen|hand)' | clean)
(PROBE_RUNTIME="Native AOT" PROBE_PG="$PG;Database=postgres" "$w/c/out/Probe.Rest.App" bench 5 16 2>&1 | grep -E '^\| (gen|hand)' | clean)
fi
if at 9; then
echo; echo "## 9. Aspire AppHost 13.6.5: PostgreSQL resource + api + worker (JIT projects), dashboard"
"$here/run.sh" "$w/f" Aspire build > "$w/f.log" 2>&1; grep -E 'warning|error|Build succeeded' "$w/f.log" | clean | sort -u
"$here/Aspire/apphost.sh" "$(dirname "$(find "$w/f/src/Aspire/bin" -name Probe.Rest.AppHost.dll | head -1)")" 2>&1 | clean
fi
if at 10; then
echo; echo "## 10. Aspire AppHost: the same with the Native AOT app as both resources (AddExecutable)"
PROBE_APP_NATIVE="$w/c/out/Probe.Rest.App" "$here/Aspire/apphost.sh" "$(dirname "$(find "$w/f/src/Aspire/bin" -name Probe.Rest.AppHost.dll | head -1)")" 2>&1 | clean
fi
if at 11; then
echo; echo "## 11. A real identity provider: Zitadel (container) on PostgreSQL 17, JIT and Native AOT"
"$here/Zitadel/start.sh" "$w/zt" 2>&1 | clean
"$here/run.sh" "$w/g" App build > "$w/g.log" 2>&1; grep -E ' error ' "$w/g.log" | clean | sort -u
oidc() { PROBE_OIDC=http://localhost:8080 PROBE_OIDC_AUDIENCE_FILE="$w/zt/project.txt" PROBE_OIDC_CLIENTS="$w/zt/clients.txt" "$@" oidc-selftest 2>&1 | selftest; }
(cd "$w/g/src/App" && oidc dotnet run --no-build --)
"$here/run.sh" "$w/h" App publish -c Release -r linux-x64 -o "$w/h/out" > "$w/h.log" 2>&1
echo "IL warnings (publish with the OIDC mode): $(grep -cE 'IL[23][0-9]{3}' "$w/h.log")"
PROBE_RUNTIME="Native AOT" oidc "$w/h/out/Probe.Rest.App"
echo "### an access token's claims (alice, machine user; the secret part of the token not shown)"
read -r _ _ cid sec _ < <(grep '^client alice ' "$w/zt/clients.txt"); pid=$(cat "$w/zt/project.txt")
curl -s -u "$cid:$sec" -d grant_type=client_credentials --data-urlencode "scope=openid urn:zitadel:iam:org:projects:roles urn:zitadel:iam:user:resourceowner urn:zitadel:iam:org:project:id:$pid:aud" http://localhost:8080/oauth/v2/token \
  | python3 -c 'import base64,json,sys; t=json.load(sys.stdin)["access_token"].split("."); d=lambda s: json.loads(base64.urlsafe_b64decode(s+"="*(-len(s)%4))); print("header:", d(t[0])); print("payload:", json.dumps(d(t[1])))'
docker rm -f zt ztpg >/dev/null 2>&1
fi
if at 12; then
echo; echo "## 12. Aspire AppHost with Zitadel: PostgreSQL + zitadel (container) + zitadel-setup + api + worker"
"$here/run.sh" "$w/i" Aspire build > "$w/i.log" 2>&1; grep -E ' error|Build succeeded' "$w/i.log" | clean | sort -u
mkdir -p "$w/zta"; chmod 777 "$w/zta"
PROBE_ZITADEL_DIR="$w/zta" PROBE_ZITADEL_SETUP="$here/Zitadel/setup.py" "$here/Aspire/apphost.sh" "$(dirname "$(find "$w/i/src/Aspire/bin" -name Probe.Rest.AppHost.dll | head -1)")" 2>&1 | clean
fi
} > "$out.part" 2>&1
if at 1; then mv "$out.part" "$out"; else cat "$out.part" >> "$out"; rm "$out.part"; fi
docker rm -f tarol-probe-pg >/dev/null 2>&1
echo "written $out"
