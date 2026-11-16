#!/usr/bin/env bash
# Runs every caching probe against PostgreSQL 17, Redis and Garnet in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>   (needs a running Docker daemon: dockerd &)
# Resumable: each section writes <work-dir>/sec-<n>.txt and a .ok marker; a rerun skips finished sections (ALL_REDO=<n> redoes one).
# output.txt is assembled from the section files at the end of every run.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; mkdir -p "$w"
[ -n "${ALL_REDO:-}" ] && rm -f "$w/sec-$ALL_REDO.ok"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-600; }
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECK|xception|^\|' | clean; }
up() {
  docker start tarol-probe-pg >/dev/null 2>&1 || docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
  docker start tarol-probe-redis >/dev/null 2>&1 || docker run -d --name tarol-probe-redis -p 56379:6379 redis:8 >/dev/null
  docker start tarol-probe-garnet >/dev/null 2>&1 || docker run -d --name tarol-probe-garnet -p 56380:6379 ghcr.io/microsoft/garnet:latest >/dev/null
  until docker exec tarol-probe-pg pg_isready -q 2>/dev/null; do sleep 1; done; sleep 2
}
app="$w/a/src/Caching"; native="$w/c/out/Probe.Caching"
J() { (cd "$app" && timeout 1800 dotnet run --no-build -c Release -- "$@" 2>&1) | show; }
N() { PROBE_RUNTIME="Native AOT" timeout 1800 "$native" "$@" 2>&1 | show; }
sec() {   # n title commands…
  local n=$1 title=$2; shift 2
  [ -f "$w/sec-$n.ok" ] && return 0
  { echo; echo "## $n. $title"; "$@"; } > "$w/sec-$n.txt" 2>&1 && touch "$w/sec-$n.ok"
  cat "$w/sec-$n.txt"
}
up
[ -f "$w/a.ok" ] || { "$here/run.sh" "$w/a" Caching build -c Release > "$w/a.log" 2>&1 && touch "$w/a.ok"; }
s0() { echo "# SDK $(dotnet --version) (JIT runs: Release build), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(free -g | awk '/Mem/{print $2}') GB; $(docker exec tarol-probe-pg postgres --version) (default settings, max_connections=300); $(docker exec tarol-probe-redis redis-server --version | cut -d' ' -f1-3); Garnet $(docker inspect ghcr.io/microsoft/garnet:latest --format '{{index .Config.Labels "org.opencontainers.image.version"}}') (ghcr.io/microsoft/garnet:latest); all in Docker on the same host"; grep -E ' error |warning ' "$w/a.log" | clean | sort -u; true; }
s1() { for k in postgres local redis garnet http; do J selftest $k; done; }
s2() { J bench seed 20000; }
s3() { J bench load 3; }
s4() { J bench find 3; }
s5() { J bench micro; }
s6() { J bench list 3; }
s7() { echo "| tier | payload | concurrency | latency | throughput |"; echo "|---|---|---|---|---|"; for k in local redis garnet; do J bench l2 $k 3; done; }
s8() { J bench http 3; }
s9() { J bench conflict 6; }
s10() { J bench stale 40; }
s11() { J bench hits 8; }
s12() {
  "$here/run.sh" "$w/c" Caching publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
  grep -E 'warning|error' "$w/c.log" | clean | sort -u
  echo "IL warnings: $(grep -oE 'IL[23][0-9]{3}' "$w/c.log" | wc -l) ($(grep -oE 'IL[23][0-9]{3}' "$w/c.log" | sort | uniq -c | tr '\n' ' '))"; echo "native binary: $(stat -c %s "$native") bytes"
  for k in postgres local redis garnet http; do N selftest $k; done
  N bench micro; N bench load 3
}
{
sec 0 "Machine" s0
sec 1 "JIT: functional checks (postgres = aggregate, lookup and page caches over the real store and read tables; local/redis/garnet = HybridCache; http = output caching)" s1
sec 2 "Seed: 20,000 auctions (open + 8 bids in one commit; 1 in 5 of tenant B), 600 replay-only ledgers, the auction-list projection caught up" s2
sec 3 "(b) Loads: stored state vs replay vs the aggregate cache vs HybridCache of the state bytes (1,000 auctions round robin; 3 s per cell)" s3
sec 4 "(c) Lookups: Find vs a cached Find" s4
sec 5 "(c) CPU per operation: codec metadata, state, view and page (de)serialization (JIT)" s5
sec 6 "(a) List pages: the endpoint's round trips vs the page cache vs Redis (3 s per cell)" s6
sec 7 "HybridCache tiers: L1 hit, L2 GET on Redis and Garnet (the L1 miss that hits L2)" s7
sec 8 "HTTP over loopback (Kestrel, HttpClient): lists and views, plain, cached, 304, output cache (3 s per cell)" s8
sec 9 "(b) Conflicts under contention: BidUp through ChangePipeline, the caller retries up to 20 times (C-7); two processes = two caches over one store (6 s per cell)" s9
sec 10 "Staleness: commit returned -> the cached answer shows the bid, per invalidation (40 commits each, 5 s cap)" s10
sec 11 "Hit ratio under writes: a running projection, 16 readers over 8 pages of tenant A, a writer at a fixed rate (8 s per cell)" s11
sec 12 "Native AOT: publish (warnings, size), the checks natively, CPU and loads natively" s12
} 2>&1 | tail -n +1 > /dev/null

{ cat "$w/sec-0.txt" | sed '1{/^$/d}'; for n in $(seq 1 12); do [ -f "$w/sec-$n.txt" ] && cat "$w/sec-$n.txt"; done; } > "$out"; rm -f "$out.tmp"
for n in $(seq 0 12); do [ -f "$w/sec-$n.ok" ] || echo "section $n not finished"; done
