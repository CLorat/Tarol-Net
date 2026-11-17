#!/usr/bin/env bash
# Runs every operations probe against PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>   (needs a running Docker daemon: dockerd &)
# Resumable: each section writes <work-dir>/sec-<n>.txt and a .ok marker; a rerun skips finished sections (ALL_REDO=<n> redoes one).
# Never run it next to the full test suite: sections 2 and 4-9 measure latency.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; mkdir -p "$w"
[ -n "${ALL_REDO:-}" ] && rm -f "$w/sec-$ALL_REDO.ok"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-900; }
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECK|xception|^\|' | clean; }
up() {
  docker start tarol-probe-pg >/dev/null 2>&1 || docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
  until docker exec tarol-probe-pg pg_isready -q 2>/dev/null; do sleep 1; done; sleep 2
}
app="$w/a/src/Operations"
J() { (cd "$app" && timeout 1800 dotnet run --no-build -c Release -- "$@" 2>&1) | show; }
N() { local v=$1; shift; (cd "$w/$v/out" && PROBE_RUNTIME="Native AOT" timeout 1800 ./Probe.Operations "$@" 2>&1) | show; }
sec() {   # n title commands…
  local n=$1 title=$2; shift 2
  [ -f "$w/sec-$n.ok" ] && return 0
  { echo; echo "## $n. $title"; "$@"; } > "$w/sec-$n.txt" 2>&1 && touch "$w/sec-$n.ok"
  cat "$w/sec-$n.txt"
}
up
[ -f "$w/a.ok" ] || { "$here/run.sh" "$w/a" Operations build -c Release > "$w/a.log" 2>&1 && touch "$w/a.ok"; }
s0() { echo "# SDK $(dotnet --version) (JIT runs: Release build), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(free -g | awk '/Mem/{print $2}') GB; $(docker exec tarol-probe-pg postgres --version) in Docker on the same host (default settings, max_connections=300); Npgsql $(grep -oE 'Npgsql" Version="[0-9.]+' "$here/../../../../Directory.Packages.props" | grep -oE '[0-9.]+$'); Microsoft.Extensions.Diagnostics.HealthChecks 11.0.0"; grep -E ' error |warning' "$w/a.log" | clean | sort -u; true; }
s1() { J selftest schema; J selftest dead; J selftest health; echo "| Npgsql tracing | idle worker, 10 s (spans by source, root or child) | 20 creations + 20 changes with their reactions, 3 s |"; echo "|---|---|---|"; J selftest spans; }
s2() { J bench metrics; }
s3() {
  [ -f "$w/c.ok" ] || { "$here/run.sh" "$w/c" Operations publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1 && touch "$w/c.ok"; }
  [ -f "$w/n.ok" ] || { "$here/run.sh" "$w/n" Operations publish -c Release -r linux-x64 -p:WithHealth=false -o "$w/n/out" > "$w/n.log" 2>&1 && touch "$w/n.ok"; }
  [ -f "$w/x.ok" ] || { "$here/run.sh" "$w/x" Operations publish -c Release -r linux-x64 -p:WithWeb=true -o "$w/x/out" > "$w/x.log" 2>&1 && touch "$w/x.ok"; }
  echo "| build | IL warnings | native binary |"; echo "|---|---|---|"
  for v in n c x; do
    case $v in n) label="no health checks (-p:WithHealth=false)";; c) label="+ Microsoft.Extensions.Diagnostics.HealthChecks 11.0.0 (worker, no web)";; x) label="+ ASP.NET Core shared framework, Kestrel, MapHealthChecks (-p:WithWeb=true)";; esac
    echo "| $label | $(grep -oE 'IL[23][0-9]{3}' "$w/$v.log" | wc -l) | $(stat -c %s "$w/$v/out/Probe.Operations") bytes |"
  done
  grep -E 'warning|error' "$w/c.log" "$w/n.log" "$w/x.log" | clean | sort -u
  N c selftest schema; N c selftest dead; N c selftest health; N x selftest health
  echo "| Npgsql tracing | idle worker, 10 s (spans by source, root or child) | 20 creations + 20 changes with their reactions, 3 s |"; echo "|---|---|---|"; N c selftest spans
}
s4() { J bench gauges; }
s5() { J bench upgrade 1000000; }
s6() { J bench retention 200000; }
s7() { echo "| drop of the old version while another session reads it for 3 s | the drop | a reader that arrives meanwhile |"; echo "|---|---|---|"; J bench drop; }
s8() { J bench runners 20000; }
s9() { N c bench metrics; N c bench runners 20000; }
{
sec 0 "Machine" s0
sec 1 "JIT: functional checks (schema = EnsureSchema, upgrade steps, rolling deploys, CONCURRENTLY; dead = paging, re-drive, discard, audit; health = snapshot and checks; spans = Npgsql tracing of an idle worker)" s1
sec 2 "Instruments on the hot path (JIT)" s2
sec 3 "Native AOT: publish with and without health checks and the web endpoints (warnings, size); the checks natively" s3
sec 4 "Observable gauges: a query per scrape vs one cached snapshot (1,000,000 events, 10,000 job rows, outbox 0 … 1,000,000 rows)" s4
sec 5 "Schema changes on a populated store (1,000,000 events) while 4 writers commit" s5
sec 6 "Retention of the dead tables" s6
sec 7 "DropVersion while old code still reads the table" s7
sec 8 "Several runners of one projection (200,000 events, batch 1,000)" s8
sec 9 "Native AOT: instruments and projection runners" s9
} 2>&1 > /dev/null

{ cat "$w/sec-0.txt" | sed '1{/^$/d}'; for n in $(seq 1 9); do [ -f "$w/sec-$n.txt" ] && cat "$w/sec-$n.txt"; done; } > "$out"
for n in $(seq 0 9); do [ -f "$w/sec-$n.ok" ] || echo "section $n not finished"; done
