#!/usr/bin/env bash
# Runs every queries probe against PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>   (needs a running Docker daemon: dockerd &)
# ALL_FROM=<n> resumes at section n (ALL_TO=<m> stops after m); keeps the work dir and the database, appends to output.txt.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; from="${ALL_FROM:-1}"; to="${ALL_TO:-99}"
if [ "$from" -le 1 ]; then rm -rf "$w"; : > "$out"; fi; mkdir -p "$w"
at() { [ "$from" -le "$1" ] && [ "$1" -le "$to" ]; }
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-600; }
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECK|xception|^\|' | clean; }
up() {   # (re)start PostgreSQL when it is not running (a session restart stops Docker); the seeded data survives in the container
  if ! docker exec tarol-probe-pg pg_isready -q 2>/dev/null; then
    docker start tarol-probe-pg >/dev/null 2>&1 || docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
    until docker exec tarol-probe-pg pg_isready -q 2>/dev/null; do sleep 1; done; sleep 2
  fi
}
app="$w/a/src/Queries"; native="$w/c/out/Probe.Queries"
J() { (cd "$app" && timeout 1800 dotnet run --no-build -- "$@" 2>&1) | show; }
{
up
if at 1; then
echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(free -g | awk '/Mem/{print $2}') GB, $(docker exec tarol-probe-pg postgres --version) in Docker (default settings, max_connections=300)"
echo; echo "## 1. JIT: functional checks (memory = the ProjectionTest shape; postgres = the production shape)"
"$here/run.sh" "$w/a" Queries build > "$w/a.log" 2>&1; grep -E ' error ' "$w/a.log" | clean | sort -u
J selftest memory; J selftest postgres
fi
if at 2; then
echo; echo "## 2. Seed: 10,000 auctions x (open + 8 bids + close) through the store, 8 writers, one commit per event; copied by SQL to 1e6 events"
J bench seed 10000 10
fi
if at 3; then
echo; echo "## 3. Catch-up from the start over 1e6 events (rows + checkpoint per batch; A1 = in one transaction, A2 = separate statements)"
J bench catchup
fi
if at 4; then
echo; echo "## 4. Rebuild: truncate + replay (what readers see) vs a new version + swap (rename under query load)"
J bench rebuild
fi
if at 5; then
echo; echo "## 5. Commit cost per candidate (2,000 sequential bids each; the first baseline warms up) and the outbox-row projection loop"
J bench commit 2000
fi
if at 6; then
echo; echo "## 6. Query latency, offset vs cursor at depth (page of 50; 100 runs, 20 at depth >= 100,000; p50 / p99)"
J bench query 1000000 100000
fi
if at 7; then
echo; echo "## 7. The stored state (streams.state) as the read model vs the projection table (1e5 auction streams after section 2)"
J bench state
fi
if at 8; then
echo; echo "## 8. Lag: commit returned -> the projection's checkpoint passed it (50 commits/s for 10 s; first 10 % skipped)"
echo "| wake-up | writers | lag | |"; echo "|---|---|---|---|"
for m in poll100 poll10 notify; do for n in 1 4; do J bench lag $m $n 10; done; done
echo; J bench ryw 400
fi
if at 9; then
echo; echo "## 9. Native AOT: publish (warnings, size), the same checks natively, lag and read-your-writes natively"
"$here/run.sh" "$w/c" Queries publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
grep -E 'warning|error' "$w/c.log" | clean | sort -u
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/c.log")"; echo "native binary: $(stat -c %s "$native") bytes"
for k in memory postgres; do PROBE_RUNTIME="Native AOT" timeout 600 "$native" selftest $k 2>&1 | grep -E '^# |FAIL|ALL|CHECK|xception' | clean; done
echo "| wake-up | writers | lag | |"; echo "|---|---|---|---|"
PROBE_RUNTIME="Native AOT" timeout 600 "$native" bench lag notify 4 10 2>&1 | show
PROBE_RUNTIME="Native AOT" timeout 600 "$native" bench ryw 400 2>&1 | show
PROBE_RUNTIME="Native AOT" timeout 900 "$native" bench catchup 2>&1 | show
fi
} 2>&1 | tee -a "$out"
