#!/usr/bin/env bash
# Runs every jobs probe against PostgreSQL 17 in Docker and writes output.txt next to this script.
# usage: all.sh <work-dir>   (needs a running Docker daemon: dockerd &)
# Resumable: each section writes <work-dir>/sec-<n>.txt and a .ok marker; a rerun skips finished sections (ALL_REDO=<n> redoes one).
# Never run it next to the full test suite: sections 4-9 measure latency and throughput.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; mkdir -p "$w"
[ -n "${ALL_REDO:-}" ] && rm -f "$w/sec-$ALL_REDO.ok"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g; s/\x1b\[[0-9;]*m//g" | cut -c1-700; }
show() { grep -E '^#|PASS|FAIL|INFO|ALL|CHECK|xception|^\|' | clean; }
up() {
  docker start tarol-probe-pg >/dev/null 2>&1 || docker run -d --name tarol-probe-pg -e POSTGRES_PASSWORD=probe -p 55432:5432 postgres:17 -c max_connections=300 >/dev/null
  until docker exec tarol-probe-pg pg_isready -q 2>/dev/null; do sleep 1; done; sleep 2
}
app="$w/a/src/Jobs"; native="$w/c/out/Probe.Jobs"
J() { (cd "$app" && timeout 1800 dotnet run --no-build -c Release -- "$@" 2>&1) | show; }
N() { (cd "$w/c/out" && PROBE_RUNTIME="Native AOT" timeout 1800 "$native" "$@" 2>&1) | show; }
sec() {   # n title commands…
  local n=$1 title=$2; shift 2
  [ -f "$w/sec-$n.ok" ] && return 0
  { echo; echo "## $n. $title"; "$@"; } > "$w/sec-$n.txt" 2>&1 && touch "$w/sec-$n.ok"
  cat "$w/sec-$n.txt"
}
up
[ -f "$w/a.ok" ] || { "$here/run.sh" "$w/a" Jobs build -c Release > "$w/a.log" 2>&1 && touch "$w/a.ok"; }
s0() { echo "# SDK $(dotnet --version) (JIT runs: Release build), $(date -u +%Y-%m-%d), $(uname -m), $(nproc) cores, $(free -g | awk '/Mem/{print $2}') GB; $(docker exec tarol-probe-pg postgres --version) in Docker on the same host (default settings, max_connections=300); Cronos 0.12.0; Npgsql $(grep -oE 'Npgsql" Version="[0-9.]+' "$here/../../../../Directory.Packages.props" | grep -oE '[0-9.]+$')"; grep -E ' error |warning ' "$w/a.log" | clean | sort -u; true; }
s1() { J selftest cron; J selftest test; J selftest postgres; }
s2() { J bench cron; }
s3() {
  "$here/run.sh" "$w/c" Jobs publish -c Release -r linux-x64 -o "$w/c/out" > "$w/c.log" 2>&1
  "$here/run.sh" "$w/n" Jobs publish -c Release -r linux-x64 -p:WithCronos=false -o "$w/n/out" > "$w/n.log" 2>&1
  for v in c n; do
    echo "| build | IL warnings | native binary |"; break; done; echo "|---|---|---|"
  for v in c n; do
    label=$([ $v = c ] && echo "with Cronos 0.12.0 (+ MiniCron)" || echo "MiniCron only (-p:WithCronos=false)")
    echo "| $label | $(grep -oE 'IL[23][0-9]{3}' "$w/$v.log" | wc -l) | $(stat -c %s "$w/$v/out/Probe.Jobs") bytes |"
  done
  grep -E 'warning|error' "$w/c.log" "$w/n.log" | clean | sort -u
  N selftest cron; N selftest test; N selftest postgres; N bench cron
}
s4() { J bench claim; }
s5() { echo "| variant | faults | processes, jobs, window | occurrences | kills / freezes / crashes | gaps | duplicate commits | duplicate runs | lateness (first start − occurrence) |"; echo "|---|---|---|---|---|---|---|---|---|"
       for v in A B D; do J chaos $v 60 3 10 none; done; PROBE_POLL_MS=1000 J chaos A 60 3 10 none | sed 's/| A | none/| A (poll 1 s) | none/'; }
s6() { echo "| variant | faults | processes, jobs, window | occurrences | kills / freezes / crashes | gaps | duplicate commits | duplicate runs | lateness (first start − occurrence) |"; echo "|---|---|---|---|---|---|---|---|---|"
       for v in A Anokey B D; do J chaos $v 60 3 10 both 25; done; }
s7() { J bench tenants 10000; }
s8() { J bench backlog 20000; }
s9() { J bench lease; }
s10() { echo "| variant | faults | processes, jobs, window | occurrences | kills / freezes / crashes | gaps | duplicate commits | duplicate runs | lateness (first start − occurrence) |"; echo "|---|---|---|---|---|---|---|---|---|"
        for v in A D; do N chaos $v 60 3 10 both 25; done; }
{
sec 0 "Machine" s0
sec 1 "JIT: functional checks (cron = DST and Cronos vs our parser; test = FakeTimeProvider and in-memory rows; postgres = variants A, B, D on the store)" s1
sec 2 "Next-occurrence cost (JIT)" s2
sec 3 "Native AOT: publish with and without Cronos (warnings, size); the checks and the cron cost natively" s3
sec 4 "Variant A: claim cost by table size" s4
sec 5 "Chaos baseline: no faults, 3 processes × 2 loops, 10 jobs every second, 60 s (fire latency)" s5
sec 6 "Chaos: kill -9 and SIGSTOP 2-6 s of a random process every 1.5-3 s, and a crash right after the commit of every 25th run of a process" s6
sec 7 "10,000 tenants, one nightly job each, all due at once" s7
sec 8 "A reaction backlog of 20,000 rows due before the job" s8
sec 9 "A run longer than the lease (5 s run, 2 s lease, 3 workers)" s9
sec 10 "Native AOT: chaos with faults" s10
} 2>&1 > /dev/null

{ cat "$w/sec-0.txt" | sed '1{/^$/d}'; for n in $(seq 1 10); do [ -f "$w/sec-$n.txt" ] && cat "$w/sec-$n.txt"; done; } > "$out"
for n in $(seq 0 10); do [ -f "$w/sec-$n.ok" ] || echo "section $n not finished"; done
