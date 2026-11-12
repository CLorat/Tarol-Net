#!/usr/bin/env bash
# Publishes every tests/*/*.AotSmoke app with Native AOT, fails on any IL2xxx/IL3xxx warning, then runs it.
# Usage: eng/aot-smoke.sh [runtime-identifier]   (default: linux-x64)
set -euo pipefail
cd "$(dirname "$0")/.."
rid=${1:-linux-x64}
status=0

for project in tests/*/*.AotSmoke/*.csproj; do
  [[ -e "$project" ]] || continue
  name=$(basename "$project" .csproj)
  log=artifacts/aot-smoke/$name.log
  mkdir -p "$(dirname "$log")"
  echo "== $name ($rid)"
  if ! dotnet publish "$project" -c Release -r "$rid" --nologo > "$log" 2>&1; then
    cat "$log"; echo "ERROR publish failed: $name" >&2; status=1; continue
  fi
  if grep -E "warning IL[0-9]{4}" "$log"; then
    echo "ERROR $name: IL warnings (the app must publish with 0 IL2xxx/IL3xxx warnings)" >&2; status=1; continue
  fi
  echo "   0 IL warnings"
  "artifacts/publish/$name/release_$rid/$name" || { echo "ERROR $name: checks failed" >&2; status=1; }
done
exit $status
