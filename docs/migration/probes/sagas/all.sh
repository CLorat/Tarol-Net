#!/usr/bin/env bash
# Runs every probe against a PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>     (needs a running Docker daemon: dockerd &)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; rm -rf "$w"; mkdir -p "$w"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g" | cut -c1-300; }
docker rm -f tarol-probe-pg >/dev/null 2>&1
docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
until docker exec tarol-probe-pg pg_isready -q; do sleep 1; done; sleep 2
export PROBE_PG="Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=150"
{
echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(docker exec tarol-probe-pg postgres --version)"
echo; echo "## App (JIT): functional checks"
"$here/run.sh" "$w/a" App run 2>&1 | grep -v '^\s*$' | clean
echo; echo "## App (JIT, Release): throughput and latency"
"$here/run.sh" "$w/b" App run -c Release -- throughput 2>&1 | grep -E '^\||^#|FAIL|xception' | clean
echo; echo "## App (Native AOT): publish warnings, then the native run"
"$here/run.sh" "$w/c" App publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
grep -E 'warning|error' "$w/c.log" | clean | sort -u
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/c.log")"
ls -l "$w/c/out/Probe.Sagas.App" | awk '{print "native binary: " $5 " bytes"}'
PROBE_RUNTIME="Native AOT" "$w/c/out/Probe.Sagas.App" 2>&1 | grep -v '^\s*$' | clean
echo; echo "## Aspire AppHost 13.6.5 on SDK 11: PostgreSQL resource + worker project (JIT), dashboard"
"$here/run.sh" "$w/d" Aspire build > "$w/d.log" 2>&1; grep -E 'warning|error|Build succeeded' "$w/d.log" | clean | sort -u
"$here/Aspire/apphost.sh" "$(dirname "$(find "$w/d/src/Aspire/bin" -name Probe.Sagas.AppHost.dll | head -1)")" 2>&1 | clean
echo; echo "## Aspire AppHost: the same with the Native AOT worker (AddExecutable)"
PROBE_WORKER_NATIVE="$w/c/out/Probe.Sagas.App" "$here/Aspire/apphost.sh" "$(dirname "$(find "$w/d/src/Aspire/bin" -name Probe.Sagas.AppHost.dll | head -1)")" 2>&1 | clean
} > "$out" 2>&1
docker rm -f tarol-probe-pg >/dev/null 2>&1
echo "written $out"
