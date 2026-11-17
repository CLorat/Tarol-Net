#!/usr/bin/env bash
# Runs every data-protection probe against PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>   (needs a running Docker daemon: dockerd &)
# Resumable: each section writes <work-dir>/sec-<n>.txt and a .ok marker; a rerun skips finished sections (ALL_REDO=<n> redoes one).
# Never run it next to the full test suite: sections 2-9 measure latency.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; mkdir -p "$w"
[ -n "${ALL_REDO:-}" ] && rm -f "$w/sec-$ALL_REDO.ok"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-900; }
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECK|xception|^\|' | clean; }
up() {
  docker start tarol-probe-dp >/dev/null 2>&1 || docker run -d --name tarol-probe-dp -e POSTGRES_PASSWORD=probe -p 55433:5432 postgres:17 -c max_connections=300 >/dev/null
  until docker exec tarol-probe-dp pg_isready -q 2>/dev/null; do sleep 1; done; sleep 2
}
app="$w/a/src/DataProtection"
J() { (cd "$app" && timeout 3600 dotnet run --no-build -c Release -- "$@" 2>&1) | show; }
N() { (cd "$w/c/out" && PROBE_RUNTIME="Native AOT" timeout 3600 ./Probe.DataProtection "$@" 2>&1) | show; }
sec() {   # n title commands…
  local n=$1 title=$2; shift 2
  [ -f "$w/sec-$n.ok" ] && return 0
  { echo; echo "## $n. $title"; "$@"; } > "$w/sec-$n.txt" 2>&1 && touch "$w/sec-$n.ok"
  cat "$w/sec-$n.txt"
}
up
[ -f "$w/a.ok" ] || { "$here/run.sh" "$w/a" DataProtection build -c Release > "$w/a.log" 2>&1 && touch "$w/a.ok"; }
s0() { echo "# SDK $(dotnet --version) (JIT runs: Release build), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(free -g | awk '/Mem/{print $2}') GB; $(docker exec tarol-probe-dp postgres --version) in Docker on the same host (default settings, max_connections=300); Npgsql $(grep -oE 'Npgsql" Version="[0-9.]+' "$here/../../../../Directory.Packages.props" | grep -oE '[0-9.]+$'); OpenSSL $(openssl version 2>/dev/null | awk '{print $2}')"; grep -E ' error |warning' "$w/a.log" | clean | sort -u; true; }
s1() { J selftest protect; }
s2() { J bench crypto; }
s3() { J bench codec; }
s4() {
  [ -f "$w/c.ok" ] || { "$here/run.sh" "$w/c" DataProtection publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1 && touch "$w/c.ok"; }
  echo "| build | IL warnings | native binary |"; echo "|---|---|---|"
  echo "| the probe (Tarol.Persistence.Npgsql, Tarol.Queries.Npgsql, System.Security.Cryptography) | $(grep -oE 'IL[23][0-9]{3}' "$w/c.log" | wc -l) | $(stat -c %s "$w/c/out/Probe.DataProtection") bytes |"
  grep -E 'warning|error' "$w/c.log" | clean | sort -u
  N selftest protect; N bench crypto; N bench codec
}
s5() { J bench load 2000; }
s6() { J bench commit 2000; }
s7() { J bench export 1000000; }
s8() { J bench forget 20000; }
s9() { J bench retention 100000; }
{
sec 0 "Machine" s0
sec 1 "JIT: functional checks (append seals, load opens, relabelled envelopes, upcasting, erasure and its NOTIFY, refusal after erasure, export, blind index, tenant key, projection re-fold, AesGcm under threads)" s1
sec 2 "The primitives (JIT): AES-256-GCM envelopes, key unwrap, blind index" s2
sec 3 "Codec candidates (JIT): one event with 2 personal fields of 3" s3
sec 4 "Native AOT: publish (warnings, size); the functional checks, the primitives and the codec natively" s4
sec 5 "Loads: today's store, plain, protected (state and replay; keys batched, cached, cold), erased (2,000 customers × 10 events)" s5
sec 6 "Commits with 4 writers: plain, protected without and with the subjects GIN index (2,000 commits per writer and kind)" s6
sec 7 "Finding one subject's events among 1,000,000 (export and erasure)" s7
sec 8 "Forgetting in a projection: rebuild vs re-fold vs delete (20,000 customers × 5 events)" s8
sec 9 "Retention by deleting whole streams in batches while 4 writers commit (100,000 customers × 5 events)" s9
} 2>&1 > /dev/null

{ cat "$w/sec-0.txt" | sed '1{/^$/d}'; for n in $(seq 1 9); do [ -f "$w/sec-$n.txt" ] && cat "$w/sec-$n.txt"; done; } > "$out"
for n in $(seq 0 9); do [ -f "$w/sec-$n.ok" ] || echo "section $n not finished"; done
