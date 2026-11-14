#!/usr/bin/env bash
# Runs every probe against a PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>     (needs a running Docker daemon: dockerd &)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; rm -rf "$w"; mkdir -p "$w"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g" | cut -c1-260; }
docker rm -f tarol-probe-pg >/dev/null 2>&1
docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=200 >/dev/null
until docker exec tarol-probe-pg pg_isready -q; do sleep 1; done; sleep 2
export PROBE_PG="Host=localhost;Port=55432;Username=postgres;Password=probe;Maximum Pool Size=150"
{
echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(docker exec tarol-probe-pg postgres --version)"
echo; echo "## App (JIT): store contract, metadata, lookups L-A/L-B, keys, outbox rows, [LoadByReplay], schema change, upcaster, ReadAll gaps, outbox claims"
"$here/run.sh" "$w/a" App run 2>&1 | grep -v '^\s*$' | clean
echo; echo "## App (JIT, Release): contention, N writers on one stream, each load → Relot → append"
echo "| guard | writers | attempts | ok | conflict | other | thrown | gapless | attempts/s | p50 ms | p99 ms |"
echo "|---|---|---|---|---|---|---|---|---|---|---|"
"$here/run.sh" "$w/b" App run -c Release -- contention 2>&1 | grep -E '^\||exception|FAILED|PASSED' | clean
echo; echo "## App (Native AOT): publish warnings, then the native run"
"$here/run.sh" "$w/c" App publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
grep -E 'warning|error' "$w/c.log" | clean | sort -u
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/c.log")"
ls -l "$w/c/out/Probe.Persistence.App" | awk '{print "native binary: " $5 " bytes"}'
PROBE_RUNTIME="Native AOT" "$w/c/out/Probe.Persistence.App" 2>&1 | clean
echo; echo "## Contract: one suite, InMemoryAggregateStore and the Npgsql store (Testcontainers postgres:17)"
"$here/run.sh" "$w/d" Contract run 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | grep -E 'Total:|error|\[FAIL\]' | sed 's/^ *//' | clean
echo; echo "## Bench: BenchmarkDotNet --job short --inProcess"
"$here/run.sh" "$w/e" Bench run -c Release -- --filter '*' --job short --inProcess 2>&1 | grep -E '^\||^BenchmarkDotNet v|^// Runtime=' | clean
} > "$out" 2>&1
docker rm -f tarol-probe-pg >/dev/null 2>&1
echo "written $out"
