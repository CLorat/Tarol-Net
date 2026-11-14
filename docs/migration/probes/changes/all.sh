#!/usr/bin/env bash
# Runs every probe and writes output.txt next to this script. usage: all.sh <work-dir>
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; w="$1"; out="$here/output.txt"; rm -rf "$w"; mkdir -p "$w"
clean() { sed "s|$w/[a-z0-9-]*/src/||g; s|$w/||g; s|/root/.nuget/packages/||g" | cut -c1-240; }
{
echo "# SDK $(dotnet --version), $(date -u +%Y-%m-%d), $(uname -m)"
echo; echo "## App (JIT): pipeline, retry, idempotency, reactions, delivery policy"
"$here/run.sh" "$w/a" App run 2>&1 | grep -v '^\s*$' | clean
echo; echo "## App (Native AOT): publish warnings, then the native run"
"$here/run.sh" "$w/a" App publish -c Release -r linux-x64 -o "$w/a/out" > "$w/a.log" 2>&1
grep -E 'warning|error' "$w/a.log" | clean | sort -u
echo "IL warnings: $(grep -cE 'IL[23][0-9]{3}' "$w/a.log")"
"$w/a/out/Probe.Changes.App" | tail -4
echo; echo "## Web, generated RequestDelegate entries (candidate A): Native AOT publish, then the native self-test"
"$here/run.sh" "$w/b" Web publish -c Release -r linux-x64 -o "$w/b/out" > "$w/b.log" 2>&1
grep -E 'warning|error' "$w/b.log" | clean | sort -u
echo "IL/RDG warnings: $(grep -cE 'IL[23][0-9]{3}|RDG[0-9]' "$w/b.log")"
ls -l "$w/b/out/Probe.Changes.Web" | awk '{print "native binary: " $5 " bytes"}'
"$w/b/out/Probe.Changes.Web" --selftest 2>&1 | clean
echo; echo "## Web, naive entries: generated MapPost(pattern, lambda) and a generic MapPost(Delegate) (-p:ProbeEndpointStyle=delegate)"
"$here/run.sh" "$w/c" Web publish -c Release -r linux-x64 -o "$w/c/out" -p:ProbeEndpointStyle=delegate 2>&1 | grep -E 'warning|error' | clean | sort -u
"$w/c/out/Probe.Changes.Web" --selftest 2>&1 | grep -v '^   at\|^---' | clean | head -3
echo; echo "## GenVisibility: candidate B's generated input record (source output) seen by the Validations and JSON generators"
"$here/run.sh" "$w/d" GenVisibility/App build 2>&1 | grep -E 'error|warning' | clean | sort -u
echo; echo "## GenTest: generator output, incrementality, TCHG001"
"$here/run.sh" "$w/e" GenTest run 2>&1 | clean
echo; echo "## Bench: BenchmarkDotNet --job short --inProcess"
"$here/run.sh" "$w/f" Bench run -c Release -- --filter '*' --job short --inProcess 2>&1 | grep -E '^\||^BenchmarkDotNet v|^// Runtime=' | clean
} > "$out" 2>&1
echo "written $out"
